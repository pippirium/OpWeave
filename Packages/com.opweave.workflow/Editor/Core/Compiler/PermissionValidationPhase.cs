#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The D6 permission and static-precondition validation phase (SPEC §D6):
    /// resolves each node's actual side effects from its §C4 declaration and an
    /// optional <see cref="IEffectResolver"/>, applies the injected
    /// <see cref="IPermissionPolicy"/> for the request's actor (deny →
    /// OW-PERM-001, ask → the approvals the Plan records), checks constant
    /// <c>path_writable</c> paths against the allowed paths (OW-PERM-002),
    /// answers <c>type_exists</c> requirements through the injected
    /// <see cref="IStaticTypeProbe"/> (OW-UNITY-006, or the OW-VAL-015 warning
    /// when earlier code generation may define the type), rejects a constant
    /// <c>unity.package.add</c> <c>id</c> that embeds credentials in its Git
    /// URL (OW-OP-006), forbids compile/code/package nodes between
    /// <c>unity.prefab.open_contents</c> and
    /// <c>unity.prefab.save_contents</c> (OW-VAL-012), and warns when a
    /// <c>type_exists</c> node follows code generation without an intervening
    /// <c>unity.script.compile</c> (OW-VAL-013).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The item-3 credential check looks only at the constant <c>id</c>
    /// (parameter or Descriptor default); an edge- or binding-fed
    /// <c>id</c> is the D4 phase's OW-OP-006 ConstantOnly finding, so the
    /// rejection fires before any Plan exists while every carried value stays
    /// out of the diagnostic (SPEC §G8.1 "出力しない").
    /// </para>
    /// <para>
    /// Local readings where SPEC leaves detail open: "path parameters" are the
    /// input ports a Descriptor binds to a <c>path_writable</c> requirement
    /// (SPEC §G1 item 4 — its allowed-paths half is the only part statically
    /// decidable; writability and immutable packages stay runtime checks);
    /// both constant parameters and Descriptor defaults are fixed paths and
    /// are checked, while edge- or binding-fed ports defer to the runtime G1
    /// check (SPEC §D6 item 2); the "earlier node" ordering follows the D5
    /// execution order, falling back to document order when the control graph
    /// failed — v1 documents are linear, so the fallback is faithful; path
    /// containment compares normalized <c>/</c>-separated paths
    /// case-insensitively, matching Unity's asset path handling; the item-4
    /// warning is OW-VAL-015 (SPEC §J, 2026-11-06), judged independently of the
    /// item-6 OW-VAL-013 so a node meeting both conditions carries both
    /// warnings; an Operation the same Run's generated code would
    /// define is not special-cased — it is simply absent from the snapshot and
    /// reported by the D3 phase as OW-OP-001 (SPEC §D6 item 4, R6).
    /// </para>
    /// </remarks>
    internal sealed class PermissionValidationPhase : IValidationPhase
    {
        internal const string PathWritableRequirement = "path_writable";
        internal const string TypeExistsRequirement = "type_exists";

        // SPEC §G6/§G8 operation ids the positional rules key on.
        internal const string PrefabOpenContentsOperation = "unity.prefab.open_contents";
        internal const string PrefabSaveContentsOperation = "unity.prefab.save_contents";
        internal const string ScriptCompileOperation = "unity.script.compile";
        internal const string PackageAddOperation = "unity.package.add";

        // The ConstantOnly unity.package.add port the §G8.1 credential rule
        // applies to (SPEC §D6 item 3, §I.4).
        internal const string PackageAddIdPort = "id";

        // The fixed English rejection message of SPEC §G8.1: the diagnostic
        // carries this text and the JsonPath only — never the rejected value.
        internal const string CredentialGitUrlMessage = "Git URL contains credentials";

        private static readonly string[] s_prefabEditForbidden =
        {
            EffectNames.Compile,
            EffectNames.CodeWrite,
            EffectNames.CodeTemplate,
            EffectNames.PackageModify,
        };

        private static readonly string[] s_codeGenerating =
        {
            EffectNames.CodeWrite,
            EffectNames.CodeTemplate,
            EffectNames.PackageModify,
        };

        private static readonly string[] s_codeWriting =
        {
            EffectNames.CodeWrite,
            EffectNames.CodeTemplate,
        };

        /// <summary>The §H4 settings default for <c>allowedPaths</c>.</summary>
        internal static readonly IReadOnlyList<string> DefaultAllowedPaths =
            ReadOnlyCollections.CopyList(new[] { "Assets/" });

        private readonly RegistrySnapshot _snapshot;
        private readonly Func<string, IReadOnlyDictionary<string, InputValue>, StaticContext> _staticContextFactory;
        private readonly Func<ActorKind, IPermissionPolicy> _policyFor;
        private readonly IStaticTypeProbe _typeProbe;
        private readonly IReadOnlyList<string> _allowedPaths;

        internal PermissionValidationPhase(
            RegistrySnapshot snapshot,
            Func<string, IReadOnlyDictionary<string, InputValue>, StaticContext> staticContextFactory,
            Func<ActorKind, IPermissionPolicy> policyFor,
            IStaticTypeProbe typeProbe,
            IReadOnlyList<string> allowedPaths)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _staticContextFactory =
                staticContextFactory ?? throw new ArgumentNullException(nameof(staticContextFactory));
            _policyFor = policyFor ?? throw new ArgumentNullException(nameof(policyFor));
            _typeProbe = typeProbe ?? throw new ArgumentNullException(nameof(typeProbe));
            _allowedPaths = allowedPaths ?? throw new ArgumentNullException(nameof(allowedPaths));
        }

        /// <summary>The §C4 default policy for an <see cref="ActorKind"/>.</summary>
        internal static IPermissionPolicy DefaultPolicyFor(ActorKind actor) =>
            DefaultPermissionPolicy.For(
                actor == ActorKind.Agent ? PolicyActor.Agent : PolicyActor.Human);

        public ValidationPhaseKind Kind => ValidationPhaseKind.D6;

        public void Run(ValidationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            var document = context.Document;

            var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var nodes = document.Nodes;
            for (var i = 0; i < nodes.Count; i++)
            {
                // Duplicate node ids are the D2 phase's OW-WF-004 finding; the
                // first occurrence wins, matching ValidationContext's rule.
                if (!nodeIndex.ContainsKey(nodes[i].Id))
                {
                    nodeIndex.Add(nodes[i].Id, i);
                }
            }

            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in context.ActiveNodes)
            {
                active.Add(node.Id);
            }

            // Positional rules follow the D5 execution order; a failed control
            // graph leaves it null and document order — faithful for the
            // linear v1 flow — takes its place.
            var ordered = context.ExecutionOrder ?? nodes;

            // Ports fed by a data edge or WF input binding defer their path
            // and type checks to the runtime (SPEC §D6 item 2).
            var fedPorts = new HashSet<PortRef>();
            foreach (var edge in document.DataEdges)
            {
                fedPorts.Add(edge.To);
            }
            foreach (var binding in document.InputBindings)
            {
                fedPorts.Add(binding.To);
            }

            // SPEC §C4/§D6 item 1: the actual effects of every resolvable
            // node — the IEffectResolver's answer for the operations that
            // declare one (§C4), the declared list otherwise. The resolver
            // runs only for still-active nodes; a resolver exception is
            // OW-RUN-006 without exception text (SPEC §0.6 SG-06), and the
            // node keeps the declared list for the positional rules below.
            var effectsByNode = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var descriptors = new Dictionary<string, OperationDescriptor>(StringComparer.Ordinal);
            foreach (var node in ordered)
            {
                if (descriptors.ContainsKey(node.Id))
                {
                    continue;
                }
                var registered = _snapshot.Find(node.Operation, node.Version);
                if (registered is null)
                {
                    // OW-OP-001/002 is the D3 phase's finding.
                    continue;
                }
                var descriptor = registered.Descriptor;
                descriptors.Add(node.Id, descriptor);
                effectsByNode.Add(
                    node.Id,
                    ActualEffects(context, node, registered, active.Contains(node.Id)));
            }

            // The Build Plan stage reads each step's actual effects from the
            // context (SPEC §D7 item 1); the dictionary holds the resolver's
            // answer for active nodes and the declared list for excluded ones.
            context.Effects = effectsByNode;

            CheckPermissions(context, ordered, active, nodeIndex, effectsByNode, descriptors);
            CheckPaths(context, active, nodeIndex, fedPorts, descriptors);
            CheckPackageIds(context, nodeIndex, fedPorts, descriptors);
            CheckTypes(context, ordered, active, nodeIndex, fedPorts, effectsByNode, descriptors);
            CheckPrefabEdit(context, ordered, active, nodeIndex, effectsByNode);
        }

        // SPEC §D6 item 1: allow/ask/deny per node under the injected policy;
        // deny produces one OW-PERM-001 per effect, ask lands on the Plan's
        // approval list the Build Plan stage reads.
        private void CheckPermissions(
            ValidationContext context,
            IReadOnlyList<NodeModel> ordered,
            ISet<string> active,
            IReadOnlyDictionary<string, int> nodeIndex,
            IReadOnlyDictionary<string, IReadOnlyList<string>> effectsByNode,
            IReadOnlyDictionary<string, OperationDescriptor> descriptors)
        {
            var policy = _policyFor(context.Actor) ?? DefaultPolicyFor(context.Actor);
            var targets = new List<PermissionTarget>();
            foreach (var node in ordered)
            {
                if (!active.Contains(node.Id) ||
                    !effectsByNode.TryGetValue(node.Id, out var effects))
                {
                    continue;
                }
                var descriptor = descriptors[node.Id];
                targets.Add(new PermissionTarget(
                    node.Id, effects, descriptor.Destructive,
                    descriptor.Undo == UndoSupport.Full));
            }

            var evaluation = PermissionEvaluator.Evaluate(policy, targets);
            context.Approvals = evaluation.Approvals;
            foreach (var diagnostic in evaluation.Diagnostics)
            {
                var index = diagnostic.NodeId is not null &&
                    nodeIndex.TryGetValue(diagnostic.NodeId, out var i)
                        ? i
                        : -1;
                context.Report(index >= 0
                    ? diagnostic with { JsonPath = $"$.nodes[{index}]" }
                    : diagnostic);
            }
        }

        // SPEC §D6 item 2: a constant path_writable port must resolve inside
        // the allowed paths (OW-PERM-002); edge- or binding-fed ports defer to
        // the runtime G1 check.
        private void CheckPaths(
            ValidationContext context,
            ISet<string> active,
            IReadOnlyDictionary<string, int> nodeIndex,
            ISet<PortRef> fedPorts,
            IReadOnlyDictionary<string, OperationDescriptor> descriptors)
        {
            var normalizedAllowed = new List<string>(_allowedPaths.Count);
            foreach (var allowed in _allowedPaths)
            {
                // An allowed prefix that fails normalization — '..' or a
                // rooted/absolute spelling — is skipped rather than degrading
                // to a match-all empty prefix (fail closed).
                var normalized = NormalizePathPrefix(allowed);
                if (normalized is not null)
                {
                    normalizedAllowed.Add(normalized);
                }
            }

            foreach (var node in context.ActiveNodes)
            {
                if (!descriptors.TryGetValue(node.Id, out var descriptor))
                {
                    continue;
                }
                var index = nodeIndex[node.Id];
                foreach (var requirement in descriptor.Requirements)
                {
                    if (requirement.Kind != PathWritableRequirement ||
                        requirement.Port is null)
                    {
                        continue;
                    }
                    var port = FindPort(descriptor, requirement.Port);
                    if (port is null ||
                        fedPorts.Contains(new PortRef(node.Id, requirement.Port)))
                    {
                        // An unknown port name is the registration-time
                        // descriptor check's finding; an edge/binding-fed path
                        // is the runtime check's.
                        continue;
                    }
                    var value = ConstantValue(context, node, port);
                    var isParameter = node.Parameters.ContainsKey(port.Name);
                    if (value is null ||
                        value.ToToken() is not JValue { Type: JTokenType.String } text)
                    {
                        continue;
                    }
                    var normalized = NormalizePath((string)text.Value!);
                    var inside = normalized is not null &&
                        IsInsideAllowedPaths(normalized, normalizedAllowed);
                    // SPEC §H4 item 5: the OpWeave settings file is never part
                    // of the permitted write paths — a widened allowedPaths
                    // cannot admit it, so it fails here like any outside path.
                    // The shared guard is asked independently of the containment
                    // result: a Windows spelling that names the settings file
                    // without sitting inside an allowed prefix
                    // ("ProjectSettings./…") is refused on its own, and the
                    // trailing dot/space, NTFS stream and 8.3 short-name
                    // spellings of the same file are refused too.
                    var settingsFile = normalized is not null &&
                        SettingsPathGuard.IsProtectedSettingsPath(normalized);
                    if (inside && !settingsFile)
                    {
                        continue;
                    }
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_PERM_002,
                        $"The constant path of port '{port.Name}' is outside the allowed paths.",
                        nodeId: node.Id,
                        port: port.Name,
                        jsonPath: isParameter
                            ? StructuralValidationPhase.MemberPath(
                                $"$.nodes[{index}].parameters", port.Name)
                            : $"$.nodes[{index}]",
                        hint: settingsFile
                            ? "The OpWeave settings file is never writable by Operations; only the human Project Settings screen saves it."
                            : _allowedPaths.Count == 0
                                ? "No path is allowed; configure allowedPaths in the project settings."
                                : "Allowed paths: " + string.Join(", ", _allowedPaths) + "."));
                }
            }
        }

        // SPEC §D6 item 3 (§G8.1): a constant unity.package.add id embedding
        // credentials in its Git URL fails OW-OP-006 before any Plan exists.
        // Edge- or binding-fed ids are the D4 phase's OW-OP-006 ConstantOnly
        // finding regardless of value, and the diagnostic carries the JsonPath
        // and the fixed English message only — never the rejected text.
        private void CheckPackageIds(
            ValidationContext context,
            IReadOnlyDictionary<string, int> nodeIndex,
            ISet<PortRef> fedPorts,
            IReadOnlyDictionary<string, OperationDescriptor> descriptors)
        {
            foreach (var node in context.ActiveNodes)
            {
                if (!string.Equals(node.Operation, PackageAddOperation, StringComparison.Ordinal) ||
                    !descriptors.TryGetValue(node.Id, out var descriptor))
                {
                    continue;
                }
                var port = FindPort(descriptor, PackageAddIdPort);
                if (port is null ||
                    fedPorts.Contains(new PortRef(node.Id, port.Name)))
                {
                    continue;
                }
                var value = ConstantValue(context, node, port);
                var isParameter = node.Parameters.ContainsKey(port.Name);
                if (value is null ||
                    value.ToToken() is not JValue { Type: JTokenType.String } text ||
                    !GitUrlCredentialCheck.ContainsCredentials((string)text.Value!))
                {
                    continue;
                }
                var index = nodeIndex[node.Id];
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_OP_006,
                    CredentialGitUrlMessage,
                    nodeId: node.Id,
                    port: port.Name,
                    jsonPath: isParameter
                        ? StructuralValidationPhase.MemberPath(
                            $"$.nodes[{index}].parameters", port.Name)
                        : $"$.nodes[{index}]",
                    hint: "Remove the credentials from the Git URL; authenticate through a credential helper or an SSH key configured outside the URL."));
            }
        }

        // SPEC §D6 items 4 and 6: type_exists requirements probe the injected
        // type space. A type the probe cannot find is OW-UNITY-006 unless an
        // earlier node's code/template/package effects may define it — the
        // OW-VAL-015 warning, rechecked at run time — and any type_exists node
        // after code.write/code.template without an intervening
        // unity.script.compile earns OW-VAL-013. The two warnings are judged
        // independently: a node meeting both conditions carries both.
        private void CheckTypes(
            ValidationContext context,
            IReadOnlyList<NodeModel> ordered,
            ISet<string> active,
            IReadOnlyDictionary<string, int> nodeIndex,
            ISet<PortRef> fedPorts,
            IReadOnlyDictionary<string, IReadOnlyList<string>> effectsByNode,
            IReadOnlyDictionary<string, OperationDescriptor> descriptors)
        {
            for (var position = 0; position < ordered.Count; position++)
            {
                var node = ordered[position];
                if (!active.Contains(node.Id) ||
                    !descriptors.TryGetValue(node.Id, out var descriptor))
                {
                    continue;
                }
                var requiresType = false;
                foreach (var requirement in descriptor.Requirements)
                {
                    if (requirement.Kind == TypeExistsRequirement)
                    {
                        requiresType = true;
                        break;
                    }
                }
                if (!requiresType)
                {
                    continue;
                }

                var index = nodeIndex[node.Id];
                CheckTypeExists(
                    context, node, index, descriptor, position,
                    ordered, fedPorts, effectsByNode);

                // SPEC §D6 item 6: code.write/code.template earlier and no
                // unity.script.compile since — the generated code may not be
                // compiled before this node uses the type. Independent of the
                // item-4 verdict (SPEC §D6, 2026-11-06).
                if (!CompiledSinceCodeGeneration(ordered, position, effectsByNode))
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_013,
                        "The node uses a type before code generated earlier in the workflow is compiled.",
                        nodeId: node.Id,
                        jsonPath: $"$.nodes[{index}]",
                        hint: "Add a unity.script.compile node between code generation and this node."));
                }
            }
        }

        // The item-4 half: probe each constant type name of the node's
        // type_exists requirements; a miss is OW-VAL-015 when an earlier
        // node's code/template/package effects may define the type (rechecked
        // at run time), OW-UNITY-006 otherwise.
        private void CheckTypeExists(
            ValidationContext context,
            NodeModel node,
            int index,
            OperationDescriptor descriptor,
            int position,
            IReadOnlyList<NodeModel> ordered,
            ISet<PortRef> fedPorts,
            IReadOnlyDictionary<string, IReadOnlyList<string>> effectsByNode)
        {
            foreach (var requirement in descriptor.Requirements)
            {
                if (requirement.Kind != TypeExistsRequirement ||
                    requirement.Port is null)
                {
                    continue;
                }
                var port = FindPort(descriptor, requirement.Port);
                if (port is null ||
                    fedPorts.Contains(new PortRef(node.Id, requirement.Port)))
                {
                    continue;
                }
                var value = ConstantValue(context, node, port);
                if (value is null ||
                    value.ToToken() is not JValue { Type: JTokenType.String } text)
                {
                    continue;
                }
                var typeName = (string)text.Value!;
                if (_typeProbe.TypeExists(typeName))
                {
                    continue;
                }
                if (EarlierCodeGeneration(ordered, position, effectsByNode))
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_015,
                        $"The type '{typeName}' was not found; an earlier node's code generation or package change may define it.",
                        nodeId: node.Id,
                        port: port.Name,
                        jsonPath: StructuralValidationPhase.MemberPath(
                            $"$.nodes[{index}].parameters", port.Name),
                        hint: "The type existence is checked again when the Run reaches this node."));
                    continue;
                }
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_006,
                    $"The type '{typeName}' required by port '{port.Name}' was not found.",
                    nodeId: node.Id,
                    port: port.Name,
                    jsonPath: StructuralValidationPhase.MemberPath(
                        $"$.nodes[{index}].parameters", port.Name),
                    hint: "Fix the type name or make sure the type is compiled before validating again; a type defined by code generated in the same run is not available yet."));
            }
        }

        // SPEC §D6 item 5 (§G6 item 6): a compile/code/template/package node
        // between unity.prefab.open_contents and its save_contents discards
        // the prefab edit on domain reload — OW-VAL-012 on the offending node.
        private void CheckPrefabEdit(
            ValidationContext context,
            IReadOnlyList<NodeModel> ordered,
            ISet<string> active,
            IReadOnlyDictionary<string, int> nodeIndex,
            IReadOnlyDictionary<string, IReadOnlyList<string>> effectsByNode)
        {
            var insideContents = false;
            foreach (var node in ordered)
            {
                if (node.Operation == PrefabOpenContentsOperation)
                {
                    insideContents = true;
                    continue;
                }
                if (node.Operation == PrefabSaveContentsOperation)
                {
                    insideContents = false;
                    continue;
                }
                if (!insideContents ||
                    !active.Contains(node.Id) ||
                    !effectsByNode.TryGetValue(node.Id, out var effects) ||
                    !HasAny(effects, s_prefabEditForbidden))
                {
                    continue;
                }
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_VAL_012,
                    "A compile, code write, template or package change node sits between unity.prefab.open_contents and unity.prefab.save_contents; a domain reload would discard the prefab edit.",
                    nodeId: node.Id,
                    jsonPath: $"$.nodes[{nodeIndex[node.Id]}]",
                    hint: "Move the node outside the open_contents/save_contents range."));
            }
        }

        // The node's actual effects: the declared list, or the resolver's
        // answer when the implementation is an IEffectResolver and the node is
        // still checked (SPEC §C4). A resolver exception is OW-RUN-006 without
        // exception text; the declared list stands in for the positional rules.
        private IReadOnlyList<string> ActualEffects(
            ValidationContext context,
            NodeModel node,
            RegisteredOperation registered,
            bool isActive)
        {
            var descriptor = registered.Descriptor;
            if (!isActive || registered.Implementation is not IEffectResolver resolver)
            {
                return descriptor.SideEffects;
            }
            try
            {
                var resolved = resolver.ResolveEffects(
                    new StaticTypedContext(
                        _staticContextFactory(node.Id, ParametersFor(context, node, descriptor)),
                        StaticTypedContext.StaticTypesFor(context, node, descriptor)));
                // A null return breaches the contract; fall back to the
                // declared list rather than dropping the node's permissions.
                return resolved ?? descriptor.SideEffects;
            }
            catch (Exception)
            {
                // SPEC §0.6 SG-06: operation exceptions surface as OW-RUN-006;
                // the exception text may carry secrets and never crosses.
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    $"The effect resolver of operation '{node.Operation}' threw an exception.",
                    nodeId: node.Id));
                return descriptor.SideEffects;
            }
        }

        // The resolved-parameter view ValidateStatic also uses: every declared
        // input port with the codec-resolved value, the written token or
        // Missing (SPEC §D0.2).
        private static IReadOnlyDictionary<string, InputValue> ParametersFor(
            ValidationContext context, NodeModel node, OperationDescriptor descriptor)
        {
            var resolved = context.ResolvedParameters is not null &&
                context.ResolvedParameters.TryGetValue(node.Id, out var parameters)
                    ? parameters
                    : null;
            var map = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var input in descriptor.Inputs)
            {
                JsonValue? value = null;
                if (resolved is not null && resolved.TryGetValue(input.Name, out var resolvedValue))
                {
                    value = resolvedValue;
                }
                else if (node.Parameters.TryGetValue(input.Name, out var written))
                {
                    value = written;
                }
                map[input.Name] =
                    value is null ? InputValue.Missing : InputValue.Present(value);
            }
            return map;
        }

        // The fixed path/type-name a requirement port resolves to: the
        // codec-resolved or written parameter, else the Descriptor default —
        // all Plan-fixed values. Null means the port is not statically known.
        private static JsonValue? ConstantValue(
            ValidationContext context, NodeModel node, PortDescriptor port)
        {
            if (context.ResolvedParameters is not null &&
                context.ResolvedParameters.TryGetValue(node.Id, out var resolved) &&
                resolved.TryGetValue(port.Name, out var resolvedValue))
            {
                return resolvedValue;
            }
            if (node.Parameters.TryGetValue(port.Name, out var written))
            {
                return written;
            }
            return port.Default;
        }

        private static PortDescriptor? FindPort(
            OperationDescriptor descriptor, string name)
        {
            foreach (var port in descriptor.Inputs)
            {
                if (string.Equals(port.Name, name, StringComparison.Ordinal))
                {
                    return port;
                }
            }
            return null;
        }

        private static bool HasAny(IReadOnlyList<string> effects, string[] wanted)
        {
            foreach (var effect in effects)
            {
                foreach (var name in wanted)
                {
                    if (string.Equals(effect, name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // True when an earlier node carries a code.write/code.template/
        // package.modify effect — the generated code or added package may
        // define the missing type (SPEC §D6 item 4).
        private static bool EarlierCodeGeneration(
            IReadOnlyList<NodeModel> ordered,
            int position,
            IReadOnlyDictionary<string, IReadOnlyList<string>> effectsByNode)
        {
            for (var i = 0; i < position; i++)
            {
                if (effectsByNode.TryGetValue(ordered[i].Id, out var effects) &&
                    HasAny(effects, s_codeGenerating))
                {
                    return true;
                }
            }
            return false;
        }

        // True when every earlier code.write/code.template node is followed
        // by a unity.script.compile node before this position — i.e. the
        // generated code was compiled before the type is used (SPEC §D6
        // item 6). False when the most recent generation-or-compile event is
        // a generation.
        private static bool CompiledSinceCodeGeneration(
            IReadOnlyList<NodeModel> ordered,
            int position,
            IReadOnlyDictionary<string, IReadOnlyList<string>> effectsByNode)
        {
            var generated = -1;
            var compiled = -1;
            for (var i = 0; i < position; i++)
            {
                var node = ordered[i];
                if (effectsByNode.TryGetValue(node.Id, out var effects) &&
                    HasAny(effects, s_codeWriting))
                {
                    generated = i;
                }
                if (string.Equals(
                        node.Operation, ScriptCompileOperation, StringComparison.Ordinal))
                {
                    compiled = i;
                }
            }
            return generated < 0 || compiled > generated;
        }

        // The allowed-paths check of SPEC §G7 item 1 on an already-normalized
        // path: a segment-boundary prefix match against an allowed path.
        // Unity asset paths compare case-insensitively.
        private static bool IsInsideAllowedPaths(string normalized, IReadOnlyList<string> allowed)
        {
            foreach (var prefix in allowed)
            {
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // '/'-separated, '.'- and empty-segment-collapsed path, or null when a
        // '..' segment escapes the root — always outside the allowed paths.
        // Rooted and absolute spellings reject before collapsing (SPEC §D6
        // item 2): a leading '/' or '\', a Windows drive prefix, a UNC or
        // device prefix and a '~' home prefix would otherwise lose their
        // first segment and pass the prefix check as a relative path.
        private static string? NormalizePath(string path)
        {
            if (path.Length > 0 &&
                (path[0] == '/' || path[0] == '\\' || path[0] == '~' ||
                 (path.Length > 1 && char.IsLetter(path[0]) && path[1] == ':')))
            {
                return null;
            }
            var segments = path.Replace('\\', '/').Split('/');
            var kept = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (segment == "..")
                {
                    return null;
                }
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }
                kept.Add(segment);
            }
            return kept.Count == 0 ? string.Empty : string.Join("/", kept);
        }

        private static string? NormalizePathPrefix(string prefix)
        {
            var normalized = NormalizePath(prefix);
            if (normalized is null)
            {
                return null;
            }
            return normalized.Length == 0 || normalized.EndsWith("/", StringComparison.Ordinal)
                ? normalized
                : normalized + "/";
        }
    }
}
