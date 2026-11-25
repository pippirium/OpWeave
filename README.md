# OpWeave

[日本語](README.ja.md)

OpWeave is a Unity Editor workflow engine: it discovers typed operations, lets you compose them into workflow documents, compiles and executes them, and recovers interrupted runs from durable journals. It ships as the UPM package `com.opweave.workflow` version **0.1.1**, licensed under [Apache License 2.0](LICENSE).

## Windows v1 capabilities

- **Operation catalog** — search built-in and extension operations by type, port compatibility, declared side effects, tags, and origin.
- **Visual workflow authoring** — on Unity 6000.5 or newer, create and edit `.opweavegraph` assets in the Graph Toolkit editor with separate control-flow and data-flow connections.
- **Canonical workflow documents** — export the authoritative `.opweave.json` document explicitly with **OpWeave > Save Graph JSON**; a graph asset save does not silently rewrite JSON.
- **Compilation and validation** — deterministic workflow compilation with typed port checking, declared effects, approval requirements, and stable plan hashes.
- **Sequential execution** — ordered step execution that waits for each operation's declared completion condition and records results and diagnostics.
- **Recovery** — durable intent/journal records so an interrupted save or run reconciles to a reviewable state instead of silently losing or duplicating work.
- **Run Viewer** — inspect run state, diagnostics, and pending human approvals through **Window > OpWeave > Runs**.
- **Agent API** — a structured interface for AI agents to discover operations, inspect schemas, validate workflows, execute approved work, and inspect run state.
- **Extensibility** — add project custom operations or install independent UPM operation extensions without modifying OpWeave itself.

## Requirements

- **Operating system:** Windows 11 x64
- **Verified Unity Editor:** 6000.5.11f1
- **Package manifest floor:** Unity `6000.0`
- **Graph Toolkit authoring UI:** Unity 6000.5 or newer; the verified configuration is 6000.5.11f1
- macOS and Linux are **not supported in v1** (see Known limitations).

The manifest floor is a compile floor, not the verified support target. Do not treat Graph Toolkit GUI functionality as supported on Unity 6000.0.

## Installation (Unity Package Manager)

Add the package through a Git URL pinned to the release tag:

1. Open **Window > Package Manager**.
2. Choose **+ > Add package from git URL…**.
3. Enter:

   ```
   https://github.com/pippirium/OpWeave.git?path=Packages/com.opweave.workflow#v0.1.1
   ```

The package declares its dependencies (`com.unity.nuget.newtonsoft-json`, `com.unity.nuget.mono-cecil`) so they resolve automatically from the Unity registry.

### Upgrading and installing older versions

To upgrade, change the tag in the Git URL and reinstall or update the package. Release tags are immutable; fixes ship as later versions rather than by moving an existing tag.

The previous release remains available at:

```
https://github.com/pippirium/OpWeave.git?path=Packages/com.opweave.workflow#v0.1.0
```

## Using OpWeave

