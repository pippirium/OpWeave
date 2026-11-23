#nullable enable

using System.Runtime.CompilerServices;

// The EditMode test assembly exercises the standard Asset Operations through
// the internal seams they keep for it — the SPEC §G7 item 1 path policy and the
// two asset-database reads behind unity.asset.find's result cap — so the public
// surface of this assembly stays the Operation classes the Registry discovers.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]
