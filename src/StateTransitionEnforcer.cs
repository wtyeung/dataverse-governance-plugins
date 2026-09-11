using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Contoso.Dataverse.Plugins
{
    /// <summary>
    /// Enforces valid status-reason transitions on Update requests using a state machine
    /// defined as a Mermaid `stateDiagram-v2` document, stored config-driven in the
    /// cfg_statetransitiondefinition table. Business users (or admins) can edit the
    /// Mermaid text directly -- it doubles as human-readable documentation of the
    /// workflow and as the executable configuration enforced by this plugin.
    ///
    /// Mermaid syntax supported:
    ///   stateDiagram-v2
    ///   Draft --> Submitted
    ///   Submitted --> Approved : Approve/Manager
    ///   Submitted --> Rejected : Reject/Manager/cfg_budget&lt;10000
    ///
    /// Transition labels (after the `:`) are optional and use the convention
    /// `Action/Role/Condition`, where each segment is optional:
    ///   - Action:    free-text description, informational only.
    ///   - Role:      security role name required to perform this transition.
    ///   - Condition: a simple comparison against a field on the record, e.g.
    ///                `cfg_budget&lt;10000` (supports &lt; &lt;= &gt; &gt;= == !=).
    ///
    /// State names are matched (case-insensitively) against the *labels* of the
    /// entity's statuscode option set, so admins never need to know numeric status
    /// reason values.
    ///
    /// Registration:
    ///   Message:    Update
    ///   Stage:      Pre-Operation (20)
    ///   Mode:       Synchronous
    ///   Image:      PreImage (Pre-Image) containing "statuscode" and any field(s)
    ///               referenced by transition conditions.
    /// </summary>
    public class StateTransitionEnforcer : IPlugin
    {
        private const string PreImageAlias = "PreImage";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const string DefinitionCacheKeyPrefix = "Contoso.Dataverse.Plugins.StateTransitionEnforcer.Definitions.";
        private const string StatusLabelCacheKeyPrefix = "Contoso.Dataverse.Plugins.StateTransitionEnforcer.StatusLabels.";

        private static readonly Regex TransitionLineRegex = new Regex(
            @"^(?<from>.+?)\s*-->\s*(?<to>[^:]+?)\s*(:\s*(?<label>.+))?$",
            RegexOptions.Compiled);

        private static readonly Regex ConditionRegex = new Regex(
            @"^\s*(?<field>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?<op>>=|<=|==|!=|>|<)\s*(?<value>-?\d+(\.\d+)?)\s*$",
            RegexOptions.Compiled);

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            tracingService.Trace("StateTransitionEnforcer: Execute started for entity '{0}', message '{1}', stage {2}.",
                context.PrimaryEntityName, context.MessageName, context.Stage);

            if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity))
            {
                return;
            }

            var target = (Entity)context.InputParameters["Target"];

            if (!target.Contains("statuscode"))
            {
                tracingService.Trace("StateTransitionEnforcer: statuscode is not part of this update, exiting.");
                return;
            }

            if (!context.PreEntityImages.Contains(PreImageAlias))
            {
                throw new InvalidPluginExecutionException(
                    $"StateTransitionEnforcer requires a PreImage named '{PreImageAlias}' to be registered, containing 'statuscode' and any fields referenced by transition conditions.");
            }

            var preImage = context.PreEntityImages[PreImageAlias];

            var oldStatus = preImage.GetAttributeValue<OptionSetValue>("statuscode");
            var newStatus = target.GetAttributeValue<OptionSetValue>("statuscode");

            if (oldStatus == null || newStatus == null || oldStatus.Value == newStatus.Value)
            {
                return; // No actual status change to validate.
            }

            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var systemService = serviceFactory.CreateOrganizationService(null);

            var definition = GetStateMachine(systemService, tracingService, context.PrimaryEntityName);
            if (definition == null)
            {
                tracingService.Trace("StateTransitionEnforcer: No active state transition definition for entity '{0}'.",
                    context.PrimaryEntityName);
                return;
            }

            var statusLabels = GetStatusLabelMap(systemService, tracingService, context.PrimaryEntityName);

            if (!statusLabels.ValueToLabel.TryGetValue(oldStatus.Value, out var oldLabel) ||
                !statusLabels.ValueToLabel.TryGetValue(newStatus.Value, out var newLabel))
            {
                tracingService.Trace(
                    "StateTransitionEnforcer: Could not resolve statuscode {0} or {1} to a label, skipping validation.",
                    oldStatus.Value, newStatus.Value);
                return;
            }

            if (!definition.Graph.TryGetValue(Normalize(oldLabel), out var candidates))
            {
                throw new InvalidPluginExecutionException(
                    $"Invalid transition: '{oldLabel}' has no outgoing transitions defined in workflow '{definition.Name}'.");
            }

            var matchingEdges = candidates.Where(e => string.Equals(Normalize(e.ToState), Normalize(newLabel), StringComparison.Ordinal)).ToList();

            if (matchingEdges.Count == 0)
            {
                throw new InvalidPluginExecutionException(
                    $"Invalid transition: '{oldLabel}' -> '{newLabel}' is not allowed by workflow '{definition.Name}'. " +
                    $"Allowed transitions from '{oldLabel}': {string.Join(", ", candidates.Select(c => c.ToState))}.");
            }

            var userService = serviceFactory.CreateOrganizationService(context.InitiatingUserId);
            string lastFailureReason = null;

            foreach (var edge in matchingEdges)
            {
                if (!string.IsNullOrWhiteSpace(edge.RequiredRole) &&
                    !UserHasSecurityRole(userService, context.InitiatingUserId, edge.RequiredRole, tracingService))
                {
                    lastFailureReason = $"requires security role '{edge.RequiredRole}'";
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(edge.Condition) &&
                    !EvaluateCondition(edge.Condition, target, preImage, tracingService, out var conditionFailureReason))
                {
                    lastFailureReason = conditionFailureReason;
                    continue;
                }

                tracingService.Trace(
                    "StateTransitionEnforcer: Transition '{0}' -> '{1}' allowed (action '{2}').",
                    oldLabel, newLabel, edge.Action);
                return; // At least one satisfied edge -- transition is allowed.
            }

            throw new InvalidPluginExecutionException(
                $"Invalid transition: '{oldLabel}' -> '{newLabel}' is defined in workflow '{definition.Name}' but its requirements were not met ({lastFailureReason}).");
        }

        /// <summary>
        /// Loads active cfg_statetransitiondefinition row(s) for the given entity, parses
        /// their Mermaid text into a merged adjacency graph, and caches the result for
        /// CacheDuration.
        /// </summary>
        private static StateMachine GetStateMachine(IOrganizationService service, ITracingService tracingService, string entityLogicalName)
        {
            var cache = MemoryCache.Default;
            var cacheKey = DefinitionCacheKeyPrefix + entityLogicalName;

            if (cache.Get(cacheKey) is StateMachine cached)
            {
                tracingService.Trace("StateTransitionEnforcer: Using cached state machine for '{0}'.", entityLogicalName);
                return cached;
            }

            var query = new QueryExpression("cfg_statetransitiondefinition")
            {
                ColumnSet = new ColumnSet("cfg_name", "cfg_mermaiddefinition"),
                Criteria = new FilterExpression(LogicalOperator.And)
                {
                    Conditions =
                    {
                        new ConditionExpression("cfg_isactive", ConditionOperator.Equal, true),
                        new ConditionExpression("cfg_entitylogicalname", ConditionOperator.Equal, entityLogicalName)
                    }
                }
            };

            var rows = service.RetrieveMultiple(query).Entities;

            StateMachine machine = null;
            if (rows.Count > 0)
            {
                var names = new List<string>();
                var graph = new Dictionary<string, List<TransitionEdge>>();

                foreach (var row in rows)
                {
                    var name = row.GetAttributeValue<string>("cfg_name");
                    var mermaid = row.GetAttributeValue<string>("cfg_mermaiddefinition");
                    names.Add(name);

                    foreach (var edge in ParseMermaid(mermaid))
                    {
                        var key = Normalize(edge.FromState);
                        if (!graph.TryGetValue(key, out var list))
                        {
                            list = new List<TransitionEdge>();
                            graph[key] = list;
                        }

                        list.Add(edge);
                    }
                }

                machine = new StateMachine
                {
                    Name = string.Join(", ", names),
                    Graph = graph
                };
            }

            cache.Set(cacheKey, machine, DateTimeOffset.UtcNow.Add(CacheDuration));
            tracingService.Trace("StateTransitionEnforcer: Loaded and cached state machine for '{0}' ({1} definition row(s)).",
                entityLogicalName, rows.Count);

            return machine;
        }

        /// <summary>
        /// Parses a Mermaid `stateDiagram-v2` document into a flat list of transition edges.
        /// Lines that aren't recognizable transitions (headers, comments, blank lines,
        /// pseudo-states like `[*]`) are ignored.
        /// </summary>
        internal static IEnumerable<TransitionEdge> ParseMermaid(string mermaidText)
        {
            if (string.IsNullOrWhiteSpace(mermaidText))
            {
                yield break;
            }

            var lines = mermaidText.Replace("\r\n", "\n").Split('\n');

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (string.IsNullOrEmpty(line)
                    || line.StartsWith("stateDiagram", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("%%")
                    || line.Contains("[*]"))
                {
                    continue;
                }

                var match = TransitionLineRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var fromState = match.Groups["from"].Value.Trim();
                var toState = match.Groups["to"].Value.Trim();
                var label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : null;

                if (string.IsNullOrEmpty(fromState) || string.IsNullOrEmpty(toState))
                {
                    continue;
                }

                string action = null, role = null, condition = null;
                if (!string.IsNullOrEmpty(label))
                {
                    var parts = label.Split('/');
                    action = parts.Length > 0 ? EmptyToNull(parts[0].Trim()) : null;
                    role = parts.Length > 1 ? EmptyToNull(parts[1].Trim()) : null;
                    condition = parts.Length > 2 ? EmptyToNull(parts[2].Trim()) : null;
                }

                yield return new TransitionEdge
                {
                    FromState = fromState,
                    ToState = toState,
                    Action = action,
                    RequiredRole = role,
                    Condition = condition
                };
            }
        }

        /// <summary>
        /// Builds a bidirectional map between an entity's `statuscode` option set values
        /// and their display labels, so Mermaid state names (labels) can be resolved to
        /// the numeric status reason values actually stored on records. Cached for
        /// CacheDuration.
        /// </summary>
        private static StatusLabelMap GetStatusLabelMap(IOrganizationService service, ITracingService tracingService, string entityLogicalName)
        {
            var cache = MemoryCache.Default;
            var cacheKey = StatusLabelCacheKeyPrefix + entityLogicalName;

            if (cache.Get(cacheKey) is StatusLabelMap cached)
            {
                return cached;
            }

            var request = new RetrieveAttributeRequest
            {
                EntityLogicalName = entityLogicalName,
                LogicalName = "statuscode",
                RetrieveAsIfPublished = false
            };

            var response = (RetrieveAttributeResponse)service.Execute(request);
            var metadata = (StatusAttributeMetadata)response.AttributeMetadata;

            var map = new StatusLabelMap
            {
                ValueToLabel = new Dictionary<int, string>(),
                LabelToValue = new Dictionary<string, int>()
            };

            foreach (var option in metadata.OptionSet.Options)
            {
                if (!option.Value.HasValue)
                {
                    continue;
                }

                var label = option.Label?.UserLocalizedLabel?.Label ?? option.Value.Value.ToString(CultureInfo.InvariantCulture);
                map.ValueToLabel[option.Value.Value] = label;
                map.LabelToValue[Normalize(label)] = option.Value.Value;
            }

            cache.Set(cacheKey, map, DateTimeOffset.UtcNow.Add(CacheDuration));
            tracingService.Trace("StateTransitionEnforcer: Loaded and cached {0} statuscode label(s) for '{1}'.",
                map.ValueToLabel.Count, entityLogicalName);

            return map;
        }

        /// <summary>
        /// Evaluates a simple `field&lt;op&gt;value` condition (e.g. `cfg_budget&lt;10000`)
        /// against the Target (falling back to the PreImage for unspecified fields).
        /// Supports Money, whole number, and decimal attribute types.
        /// </summary>
        private static bool EvaluateCondition(string condition, Entity target, Entity preImage, ITracingService tracingService, out string failureReason)
        {
            var match = ConditionRegex.Match(condition);
            if (!match.Success)
            {
                tracingService.Trace("StateTransitionEnforcer: Could not parse condition '{0}', treating as not satisfied.", condition);
                failureReason = $"condition '{condition}' could not be evaluated";
                return false;
            }

            var field = match.Groups["field"].Value;
            var op = match.Groups["op"].Value;
            var threshold = decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);

            object rawValue = target.Contains(field) ? target[field] : (preImage.Contains(field) ? preImage[field] : null);

            if (!TryConvertToDecimal(rawValue, out var actual))
            {
                failureReason = $"field '{field}' referenced by condition '{condition}' has no comparable value";
                return false;
            }

            bool result;
            switch (op)
            {
                case "<": result = actual < threshold; break;
                case "<=": result = actual <= threshold; break;
                case ">": result = actual > threshold; break;
                case ">=": result = actual >= threshold; break;
                case "==": result = actual == threshold; break;
                case "!=": result = actual != threshold; break;
                default: result = false; break;
            }

            failureReason = result ? null : $"condition '{condition}' was not satisfied (actual value: {actual})";
            return result;
        }

        private static bool TryConvertToDecimal(object value, out decimal result)
        {
            switch (value)
            {
                case Money money:
                    result = money.Value;
                    return true;
                case decimal dec:
                    result = dec;
                    return true;
                case int i:
                    result = i;
                    return true;
                case double d:
                    result = (decimal)d;
                    return true;
                default:
                    result = 0;
                    return false;
            }
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

                return userService.RetrieveMultiple(query).Entities.Count > 0;
            }
            catch (Exception ex)
            {
                tracingService.Trace("StateTransitionEnforcer: Error checking security role '{0}': {1}", roleName, ex.Message);
                return false;
            }
        }

        private static string Normalize(string value) => value?.Trim().ToLowerInvariant();

        private static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

        internal class TransitionEdge
        {
            public string FromState { get; set; }
            public string ToState { get; set; }
            public string Action { get; set; }
            public string RequiredRole { get; set; }
            public string Condition { get; set; }
        }

        private class StateMachine
        {
            public string Name { get; set; }
            public Dictionary<string, List<TransitionEdge>> Graph { get; set; }
        }

        private class StatusLabelMap
        {
            public Dictionary<int, string> ValueToLabel { get; set; }
            public Dictionary<string, int> LabelToValue { get; set; }
        }
    }
}
