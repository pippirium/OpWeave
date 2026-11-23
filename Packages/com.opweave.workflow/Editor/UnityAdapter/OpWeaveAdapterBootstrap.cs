#nullable enable

using System;
using OpWeave.Core.Compiler;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The OpWeave Editor entry point guarded by SPEC §G1 item 3: asset
    /// import worker processes (<see cref="AssetDatabase.IsAssetImportWorkerProcess"/>)
    /// run the same managed domain-load hooks as the main Editor, so OpWeave
    /// initialization is skipped there entirely. The <c>[InitializeOnLoad]</c>
    /// static constructor runs <see cref="Initialize"/>
    /// once per domain; today it installs <see cref="MainThreadDispatcher"/>,
    /// the SPEC §D8 Dry Run environment the public
    /// <c>WorkflowCompiler</c> binds, and the runner host of SPEC §E3.
    /// </summary>
    [InitializeOnLoad]
    public static class OpWeaveAdapterBootstrap
    {
        static OpWeaveAdapterBootstrap()
        {
            Initialize(AssetDatabase.IsAssetImportWorkerProcess, InitializeServices);
        }

        /// <summary>
        /// Runs <paramref name="initializeServices"/> unless
        /// <paramref name="isAssetImportWorkerProcess"/> reports an asset
        /// import worker process (SPEC §G1 item 3). The worker-process
        /// predicate and the service initialization are injected so the
        /// guard is exercisable from tests.
        /// </summary>
        /// <returns>
        /// <c>false</c> when initialization was skipped because the predicate
        /// reported a worker process — no OpWeave service is initialized then.
        /// </returns>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public static bool Initialize(Func<bool> isAssetImportWorkerProcess, Action initializeServices)
        {
            if (isAssetImportWorkerProcess is null)
            {
                throw new ArgumentNullException(nameof(isAssetImportWorkerProcess));
            }
            if (initializeServices is null)
            {
                throw new ArgumentNullException(nameof(initializeServices));
            }
            if (isAssetImportWorkerProcess())
            {
                return false;
            }
            initializeServices();
            return true;
        }

        private static void InitializeServices()
        {
            // The SPEC §G4 domain counter first: every reload counts one
            // domain load, and the counter is what a resumed wait compares
            // its token snapshot against.
            DomainReloadCounter.CountDomain();
            MainThreadDispatcher.Install();
            // The §D8 Dry Run environment the public snapshot-backed
            // WorkflowCompiler binds: the UnityStepContext factory a Run's
            // Preflight sees, the live Editor-state probe and the
            // dispatcher's main-thread answer — the compiler keeps failing
            // closed where this domain registration never ran.
            DryRunEnvironment.Install(
                static types => new DryRunStepContextFactory(types).Create,
                new EditorStateProbe(),
                static () => MainThreadDispatcher.IsMainThread);
            try
            {
                OpWeaveRunnerHost.InstallProduction();
            }
            catch (System.Exception exception)
            {
                // A failed runner-host construction must not kill the Editor
                // domain; the gate chain still fails closed (no Runs are
                // accepted while the host is absent).
                UnityEngine.Debug.LogException(exception);
            }
        }
    }
}
