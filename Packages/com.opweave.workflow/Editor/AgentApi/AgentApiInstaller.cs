#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The production installation of the Agent API service (SPEC §H1): once
    /// per Editor domain it builds the observation dependencies over the
    /// installed <see cref="OpWeaveRunnerHost"/> — the SPEC §H2 item 3
    /// thread-safe snapshot seeded from the Run Store and kept current by the
    /// host's §E8 events on the main thread, the §F1 Run Store root the §F6
    /// log reader opens and the §F7 journaled-change read — then installs the
    /// constructed <see cref="AgentApiService"/> on
    /// <see cref="AgentApiHost"/> so <see cref="AgentApiHost.Api"/> answers the
    /// observation commands in a real Editor domain.
    /// </summary>
    /// <remarks>
    /// The install runs on the first <see cref="EditorApplication.delayCall"/>:
    /// [InitializeOnLoad] order between OpWeave assemblies is unspecified, so
    /// the runner host's own domain-load install may not have landed yet when
    /// this static constructor runs — by the first delayCall every
    /// [InitializeOnLoad] constructor has completed. Asset import worker
    /// processes (SPEC §G1 item 3) skip it entirely, and a domain whose
    /// runner host failed to build simply keeps no service:
    /// <see cref="AgentApiHost.Api"/> stays uninstalled — the same fail-closed
    /// posture the acceptance gate chain already takes.
    /// </remarks>
    [InitializeOnLoad]
    internal static class AgentApiInstaller
    {
        private static RunStatusTracker? s_tracker;

        static AgentApiInstaller()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                return;
            }
            EditorApplication.delayCall += InstallOnce;
            AssemblyReloadEvents.beforeAssemblyReload += Reset;
        }

        // The first-delayCall install: every [InitializeOnLoad] constructor
        // has completed, so the runner host's production install already ran —
        // or failed, in which case Current is null and nothing is installed.
        private static void InstallOnce()
        {
            var host = OpWeaveRunnerHost.Current;
            if (host is null)
            {
                return;
            }
            try
            {
                Install(host);
            }
            catch (Exception exception)
            {
                // A failed Agent API install must not take the Editor domain
                // down with it — the commands report the service as absent.
                Debug.LogException(exception);
            }
        }

        /// <summary>
        /// Constructs the observation dependencies over
        /// <paramref name="host"/>'s own seams and installs the service — the
        /// one call the domain entry point and the wiring tests share. A
        /// re-install retires the previous tracker before the new one is
        /// remembered, so the §E8 subscriptions never multiply.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is null.</exception>
        internal static AgentApiService Install(OpWeaveRunnerHost host)
        {
            if (host is null)
            {
                throw new ArgumentNullException(nameof(host));
            }
            var tracker = new RunStatusTracker(
                host.Events,
                host.Store.Load,
                () => host.Store.List(new RunQuery()));
            var root = host.RunStoreRoot;
            var api = new AgentApiService(
                host.Store,
                AgentApiCursorCodec.Random(),
                host.Coordinator.Cancel,
                runStatus: tracker,
                runStoreRoot: root,
                debugLogging: DebugLoggingEnabled,
                readChanges: runId => ReadJournaledChanges(root, runId),
                // The SG-16 tombstone verdict is the Run Store's own —
                // a binding to the Core lookup, never a second tombstone
                // store on this side (SPEC §0.6 SG-16).
                isDeletedRun: host.Store.IsDeletedRun,
                workflows: new UnityAgentWorkflowBackend(
                    ProjectRoot(),
                    host.CurrentRunPolicyEnvironment,
                    host.CurrentRegistry,
                    TypeProviders,
                    host.AcceptRun,
                    // The durable previous-definition records behind the
                    // D3 OW-OP-001 "previously defined in <file>" hint —
                    // the same diff recorder get_registry_status's Latest
                    // reads, so a deleted custom Operation keeps its
                    // source hint for the Agent-facing validate (SPEC §C3
                    // rule 7).
                    previousDefinitions: () =>
                        RegistryDiffRecorder.Shared.PreviousDefinitions),
                catalog: new UnityAgentCatalogBackend(
                    ProjectRoot(),
                    WorkflowSchemaPath(),
                    () => RegistryPublication.Shared.Snapshot,
                    () => RegistryPublication.Shared.Stale,
                    () => RegistryDiffRecorder.Shared.Latest,
                    RegistryLoadDiagnostics,
                    AgentLanguage));
            AgentApiHost.Install(api);
            s_tracker?.Dispose();
            s_tracker = tracker;
            return api;
        }

        /// <summary>
        /// Drops the installed service and unsubscribes its tracker — the
        /// domain-unload pair of <see cref="Install"/> and the reset the
        /// wiring tests perform in teardown.
        /// </summary>
        internal static void Reset()
        {
            s_tracker?.Dispose();
            s_tracker = null;
            AgentApiHost.Reset();
        }

        // The §F7 read-only journal read behind get_run_logs' change list:
        // the full durable union or an OW-STORE-002 failure — a partial list
        // is never served as the complete one.
        private static Result<IReadOnlyList<ChangeRecord>> ReadJournaledChanges(
            string root, string runId)
        {
            var records = JournaledChanges.Read(
                JournaledChanges.PathOf(root, runId), out var failure);
            return failure is null
                ? Result<IReadOnlyList<ChangeRecord>>.Success(records)
                : Result<IReadOnlyList<ChangeRecord>>.Failure(
                    ErrorCodes.Create(ErrorCodes.OW_STORE_002, failure));
        }

        // The SPEC §F6 SG-08 read-time gate, read lazily on each request like
        // the host's own write-path gate: a failed or invalid settings load
        // answers the SPEC default false.
        private static bool DebugLoggingEnabled() =>
            OpWeaveSettingsLoader.Load() is { Ok: true, Value: { } settings } &&
            settings.DebugLogging;

        // The project root the workflow 'path' arguments anchor to — the
        // same Assets/.. resolution the production host's Run Store root
        // uses.
        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // The TypeProvider set of the last stage-4 build — the weak
        // fingerprint bindings the §D7 planHash material needs
        // (RegistryBuildHost.Latest is null only before the first publish,
        // where the empty standard Registry has no compiled Plan to hash
        // against; the standard pair is the same fallback the host's own
        // EmptyRegistry uses).
        private static IReadOnlyList<TypeProviderCandidate> TypeProviders() =>
            RegistryBuildHost.Latest?.Providers ?? StandardTypeProviders();

        private static IReadOnlyList<TypeProviderCandidate> StandardTypeProviders() =>
            new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(),
                    OperationOrigin.Standard),
                new TypeProviderCandidate(
                    UnityTypeProvider.ProviderId, new UnityTypeProvider(),
                    OperationOrigin.Standard),
            };

        // The shipped §A2 artifact's resolved path: the package manager
        // knows the package's installed location whether the project
        // embeds it or caches it — the project-local path is the
        // development-layout fallback when the package cannot be found.
        private static string WorkflowSchemaPath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(AgentApiService).Assembly);
            var packageRoot = info?.resolvedPath;
            return Path.Combine(
                string.IsNullOrEmpty(packageRoot)
                    ? Path.Combine(ProjectRoot(), "Packages", "com.opweave.workflow")
                    : packageRoot!,
                "Schemas~", "opweave-1.0.schema.json");
        }

        // The §H4.1 load diagnostics get_registry_status reports: the
        // ones the latest stage-4 build observed, or a fresh load's before
        // the first build ran — the same reading the Runner's own gate
        // sees, so the status answer never invents a different verdict.
        private static IReadOnlyList<Diagnostic> RegistryLoadDiagnostics()
        {
            var built = RegistryBuildHost.Latest;
            if (built is not null)
            {
                return built.SettingsDiagnostics;
            }
            return OpWeaveSettingsLoader.Load().Diagnostics;
        }

        // The §C6 item-5 default language: the live agentLanguage
        // setting — "en" on any load failure, the fail-closed reading the
        // spec's §H4.1 default implies (no Agent-facing localization).
        private static string AgentLanguage()
        {
            var loaded = OpWeaveSettingsLoader.Load();
            return loaded.Ok ? loaded.Value!.AgentLanguage : "en";
        }
    }
}
