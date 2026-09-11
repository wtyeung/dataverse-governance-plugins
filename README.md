# Dataverse Field Lock Plugin

A config-driven Microsoft Dataverse plugin that enforces field-level locking based
on record status (`statuscode`). Lock rules live in Dataverse tables rather than in
code, so administrators can add, change, or disable a rule without a plugin
redeployment.

## Purpose

Many Dataverse solutions need to prevent certain fields from being edited once a
record reaches a given status (e.g. you shouldn't be able to change the `Amount` on
a claim once it's `Pending Approval`). Doing this with hardcoded plugin logic means
every new rule requires a code change, a rebuild, and a redeploy. This project
externalizes that logic into two configuration tables so business rules can evolve
independently of the plugin binary.

## Architecture

This is a **defense-in-depth** approach with two layers:

1. **Business Rules / form scripting (client-side, UX layer)** — Dataverse
   Business Rules (or JavaScript) should be used to disable/lock fields in the UI
   based on status, giving users immediate, friendly feedback. This is *not*
   security — it only affects the model-driven app form.
2. **`FieldLockEnforcer` plugin (server-side, enforcement layer)** — Registered on
   `Update`, Pre-Operation, synchronous. This is the authoritative enforcement
   point: it runs for **every** update regardless of client (model-driven app,
   Power Automate, custom app, Web API, data import, etc.) and throws an
   `InvalidPluginExecutionException` if a locked field is changed while the
   record is in a matching status.

Because the UX layer can always be bypassed (API calls, flows, integrations), the
plugin is what actually guarantees the field is protected.

### How rule evaluation works

1. On `Update`, the plugin reads `statuscode` from the required `PreImage`.
2. It queries `cfg_fieldlockrule` (filtered to `cfg_isactive = true`) joined with
   its child `cfg_lockedfield` rows, and caches the result in-memory for 5 minutes
   to avoid re-querying Dataverse on every request.
3. It finds rules where `cfg_entitylogicalname` matches the entity being updated
   and `cfg_statusreasonvalue` matches the current `statuscode`.
4. For each locked field in a matching rule, it compares the value in the
   `Target` (the incoming change) against the value in the `PreImage` (the value
   before the update), correctly handling `OptionSetValue`, `EntityReference`, and
   `Money` attribute types.
5. If a locked field's value actually changed, it throws an
   `InvalidPluginExecutionException`, which surfaces as a business error and rolls
   back the transaction.
6. If the rule specifies `cfg_bypasssecurityrole`, and the initiating user holds
   that security role, the rule is skipped entirely for that request.

See `docs/config-schema.md` for the full table schema and a sample configuration.

## Building locally

Requirements: .NET SDK 8.0+ (used only to drive the `net462` build; the compiled
assembly still targets .NET Framework 4.6.2, which is required for Dataverse
plugin registration).

```bash
dotnet restore src/FieldLockPlugin.csproj
dotnet build src/FieldLockPlugin.csproj -c Release
```

The compiled `FieldLockPlugin.dll` will be under `src/bin/Release/net462/`.

CI builds the same way on every push to `main` via
[`.github/workflows/build.yml`](.github/workflows/build.yml), which also uploads
the DLL as a build artifact and publishes it to a tagged GitHub Release
(`v1.0.<run_number>`).

## Configuration tables

Lock rules are defined in two tables — see [`docs/config-schema.md`](docs/config-schema.md)
for full column definitions and a sample record:

- **`cfg_fieldlockrule`** — one row per (entity, status reason) rule, including an
  optional bypass security role.
- **`cfg_lockedfield`** — one row per field locked by a given rule, linked back to
  its parent rule via `cfg_fieldlockrule`.

No code changes are needed to add a new rule: create a `cfg_fieldlockrule` row,
add its `cfg_lockedfield` children, and activate it.

## Registering the plugin

You can register the plugin either with the **Plugin Registration Tool** or the
**Dataverse Web API**.

### Option A: Plugin Registration Tool

1. Build `FieldLockPlugin.dll` in `Release` configuration.
2. Open the Plugin Registration Tool and connect to your environment.
3. **Register a New Assembly**, selecting `FieldLockPlugin.dll`.
4. On the `Contoso.Dataverse.Plugins.FieldLockEnforcer` type, **Register New Step**:
   - Message: `Update`
   - Primary Entity: the entity you want to protect (e.g. `contoso_claim`), or
     register one step per entity referenced by your rules.
   - Stage: `Pre-Operation`
   - Execution Mode: `Synchronous`
5. On the new step, **Register New Image**:
   - Image Type: `Pre Image`
   - Entity Alias / Name: `PreImage` (must match exactly — this is the alias the
     plugin looks up)
   - Attributes: `statuscode` **plus every attribute referenced by any
     `cfg_lockedfield.cfg_fieldlogicalname` for that entity**. If you add a new
     locked field later, update this image too.

### Option B: Dataverse Web API

1. Upload the assembly via `POST /api/data/v9.2/pluginassemblies` (base64-encoded
   `content`, `isolationmode = 2` for sandbox).
2. Create the plugin type record (`plugintype`) referencing the assembly, with
   `typename = Contoso.Dataverse.Plugins.FieldLockEnforcer`.
2. Create an `sdkmessageprocessingstep` for the `Update` message on the target
   entity, with `stage = 20` (Pre-Operation) and `mode = 0` (Synchronous).
3. Create an `sdkmessageprocessingstepimage` on that step with
   `imagetype = 0` (Pre Image), `entityalias = PreImage`, and `attributes`
   listing `statuscode` plus every field any active rule locks for that entity.

> ⚠️ **PreImage requirement**: The plugin will throw immediately if the `PreImage`
> is missing. Whenever you add a new locked field to `cfg_lockedfield`, remember to
> also add that field's logical name to the registered PreImage attribute list —
> otherwise the "before" value the plugin compares against will be unavailable and
> the field will effectively be un-enforceable.

## License

Internal / proprietary — adapt as needed for your organization.
