#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Validates one installed extension's <see cref="ExtensionManifest"/>
    /// against what the environment actually contains (SPEC §X.3.2):
    /// declared assemblies must exist inside the package, declared
    /// TypeProviders must be found in their named assemblies, the declared
    /// <c>operations[]</c> list must equal the Descriptor identities
    /// actually discovered in the extension's assemblies, the Type IDs its
    /// instantiated providers offer must be canonical, unreserved and
    /// unambiguous, declared example paths must resolve to files inside the
    /// package, and the declared Unity minimum must not exceed the running
    /// Editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A package containing extension candidates (discovered TypeProviders
    /// or observed Operations) but no <c>opweave.extension.json</c> is
    /// rejected as a whole — the manifest is a required part of the
    /// distribution unit (SPEC §X.2). Unpackaged project Editor assemblies
    /// never reach this validator: they are compiled against the current
    /// Core and skip the range check by SPEC §B0.2 item 4.
    /// </para>
    /// <para>
    /// The published codes assigned by T-W2.12-03: OW-OP-018 for every
    /// declared-vs-observed verdict (SPEC §J.1 "外部拡張メタデータの宣言と
    /// 実体の不一致" — a missing manifest or a malformed offered Type ID
    /// carries no declaration to compare and keeps the safe empty-code
    /// internal diagnostic), OW-OP-013 for duplicated Type/Provider IDs
    /// and OW-OP-007 for a reserved-namespace offer; the reader's
    /// OW-OP-010/OW-OP-012 diagnostics pass through unchanged. A rejected
    /// extension registers none of its Operations or TypeProviders.
    /// </para>
    /// </remarks>
    public static class ExtensionManifestValidator
    {
        /// <summary>
        /// True when <paramref name="text"/> has the declared Unity version
        /// shape <c>MAJOR.MINOR</c> or <c>MAJOR.MINOR.PATCH</c> of
        /// non-negative integers (SPEC §X.3.2 uses <c>"6000.0"</c>).
        /// </summary>
        public static bool IsUnityVersionText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            var parts = text!.Split('.');
            if (parts.Length is < 2 or > 3)
            {
                return false;
            }
            foreach (var part in parts)
            {
                if (part.Length == 0)
                {
                    return false;
                }
                foreach (var c in part)
                {
                    if (c < '0' || c > '9')
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Validates the extension described by <paramref name="input"/>:
        /// locates and parses its manifest, then compares every declaration
        /// against the discovered reality of the package.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static ExtensionValidationOutcome Validate(ExtensionValidationInput input) =>
            Validate(input, registrationGate: false);

        /// <summary>
        /// <see cref="Validate"/> as the SPEC §C3 rebuild's pre-publication
        /// observed-fact gate applies it (<paramref name="registrationGate"/>).
        /// Per-candidate defects of the offered Type IDs — malformed,
        /// reserved or duplicated inside the package — are not package
        /// verdicts: the §C3 step-4 type build already screens each offered
        /// definition individually, invalidating both ends of a within-
        /// package Type ID collision so only dependent Operations fail
        /// (SPEC §X.6). The declared-vs-observed comparison also keeps a
        /// third state — a declared Operation whose candidate exists but
        /// produced no Descriptor is "not observed because of an individual
        /// failure" (<see cref="ExtensionValidationInput.UnobservedOperationCandidates"/>),
        /// never a mismatch.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        internal static ExtensionValidationOutcome Validate(
            ExtensionValidationInput input, bool registrationGate)
        {
            if (input is null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            // A manifest is required for any packaged extension (SPEC §X.2);
            // a package contributing candidates without one is refused.
            if (input.ManifestText is null)
            {
                if (input.DiscoveredTypeProviders.Count == 0 &&
                    input.ObservedOperations.Count == 0 &&
                    input.ObservedTypes.Count == 0)
                {
                    return ExtensionValidationOutcome.Accept(
                        input.PackageId, manifest: null, typeProviders: null);
                }
                return ExtensionValidationOutcome.Reject(
                    input.PackageId,
                    manifestPresent: false,
                    manifest: null,
                    diagnostics: new[]
                    {
                        Detail(
                            $"Package '{input.PackageId}' contains OpWeave extension candidates but no '{ExtensionManifestReader.FileName}' at its root.",
                            $"Candidates refused: {input.DiscoveredTypeProviders.Count} TypeProvider(s), {input.ObservedOperations.Count} Operation(s), {input.ObservedTypes.Count} Type(s). The extension manifest is required (SPEC §X.2)."),
                    });
            }

            var parsed = ExtensionManifestReader.Parse(input.ManifestText);
            if (!parsed.Ok)
            {
                return ExtensionValidationOutcome.Reject(
                    input.PackageId,
                    manifestPresent: true,
                    manifest: null,
                    diagnostics: parsed.Diagnostics);
            }
            var manifest = parsed.Value!;
            var diagnostics = new List<Diagnostic>();

            CheckDeclaredAssemblies(input, manifest, diagnostics);
            CheckOperations(input, manifest, diagnostics);
            var bindings = CheckTypeProviders(input, manifest, diagnostics);
            if (!registrationGate)
            {
                CheckObservedTypes(input, diagnostics);
            }
            CheckUnityMinimum(input, manifest, diagnostics);
            CheckExamples(input, manifest, diagnostics);

            return diagnostics.Count > 0
                ? ExtensionValidationOutcome.Reject(
                    input.PackageId, manifestPresent: true, manifest, diagnostics)
                : ExtensionValidationOutcome.Accept(input.PackageId, manifest, bindings);
        }

        /// <summary>
        /// Validates every input, then applies the cross-extension
        /// uniqueness rules of SPEC §X.3.2: duplicate
        /// <c>extensionId</c> / Operation <c>(id, version)</c> / Provider ID /
        /// offered Type ID across packages rejects every extension involved;
        /// an extension already rejected keeps its own diagnostics.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="inputs"/> is null.</exception>
        public static IReadOnlyList<ExtensionValidationOutcome> ValidateAll(
            IReadOnlyList<ExtensionValidationInput> inputs)
        {
            if (inputs is null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            var outcomes = new List<ExtensionValidationOutcome>(inputs.Count);
            foreach (var input in inputs)
            {
                outcomes.Add(Validate(input));
            }

            var rejected = new Dictionary<int, List<Diagnostic>>();
            void CrossReject(int index, Diagnostic diagnostic)
            {
                if (!rejected.TryGetValue(index, out var list))
                {
                    list = new List<Diagnostic>();
                    rejected[index] = list;
                }
                list.Add(diagnostic);
            }

            var extensionIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var operationIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var providerIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var typeIds = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                var manifest = outcome.Manifest;
                if (!outcome.Accepted || manifest is null)
                {
                    continue;
                }
                if (extensionIds.TryGetValue(manifest.ExtensionId, out var firstIndex))
                {
                    var message =
                        $"extensionId '{PublishedId.OperationId(manifest.ExtensionId)}' is declared by more than one package.";
                    CrossReject(i, Internal(message,
                        $"Also declared by '{outcomes[firstIndex].PackageId}' (SPEC §X.3.2)."));
                    CrossReject(firstIndex, Internal(message,
                        $"Also declared by '{outcome.PackageId}' (SPEC §X.3.2)."));
                }
                else
                {
                    extensionIds.Add(manifest.ExtensionId, i);
                }
                // Operation IDs duplicate by ID alone — the same ID at two
                // versions is still a duplicate (SPEC §X.3.2).
                CheckDuplicateDeclaredIds(
                    manifest.Operations, operationIds, outcomes, i, CrossReject,
                    op => op.Id,
                    "Operation");
                CheckDuplicateDeclaredIds(
                    manifest.TypeProviders, providerIds, outcomes, i, CrossReject,
                    provider => provider.Id,
                    "Provider ID");
                // Offered Type IDs are not declared in the manifest — the
                // registration stage observes them from instantiated
                // providers (SPEC §C3 steps 3–4); §X.3.2 bans the same Type
                // ID across packages just like the declared identities.
                CheckDuplicateDeclaredIds(
                    inputs[i].ObservedTypes, typeIds, outcomes, i, CrossReject,
                    type => type.TypeId,
                    "Type ID");
            }

            for (var i = 0; i < outcomes.Count; i++)
            {
                if (!rejected.TryGetValue(i, out var diagnostics) || !outcomes[i].Accepted)
                {
                    continue;
                }
                var outcome = outcomes[i];
                outcomes[i] = ExtensionValidationOutcome.Reject(
                    outcome.PackageId,
                    manifestPresent: true,
                    outcome.Manifest,
                    diagnostics);
            }
            return outcomes;
        }

        private static void CheckDuplicateDeclaredIds<T>(
            IReadOnlyList<T> declared,
            Dictionary<string, int> seen,
            IReadOnlyList<ExtensionValidationOutcome> outcomes,
            int index,
            Action<int, Diagnostic> crossReject,
            Func<T, string> keyOf,
            string kind)
        {
            // Type/Provider ID duplicates across packages are OW-OP-013;
            // extensionId/Operation ID duplicates are OW-OP-018 (SPEC §J.1,
            // T-W2.12-03).
            var code = string.Equals(kind, "Provider ID", StringComparison.Ordinal) ||
                string.Equals(kind, "Type ID", StringComparison.Ordinal)
                ? ErrorCodes.OW_OP_013
                : ErrorCodes.OW_OP_018;
            foreach (var entry in declared)
            {
                var key = keyOf(entry);
                if (seen.TryGetValue(key, out var firstIndex))
                {
                    // Declared keys are reader-validated but observed keys
                    // are not — an unvalidated ID is published in its safe
                    // form, never verbatim (SPEC §C3 error paragraph).
                    var safe = string.Equals(kind, "Type ID", StringComparison.Ordinal)
                        ? PublishedId.TypeId(key)
                        : PublishedId.OperationId(key);
                    var message = $"{kind} '{safe}' is declared by more than one package.";
                    crossReject(index, ErrorCodes.Create(code, message,
                        hint: $"Also declared by '{outcomes[firstIndex].PackageId}' (SPEC §X.3.2)."));
                    crossReject(firstIndex, ErrorCodes.Create(code, message,
                        hint: $"Also declared by '{outcomes[index].PackageId}' (SPEC §X.3.2)."));
                }
                else
                {
                    seen.Add(key, index);
                }
            }
        }

        // Every declared assembly must be a real Editor assembly rooted in
        // the package; every assembly actually contributing candidates must
        // be declared.
        private static void CheckDeclaredAssemblies(
            ExtensionValidationInput input,
            ExtensionManifest manifest,
            List<Diagnostic> diagnostics)
        {
            var packageAssemblies = new HashSet<string>(input.PackageEditorAssemblies, StringComparer.Ordinal);
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in manifest.Assemblies)
            {
                declared.Add(name);
                if (!packageAssemblies.Contains(name))
                {
                    diagnostics.Add(Internal(
                        $"Extension manifest declares assembly '{name}', which is not rooted in package '{input.PackageId}'.",
                        "Declared assemblies must be Editor assemblies inside the extension package (SPEC §X.3.2)."));
                }
            }
            foreach (var provider in input.DiscoveredTypeProviders)
            {
                if (!declared.Contains(provider.AssemblyName))
                {
                    diagnostics.Add(Internal(
                        $"TypeProvider class '{provider.TypeFullName}' lives in undeclared assembly '{provider.AssemblyName}'.",
                        "Every assembly contributing extension candidates must be listed in 'assemblies' (SPEC §X.3.2)."));
                }
            }
            foreach (var operation in input.ObservedOperations)
            {
                if (!declared.Contains(operation.AssemblyName))
                {
                    diagnostics.Add(Internal(
                        $"Operation '{PublishedId.OperationId(operation.Id)}' lives in undeclared assembly '{operation.AssemblyName}'.",
                        "Every assembly contributing extension candidates must be listed in 'assemblies' (SPEC §X.3.2)."));
                }
            }
            foreach (var observedType in input.ObservedTypes)
            {
                if (!declared.Contains(observedType.AssemblyName))
                {
                    diagnostics.Add(Internal(
                        $"Type '{PublishedId.TypeId(observedType.TypeId)}' lives in undeclared assembly '{observedType.AssemblyName}'.",
                        "Every assembly contributing extension candidates must be listed in 'assemblies' (SPEC §X.3.2)."));
                }
            }
        }

        // SPEC §X.3.2: the declared operations list must equal the actual
        // Descriptor identities discovered in the extension's assemblies.
        // Each declared entry lands in one of three states: observed-equal
        // (its id@version was read), observed-different (the same id was
        // read at another version, or a Descriptor outside the declaration
        // was read — both proven mismatches) and "not observed" (no
        // Descriptor for it was read at all). A policy-excluded candidate's
        // attribute-declared (id, version) is known before exclusion, so it
        // satisfies only the declared entry naming that same identity —
        // an intentional exclusion is not a mismatch, but it can never
        // stand in for a different declared Operation (SPEC §X.3.2,
        // §C3 rule 5). A not-observed entry left over after that is only
        // a mismatch when no candidate could have produced it — each
        // admitted candidate that failed before its Descriptor was read
        // (an individual OW-OP-008 failure) covers exactly one such entry;
        // what exceeds that budget is a declared item whose class does not
        // exist in the package's assemblies at all.
        private static void CheckOperations(
            ExtensionValidationInput input,
            ExtensionManifest manifest,
            List<Diagnostic> diagnostics)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var op in manifest.Operations)
            {
                declared.Add(Key(op.Id, op.Version));
            }
            var observed = new HashSet<string>(StringComparer.Ordinal);
            var observedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var op in input.ObservedOperations)
            {
                observed.Add(Key(op.Id, op.Version));
                observedIds.Add(op.Id);
            }
            // The attribute-declared identities of policy-excluded
            // candidates, as a multiset — each excluded candidate covers
            // only one declared entry naming exactly its own (id, version).
            var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var op in input.ExcludedOperations)
            {
                var key = Key(op.Id, op.Version);
                excluded.TryGetValue(key, out var excludedCount);
                excluded[key] = excludedCount + 1;
            }
            // The comparison sets stay on raw ids; the display lists carry
            // the publishable form — a Descriptor-supplied id is
            // unvalidated, so a malformed one is never shown verbatim
            // (SPEC §C3 error paragraph).
            var missing = new List<string>();
            var notObserved = new List<string>();
            foreach (var op in manifest.Operations)
            {
                var key = Key(op.Id, op.Version);
                if (observed.Contains(key))
                {
                    continue;
                }
                if (excluded.TryGetValue(key, out var available) && available > 0)
                {
                    excluded[key] = available - 1;
                    continue;
                }
                if (observedIds.Contains(op.Id))
                {
                    // The id was actually read at another version — a
                    // proven declared-vs-observed difference.
                    missing.Add(SafeKey(op.Id, op.Version));
                }
                else
                {
                    notObserved.Add(SafeKey(op.Id, op.Version));
                }
            }
            var uncovered = notObserved.Count - input.UnobservedOperationCandidates;
            if (uncovered > 0)
            {
                notObserved.Sort(StringComparer.Ordinal);
                for (var i = 0; i < uncovered; i++)
                {
                    missing.Add(notObserved[i]);
                }
            }
            var extra = new List<string>();
            var extraSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var op in input.ObservedOperations)
            {
                var key = Key(op.Id, op.Version);
                if (!declared.Contains(key) && extraSeen.Add(key))
                {
                    extra.Add(SafeKey(op.Id, op.Version));
                }
            }
            if (missing.Count > 0 || extra.Count > 0)
            {
                missing.Sort(StringComparer.Ordinal);
                extra.Sort(StringComparer.Ordinal);
                var hint = missing.Count > 0 && extra.Count > 0
                    ? $"Declared but not found: {string.Join(", ", missing)}. Found but not declared: {string.Join(", ", extra)}."
                    : missing.Count > 0
                        ? $"Declared but not found: {string.Join(", ", missing)}."
                        : $"Found but not declared: {string.Join(", ", extra)}.";
                diagnostics.Add(Internal(
                    $"Extension manifest 'operations' does not match the Operation Descriptors found in package '{input.PackageId}'.",
                    hint));
            }

            static string Key(string id, int version) =>
                id + "@" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

            static string SafeKey(string id, int version) =>
                PublishedId.OperationId(id) + "@" +
                version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // SPEC §X.3.2 / §X.6: each declared Provider ID names an assembly
        // containing a [OpWeaveTypeProvider] class, and every provider class
        // in the package's declared assemblies must be bound to a declared
        // ID. Per-assembly the discovered classes and declared entries pair
        // deterministically — classes ordered by full name ordinal, entries
        // by Provider ID ordinal.
        private static List<TypeProviderBinding> CheckTypeProviders(
            ExtensionValidationInput input,
            ExtensionManifest manifest,
            List<Diagnostic> diagnostics)
        {
            var bindings = new List<TypeProviderBinding>();
            var discoveredByAssembly = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var provider in input.DiscoveredTypeProviders)
            {
                if (!discoveredByAssembly.TryGetValue(provider.AssemblyName, out var list))
                {
                    list = new List<string>();
                    discoveredByAssembly[provider.AssemblyName] = list;
                }
                list.Add(provider.TypeFullName);
            }
            var declaredByAssembly = new Dictionary<string, List<ExtensionTypeProviderRef>>(StringComparer.Ordinal);
            foreach (var providerRef in manifest.TypeProviders)
            {
                if (!declaredByAssembly.TryGetValue(providerRef.Assembly, out var list))
                {
                    list = new List<ExtensionTypeProviderRef>();
                    declaredByAssembly[providerRef.Assembly] = list;
                }
                list.Add(providerRef);
            }

            foreach (var pair in declaredByAssembly)
            {
                var assembly = pair.Key;
                var discovered = discoveredByAssembly.TryGetValue(assembly, out var found)
                    ? found
                    : new List<string>();
                var declaredRefs = pair.Value;
                if (discovered.Count == 0)
                {
                    diagnostics.Add(Internal(
                        $"No [OpWeaveTypeProvider] class found in assembly '{assembly}' for package '{input.PackageId}'.",
                        "Every 'typeProviders' entry must resolve to a provider class in the named assembly (SPEC §X.3.2)."));
                    continue;
                }
                if (discovered.Count != declaredRefs.Count)
                {
                    diagnostics.Add(Internal(
                        $"Assembly '{assembly}' contains {discovered.Count} [OpWeaveTypeProvider] class(es) but the manifest declares {declaredRefs.Count} Provider ID(s) for it.",
                        "Provider IDs and provider classes must pair one to one (SPEC §X.3.2)."));
                    continue;
                }
                discovered.Sort(StringComparer.Ordinal);
                declaredRefs.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                for (var i = 0; i < declaredRefs.Count; i++)
                {
                    bindings.Add(new TypeProviderBinding(declaredRefs[i].Id, discovered[i], assembly));
                }
            }
            foreach (var pair in discoveredByAssembly)
            {
                if (!declaredByAssembly.ContainsKey(pair.Key))
                {
                    diagnostics.Add(Internal(
                        $"Assembly '{pair.Key}' contains {pair.Value.Count} [OpWeaveTypeProvider] class(es) with no 'typeProviders' entry.",
                        "Every provider class must be bound to a declared Provider ID (SPEC §X.3.2)."));
                }
            }
            return bindings;
        }

        // SPEC §X.3.2 / §B0.1: the Type IDs an extension's providers offer
        // must be canonical §B1 IDs, must not use a namespace reserved for
        // standard providers, and must not repeat inside one extension —
        // the same screening TypeRegistryBuilder applies at registration,
        // enforced here at whole-extension granularity. Cross-package
        // duplicates are rejected in ValidateAll.
        private static void CheckObservedTypes(
            ExtensionValidationInput input,
            List<Diagnostic> diagnostics)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observed in input.ObservedTypes)
            {
                var id = observed.TypeId ?? string.Empty;
                // The offered Type ID is unvalidated provider text — every
                // diagnostic carries its publishable form, never the raw
                // string (SPEC §C3 error paragraph).
                var safeId = PublishedId.TypeId(id);
                if (!seen.Add(id))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_013,
                        $"Extension '{input.PackageId}' offers Type ID '{safeId}' more than once.",
                        hint: "Type IDs must not be duplicated (SPEC §X.3.2)."));
                }
                var name = id.EndsWith("<>", StringComparison.Ordinal)
                    ? id.Substring(0, id.Length - 2)
                    : id;
                var parsed = name.Length > 0 ? TypeRef.Parse(name) : default;
                if (name.Length == 0 || !parsed.Ok || parsed.Value!.Args.Count != 0 ||
                    !string.Equals(parsed.Value!.ToString(), name, StringComparison.Ordinal))
                {
                    // No §J.1 code is assigned to a malformed offered Type
                    // ID (OW-OP-016 is the Codec bucket only): the safe
                    // empty-code internal diagnostic of the type build.
                    diagnostics.Add(Detail(
                        $"Extension offers Type ID '{safeId}' which is not a canonical SPEC §B1 Type ID.",
                        "Offered Type IDs must be canonical (SPEC §B1, §X.3.2)."));
                    continue;
                }
                if (IdGrammar.HasReservedOperationPrefix(name))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_007,
                        $"Extension offers Type ID '{safeId}' which uses a reserved namespace.",
                        hint: "core.*, unity.* and opweave.* are reserved (SPEC §X.3.2)."));
                }
            }
        }

        // Declared unity.minInclusive must not exceed the running Editor —
        // numeric MAJOR.MINOR[.PATCH] comparison; the current version's own
        // suffix (e.g. "85f1" in "6000.0.85f1") is taken numerically.
        private static void CheckUnityMinimum(
            ExtensionValidationInput input,
            ExtensionManifest manifest,
            List<Diagnostic> diagnostics) =>
            AppendUnityEligibilityDiagnostics(
                manifest, input.CurrentUnityVersion, diagnostics);

        // The environment eligibility rule of SPEC §X.3.2 in reusable form:
        // the registration stage applies it before binding a packaged
        // provider, exactly as whole-extension validation does — a package
        // whose declared Unity minimum exceeds the running Editor registers
        // none of its candidates.
        internal static void AppendUnityEligibilityDiagnostics(
            ExtensionManifest manifest,
            string currentUnityVersion,
            List<Diagnostic> diagnostics)
        {
            if (!TryParseUnityVersion(manifest.UnityMinInclusive, out var declared))
            {
                // Unreachable: the reader already enforced the format.
                diagnostics.Add(Detail(
                    $"Extension manifest 'unity.minInclusive' '{manifest.UnityMinInclusive}' is malformed.",
                    "Expected MAJOR.MINOR or MAJOR.MINOR.PATCH (SPEC §X.3.2)."));
                return;
            }
            if (!TryParseUnityVersion(currentUnityVersion, out var current))
            {
                diagnostics.Add(Detail(
                    $"The running Unity version '{currentUnityVersion}' could not be evaluated.",
                    "The extension cannot be proven compatible; failing closed."));
                return;
            }
            if (declared.CompareTo(current) > 0)
            {
                diagnostics.Add(Internal(
                    $"Extension requires Unity >= {manifest.UnityMinInclusive} but the running Editor is {currentUnityVersion}.",
                    null));
            }
        }

        private static void CheckExamples(
            ExtensionValidationInput input,
            ExtensionManifest manifest,
            List<Diagnostic> diagnostics)
        {
            foreach (var example in manifest.Examples)
            {
                if (!TryResolvePackagePath(input.PackageRootPath, example, out var fullPath))
                {
                    diagnostics.Add(Internal(
                        $"Extension example path '{example}' escapes the package root.",
                        "Example paths must be package-relative (SPEC §X.3.2)."));
                    continue;
                }
                if (!File.Exists(fullPath))
                {
                    diagnostics.Add(Internal(
                        $"Extension example '{example}' was not found inside package '{input.PackageId}'.",
                        "Every 'examples' entry must resolve to an existing file inside the package (SPEC §X.3.2)."));
                }
            }
        }

        // A package-relative path that stays under the root: rejects rooted
        // paths and '..' escapes after canonical resolution.
        private static bool TryResolvePackagePath(string packageRoot, string relative, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
            {
                return false;
            }
            string root;
            try
            {
                root = Path.GetFullPath(packageRoot).Replace('\\', '/').TrimEnd('/') + "/";
                fullPath = Path.GetFullPath(Path.Combine(packageRoot, relative)).Replace('\\', '/');
            }
            catch (Exception)
            {
                return false;
            }
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullPath, root.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        // Parses MAJOR.MINOR[.PATCH], tolerating the running version's
        // alphanumeric suffix ("6000.0.85f1" → 6000.0.85).
        private static bool TryParseUnityVersion(string text, out VersionTriple version)
        {
            version = default;
            var parts = (text ?? string.Empty).Split('.');
            if (parts.Length is < 2 or > 3)
            {
                return false;
            }
            var numbers = new int[3];
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var digits = 0;
                while (digits < part.Length && part[digits] >= '0' && part[digits] <= '9')
                {
                    digits++;
                }
                if (digits == 0)
                {
                    return false;
                }
                // A non-digit suffix is only allowed on the last component of
                // the running version ("85f1"); the declared form was already
                // restricted to digits by the reader.
                if (digits != part.Length && i != parts.Length - 1)
                {
                    return false;
                }
                if (!int.TryParse(part.Substring(0, digits), out numbers[i]))
                {
                    return false;
                }
            }
            version = new VersionTriple(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        private readonly struct VersionTriple : IComparable<VersionTriple>
        {
            private readonly int _major;
            private readonly int _minor;
            private readonly int _patch;

            public VersionTriple(int major, int minor, int patch)
            {
                _major = major;
                _minor = minor;
                _patch = patch;
            }

            public int CompareTo(VersionTriple other)
            {
                var major = _major.CompareTo(other._major);
                if (major != 0)
                {
                    return major;
                }
                var minor = _minor.CompareTo(other._minor);
                return minor != 0 ? minor : _patch.CompareTo(other._patch);
            }
        }

        // A declared-vs-observed extension-metadata verdict:
        // OW-OP-018 (SPEC §J.1, T-W2.12-03).
        private static Diagnostic Internal(string message, string? hint) =>
            ErrorCodes.Create(ErrorCodes.OW_OP_018, message, hint: hint);

        // A rejection with no assigned published code — an absent or
        // malformed manifest, a malformed offered Type ID, or an
        // environment the validator cannot evaluate: the safe empty-code
        // internal diagnostic (SG-02 convention).
        private static Diagnostic Detail(string message, string? hint) =>
            new Diagnostic(string.Empty, Severity.Error, message, Hint: hint);
    }
}
