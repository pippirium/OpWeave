#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The <c>AssetImported(paths)</c> row of SPEC §G4: the wait a
    /// <c>unity.asset.import</c> step (or a custom Operation importing an
    /// asset) performs — <see cref="WaitStatusKind.Done"/> once the Editor is
    /// not updating and every path loads through
    /// <c>AssetDatabase.LoadMainAssetAtPath</c>,
    /// <see cref="WaitStatusKind.Failed"/> with OW-UNITY-008 when three
    /// consecutive checks while not updating still find a path unimported,
    /// carrying the step's captured import errors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The resume token is the whole wait state</b> (SPEC §G4 "条件は
    /// resumeToken だけから復元できる"). It is a JSON object with three
    /// members — <c>paths</c> (the asset paths being waited on),
    /// <c>nulls</c> (the consecutive failed checks so far) and
    /// <c>errors</c> (the import errors this step already captured). A
    /// condition created in a fresh domain and handed the token continues
    /// exactly where the lost one stopped: even the consecutive-null streak
    /// survives the reload, so "null が 3 回続く" counts checks, not
    /// per-domain calls.
    /// </para>
    /// <para>
    /// <b>While the Editor is updating, nothing is judged.</b> A null load
    /// during <c>isUpdating</c> is the import still running, not a failure —
    /// the streak is only counted on checks made while not updating, and the
    /// check before an update does not break a streak that resumes after it.
    /// </para>
    /// <para>
    /// <b>Import errors.</b> Unity reports an import failure only as a
    /// Console error, so <see cref="ImportErrorCapture"/> keeps the
    /// error-kind log entries the step produced — its
    /// <see cref="ImportErrorCapture.BeginStep"/> boundary keeps an error an
    /// earlier import of the same path logged from being attributed to this
    /// step — and the entries naming a waited path are folded into the
    /// token's <c>errors</c> at <see cref="StepResult.Waiting"/> time and on
    /// each check. The capture persists every entry to SessionState as it
    /// is logged, so an error captured between checks still reaches the
    /// fold even when a domain reload came first. On the failure verdict
    /// they are attached to
    /// the OW-UNITY-008 diagnostic (SPEC §G4 "ステップ中のインポート
    /// エラーを添付"). A token the step itself never wrote — no
    /// <c>paths</c> array, a non-string entry — is malformed input, which is
    /// a caller bug, so <see cref="Check"/> refuses it with
    /// <see cref="ArgumentException"/> rather than misjudging it.
    /// </para>
    /// </remarks>
    public sealed class AssetImportedCondition : IWaitCondition
    {
        /// <summary>How many consecutive not-updating checks may return null before the wait fails (SPEC §G4).</summary>
        internal const int MaxConsecutiveNulls = 3;

        /// <summary>The bound on the error lines one token accumulates.</summary>
        internal const int MaxCollectedErrors = 8;

        private const string PathsField = "paths";
        private const string NullsField = "nulls";
        private const string ErrorsField = "errors";

        private readonly Func<bool> _isUpdating;
        private readonly Func<string, UnityEngine.Object?> _loadMainAsset;
        private readonly Func<IReadOnlyList<string>> _recentImportErrors;

        /// <summary>Creates the condition over the live Editor state.</summary>
        public AssetImportedCondition()
            : this(
                static () => EditorApplication.isUpdating,
                static path => AssetDatabase.LoadMainAssetAtPath(path),
                static () => ImportErrorCapture.RecentErrors)
        {
        }

        /// <summary>
        /// The test seam over the three live reads the check performs — the
        /// <see cref="EditorStateProbe"/> pattern — so the consecutive-null
        /// streak and the error attachment are provable without driving a
        /// real failing import three times.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any reader is null.</exception>
        internal AssetImportedCondition(
            Func<bool> isUpdating,
            Func<string, UnityEngine.Object?> loadMainAsset,
            Func<IReadOnlyList<string>> recentImportErrors)
        {
            _isUpdating = isUpdating ?? throw new ArgumentNullException(nameof(isUpdating));
            _loadMainAsset =
                loadMainAsset ?? throw new ArgumentNullException(nameof(loadMainAsset));
            _recentImportErrors = recentImportErrors ??
                throw new ArgumentNullException(nameof(recentImportErrors));
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="token"/> is not an AssetImported token — no
        /// <c>paths</c> array of at least one string.
        /// </exception>
        public WaitStatus Check(JsonValue token)
        {
            if (token is null)
            {
                throw new ArgumentNullException(nameof(token));
            }
            var state = TokenState.Read(token);
            // A wait continued from its persisted token still owns the §G4
            // capture window: the BeginStep that armed it ran in the domain
            // (or Editor session) the reload/restart dropped, so the open
            // window is re-armed here — an import error the resumed wait
            // still logs keeps the same immediate journal flush.
            ImportErrorCapture.ResumeWait();
            var errors = CollectErrors(state);

            // While the Editor is updating a null load is the import still
            // running: the streak is judged only on not-updating checks, so
            // the token's count is handed back unchanged.
            if (_isUpdating())
            {
                return WaitStatus.Pending(
                    WriteToken(state.Paths, state.Nulls, errors));
            }

            var allLoaded = true;
            for (var i = 0; i < state.Paths.Count; i++)
            {
                if (_loadMainAsset(state.Paths[i]) is null)
                {
                    allLoaded = false;
                    break;
                }
            }
            if (allLoaded)
            {
                return WaitStatus.Done();
            }

            var nulls = state.Nulls + 1;
            if (nulls >= MaxConsecutiveNulls)
            {
                return WaitStatus.Failed(ImportFailed(errors));
            }
            return WaitStatus.Pending(WriteToken(state.Paths, nulls, errors));
        }

        /// <summary>
        /// The token a Waitable Operation stores in its first
        /// <see cref="StepResult.Waiting"/>: the paths the import waits on, a
        /// fresh streak and no captured errors yet.
        /// </summary>
        internal static JsonValue InitialToken(IReadOnlyList<string> paths) =>
            WriteToken(paths, 0, Array.Empty<string>());

        /// <summary>
        /// The initial token with the import errors the step already logged:
        /// the queued import can report its failure before the first
        /// <see cref="Check"/> runs and only the token survives a domain
        /// reload, so <paramref name="recentImportErrors"/> — the capture's
        /// entries since the step began — are folded in right away.
        /// </summary>
        internal static JsonValue InitialToken(
            IReadOnlyList<string> paths, IReadOnlyList<string> recentImportErrors) =>
            WriteToken(
                paths, 0,
                FoldErrors(paths, new List<string>(), recentImportErrors));

        /// <summary>The <c>paths</c> a token waits on, in token order.</summary>
        /// <exception cref="ArgumentException"><paramref name="token"/> is not an AssetImported token.</exception>
        internal static IReadOnlyList<string> PathsOf(JsonValue token) =>
            TokenState.Read(token).Paths;

        // The drain of the step's import errors into the token: the capture's
        // recent entries that name a waited path and are not already there,
        // up to the bound. Errors never leave the token once folded in — the
        // merge is monotone, which is what keeps them across a reload.
        private List<string> CollectErrors(TokenState state) =>
            FoldErrors(
                state.Paths, new List<string>(state.Errors),
                _recentImportErrors());

        // The monotone fold shared by Check's drain and the initial token:
        // recent entries that name a waited path and are not already
        // captured join the list, up to the bound.
        private static List<string> FoldErrors(
            IReadOnlyList<string> paths,
            List<string> errors,
            IReadOnlyList<string> recent)
        {
            for (var i = 0; i < recent.Count && errors.Count < MaxCollectedErrors; i++)
            {
                var message = recent[i];
                if (!MentionsPath(message, paths) ||
                    errors.Contains(message))
                {
                    continue;
                }
                errors.Add(message);
            }
            return errors;
        }

        // Attribution by path mention: an import error names the asset path it
        // failed on, which is the only identity a Console line carries.
        private static bool MentionsPath(string message, IReadOnlyList<string> paths)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (message.IndexOf(paths[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static JsonValue WriteToken(
            IReadOnlyList<string> paths, int nulls, IReadOnlyList<string> errors) =>
            JsonValue.From(new JObject
            {
                [PathsField] = new JArray(paths),
                [NullsField] = nulls,
                [ErrorsField] = new JArray(errors),
            });

        // The OW-UNITY-008 verdict with the step's import errors attached
        // (SPEC §G4). The message stays the fixed English sentence; the
        // captured error lines go to the hint, which is the Diagnostic member
        // meant for remediation detail.
        private static Diagnostic ImportFailed(IReadOnlyList<string> errors) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The asset import did not produce a loadable asset.",
                hint: errors.Count == 0
                    ? "Check the Editor log for the import error of the asset."
                    : "Import errors: " + string.Join(" | ", errors));

        private sealed class TokenState
        {
            internal IReadOnlyList<string> Paths = Array.Empty<string>();
            internal int Nulls;
            internal List<string> Errors = new List<string>(0);

            // Reads the whole wait state out of the resume token. A missing
            // streak or error list is a first-wait token — they default to
            // none; a missing or malformed paths array means the caller wrote
            // a token this condition never issued, which is refused rather
            // than guessed.
            internal static TokenState Read(JsonValue token)
            {
                if (token.ToToken() is not JObject obj ||
                    obj[PathsField] is not JArray paths || paths.Count == 0)
                {
                    throw new ArgumentException(
                        "The resume token is not an AssetImported token: " +
                        "a 'paths' array of at least one path is required.",
                        nameof(token));
                }
                var state = new TokenState();
                var list = new List<string>(paths.Count);
                for (var i = 0; i < paths.Count; i++)
                {
                    if (paths[i] is not JValue { Type: JTokenType.String } entry)
                    {
                        throw new ArgumentException(
                            "The resume token is not an AssetImported token: " +
                            "every 'paths' entry must be a string.",
                            nameof(token));
                    }
                    list.Add((string)entry.Value!);
                }
                state.Paths = list;
                if (obj[NullsField] is JValue { Type: JTokenType.Integer } nulls)
                {
                    state.Nulls = Math.Max(0, (int)(long)nulls.Value!);
                }
                if (obj[ErrorsField] is JArray stored)
                {
                    var kept = new List<string>(stored.Count);
                    for (var i = 0; i < stored.Count && kept.Count < MaxCollectedErrors; i++)
                    {
                        if (stored[i] is JValue { Type: JTokenType.String } line)
                        {
                            kept.Add((string)line.Value!);
                        }
                    }
                    state.Errors = kept;
                }
                return state;
            }
        }
    }
}