1. On Unity 6000.5 or newer, use **Assets > Create > OpWeave > Graph** (also available from the Project window's Create menu) to create a `.opweavegraph` asset. Double-click it to open the Graph Toolkit editor.
2. Search the operation catalog and place operation nodes; connect control-flow edges (execution order) and data ports (typed values). Graph validation reports missing or incompatible connections while you edit.
3. Save the graph asset when you want to keep the visual graph. Use **OpWeave > Save Graph JSON** from the Assets or Tools menu to write the canonical `.opweave.json` workflow document. Creating, renaming, or saving the graph asset never writes that JSON implicitly.
4. Validate and run the workflow through the editor integration or Agent API. Execution waits for each operation's declared completion condition and reports per-step results and diagnostics.
5. Open **Window > OpWeave > Runs** to inspect active and completed runs, diagnostics, and operations waiting for a human approval decision.
6. If a run or JSON save is interrupted, recovery reconciles the durable journal into a reviewable state. Uncertain states surface as needing review; they are never treated as silent successes.

## Standard operations

The built-in catalog covers the Windows v1 Unity Editor operation groups:

- Scene and GameObject creation, lookup, hierarchy, transform, component, and property operations.
- Prefab creation, instantiation, contents editing, overrides, unpacking, and saving.
- Asset creation, import, move/copy/delete, folder, material, ScriptableObject, and script-template operations.
- Script compilation and Unity package add/remove operations.

Operations declare their inputs, outputs, effects, completion conditions, undo support, and idempotency behavior. Operations that can destroy data, write arbitrary code, or otherwise require review use the human approval flow; agents cannot approve their own approval-requiring work.

## Agent integration

OpWeave exposes a structured Agent API for operation search, operation description, schema retrieval, workflow validation, execution, migration, run status/logs, cancellation, and registry status. When the optional `com.unity.pipeline` **0.9.1-exp.1** package is installed in the using project, the `OpWeave.Pipeline` adapter exposes these commands through Unity's command transport in the `opweave` command group.

The normal agent flow is:

1. `search_operations` to find candidate operations.
2. `describe_operation` to inspect one operation's contract and examples.
3. `validate_workflow` to compile the workflow and obtain diagnostics, effects, approvals, and a plan hash.
4. `execute_workflow` to admit execution, optionally bound to the validated plan hash.
5. `get_run_status` and `get_run_logs` to follow execution to a terminal state.

Business failures are returned in the structured response envelope with `ok: false` and diagnostics; agents should inspect `ok` rather than relying only on transport exit status.

## Custom and external operations

You can extend the catalog without editing `com.opweave.workflow`:

- **Project custom operations** live in Editor-only assemblies in the using project.
- **External operation extensions** are independent UPM packages that carry operations, type providers, codecs, and examples.

An operation implementation normally derives from `OperationBase`, declares its identity with `[OpWeaveOperation(id, version)]`, and builds a matching descriptor that declares ports, effects, completion behavior, undo support, idempotency, examples, and localization. Eligible operations are discovered after compilation/domain reload and are searchable through the same catalog as the built-in operations. Templates are included in the package under `Editor/Templates~`.

## Package structure

| Path | Contents |
| --- | --- |
| `Editor/Core` | Canonical document model, compiler, execution runtime, registry, recovery, policy |
| `Editor/Operations` | Built-in operation implementations |
| `Editor/AgentApi` | Structured agent-facing API |
| `Editor/GraphToolkit`, `Editor/Viewer` | Graph Toolkit editor integration and Run Viewer UI |
| `Editor/Pipeline`, `Editor/UnityAdapter` | Optional command transport and Unity-specific adapters |
| `Editor/Testing` | Test support (test-only assembly) |
| `Schemas~`, `Samples~`, `Editor/Templates~` | JSON schemas, sample workflows, templates (Unity-ignored folders) |
| `Tests` | Package test assemblies and vendored test-only plugin binaries |

## What changed in 0.1.1

Version 0.1.1 is a backwards-compatible patch release:

- Added **Assets > Create > OpWeave > Graph** for creating `.opweavegraph` assets directly from the Unity Project Browser.
- Improved runner active-lock handoff so a run lock is retained until its exclusion is released.
- Corrected preflight handling for pending edge-fed inputs across scene, GameObject, transform, and prefab operations.
- Improved stability and regression-test integrity around domain reloads, interruption handling, registry publication, and recovery.
- Separated Unity 6000.5-gated Graph Toolkit tests into their own test assembly. This is test-infrastructure quality work, not a new public feature.

No existing public API, operation/type ID, plan hash, journal format, or settings format intentionally changed in 0.1.1.

## Version history

| Version | Status | Notes |
| --- | --- | --- |
| 0.1.1 | Current public release | Project Browser graph creation, runner lock handoff fix, preflight pending-input fixes, stability and acceptance improvements |
| 0.1.0 | Previous public release | Initial Windows v1 public package transfer |

## License

OpWeave-authored code and documentation are licensed under the [Apache License, Version 2.0](LICENSE); see [NOTICE](NOTICE) and [LICENSING.md](LICENSING.md). Third-party components keep their own licenses — see [Third Party Notices](Packages/com.opweave.workflow/Third%20Party%20Notices.md), the test-plugin inventory `Packages/com.opweave.workflow/Tests/Plugins/THIRD_PARTY_NOTICES.txt`, and the license texts under `Tests/Plugins/Licenses/`.

## Known limitations

- **Windows-only.** macOS/Linux support is deferred past v1; running elsewhere is unsupported.
- **Editor-time only.** Runtime/players and in-game visual scripting are out of scope.
- **Sequential v1 execution.** Branching, loops, parallel execution, nested workflows, and reusable blueprint execution are outside the v1 execution contract.
- **Human approval boundary.** Operations that require approval stop for a person; an agent cannot approve its own approval-requiring work.
- Initial scope targets Unity Editor editing operations; unrestricted execution of arbitrary C# is outside the v1 contract.

## Provenance

Version `0.1.1` corresponds to the accepted Windows v1 development revision recorded in [VERSION](VERSION) and [release-lock.json](release-lock.json) (source: private `pippirium/OpWeave-dev`; G-09 acceptance evidence referenced there). `0.1.1` identifies one immutable product payload; corrections ship under a later version, never a retargeted tag.
