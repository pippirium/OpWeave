#nullable enable

using System;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The domain holder of the installed <see cref="AgentApiService"/> — the
    /// single <c>Api</c> the Pipeline adapter calls into (SPEC §H2). Production
    /// installation lands with the command tickets (the wiring needs the Runner
    /// host and Registry seams those tickets bring); <see cref="Install"/> is
    /// internal so only that wiring and tests write it.
    /// </summary>
    public static class AgentApiHost
    {
        private static AgentApiService? s_api;

        /// <summary>The installed service.</summary>
        /// <exception cref="InvalidOperationException">
        /// No service has been installed in this Editor domain yet.
        /// </exception>
        public static AgentApiService Api =>
            s_api ?? throw new InvalidOperationException(
                "The AgentApi service is not installed in this Editor domain.");

        internal static void Install(AgentApiService api)
        {
            s_api = api ?? throw new ArgumentNullException(nameof(api));
        }

        /// <summary>Drops the installed service (domain teardown and tests).</summary>
        internal static void Reset() => s_api = null;
    }
}
