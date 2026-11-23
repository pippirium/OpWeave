#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The closed SPEC §C4 side-effect vocabulary. Custom Operations use the same
    /// names and cannot invent their own; an Operation that declares an effect
    /// outside this set is rejected at registration with OW-OP-003.
    /// </summary>
    public static class EffectNames
    {
        public const string Read = "read";
        public const string SceneLoad = "scene.load";
        public const string SceneModify = "scene.modify";
        public const string SceneSave = "scene.save";
        public const string AssetCreate = "asset.create";
        public const string AssetModify = "asset.modify";
        public const string AssetMove = "asset.move";
        public const string AssetDelete = "asset.delete";
        public const string CodeTemplate = "code.template";
        public const string CodeWrite = "code.write";
        public const string Compile = "compile";
        public const string PackageModify = "package.modify";

        /// <summary>Every §C4 effect name.</summary>
        public static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[]
        {
            Read, SceneLoad, SceneModify, SceneSave,
            AssetCreate, AssetModify, AssetMove, AssetDelete,
            CodeTemplate, CodeWrite, Compile, PackageModify,
        });

        /// <summary>True when <paramref name="effect"/> belongs to the §C4 vocabulary.</summary>
        public static bool IsKnown(string effect)
        {
            for (var i = 0; i < All.Count; i++)
            {
                if (All[i] == effect)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
