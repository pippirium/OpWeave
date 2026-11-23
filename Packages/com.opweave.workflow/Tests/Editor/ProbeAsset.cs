#nullable enable

// T-W2.7-01 fixture: the asset the Asset Operation tests save and re-read.
// Unity resolves an asset's script by a file of the same name as its type, so
// the type owns this file: as a nested class of a test fixture the Editor
// finds no script asset for it ("No script asset for ProbeAsset") and
// AssetDatabase.CreateAsset never writes the file the test then reads.
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Fixture asset whose serialized <see cref="marker"/> is the value a save
    /// must move from memory to disk.
    /// </summary>
    public sealed class ProbeAsset : ScriptableObject
    {
        public int marker;
    }
}
