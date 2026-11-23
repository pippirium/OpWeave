#nullable enable

using System.Runtime.CompilerServices;

// Core internals (Canonical JSON number machinery, SG-02 low-level diagnostics) are
// exercised directly by the shared Core test assembly; the public surface stays the
// SPEC-defined one.
[assembly: InternalsVisibleTo("OpWeave.Core.Tests")]

// The UnityAdapter runner host (SPEC §E3, T-W2.1-04) instantiates the internal
// RunCoordinator/RunLoop and implements the internal IRequirementEvaluator seam;
// no public API is added for it.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter")]

// The standard Operations assembly is first-party: its Operations (the Prefab
// open_contents of SPEC §G6) register Run resources through the internal
// IRunResourceRegistry seam of SPEC §E6 item 4; external extensions keep public
// contracts only.
[assembly: InternalsVisibleTo("OpWeave.Operations")]

// The EditMode test assembly builds Core-internal fixtures (CompiledPlan, the
// planHash input parts) to exercise that host; the public surface stays the
// SPEC-defined one.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]

// SPEC §H1–§H4: the Agent API's production installer binds the runner host's
// §E8 dispatcher (RunEvents) and the §E6 cancel entry point
// (RunCoordinator.Cancel) — the same internal seams the runner host and the
// Run Viewer already bind, never a second journal writer.
[assembly: InternalsVisibleTo("OpWeave.AgentApi")]

// SPEC §H5: the Run Viewer reads the coordinator's state/query surface and
// subscribes to its §E8 dispatcher through the internal RunEvents type — the
// same internals the runner host already sees, never a second journal writer.
[assembly: InternalsVisibleTo("OpWeave.Viewer")]

// SPEC §H7 item 3: the Graph Toolkit JSON sync drives the internal
// JsonSaveCoordinator — the single cross-process write path for
// OpWeave-controlled .opweave.json files — and its intent records; no public
// API is added for it.
// SPEC §H6.5: the graph's Registry-change refresh subscribes to the §E8
// dispatcher through the internal RunEvents type — the same seam the
// Viewer binds, never a second journal writer.
[assembly: InternalsVisibleTo("OpWeave.GraphToolkit")]

// The OpWeave.LockSmoke second-process helper exercises JsonSaveCoordinator
// across a real OS process boundary exactly like the CoordinatorRunLock
// smokes — the product path, never a reimplementation.
[assembly: InternalsVisibleTo("OpWeave.LockSmoke")]

// The SPEC §M.6 test harness reads the same internal seams the run loop reads —
// StepContext.Outputs after Execute, and the §F6 credential mask — so the
// outputs, changes and journal it reports are the production ones rather than a
// second implementation. OpWeave.Testing is compiled only inside
// UNITY_INCLUDE_TESTS and no production assembly may reference it (SPEC §0.5),
// so the exposure cannot reach shipped or external code.
[assembly: InternalsVisibleTo("OpWeave.Testing")]
