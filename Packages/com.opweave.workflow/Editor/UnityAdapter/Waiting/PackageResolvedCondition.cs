#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The <c>PackageResolved(id)</c> row of SPEC §G4: the wait a
    /// <c>unity.package.add</c>/<c>unity.package.remove</c> step (or a custom
    /// Operation driving a UPM request) performs — in the request's domain
    /// the Request's own <see cref="Request.IsCompleted"/> and
    /// <see cref="Request.Status"/> judge; after a domain reload the
    /// registered package list does: the wait is
    /// <see cref="WaitStatusKind.Done"/> once the target's membership in the
    /// list matches the awaited state while the Editor is neither compiling
    /// nor updating ("リロード後は登録済みパッケージに対象があり、かつ
    /// コンパイル・更新中でない" — SPEC §T.4 L-07's registered-package-list
    /// confirmation), and <see cref="WaitStatusKind.Failed"/> with
    /// OW-UNITY-009 when the Request reports failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The resume token is the whole wait state</b> (SPEC §G4 "条件は
    /// resumeToken だけから復元できる"). It is a JSON object —
    /// <c>id</c> (the verbatim identifier the request was issued for),
    /// <c>expectPresent</c> (what resolution means: the package present for
    /// an add, absent for a remove), <c>request</c> (the slot of the tracked
    /// UPM Request, absent for a requestless wait), <c>domain</c> (the
    /// generation stamp of the domain that tracked the Request — written
    /// together with <c>request</c> so a slot number a later domain hands
    /// out again can never be mistaken for this domain's table entry) and
    /// <c>name</c> (the package name the list membership is judged by — a
    /// registry <c>name@version</c> form contributes it; a Git URL leaves
    /// it absent because only UPM's resolve learns it). A condition
    /// created in a fresh domain and handed the token continues exactly
    /// where the lost one stopped.
    /// </para>
    /// <para>
    /// <b>How the two judgements divide.</b> The Request object is a live
    /// handle that cannot survive a domain reload, so it is kept in the
    /// session's request table under the slot the token names — the
    /// table dies with the domain exactly like the Request itself. The
    /// slot alone cannot be the same-domain test, though: a fresh domain
    /// restarts the slot counter, so the token also carries the domain's
    /// generation stamp — a random id each static initialization mints
    /// and the reload discards together with the table. Only a slot
    /// stamped with this domain's id resolves to a Request (the §G4
    /// same-domain column); every other token — the post-reload case —
    /// can only be judged by the registered package list. A requestless
    /// wait (an identifier armed without a Request — a manifest edit plus
    /// <see cref="Client.Resolve"/>, say) runs the list judgement from the
    /// start.
    /// </para>
    /// <para>
    /// <b>Judgement order.</b> While the request is in flight nothing else
    /// is asked — <c>IsCompleted</c> false is Pending and
    /// compiling/updating is never consulted on the request path ("同じ
    /// ドメインなら Request の IsCompleted と Status"). A completed
    /// Success is Done, a completed Failure is the OW-UNITY-009 carrying
    /// the request's error text. The list judgement is read only on idle
    /// checks — a membership read during <c>isCompiling</c> or
    /// <c>isUpdating</c> can only misreport a resolve still settling, so
    /// those checks stay Pending. SPEC §G4 names no post-reload failure
    /// for this row, so a target the list never shows keeps the wait
    /// Pending rather than inventing one — the same rule
    /// <see cref="AssetImportedCondition"/> applies where SPEC gives the
    /// bounded form and the unbounded one elsewhere.
    /// </para>
    /// <para>
    /// <b>Identifying the target.</b> For a registry id the token's
    /// <c>name</c> matches a registered name. For a Git id the request
    /// URL, normalized through the same SG-19 parse
    /// (<see cref="PackageIdSource"/>) the allowlist applies, is matched
    /// against each registered package's dependency text — only an entry
    /// the resolve tied to the requested origin is "対象"; an unrelated
    /// package merely appearing in the list cannot be tied to the
    /// request, so the wait stays Pending for the step's deadline to
    /// bound. For a remove the awaited state is inverted: the named
    /// package absent.
    /// </para>
    /// <para>
    /// A token the step itself never wrote — no <c>id</c> string, no
    /// <c>expectPresent</c> flag, a remove wait without a <c>name</c> — is
    /// malformed input, which is a caller bug, so <see cref="Check"/>
    /// refuses it with <see cref="ArgumentException"/> rather than
    /// misjudging it.
    /// </para>
    /// <para>
    /// <b>The public lifecycle (§G4 "カスタム Operation も利用できる").</b>
    /// <see cref="InitialToken(string, bool, Request)"/> builds the
    /// request-time token <i>and</i> registers the Request it waits on —
    /// the Operation calls it right after issuing the UPM request; a
    /// terminal <see cref="Check"/> verdict releases the slot again, and
    /// <see cref="EndWait"/> covers the paths that end the wait without a
    /// terminal check, like a cancelled Run. The whole lifecycle runs
    /// through public members so a custom Operation waits exactly the way
    /// <c>unity.package.add</c> does.
    /// </para>
    /// </remarks>
    public sealed class PackageResolvedCondition : IWaitCondition
    {
        private const string IdField = "id";
        private const string ExpectPresentField = "expectPresent";
        private const string RequestField = "request";
        private const string DomainField = "domain";
        private const string NameField = "name";

        // The session's live UPM requests, keyed by the slot the token
        // carries. AppDomain-lifetime state: a domain reload drops the
        // table together with the Requests in it — SessionState keeps
        // scalars across reloads but cannot hold a live Request.
        private static readonly Dictionary<int, Request> s_requests =
            new Dictionary<int, Request>();

        private static int s_nextRequestSlot;

        // The generation stamp of this domain: minted once per static
        // initialization and discarded with the request table at the next
        // domain reload, so a slot number the fresh counter hands out
        // again is never confused with the old domain's tracked Request.
        // Deliberately a plain static, not SessionState — a value that
        // survives the reload could not tell the domains apart.
        private static readonly string s_domainId =
            Guid.NewGuid().ToString("N");

        private readonly Func<int, RequestSnapshot?> _requestSnapshot;
        private readonly Action<int> _releaseRequest;
        private readonly Func<bool> _isCompiling;
        private readonly Func<bool> _isUpdating;
        private readonly Func<IReadOnlyList<RegisteredPackage>> _registeredPackages;

        /// <summary>
        /// Creates the condition over the live Editor state — the request
        /// table, the compile/update flags and UPM's registered package
        /// list.
        /// </summary>
        public PackageResolvedCondition()
            : this(
                static slot => SnapshotRequest(slot),
                static slot => ReleaseRequest(slot),
                static () => EditorApplication.isCompiling,
                static () => EditorApplication.isUpdating,
                static () => ReadRegisteredPackages())
        {
        }

        /// <summary>
        /// The test seam over the live reads the check performs — the
        /// <see cref="EditorStateProbe"/> pattern — so the request verdicts,
        /// the idle gate and the list judgements are provable without
        /// driving a real package resolution.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal PackageResolvedCondition(
            Func<int, RequestSnapshot?> requestSnapshot,
            Action<int> releaseRequest,
            Func<bool> isCompiling,
            Func<bool> isUpdating,
            Func<IReadOnlyList<RegisteredPackage>> registeredPackages)
        {
            _requestSnapshot =
                requestSnapshot ?? throw new ArgumentNullException(nameof(requestSnapshot));
            _releaseRequest =
                releaseRequest ?? throw new ArgumentNullException(nameof(releaseRequest));
            _isCompiling =
                isCompiling ?? throw new ArgumentNullException(nameof(isCompiling));
            _isUpdating =
                isUpdating ?? throw new ArgumentNullException(nameof(isUpdating));
            _registeredPackages =
                registeredPackages ??
                throw new ArgumentNullException(nameof(registeredPackages));
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="token"/> is not a PackageResolved token — no
        /// <c>id</c> string or no <c>expectPresent</c> flag, or a remove
        /// wait whose <c>name</c> is missing.
        /// </exception>
        public WaitStatus Check(JsonValue token)
        {
            if (token is null)
            {
                throw new ArgumentNullException(nameof(token));
            }
            var state = TokenState.Read(token);

            // The same-domain judgement: a slot stamped with this
            // domain's generation id still names a Request alive in this
            // domain, so the §G4 request column applies — completion and
            // status are the verdict and the busy flags are never
            // consulted. A foreign stamp — every token written before a
            // reload, when the slot counter started over — never reaches
            // the table: the registered-package path is the only honest
            // judgement left for it, so an unrelated wait's Request can
            // never lend this token its verdict.
            if (state.RequestSlot is int slot &&
                string.Equals(
                    state.Domain, s_domainId, StringComparison.Ordinal) &&
                _requestSnapshot(slot) is { } snapshot)
            {
                if (!snapshot.IsCompleted)
                {
                    return WaitStatus.Pending(token);
                }
                _releaseRequest(slot);
                if (snapshot.Status == StatusCode.Failure)
                {
                    return WaitStatus.Failed(RequestFailed(snapshot.ErrorMessage));
                }
                if (snapshot.Status == StatusCode.Success)
                {
                    return WaitStatus.Done();
                }
                // IsCompleted with a still-InProgress status: the request
                // is mid-transition — wait for it to settle rather than
                // inventing an outcome.
                return WaitStatus.Pending(token);
            }

            // The post-reload / requestless judgement (SPEC §T.4 L-07):
            // the registered package list decides, and only while the
            // Editor is neither compiling nor updating — a membership
            // read under in-flight work can only misreport a resolve that
            // is still settling.
            if (_isCompiling() || _isUpdating())
            {
                return WaitStatus.Pending(token);
            }
            var registered = _registeredPackages();
            var satisfied = state.ExpectPresent
                ? TargetPresent(state, registered)
                : !NameRegistered(state.Name!, registered);
            return satisfied ? WaitStatus.Done() : WaitStatus.Pending(token);
        }

        /// <summary>
        /// The token a Waitable Operation stores in its first
        /// <see cref="StepResult.Waiting"/>: the verbatim identifier, the
        /// awaited membership and the slot <paramref name="request"/> was
        /// tracked under — the in-flight UPM Request the same-domain
        /// judgement reads — stamped with this domain's generation id so
        /// a resumed token can never borrow a later domain's Request.
        /// The Operation calls this right after issuing the request,
        /// before the first <see cref="Check"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="packageId"/> is empty, or a remove wait
        /// (<paramref name="expectPresent"/> false) whose id carries no
        /// registry package name.
        /// </exception>
        public static JsonValue InitialToken(
            string packageId, bool expectPresent, Request request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return InitialToken(packageId, expectPresent, TrackRequest(request));
        }

        /// <summary>
        /// The requestless form of <see cref="InitialToken(string, bool, Request)"/>:
        /// for waits whose resolution was driven without a UPM Request in
        /// hand — a manifest edit followed by <see cref="Client.Resolve"/>,
        /// say — so only the registered-package-list judgement ever
        /// applies.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="packageId"/> is empty, or a remove wait whose id
        /// carries no registry package name.
        /// </exception>
        public static JsonValue InitialToken(string packageId, bool expectPresent) =>
            InitialToken(packageId, expectPresent, (int?)null);

        /// <summary>
        /// The step-end half of the §G4 package-wait lifecycle the
        /// <see cref="InitialToken(string, bool, Request)"/> track begins:
        /// releases the slot for the paths that end the wait without a
        /// terminal <see cref="Check"/> — a cancelled Run's
        /// <see cref="IOperation.Cancel"/>, say. A terminal verdict already
        /// released it; a token without a request slot is a no-op, and so
        /// is a token stamped by another domain — its slot names a
        /// different wait's Request here, which must stay tracked.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
        public static void EndWait(JsonValue token)
        {
            if (token is null)
            {
                throw new ArgumentNullException(nameof(token));
            }
            if (token.ToToken() is JObject obj &&
                obj[RequestField] is JValue { Type: JTokenType.Integer } slot &&
                obj[DomainField] is JValue { Type: JTokenType.String } domain &&
                string.Equals(
                    (string)domain.Value!, s_domainId, StringComparison.Ordinal))
            {
                ReleaseRequest((int)(long)slot.Value!);
            }
        }

        // The shared writer of the two public InitialToken forms.
        private static JsonValue InitialToken(
            string packageId, bool expectPresent, int? requestSlot)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                throw new ArgumentException(
                    "A PackageResolved token needs the package identifier " +
                    "the wait was armed for.", nameof(packageId));
            }
            var name = RegistryNameOf(packageId);
            if (!expectPresent && name is null)
            {
                throw new ArgumentException(
                    "A PackageResolved remove wait needs a registry package " +
                    "name: the post-reload judgement looks the name up in " +
                    "the registered package list.", nameof(packageId));
            }
            return WriteToken(
                packageId, expectPresent, requestSlot, name, s_domainId);
        }

        /// <summary>
        /// The token over explicit state — the test seam that lets a check
        /// run against a token written in another (simulated) domain:
        /// <paramref name="domain"/> overrides the generation stamp the
        /// slot is written with (null mints this domain's own), so a
        /// resumed post-reload token is provable without a real reload.
        /// Unlike the public forms it touches no live state: nothing is
        /// tracked.
        /// </summary>
        internal static JsonValue InitialToken(
            string packageId, bool expectPresent, int? requestSlot,
            string? name, string? domain = null)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                throw new ArgumentException(
                    "A PackageResolved token needs the package identifier " +
                    "the wait was armed for.", nameof(packageId));
            }
            return WriteToken(
                packageId, expectPresent, requestSlot, name,
                domain ?? s_domainId);
        }

        /// <summary>The <c>name</c> a token waits on — null for a Git add.</summary>
        /// <exception cref="ArgumentException"><paramref name="token"/> is not a PackageResolved token.</exception>
        internal static string? NameOf(JsonValue token) =>
            TokenState.Read(token).Name;

        // The registry name an id resolves against — the PackageName of a
        // registry parse; null for a Git id (UPM's resolve learns the name)
        // and for an id the SG-19 grammar cannot read.
        private static string? RegistryNameOf(string packageId) =>
            PackageIdSource.TryParse(packageId, out var source) &&
            source is { Kind: PackageIdKind.Registry }
                ? source.PackageName
                : null;

        // The live registered package list (SPEC §T.4 L-07): each entry's
        // name and the dependency half of its packageId ("name@version" /
        // "name@<verbatim request>"), which is where a Git install keeps
        // its origin.
        private static IReadOnlyList<RegisteredPackage> ReadRegisteredPackages()
        {
            var infos = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            var list = new List<RegisteredPackage>(infos.Length);
            for (var i = 0; i < infos.Length; i++)
            {
                list.Add(new RegisteredPackage(
                    infos[i].name ?? string.Empty,
                    DependencyOf(infos[i].packageId)));
            }
            return list;
        }

        // The dependency text inside a UPM packageId: "name@<dep>" splits
        // at the first '@' — a package name never contains one, while a
        // Git dependency may (the SCP 'git@host:path' form).
        private static string? DependencyOf(string? packageId)
        {
            if (packageId is null)
            {
                return null;
            }
            var at = packageId.IndexOf('@');
            return at >= 0 ? packageId.Substring(at + 1) : null;
        }

        private static bool NameRegistered(
            string name, IReadOnlyList<RegisteredPackage> registered)
        {
            for (var i = 0; i < registered.Count; i++)
            {
                if (string.Equals(
                    registered[i].Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // The add-side membership test: for a named wait the name is
        // listed; for a nameless (Git) add only the entry whose dependency
        // text parses to the same normalized Git origin and subPath as the
        // request is "対象" — an unrelated package merely appearing in the
        // list during resolution cannot be tied to the request, so it
        // never satisfies the wait and the step's deadline bounds the
        // Pending instead (SPEC §G4 names no post-reload failure).
        private static bool TargetPresent(
            TokenState state, IReadOnlyList<RegisteredPackage> registered)
        {
            if (state.Name is not null)
            {
                return NameRegistered(state.Name, registered);
            }
            if (!PackageIdSource.TryParse(state.Id, out var wanted) ||
                wanted is not { Kind: PackageIdKind.Git })
            {
                return false;
            }
            for (var i = 0; i < registered.Count; i++)
            {
                var entry = registered[i];
                if (entry.Dependency is { } dependency &&
                    PackageIdSource.TryParse(dependency, out var resolved) &&
                    resolved is { Kind: PackageIdKind.Git } &&
                    string.Equals(
                        resolved.NormalizedOrigin, wanted.NormalizedOrigin,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        resolved.SubPath, wanted.SubPath,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // The slot the request table hands a tracked Request: the next
        // counter value — stable within the domain, which is all it needs
        // to be, and void of any assumption about UPM's own numbering.
        private static int TrackRequest(Request request)
        {
            var slot = ++s_nextRequestSlot;
            s_requests[slot] = request;
            return slot;
        }

        private static void ReleaseRequest(int slot) => s_requests.Remove(slot);

        private static RequestSnapshot? SnapshotRequest(int slot)
        {
            if (!s_requests.TryGetValue(slot, out var request))
            {
                return null;
            }
            var status = request.Status;
            return new RequestSnapshot(
                request.IsCompleted, status,
                status == StatusCode.Failure ? request.Error?.message : null);
        }

        private static JsonValue WriteToken(
            string id, bool expectPresent, int? requestSlot, string? name,
            string domain)
        {
            var obj = new JObject
            {
                [IdField] = id,
                [ExpectPresentField] = expectPresent,
            };
            if (requestSlot is int slot)
            {
                obj[RequestField] = slot;
                obj[DomainField] = domain;
            }
            if (name is not null)
            {
                obj[NameField] = name;
            }
            return JsonValue.From(obj);
        }

        // The OW-UNITY-009 verdict with the request's own error text
        // attached (SPEC §G4 "Request の失敗"). The message stays the
        // fixed English sentence; the package identifier is never echoed —
        // the same no-echo rule PackageSourcePolicy holds on candidate
        // text.
        private static Diagnostic RequestFailed(string? errorMessage) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_009,
                "The package operation failed.",
                hint: string.IsNullOrEmpty(errorMessage)
                    ? "The Package Manager request failed; check the Editor log for the request error."
                    : errorMessage);

        /// <summary>
        /// The state a <see cref="Request"/> reports at one check — the
        /// three members the §G4 request judgement reads. Exposed through
        /// the seam so tests never need a live UPM Request.
        /// </summary>
        internal readonly struct RequestSnapshot
        {
            internal RequestSnapshot(
                bool isCompleted, StatusCode status, string? errorMessage)
            {
                IsCompleted = isCompleted;
                Status = status;
                ErrorMessage = errorMessage;
            }

            internal bool IsCompleted { get; }
            internal StatusCode Status { get; }
            internal string? ErrorMessage { get; }
        }

        /// <summary>
        /// One entry of the registered package list (SPEC §T.4 L-07): the
        /// package name and the dependency half of its UPM
        /// <c>packageId</c> — null when the identity carries none.
        /// </summary>
        internal readonly struct RegisteredPackage
        {
            internal RegisteredPackage(string name, string? dependency)
            {
                Name = name;
                Dependency = dependency;
            }

            internal string Name { get; }
            internal string? Dependency { get; }
        }

        private sealed class TokenState
        {
            internal string Id = string.Empty;
            internal bool ExpectPresent;
            internal int? RequestSlot;
            internal string? Domain;
            internal string? Name;

            // Reads the whole wait state out of the resume token. The id
            // and the awaited membership are required — without them there
            // is nothing to judge; slot, domain and name are the optional
            // halves a first-wait or requestless token may lack, and a
            // slot without its domain stamp simply cannot reach the
            // request table.
            internal static TokenState Read(JsonValue token)
            {
                if (token.ToToken() is not JObject obj)
                {
                    throw new ArgumentException(
                        "The resume token is not a PackageResolved token: " +
                        "a token object is required.", nameof(token));
                }
                if (obj[IdField] is not JValue { Type: JTokenType.String } id ||
                    ((string)id.Value!).Length == 0)
                {
                    throw new ArgumentException(
                        "The resume token is not a PackageResolved token: " +
                        "a non-empty 'id' string is required.", nameof(token));
                }
                if (obj[ExpectPresentField] is not JValue { Type: JTokenType.Boolean } expect)
                {
                    throw new ArgumentException(
                        "The resume token is not a PackageResolved token: " +
                        "an 'expectPresent' flag is required.", nameof(token));
                }
                var state = new TokenState
                {
                    Id = (string)id.Value!,
                    ExpectPresent = (bool)expect.Value!,
                };
                if (obj[RequestField] is JValue { Type: JTokenType.Integer } slot &&
                    (long)slot.Value! >= 0)
                {
                    state.RequestSlot = (int)(long)slot.Value!;
                }
                if (obj[DomainField] is JValue { Type: JTokenType.String } domain &&
                    ((string)domain.Value!).Length > 0)
                {
                    state.Domain = (string)domain.Value!;
                }
                if (obj[NameField] is JValue { Type: JTokenType.String } name &&
                    ((string)name.Value!).Length > 0)
                {
                    state.Name = (string)name.Value!;
                }
                if (!state.ExpectPresent && state.Name is null)
                {
                    throw new ArgumentException(
                        "The resume token is not a PackageResolved token: " +
                        "a remove wait needs the target package 'name'.",
                        nameof(token));
                }
                return state;
            }
        }
    }
}
