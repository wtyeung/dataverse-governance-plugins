using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Contoso.Dataverse.Plugins
{
    /// <summary>
    /// Enforces field-level locking on Update requests based on the record's statuscode
    /// (status reason). Lock rules are config-driven, stored in the cfg_fieldlockrule
    /// and cfg_lockedfield entities, so new rules can be added by administrators without
    /// redeploying this plugin.
    ///
    /// Registration:
    ///   Message:    Update
    ///   Stage:      Pre-Operation (20)
    ///   Mode:       Synchronous
    ///   Image:      PreImage (Pre-Image) containing "statuscode" and every field that
    ///               might be locked by a rule.
    /// </summary>
    public class FieldLockEnforcer : IPlugin
    {
        private const string PreImageAlias = "PreImage";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const string CacheKey = "Contoso.Dataverse.Plugins.FieldLockEnforcer.Rules";

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            tracingService.Trace("FieldLockEnforcer: Execute started for entity '{0}', message '{1}', stage {2}.",
                context.PrimaryEntityName, context.MessageName, context.Stage);

            if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase))
            {
                tracingService.Trace("FieldLockEnforcer: Message is not Update, exiting.");
                return;
            }

            if (!(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
            {
                tracingService.Trace("FieldLockEnforcer: No Target entity found, exiting.");
                return;
            }

            var target = (Entity)context.InputParameters["Target"];

            if (!context.PreEntityImages.Contains(PreImageAlias))
            {
                throw new InvalidPluginExecutionException(
                    $"FieldLockEnforcer requires a PreImage named '{PreImageAlias}' to be registered, containing 'statuscode' and all lockable fields.");
            }

            var preImage = context.PreEntityImages[PreImageAlias];

            if (!preImage.Contains("statuscode"))
            {
                tracingService.Trace("FieldLockEnforcer: PreImage does not contain 'statuscode', exiting.");
                return;
            }

            var currentStatusReason = GetOptionSetValue(preImage, "statuscode");
            if (currentStatusReason == null)
            {
                tracingService.Trace("FieldLockEnforcer: statuscode is null on PreImage, exiting.");
                return;
            }

            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var systemService = serviceFactory.CreateOrganizationService(null);

            var rules = GetActiveLockRules(systemService, tracingService);

            var matchingRules = rules
                .Where(r => string.Equals(r.EntityLogicalName, context.PrimaryEntityName, StringComparison.OrdinalIgnoreCase)
                            && r.StatusReasonValue == currentStatusReason.Value)
                .ToList();

            if (matchingRules.Count == 0)
            {
                tracingService.Trace("FieldLockEnforcer: No matching lock rules for entity '{0}' with statuscode {1}.",
                    context.PrimaryEntityName, currentStatusReason.Value);
                return;
            }

            var userService = serviceFactory.CreateOrganizationService(context.InitiatingUserId);

            foreach (var rule in matchingRules)
            {
                tracingService.Trace("FieldLockEnforcer: Evaluating rule '{0}' ({1} locked field(s)).",
                    rule.Name, rule.LockedFields.Count);

                if (!string.IsNullOrWhiteSpace(rule.BypassSecurityRole)
                    && UserHasSecurityRole(userService, context.InitiatingUserId, rule.BypassSecurityRole, tracingService))
                {
                    tracingService.Trace("FieldLockEnforcer: Initiating user holds bypass role '{0}', skipping rule '{1}'.",
                        rule.BypassSecurityRole, rule.Name);
                    continue;
                }

                foreach (var fieldLogicalName in rule.LockedFields)
                {
                    if (!target.Contains(fieldLogicalName))
                    {
                        continue; // Field is not part of this update, nothing to enforce.
                    }

                    var newValue = target[fieldLogicalName];
                    var oldValue = preImage.Contains(fieldLogicalName) ? preImage[fieldLogicalName] : null;

                    if (!ValuesAreEqual(newValue, oldValue))
                    {
                        tracingService.Trace(
                            "FieldLockEnforcer: Field '{0}' is locked by rule '{1}' and was changed. Blocking update.",
                            fieldLogicalName, rule.Name);

                        throw new InvalidPluginExecutionException(
                            $"The field '{fieldLogicalName}' is locked and cannot be modified while the record status is '{rule.StatusReasonValue}' (rule: '{rule.Name}').");
                    }
                }
            }

            tracingService.Trace("FieldLockEnforcer: Execute completed.");
        }

        /// <summary>
        /// Retrieves active lock rules (cfg_fieldlockrule + child cfg_lockedfield rows),
        /// caching the result in-memory for CacheDuration to avoid repeated Dataverse queries.
        /// </summary>
        private static List<FieldLockRule> GetActiveLockRules(IOrganizationService service, ITracingService tracingService)
        {
            var cache = MemoryCache.Default;

            if (cache.Get(CacheKey) is List<FieldLockRule> cached)
            {
                tracingService.Trace("FieldLockEnforcer: Using cached lock rules ({0} rule(s)).", cached.Count);
                return cached;
            }

            tracingService.Trace("FieldLockEnforcer: Cache miss, querying cfg_fieldlockrule / cfg_lockedfield.");

            var ruleQuery = new QueryExpression("cfg_fieldlockrule")
            {
                ColumnSet = new ColumnSet(
                    "cfg_fieldlockruleid",
                    "cfg_name",
                    "cfg_entitylogicalname",
                    "cfg_statusreasonvalue",
                    "cfg_isactive",
                    "cfg_bypasssecurityrole"),
                Criteria = new FilterExpression(LogicalOperator.And)
                {
                    Conditions =
                    {
                        new ConditionExpression("cfg_isactive", ConditionOperator.Equal, true)
                    }
                }
            };

            var lockedFieldLink = ruleQuery.AddLink("cfg_lockedfield", "cfg_fieldlockruleid", "cfg_fieldlockrule");
            lockedFieldLink.EntityAlias = "lf";
            lockedFieldLink.Columns = new ColumnSet("cfg_lockedfieldid", "cfg_fieldlogicalname");
            lockedFieldLink.JoinOperator = JoinOperator.LeftOuter;

            var rules = new Dictionary<Guid, FieldLockRule>();

            var result = service.RetrieveMultiple(ruleQuery);
            foreach (var row in result.Entities)
            {
                var ruleId = row.Id;
                if (!rules.TryGetValue(ruleId, out var rule))
                {
                    rule = new FieldLockRule
                    {
                        Id = ruleId,
                        Name = row.GetAttributeValue<string>("cfg_name"),
                        EntityLogicalName = row.GetAttributeValue<string>("cfg_entitylogicalname"),
                        StatusReasonValue = row.GetAttributeValue<int?>("cfg_statusreasonvalue") ?? 0,
                        BypassSecurityRole = row.GetAttributeValue<string>("cfg_bypasssecurityrole"),
                        LockedFields = new List<string>()
                    };
                    rules[ruleId] = rule;
                }

                var fieldLogicalName = row.GetAttributeValue<AliasedValue>("lf.cfg_fieldlogicalname")?.Value as string;
                if (!string.IsNullOrWhiteSpace(fieldLogicalName) && !rule.LockedFields.Contains(fieldLogicalName))
                {
                    rule.LockedFields.Add(fieldLogicalName);
                }
            }

            var ruleList = rules.Values.ToList();

            cache.Set(CacheKey, ruleList, DateTimeOffset.UtcNow.Add(CacheDuration));

            tracingService.Trace("FieldLockEnforcer: Loaded and cached {0} active rule(s).", ruleList.Count);

            return ruleList;
        }

        private static bool UserHasSecurityRole(IOrganizationService userService, Guid userId, string roleName, ITracingService tracingService)
        {
            try
            {
                var query = new QueryExpression("role")
                {
                    ColumnSet = new ColumnSet("roleid", "name"),
                    Criteria = new FilterExpression(LogicalOperator.And)
                    {
                        Conditions = { new ConditionExpression("name", ConditionOperator.Equal, roleName) }
                    }
                };

                var link = query.AddLink("systemuserroles", "roleid", "roleid");
                link.LinkCriteria.AddCondition("systemuserid", ConditionOperator.Equal, userId);

                var results = userService.RetrieveMultiple(query);
                return results.Entities.Count > 0;
            }
            catch (Exception ex)
            {
                tracingService.Trace("FieldLockEnforcer: Error checking security role '{0}': {1}", roleName, ex.Message);
                return false;
            }
        }

        private static OptionSetValue GetOptionSetValue(Entity entity, string attributeName)
        {
            if (!entity.Contains(attributeName))
            {
                return null;
            }

            return entity[attributeName] as OptionSetValue;
        }

        private static bool ValuesAreEqual(object newValue, object oldValue)
        {
            if (newValue == null && oldValue == null)
            {
                return true;
            }

            if (newValue == null || oldValue == null)
            {
                return false;
            }

            switch (newValue)
            {
                case OptionSetValue newOptionSet when oldValue is OptionSetValue oldOptionSet:
                    return newOptionSet.Value == oldOptionSet.Value;

                case EntityReference newRef when oldValue is EntityReference oldRef:
                    return newRef.LogicalName == oldRef.LogicalName && newRef.Id == oldRef.Id;

                case Money newMoney when oldValue is Money oldMoney:
                    return newMoney.Value == oldMoney.Value;

                default:
                    return newValue.Equals(oldValue);
            }
        }

        private class FieldLockRule
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
            public string EntityLogicalName { get; set; }
            public int StatusReasonValue { get; set; }
            public string BypassSecurityRole { get; set; }
            public List<string> LockedFields { get; set; }
        }
    }
}
