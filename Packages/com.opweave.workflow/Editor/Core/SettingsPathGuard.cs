#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The single SPEC §H4 item 5 test for "this path is the OpWeave settings
    /// file": the D6 allowed-paths check, the runtime <c>path_writable</c>
    /// requirement and the Operations write-path policy all ask this class, so
    /// a spelling one layer recognizes cannot be missed by another. The adapter
    /// and the standard Operations already reach Core internals through
    /// <c>InternalsVisibleTo</c>, so no assembly gains a public member and no
    /// assembly reference is added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a canonical form.</b> Each layer compares an already-normalized
    /// <c>/</c>-separated path case-insensitively. That catches a differently
    /// cased or backslash spelling of the settings file, but not the Windows
    /// spellings that open the same file: a trailing '.' or space on any
    /// segment (<c>OpWeaveSettings.json.</c>, <c>OpWeaveSettings.json </c>,
    /// <c>ProjectSettings./</c> — Windows strips both characters from the end
    /// of every segment) and the NTFS stream spelling
    /// <c>OpWeaveSettings.json::$DATA</c> (everything from the first ':' on
    /// names a stream of that file, not a different file). The canonical form
    /// of a segment is therefore its text up to the first ':' with every
    /// trailing '.' and ' ' removed, and the comparison is made on that form.
    /// A spelling that already matched literally canonicalizes to itself, so
    /// every literal that was refused before this form existed stays refused.
    /// </para>
    /// <para>
    /// <b>8.3 short names, narrowed to the guarded names.</b> Windows also
    /// opens a file through its old-style short name (<c>OPWEAV~1.JSO</c>), and
    /// the short name of a checked-out file cannot be resolved from managed
    /// code, so that spelling has no canonical form to compare against.
    /// Refusing every name that merely looks like a short name would be wrong:
    /// <c>Data~1/output.json</c> is an ordinary folder a project may own, and
    /// SPEC §D6 and §G7 admit any path inside <c>allowedPaths</c> while §H4
    /// item 5 excludes only the settings file, so the guard may not invent a
    /// refusal for it. The question it asks instead is narrower — could this
    /// segment be the alias <i>of the name it guards</i>? Windows derives an
    /// alias from the long name in one of two shapes. The classic one keeps a
    /// leading part of the long name and appends <c>~&lt;digits&gt;</c> while
    /// the whole stays within eight characters (<c>PROJEC~1</c>,
    /// <c>PROJE~10</c>, <c>PROJ~100</c>); once a directory has too many
    /// colliding names it uses a hashed one — the first two characters of the
    /// long name, four hex digits, then <c>~&lt;digits&gt;</c>
    /// (<c>PR1A2B~1</c>). The alias of a file carries a leading part of the
    /// long extension too (<c>OPWEA~10.JSO</c>). Every spelling of either
    /// shape of a guarded name is refused — fail closed — and one that is not
    /// of either shape is the neighbour it reads as, keeping its normal verdict.
    /// </para>
    /// <para>
    /// <b>Both guarded segments carry an alias.</b> Inside
    /// <c>ProjectSettings/</c> the remaining segments are tested against the
    /// aliases of <c>OpWeaveSettings.json</c>. The folder carries one as well,
    /// so <c>PROJEC~1/OpWeaveSettings.json</c> and
    /// <c>PROJEC~1/OPWEAV~1.JSO</c> open the settings file while neither
    /// segment reads as <c>ProjectSettings</c>; a rule that only inspected what
    /// follows a literal <c>ProjectSettings</c> segment would let both through
    /// the moment <c>allowedPaths</c> is widened to the project root
    /// (<c>"."</c> or <c>""</c>). Nothing under such a first segment can be
    /// shown to be a neighbour — under a folder that may be the settings
    /// folder, any file name may be the settings file's alias — so a path with
    /// a second segment is refused whole, while a bare short name with nothing
    /// under it names a top-level folder alone and stays unrestricted.
    /// </para>
    /// </remarks>
    internal static class SettingsPathGuard
    {
        // The §0.2 settings path — the file OpWeaveSettingsLoader.DefaultFilePath
        // reads and the Project Settings screen saves — written segment by
        // segment, because the canonical comparison below is made per segment.
        // These two literals are the only written form of it here.
        private const string SettingsFolder = "ProjectSettings";

        private const string SettingsFileName = "OpWeaveSettings.json";

        // The same two names in the form an 8.3 alias is derived from: uppercase,
        // because Windows spells a short name in capitals, and for the file with
        // the name part and the extension kept apart. The comparisons below stay
        // case-insensitive, so a lower-case alias spelling is caught too.
        private const string SettingsFolderStem = "PROJECTSETTINGS";

        private const string SettingsFileStem = "OPWEAVESETTINGS";

        private const string SettingsFileExtension = "JSON";

        private static readonly string[] s_settingsPathSegments =
            { SettingsFolder, SettingsFileName };

        /// <summary>
        /// Whether <paramref name="path"/> — as the calling layer normalized it —
        /// reaches the OpWeave settings file: its canonical form is the §0.2
        /// settings path, or it lies under a first segment that could be that
        /// folder's 8.3 alias, or inside <c>ProjectSettings/</c> under a segment
        /// that could be that file's alias. Null, empty and every other path are
        /// not protected; a neighbouring file of either folder never is, nor is
        /// a short-name-shaped name that cannot be an alias of either guarded
        /// name.
        /// </summary>
        internal static bool IsProtectedSettingsPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            // Both separators are cut here although every caller has already
            // collapsed '\' to '/': this is the last line of a permission
            // boundary, so it must not depend on that step having run.
            var segments = path!.Split('/', '\\');
            var canonical = new List<string>(segments.Length);
            for (var i = 0; i < segments.Length; i++)
            {
                var text = CanonicalSegment(segments[i]);
                // The '.' and empty segments of the layers' normalization carry
                // no name, so the guard collapses them too. A '..' segment is
                // collapsed with them rather than kept: CanonicalSegment strips
                // the trailing '.' characters Windows would strip, so nothing of
                // '..' survives to compare against. Every caller has already
                // refused such a path, and collapsing it only ever refuses more
                // — '../ProjectSettings/OpWeaveSettings.json' now reads as the
                // settings path instead of escaping the comparison.
                if (text.Length == 0 || text == ".")
                {
                    continue;
                }
                canonical.Add(text);
            }
            if (IsSettingsPath(canonical))
            {
                return true;
            }
            if (canonical.Count < 2)
            {
                return false;
            }
            // The folder can be spelled by its alias as well: Windows resolves
            // PROJEC~1 to ProjectSettings, so under a widened allowedPaths
            // 'PROJEC~1/<…>' reaches the settings file. Which long name an
            // alias resolves to is not decidable from managed code, so nothing
            // under such a segment counts as a neighbour and the whole path
            // fails closed.
            if (CouldBeFolderAlias(canonical[0]))
            {
                return true;
            }
            if (!string.Equals(canonical[0], SettingsFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            for (var i = 1; i < canonical.Count; i++)
            {
                if (CouldBeFileAlias(canonical[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // The Windows-canonical spelling of one path segment: the text before
        // the first ':' — an NTFS stream name is not part of the file name —
        // with the trailing '.' and ' ' characters Windows strips from a
        // segment's end removed.
        private static string CanonicalSegment(string segment)
        {
            var colon = segment.IndexOf(':');
            var end = colon >= 0 ? colon : segment.Length;
            while (end > 0 && (segment[end - 1] == '.' || segment[end - 1] == ' '))
            {
                end--;
            }
            return end == segment.Length ? segment : segment.Substring(0, end);
        }

        // Whether the canonical segments are exactly the settings path — the
        // comparison stays case-insensitive because on a case-insensitive file
        // system a differently cased spelling names the same file.
        private static bool IsSettingsPath(List<string> canonical)
        {
            if (canonical.Count != s_settingsPathSegments.Length)
            {
                return false;
            }
            for (var i = 0; i < canonical.Count; i++)
            {
                if (!string.Equals(
                        canonical[i], s_settingsPathSegments[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        // Whether a canonical segment could be the folder's 8.3 alias. A folder
        // alias carries no extension, so the name part is the whole segment.
        private static bool CouldBeFolderAlias(string segment) =>
            CouldBeAliasOf(segment, SettingsFolderStem, null);

        // Whether a canonical segment could be the settings file's 8.3 alias,
        // which carries a leading part of the long extension with it.
        private static bool CouldBeFileAlias(string segment) =>
            CouldBeAliasOf(segment, SettingsFileStem, SettingsFileExtension);

        // The two shapes Windows derives from a long name, tested on an already
        // canonical segment: the classic '<leading part of the stem>~<digits>',
        // which must still fit the eight characters of a name part, and the
        // hashed '<stem's first two characters><4 hex digits>~<digits>' it falls
        // back to once a directory holds too many colliding names. A '~' with no
        // digit after it is just a character in a long name, so it is not an
        // alias; a name part that is not a leading part of the guarded stem (and
        // is not the hashed spelling of it) names another file, so it is not one
        // either.
        private static bool CouldBeAliasOf(string segment, string stem, string? extension)
        {
            var tilde = segment.IndexOf('~');
            if (tilde <= 0)
            {
                return false;
            }
            var tail = tilde + 1;
            while (tail < segment.Length && char.IsDigit(segment[tail]))
            {
                tail++;
            }
            var digits = tail - tilde - 1;
            if (digits == 0)
            {
                return false;
            }
            var name = segment.Substring(0, tilde);
            if (!MatchesExtension(segment.Substring(tail), extension))
            {
                return false;
            }
            if (name.Length + 1 + digits <= 8 &&
                stem.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return name.Length == 6 &&
                string.Compare(name, 0, stem, 0, 2, StringComparison.OrdinalIgnoreCase) == 0 &&
                IsHexDigit(name[2]) && IsHexDigit(name[3]) &&
                IsHexDigit(name[4]) && IsHexDigit(name[5]);
        }

        // The extension part of an alias: none when the guarded name has none,
        // otherwise a leading part of its extension — Windows keeps the first
        // three characters of a longer one. A shorter tail than the guarded
        // name's is refused with the rest: it names no file this guard owns, so
        // reading it as an alias can only ever refuse more.
        private static bool MatchesExtension(string rest, string? extension)
        {
            if (extension is null)
            {
                return rest.Length == 0;
            }
            if (rest.Length < 2 || rest[0] != '.')
            {
                return false;
            }
            var tail = rest.Substring(1);
            return extension.StartsWith(tail, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHexDigit(char value) =>
            (value >= '0' && value <= '9') ||
            (value >= 'a' && value <= 'f') ||
            (value >= 'A' && value <= 'F');
    }
}
