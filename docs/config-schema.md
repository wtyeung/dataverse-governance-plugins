# Configuration Schema

Field lock rules are stored entirely in Dataverse configuration tables, so new rules
can be created (or existing ones changed) by an administrator without redeploying
the plugin.

## `cfg_fieldlockrule` (parent)

One row per lock rule. A rule ties a specific entity + status reason combination to a
set of fields that should be locked.

| Column                     | Type              | Description                                                                                     |
|----------------------------|-------------------|---------------------------------------------------------------------------------------------------|
| `cfg_fieldlockruleid`      | Unique Identifier | Primary key.                                                                                     |
| `cfg_name`                 | Text              | Primary name / friendly label for the rule, e.g. `Claim - Pending Approval`.                    |
| `cfg_entitylogicalname`    | Text              | Logical name of the target entity, e.g. `contoso_claim`.                                        |
| `cfg_statusreasonvalue`    | Whole Number      | The `statuscode` (status reason) option set value that activates this rule.                     |
| `cfg_isactive`             | Yes/No            | Whether the rule is enabled. Inactive rules are ignored by the plugin.                           |
| `cfg_bypasssecurityrole`   | Text              | (Optional) Name of a security role that, if held by the acting user, bypasses this rule.         |
| `cfg_description`          | Multiline Text    | Free-text description of why the rule exists / what it protects.                                 |

## `cfg_lockedfield` (child, N:1 to `cfg_fieldlockrule`)

One row per field locked by a given rule.

| Column                    | Type              | Description                                                          |
|---------------------------|-------------------|------------------------------------------------------------------------|
| `cfg_lockedfieldid`       | Unique Identifier | Primary key.                                                          |
| `cfg_name`                | Text              | Primary name, e.g. `Amount`.                                          |
| `cfg_fieldlockrule`       | Lookup            | Parent lookup to `cfg_fieldlockrule`.                                 |
| `cfg_fieldlogicalname`    | Text              | Logical name of the field to lock, e.g. `contoso_amount`.             |

## Sample configuration

The example below locks the `Amount` (`contoso_amount`) and `Payee`
(`contoso_payee`) fields on the `contoso_claim` entity whenever the record's
status reason is `Pending Approval` (status reason value `100000001`), unless
the acting user holds the `Claims Administrator` security role.

```json
{
  "cfg_fieldlockrule": {
    "cfg_name": "Claim - Pending Approval",
    "cfg_entitylogicalname": "contoso_claim",
    "cfg_statusreasonvalue": 100000001,
    "cfg_isactive": true,
    "cfg_bypasssecurityrole": "Claims Administrator",
    "cfg_description": "Prevents changes to financial and payee details once a claim has been submitted for approval.",
    "cfg_lockedfield": [
      {
        "cfg_name": "Amount",
        "cfg_fieldlogicalname": "contoso_amount"
      },
      {
        "cfg_name": "Payee",
        "cfg_fieldlogicalname": "contoso_payee"
      }
    ]
  }
}
```

## Notes

- The plugin caches the active rule set in-memory for 5 minutes. Changes to
  `cfg_fieldlockrule` / `cfg_lockedfield` may take up to 5 minutes to take effect,
  or until the sandbox worker process recycles.
- `cfg_statusreasonvalue` must match the underlying integer value of the
  `statuscode` option set on the target entity, not its label.
- If `cfg_bypasssecurityrole` is left blank, the rule cannot be bypassed by any role.
