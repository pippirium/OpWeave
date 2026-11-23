#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Testing
{
    /// <summary>
    /// Finds the <see cref="IOperation"/> implementation a harness run names
    /// (SPEC §M.6 item 1) by scanning the loaded managed assemblies for
    /// SPEC §C2 item 1 Operation classes and matching their Descriptor identity
    /// — the standard set inside <c>com.opweave.workflow</c> and a custom
    /// Operation in the author's own assembly alike, which is exactly the pair
    /// SPEC §M.6 wants to hold to the same contract test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately not the SPEC §C3 Registry: the Registry is built by
    /// the Editor pipeline and published through the Runner host, and a contract
    /// test must be able to run an Operation that was never registered — a
    /// fixture that fails its Descriptor check, an excluded extension, an
    /// Operation whose settings entry disables it. The Registry's own rules
    /// (reserved namespaces, Waitable Poll metadata, the §C3 step-4 type build)
    /// are what <see cref="OperationContractAsserts"/>-style descriptor checks
    /// cover, not what a harness run needs.
    /// </para>
    /// <para>
    /// The candidate table is built once per domain on the first lookup and
    /// keyed by Descriptor id (Ordinal) then version; a class whose constructor
    /// or Descriptor getter fails is skipped, because such a class can never
    /// name a run. A fresh instance is created for every run — SPEC §C2 item 4
    /// forbids instance state, and
    /// <see cref="OperationTestHarness.RunInterrupted"/> must not inherit what
    /// an earlier <see cref="OperationTestHarness.Run"/> left behind.
    /// </para>
    /// </remarks>
    internal static class HarnessOperationSource
    {
        private static Dictionary<string, Dictionary<int, List<Type>>>? _index;

        // The (id, version) → factory overrides a contract check registers
        // for a standard Operation whose only legal live binding is the
        // active Run's frozen settings snapshot — e.g. the §I.3 Operations
        // reading OpWeaveRunnerHost.Current.ActiveRunAllowedPaths, which no
        // workflow-less harness run can produce (SPEC §H4.1 keeps that
        // authority on the Run's own acceptance record). The factory still
        // has to build the same implementation class through its own seam,
        // so the Operation under contract stays the standard one — only the
        // snapshot provider is substituted, the stand-in the harness's own
        // requirement evaluator makes for path_writable.
        private static readonly Dictionary<(string Id, int Version), Func<IOperation>> _overrides =
            new Dictionary<(string Id, int Version), Func<IOperation>>();

        /// <summary>
        /// Resolves <paramref name="operationId"/> at
        /// <paramref name="version"/> to a new implementation instance.
        /// An id no loaded class declares is OW-OP-001, an id declared only at
        /// another version is OW-OP-002, several classes sharing the
        /// (id, version) pair is OW-OP-004 — none of them registered — and a
        /// class that cannot be constructed answers OW-OP-008 (SPEC §J).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> is null or empty.
        /// </exception>
        internal static Result<IOperation> Resolve(string operationId, int version)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                throw new ArgumentException(
                    "The Operation ID must not be null or empty.", nameof(operationId));
            }

            // A caller-supplied ID is unvalidated text that may carry
            // credentials, so only its publishable form names the diagnostic
            // (SPEC §C3 error paragraph).
            var safeId = PublishedId.OperationId(operationId);
            var index = Index();
            if (!index.TryGetValue(operationId, out var byVersion) || byVersion.Count == 0)
            {
                return Result<IOperation>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_001,
                    $"Operation '{safeId}' is not implemented by any loaded assembly.",
                    hint: "The [OpWeaveOperation] class must be compiled into an assembly " +
                        "the Editor domain loaded."));
            }
            if (!byVersion.TryGetValue(version, out var candidates) || candidates.Count == 0)
            {
                var versions = new List<int>(byVersion.Keys);
                versions.Sort();
                return Result<IOperation>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_002,
                    $"Operation '{safeId}' version {version} is not implemented; " +
                        $"declared versions: {string.Join(", ", versions)}.",
                    hint: "Pass the version the Descriptor declares."));
            }
            if (candidates.Count > 1)
            {
                return Result<IOperation>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_004,
                    $"Operation '{safeId}' version {version} is declared by " +
                        $"{candidates.Count} candidates; none were registered.",
                    hint: "Two loaded classes declare the same Operation identity."));
            }
            // An override keeps every discovery check above — the pair must
            // still resolve to exactly one eligible class — and only swaps
            // the construction, so the Run-snapshot substitution can never
            // resurrect an id no standard implementation declares.
            if (_overrides.TryGetValue((operationId, version), out var factory))
            {
                var instance = factory();
                if (instance is null)
                {
                    return Result<IOperation>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_OP_008,
                        $"Operation '{safeId}' version {version} could not be constructed.",
                        hint: "The registered harness override returned no instance."));
                }
                return Result<IOperation>.Success(instance);
            }
            return OperationEligibility.Instantiate(candidates[0]);
        }

        /// <summary>
        /// The versions of <paramref name="operationId"/> the loaded classes
        /// declare, ascending; empty when no eligible class declares the id —
        /// the index view a Descriptor check needs to cover every implemented
        /// version, not just the one a run names.
        /// </summary>
        internal static List<int> VersionsOf(string operationId)
        {
            var versions = new List<int>();
            if (Index().TryGetValue(operationId, out var byVersion))
            {
                versions.AddRange(byVersion.Keys);
                versions.Sort();
            }
            return versions;
        }

        /// <summary>
        /// Drops the built table — the seam a test uses when it wants the next
        /// lookup to re-scan the loaded assemblies.
        /// </summary>
        internal static void Invalidate() => _index = null;

        /// <summary>
        /// Registers <paramref name="factory"/> as the instance
        /// <see cref="Resolve"/> produces for the (id, version) pair — the
        /// seam a contract check uses for an Operation whose only legal live
        /// binding is the active Run's frozen settings snapshot (SPEC §H4.1),
        /// which no workflow-less run can produce. The factory must construct
        /// the same implementation class through its own test-seam
        /// constructor — the Operation under contract stays the standard
        /// implementation; only the snapshot provider is substituted, the
        /// stand-in the harness's own requirement evaluator already makes for
        /// path_writable.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> is null or empty.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
        internal static void Override(
            string operationId, int version, Func<IOperation> factory)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                throw new ArgumentException(
                    "The Operation ID must not be null or empty.", nameof(operationId));
            }
            _overrides[(operationId, version)] =
                factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>
        /// Drops every <see cref="Override"/> registration — the mirror a
        /// test teardown needs so an override never leaks into another
        /// suite's contract run.
        /// </summary>
        internal static void ClearOverrides() => _overrides.Clear();

        private static Dictionary<string, Dictionary<int, List<Type>>> Index()
        {
            if (_index is not null)
            {
                return _index;
            }
            var index = new Dictionary<string, Dictionary<int, List<Type>>>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in TypesOf(assembly))
                {
                    // A class the §C2 item 1 check refuses can never name a
                    // run, so it is not a candidate for any id.
                    if (OperationEligibility.Check(type).Count != 0)
                    {
                        continue;
                    }
                    var created = OperationEligibility.Instantiate(type);
                    if (!created.Ok || created.Value is null)
                    {
                        continue;
                    }
                    OperationDescriptor descriptor;
                    try
                    {
                        descriptor = created.Value.Descriptor;
                    }
                    catch (Exception)
                    {
                        // A Descriptor that throws names no id; the §C3 step-6
                        // registration reports it, a harness lookup may not.
                        continue;
                    }
                    AddCandidate(index, descriptor.Id, descriptor.Version, type);
                }
            }
            _index = index;
            return _index;
        }

        private static void AddCandidate(
            Dictionary<string, Dictionary<int, List<Type>>> index,
            string id,
            int version,
            Type type)
        {
            if (!index.TryGetValue(id, out var byVersion))
            {
                byVersion = new Dictionary<int, List<Type>>();
                index[id] = byVersion;
            }
            if (!byVersion.TryGetValue(version, out var types))
            {
                types = new List<Type>();
                byVersion[version] = types;
            }
            if (!types.Contains(type))
            {
                types.Add(type);
            }
        }

        // The assembly's declared types. An assembly whose type graph cannot be
        // loaded (a missing dependency) still offers the types it resolved,
        // exactly as the Editor's own discovery pass treats them; an assembly
        // that cannot be reflected at all offers none.
        private static IEnumerable<Type> TypesOf(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                var loaded = new List<Type>();
                foreach (var type in exception.Types)
                {
                    if (type is not null)
                    {
                        loaded.Add(type);
                    }
                }
                return loaded;
            }
            catch (Exception)
            {
                return Array.Empty<Type>();
            }
        }
    }
}
