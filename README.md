# OpWeave

[日本語](README.ja.md)

OpWeave is a Unity Editor workflow engine: it discovers typed operations, lets you compose them into workflow documents, compiles and executes them, and recovers interrupted runs from durable journals. It ships as the UPM package `com.opweave.workflow` version **0.1.0**, licensed under [Apache License 2.0](LICENSE).

## Windows v1 capabilities

- **Operation catalog** — search built-in and extension operations by type, port compatibility, and declared side effects.
- **Visual workflow authoring** — a node editor with separate control-flow and data-flow connections, backed by a canonical `.opweave.json` document.
- **Compilation and validation** — deterministic workflow compilation with typed port checking and stable plan hashing.
- **Sequential execution** — ordered step execution that waits for each operation's declared completion condition and records results and diagnostics.
- **Recovery** — durable intent/journal records so an interrupted save or run reconciles to a reviewable state instead of silently losing or duplicating work.
- **Agent API** — a structured interface for AI agents to discover operations, inspect schemas, and validate workflows.
- **Extensibility** — third-party operations, type providers, and codecs via a public extension contract.

## Requirements

- **Operating system:** Windows 11 x64
- **Verified Unity Editor:** 6000.5.11f1 (the package manifest floor is `6000.0`; the verified support target is 6000.5.11f1)
- macOS and Linux are **not supported in v1** (deferred; see Known limitations).

## Installation (Unity Package Manager)

Add the package through a Git URL, pinned to the release tag:

1. Open **Window > Package Manager**.
2. Choose **+ > Add package from git URL…**.
3. Enter:

   ```
   https://github.com/pippirium/OpWeave.git?path=Packages/com.opweave.workflow#v0.1.0
   ```

The package declares its dependencies (`com.unity.nuget.newtonsoft-json`, `com.unity.nuget.mono-cecil`) so they resolve automatically from the Unity registry.

## Using OpWeave

1. Create a workflow asset and open the OpWeave node editor.
2. Search the operation catalog and place operation nodes; connect control-flow edges (execution order) and data ports (typed values).
3. Save the graph; use **OpWeave > Save Graph JSON** to write the canonical workflow document.
4. Run the workflow from the editor integration; execution waits for each operation's declared completion and reports per-step results and diagnostics.
5. If a run is interrupted, recovery reconciles the journal into a reviewable state — uncertain states surface as needing review, never silent success.

## Package structure

| Path | Contents |
| --- | --- |
| `Editor/Core` | Canonical document model, compiler, execution runtime, registry, recovery, policy |
| `Editor/Operations` | Built-in operation implementations |
| `Editor/AgentApi` | Structured agent-facing API |
| `Editor/GraphToolkit`, `Editor/Viewer` | Node editor and graph viewer UI |
| `Editor/Pipeline`, `Editor/UnityAdapter` | Unity Pipeline and Unity-specific adapters |
| `Editor/Testing` | Test support (test-only assembly) |
| `Schemas~`, `Samples~`, `Editor/Templates~` | JSON schemas, sample workflows, templates (Unity-ignored folders) |
| `Tests` | Package test assemblies and vendored test-only plugin binaries |

## License

OpWeave-authored code and documentation are licensed under the [Apache License, Version 2.0](LICENSE); see [NOTICE](NOTICE) and [LICENSING.md](LICENSING.md). Third-party components keep their own licenses — see [Third Party Notices](Packages/com.opweave.workflow/Third%20Party%20Notices.md), the test-plugin inventory `Packages/com.opweave.workflow/Tests/Plugins/THIRD_PARTY_NOTICES.txt`, and the license texts under `Tests/Plugins/Licenses/`.

## Known limitations

- **Windows-only.** macOS/Linux support is deferred past v1; running elsewhere is unsupported.
- **Editor-time only.** Runtime/players and in-game visual scripting are out of scope.
- Initial scope targets Unity Editor editing operations; unrestricted execution of arbitrary C# is outside the v1 contract.

## Provenance

Version `0.1.0` corresponds to the accepted Windows v1 development baseline recorded in [VERSION](VERSION) and [release-lock.json](release-lock.json) (source: private `pippirium/OpWeave-dev`; G-09 acceptance evidence referenced there). `0.1.0` identifies one immutable product payload; corrections ship under a later version, never a retargeted tag.
