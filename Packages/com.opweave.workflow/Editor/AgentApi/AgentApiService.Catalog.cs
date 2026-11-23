#nullable enable

// T-W3.2-02: the SPEC §H1 discovery and Registry commands —
// search_operations (§C6), describe_operation (§C8),
// search_compatible_operations (§C7), list_types (§B2),
// get_workflow_schema (§A2), list_workflows (§A8) and
// get_registry_status (§C3) — as direct AgentApi methods callable without
// Pipeline (SPEC §H1 test 1). Every command projects the published
// RegistrySnapshot and environment seams of IAgentCatalogBackend into
// the shared response envelope; pagination, cursors and the 64 KiB cap go
// through the same PageResponse/EnforceCap machinery as the run commands.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;

namespace OpWeave.AgentApi
{
    public sealed partial class AgentApiService
    {
        // The catalog backend every §H1 discovery/Registry command
        // shares; absent means the command layer was installed without
        // its environment — an internal error the command answers as the
        // fail-closed OW-API-004 diagnostic, never as an exception or a
        // partial behaviour (failures cross layers as Result/Diagnostic).
        private IAgentCatalogBackend? Catalog() => _catalog;

        // The fail-closed answer of a command whose catalog backend was
        // never installed — the same OW-API-004 Invoke's escape hatch
        // reports, minus the exception: a missing environment seam is an
        // expected absent-seam state, not a crash.
        private AgentApiResponse MissingCatalogBackend() => Failure(new[]
        {
            AgentApiErrors.InternalError(
                "The operation catalog backend is not installed on this service."),
        });

        // The snapshot the Registry-scoped commands answer from: the
        // pre-build Registry is stale with no published snapshot (SPEC §C3
        // rule 9), and an empty answer would falsely claim a Registry of
        // zero Operations — the missing snapshot is the fail-closed
        // OW-API-004 internal error, not an empty result and not an
        // exception.
        private AgentApiResponse MissingRegistrySnapshot() => Failure(new[]
        {
            AgentApiErrors.InternalError(
                "The operation registry is not available."),
        });

        /// <summary>
        /// The <c>search_operations</c> command of SPEC §H1, bound to §C6:
        /// the free-text query plus the conjunctive filters return the
        /// compact per-Operation summaries — identity, origin, localized
        /// display text, port name→Type ID maps, effects, destructive flag
        /// and execution mode — ranked by the §C6 score and paged through
        /// the §H3 item-3 cursor machinery.
        /// </summary>
        /// <remarks>
        /// The §C6 item-4 page contract folds into the envelope: the
        /// default <c>limit</c> is 10, the maximum 50 — a larger request
        /// value clamps, never errors, and a non-positive one is
        /// OW-API-001. Every emitted item stays within the §H3 bound of
        /// 600 UTF-8 bytes: <see cref="SearchItemSummary"/> in
        /// OpWeave.Core owns the projection and its reduction, and this
        /// command emits its output unchanged — a reduced item declares
        /// <c>truncated</c> so the Agent asks <c>describe_operation</c>
        /// for the unreduced contract.
        /// </remarks>
        public AgentApiResponse SearchOperations(SearchOperationsRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var catalog = Catalog();
                if (catalog is null)
                {
                    return MissingCatalogBackend();
                }
                OperationOrigin? origin = null;
                if (request.Origin is { } originText)
                {
                    switch (originText)
                    {
                        case "standard":
                            origin = OperationOrigin.Standard;
                            break;
                        case "custom":
                            origin = OperationOrigin.Custom;
                            break;
                        default:
                            return Failure(new[]
                            {
                                AgentApiErrors.MalformedRequest(
                                    "The 'origin' argument must be 'standard' or 'custom'.",
                                    jsonPath: "$.origin"),
                            });
                    }
                }
                var snapshot = catalog.Snapshot();
                if (snapshot is null)
                {
                    return MissingRegistrySnapshot();
                }
                var service = new OperationSearchService(snapshot);
                // The full ranked listing, gathered through the Core
                // service's own §C6 pages so the §H3 cursor machinery
                // slices one identical ordering.
                var items = new List<JToken>();
                var language = catalog.AgentLanguage();
                for (int? offset = 0; offset is { } start;)
                {
                    var page = service.SearchOperations(
                        new OpWeave.Core.Search.OperationSearchRequest
                        {
                            Query = request.Query,
                            AcceptsType = request.AcceptsType,
                            ProducesType = request.ProducesType,
                            AllowedEffects = request.AllowedEffects,
                            Tags = request.Tags,
                            Category = request.Category,
                            Origin = origin,
                            Lang = request.Lang,
                            Limit = OperationSearchService.MaxLimit,
                            Offset = start,
                        },
                        language);
                    if (!page.Ok)
                    {
                        return Failure(page);
                    }
                    var results = page.Value!.Items;
                    for (var i = 0; i < results.Count; i++)
                    {
                        items.Add(SearchItemSummary.Build(results[i]));
                    }
                    offset = page.Value.NextOffset;
                }
                var context = PageContext("search_operations", SearchOperationsArguments(request));
                var limit = Math.Min(
                    request.Limit ?? OperationSearchService.DefaultLimit,
                    OperationSearchService.MaxLimit);
                return PageResponse(
                    context, request.Cursor, items,
                    (start, count) => SearchPageJson(context, items, start, count),
                    limit: limit);
            });
        }

        /// <summary>
        /// The <c>describe_operation</c> command of SPEC §H1, bound to §C8:
        /// the complete contract of one registered Operation — the full
        /// Descriptor, input/output §B8 schemas, requirement descriptions,
        /// complete-WF examples, origin, package, assembly, fingerprints,
        /// Registry generation, the Operation's own diagnostics and the
        /// custom provenance source file — narrowed by the optional
        /// <c>fields</c> projection. An unknown ID answers OW-OP-001 with
        /// its hint, an unregistered version OW-OP-002.
        /// </summary>
        public AgentApiResponse DescribeOperation(DescribeOperationRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var catalog = Catalog();
                if (catalog is null)
                {
                    return MissingCatalogBackend();
                }
                if (string.IsNullOrEmpty(request.Id))
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'id' argument must be an Operation ID.",
                            jsonPath: "$.id"),
                    });
                }
                if (request.Version is { } version && version < 1)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'version' argument must be a positive integer.",
                            jsonPath: "$.version"),
                    });
                }
                var snapshot = catalog.Snapshot();
                if (snapshot is null)
                {
                    return MissingRegistrySnapshot();
                }
                var described = new OperationDescribeService(snapshot).DescribeOperation(
                    new OpWeave.Core.Search.DescribeOperationRequest(request.Id!)
                    {
                        Version = request.Version,
                        Fields = request.Fields,
                        Lang = request.Lang,
                    },
                    catalog.AgentLanguage());
                if (!described.Ok)
                {
                    return Failure(described);
                }
                return Ok(
                    DescriptionJson(described.Value!),
                    context: PageContext(
                        "describe_operation", DescribeOperationArguments(request)),
                    cursor: request.Cursor);
            });
        }

        /// <summary>
        /// The <c>search_compatible_operations</c> command of SPEC §H1,
        /// bound to §C7: every registered Operation/port pair that
        /// connects to the queried type in the request direction, each
        /// classified <c>exact</c>/<c>base</c>/<c>json</c> with its
        /// registry origin. Exactly one query form answers —
        /// <c>{ "type": … }</c> or <c>{ "operation": …, "port": … }</c> —
        /// and <c>includeJson</c> admits the pairs that match only through
        /// the <c>core.Json</c> rule (SPEC §B6 rule 2).
        /// </summary>
        public AgentApiResponse SearchCompatibleOperations(
            SearchCompatibleOperationsRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var catalog = Catalog();
                if (catalog is null)
                {
                    return MissingCatalogBackend();
                }
                CompatibleDirection direction;
                switch (request.Direction)
                {
                    case "downstream":
                        direction = CompatibleDirection.Downstream;
                        break;
                    case "upstream":
                        direction = CompatibleDirection.Upstream;
                        break;
                    default:
                        return Failure(new[]
                        {
                            AgentApiErrors.MalformedRequest(
                                "The 'direction' argument must be 'downstream' or 'upstream'.",
                                jsonPath: "$.direction"),
                        });
                }
                var hasType = !string.IsNullOrEmpty(request.Type);
                var hasOperation = !string.IsNullOrEmpty(request.Operation);
                var hasPort = !string.IsNullOrEmpty(request.Port);
                // Exactly one query form: 'type' alone, or 'operation' and
                // 'port' together. Every other combination — both forms,
                // neither, or one member of the Operation pair — is
                // request-shape misuse.
                if (hasType && (hasOperation || hasPort))
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'type' and 'operation'/'port' forms must not be combined.",
                            jsonPath: "$.type"),
                    });
                }
                if (hasOperation != hasPort)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'operation' and 'port' arguments must be supplied together.",
                            jsonPath: hasOperation ? "$.port" : "$.operation"),
                    });
                }
                if (!hasType && !hasOperation)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The request must supply 'type' or 'operation' and 'port'."),
                    });
                }
                var snapshot = catalog.Snapshot();
                if (snapshot is null)
                {
                    return MissingRegistrySnapshot();
                }
                var query = hasType
                    ? CompatibleOperationsRequest.ForType(
                        request.Type!, direction, request.IncludeJson)
                    : CompatibleOperationsRequest.ForOperationPort(
                        request.Operation!, request.Port!, direction, request.IncludeJson);
                var found = new OperationSearchService(snapshot)
                    .SearchCompatibleOperations(query);
                if (!found.Ok)
                {
                    return Failure(found);
                }
                var items = new List<JToken>(found.Value!.Count);
                for (var i = 0; i < found.Value.Count; i++)
                {
                    items.Add(CompatibleMatchJson(found.Value[i]));
                }
                var context = PageContext(
                    "search_compatible_operations", CompatibleOperationsArguments(request));
                return PageResponse(
                    context, request.Cursor, items,
                    (start, count) => new JObject
                    {
                        ["matches"] = new JArray(Slice(items, start, count)),
                    });
            });
        }

        /// <summary>
        /// The <c>list_types</c> command of SPEC §H1, bound to §B2: the
        /// registered Type IDs with their kind, description, explicit base
        /// types and generic shape — the snapshot's deterministic order,
        /// optionally narrowed to the IDs starting with
        /// <c>prefix</c> (Ordinal).
        /// </summary>
        public AgentApiResponse ListTypes(ListTypesRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var catalog = Catalog();
                if (catalog is null)
                {
                    return MissingCatalogBackend();
                }
                var snapshot = catalog.Snapshot();
                if (snapshot is null)
                {
                    return MissingRegistrySnapshot();
                }
                var types = snapshot.Types.All;
                var items = new List<JToken>(types.Count);
                for (var i = 0; i < types.Count; i++)
                {
                    if (request.Prefix is { } prefix &&
                        !types[i].Id.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    items.Add(TypeEntryJson(types[i]));
                }
                var context = PageContext("list_types", ListTypesArguments(request));
                return PageResponse(
                    context, request.Cursor, items,
                    (start, count) => new JObject
                    {
                        ["types"] = new JArray(Slice(items, start, count)),
                    });
            });
        }

        /// <summary>
        /// The <c>get_workflow_schema</c> command of SPEC §H1, bound to
        /// §A2: the shipped <c>Schemas~/opweave-1.0.schema.json</c>
        /// artifact verbatim. An artifact that cannot be read or that no
        /// longer parses is a broken installation — OW-API-004, never a
        /// substituted schema.
        /// </summary>
        public AgentApiResponse GetWorkflowSchema(GetWorkflowSchemaRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var catalog = Catalog();
                if (catalog is null)
                {
                    return MissingCatalogBackend();
                }
                var text = catalog.ReadWorkflowSchema();
                if (!text.Ok)
                {
                    return Failure(text);
                }
                var schema = JsonValue.Parse(text.Value!);
                if (!schema.Ok || schema.Value is null)
                {
                    // The packaged artifact must always parse — a broken
                    // schema file is an installation defect, reported with
                    // the internal-error code and no detail leak.
                    return Failure(new[]
                    {
                        AgentApiErrors.InternalError(
                            "The workflow schema artifact is not valid JSON."),
                    });
                }
                return Ok(
                    schema.Value.ToToken(),
                    context: PageContext("get_workflow_schema", new JObject()),
                    cursor: request.Cursor);
            });
        }

        /// <summary>
        /// The <c>list_workflows</c> command of SPEC §H1, bound to §A8:
        /// every <c>*.opweave.json</c> document under the project-relative
        /// folder (default <c>"Assets"</c>) whose id/title/description/tags
        /// match all whitespace-separated <c>query</c> terms, reporting
        /// the §A8 metadata (id, title, path plus description and tags
        /// when declared) in path order. Files that cannot be read or
        /// parsed — and IDs declared by more than one document — surface
        /// as warnings; a folder escaping the project root is OW-API-001.
        /// </summary>
        public AgentApiResponse ListWorkflows(ListWorkflowsRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var catalog = Catalog();
                if (catalog is null)
                {
                    return MissingCatalogBackend();
                }
                if (request.Folder is { } folder && folder.Length == 0)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'folder' argument must name a readable directory inside the project.",
                            jsonPath: "$.folder"),
                    });
                }
                var listed = catalog.ListWorkflowFiles(request.Folder ?? "Assets");
                if (!listed.Ok)
                {
                    return Failure(listed);
                }
                var terms = QueryTerms(request.Query);
                var warnings = new List<Diagnostic>();
                var items = new List<JToken>();
                var idsByPath = new Dictionary<string, string>(StringComparer.Ordinal);
                var files = listed.Value!;
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    if (file.Text is null)
                    {
                        warnings.Add(UnreadableWorkflow(file.Path));
                        continue;
                    }
                    var document = WorkflowSerializer.Read(file.Text);
                    if (!document.Ok)
                    {
                        // The file is listed but unusable — one warning per
                        // file names the path and the first diagnostic's
                        // message; the document's own diagnostics stay in
                        // the file, which an Agent can validate directly.
                        warnings.Add(UnreadableWorkflow(file.Path));
                        continue;
                    }
                    var value = document.Value!;
                    if (!MatchesTerms(terms, value))
                    {
                        continue;
                    }
                    if (idsByPath.TryGetValue(value.Id, out var earlier))
                    {
                        warnings.Add(ErrorCodes.Create(
                            ErrorCodes.OW_WF_004,
                            "The workflow id '" + value.Id + "' is declared by both '" +
                                earlier + "' and '" + file.Path + "'."));
                    }
                    else
                    {
                        idsByPath[value.Id] = file.Path;
                    }
                    items.Add(WorkflowEntryJson(value, file.Path));
                }
                var context = PageContext("list_workflows", ListWorkflowsArguments(request));
                return PageResponse(
                    context, request.Cursor, items,
                    (start, count) => new JObject
                    {
                        ["workflows"] = new JArray(Slice(items, start, count)),
                    },
                    warnings: warnings);
            });
        }

        /// <summary>
        /// The <c>get_registry_status</c> command of SPEC §H1, bound to
        /// §C3: the published snapshot's generation ID and §C3 rule-6
        /// registryHash, the per-origin registration counts, every
        /// rejected candidate's safe diagnostic data (OW-OP-013..019 and
        /// the stage rejections beside them), the type-build failure
        /// records, the deliberate policy exclusions, the §H4.1 settings
        /// diagnostics with their JsonPaths, the §C3 rule-9 stale flag and
        /// the §C3 rule-7 diff — <c>previousSnapshot: "unavailable"</c>
        /// with its reason whenever the prior summary cannot be proven.
        /// </summary>
        /// <remarks>
        /// <paramref name="request"/>'s <c>since</c> is the generation ID
        /// (or registryHash) the caller last saw: the current generation
        /// answers an empty change list, the persisted previous summary
        /// answers the recorded build diff, and any other generation —
        /// the durable store keeps only the last two summaries — is
        /// reported <c>"unavailable"</c>, never guessed (SPEC §C3 rule 7).
        /// </remarks>
        public AgentApiResponse GetRegistryStatus(GetRegistryStatusRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var catalog = Catalog();
                if (catalog is null)
                {
                    return MissingCatalogBackend();
                }
                if (request.Since is { } since && since.Length == 0)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'since' argument must be a Registry generation ID.",
                            jsonPath: "$.since"),
                    });
                }
                var snapshot = catalog.Snapshot();
                var diff = catalog.LatestDiff();
                var data = new JObject
                {
                    ["stale"] = catalog.RegistryStale(),
                    ["counts"] = OriginCountsJson(snapshot),
                    ["failures"] = CandidateFailuresJson(snapshot),
                    ["typeFailures"] = TypeFailuresJson(snapshot),
                    ["exclusions"] = ExclusionsJson(snapshot),
                    ["settingsDiagnostics"] = DiagnosticsJson(
                        catalog.RegistryLoadDiagnostics()),
                    ["diff"] = DiffJson(snapshot, diff, request.Since),
                };
                if (snapshot is not null)
                {
                    data["generationId"] = snapshot.GenerationId;
                    data["registryHash"] = snapshot.RegistryHash;
                }
                return Ok(
                    data,
                    context: PageContext(
                        "get_registry_status", RegistryStatusArguments(request)),
                    cursor: request.Cursor);
            });
        }

        // ── Request-argument material for cursor binding ──────────────────

        private static JObject SearchOperationsArguments(SearchOperationsRequest request)
        {
            var arguments = new JObject();
            if (request.Query is not null)
            {
                arguments["query"] = request.Query;
            }
            if (request.AcceptsType is not null)
            {
                arguments["acceptsType"] = request.AcceptsType;
            }
            if (request.ProducesType is not null)
            {
                arguments["producesType"] = request.ProducesType;
            }
            if (request.AllowedEffects is not null)
            {
                arguments["allowedEffects"] = new JArray(request.AllowedEffects);
            }
            if (request.Tags is not null)
            {
                arguments["tags"] = new JArray(request.Tags);
            }
            if (request.Category is not null)
            {
                arguments["category"] = request.Category;
            }
            if (request.Origin is not null)
            {
                arguments["origin"] = request.Origin;
            }
            if (request.Lang is not null)
            {
                arguments["lang"] = request.Lang;
            }
            if (request.Limit is { } limit)
            {
                arguments["limit"] = limit;
            }
            return arguments;
        }

        private static JObject DescribeOperationArguments(DescribeOperationRequest request)
        {
            var arguments = new JObject { ["id"] = request.Id };
            if (request.Version is { } version)
            {
                arguments["version"] = version;
            }
            if (request.Fields is not null)
            {
                arguments["fields"] = new JArray(request.Fields);
            }
            if (request.Lang is not null)
            {
                arguments["lang"] = request.Lang;
            }
            return arguments;
        }

        private static JObject CompatibleOperationsArguments(
            SearchCompatibleOperationsRequest request)
        {
            var arguments = new JObject();
            if (request.Type is not null)
            {
                arguments["type"] = request.Type;
            }
            if (request.Operation is not null)
            {
                arguments["operation"] = request.Operation;
            }
            if (request.Port is not null)
            {
                arguments["port"] = request.Port;
            }
            if (request.Direction is not null)
            {
                arguments["direction"] = request.Direction;
            }
            arguments["includeJson"] = request.IncludeJson;
            return arguments;
        }

        private static JObject ListTypesArguments(ListTypesRequest request)
        {
            var arguments = new JObject();
            if (request.Prefix is not null)
            {
                arguments["prefix"] = request.Prefix;
            }
            return arguments;
        }

        private static JObject ListWorkflowsArguments(ListWorkflowsRequest request)
        {
            var arguments = new JObject();
            if (request.Query is not null)
            {
                arguments["query"] = request.Query;
            }
            if (request.Folder is not null)
            {
                arguments["folder"] = request.Folder;
            }
            return arguments;
        }

        private static JObject RegistryStatusArguments(GetRegistryStatusRequest request)
        {
            var arguments = new JObject();
            if (request.Since is not null)
            {
                arguments["since"] = request.Since;
            }
            return arguments;
        }

        // ── Wire serialization ───────────────────────────────────────────

        // The §C6 example's data shape: the items plus the continuation —
        // an explicit null when the listing ended, matching the
        // documented example verbatim. The envelope carries the same
        // cursor on the §H3 member.
        private JObject SearchPageJson(
            string context, IReadOnlyList<JToken> items, int start, int count)
        {
            var end = start + count;
            return new JObject
            {
                ["items"] = new JArray(Slice(items, start, count)),
                ["nextCursor"] = end < items.Count
                    ? new JValue(_cursors.Issue(context, end))
                    : JValue.CreateNull(),
            };
        }

        // The §C8 response: the (id, version) identity plus every
        // populated section — members absent from the projection stay
        // Missing rather than null.
        private static JObject DescriptionJson(OperationDescription description)
        {
            var data = new JObject
            {
                ["id"] = description.Id,
                ["version"] = description.Version,
            };
            if (description.Descriptor is { } descriptor)
            {
                data["descriptor"] = DescriptorHash.ToToken(descriptor);
            }
            if (description.DisplayName is { } displayName)
            {
                data["displayName"] = displayName;
            }
            if (description.Summary is { } summary)
            {
                data["summary"] = summary;
            }
            if (description.Description is { } text)
            {
                data["description"] = text;
            }
            if (description.Inputs is { } inputs)
            {
                data["inputs"] = DescribedPortsJson(inputs);
            }
            if (description.Outputs is { } outputs)
            {
                data["outputs"] = DescribedPortsJson(outputs);
            }
            if (description.Requirements is { } requirements)
            {
                data["requirements"] = RequirementsJson(requirements);
            }
            if (description.Examples is { } examples)
            {
                data["examples"] = ExamplesJson(examples);
            }
            if (description.Origin is { } origin)
            {
                data["origin"] = JsonNaming.ToSnakeCase(origin.ToString());
            }
            if (description.PackageId is { } packageId)
            {
                data["packageId"] = packageId;
            }
            if (description.ResolvedCommit is { } resolvedCommit)
            {
                data["resolvedCommit"] = resolvedCommit;
            }
            if (description.AssemblyName is { } assemblyName)
            {
                data["assemblyName"] = assemblyName;
            }
            var descriptorHash = description.DescriptorHash;
            var implementationHash = description.ImplementationHash;
            if (descriptorHash is not null || implementationHash is not null)
            {
                var fingerprints = new JObject();
                if (descriptorHash is not null)
                {
                    fingerprints["descriptorHash"] = descriptorHash;
                }
                if (implementationHash is not null)
                {
                    fingerprints["implementationHash"] = implementationHash;
                }
                data["fingerprints"] = fingerprints;
            }
            if (description.RegistryGeneration is { } generation)
            {
                data["registryGeneration"] = generation;
            }
            if (description.Diagnostics is { } diagnostics)
            {
                data["diagnostics"] = DiagnosticsJson(diagnostics);
            }
            if (description.SourceFile is { } sourceFile)
            {
                data["sourceFile"] = sourceFile;
            }
            return data;
        }

        private static JArray DescribedPortsJson(IReadOnlyList<DescribedPort> ports)
        {
            var json = new JArray();
            for (var i = 0; i < ports.Count; i++)
            {
                json.Add(DescribedPortJson(ports[i]));
            }
            return json;
        }

        private static JArray RequirementsJson(IReadOnlyList<DescribedRequirement> requirements)
        {
            var json = new JArray();
            for (var i = 0; i < requirements.Count; i++)
            {
                json.Add(RequirementJson(requirements[i]));
            }
            return json;
        }

        private static JArray ExamplesJson(IReadOnlyList<OperationExample> examples)
        {
            var json = new JArray();
            for (var i = 0; i < examples.Count; i++)
            {
                json.Add(ExampleJson(examples[i]));
            }
            return json;
        }

        // One described port: the §C1 contract members plus the §B8 JSON
        // Schema (with $defs) the section exists for.
        private static JObject DescribedPortJson(DescribedPort described)
        {
            var port = described.Port;
            var json = new JObject
            {
                ["name"] = port.Name,
                ["type"] = port.Type,
                ["required"] = port.Required,
            };
            if (port.Default is not null)
            {
                json["default"] = port.Default.ToToken();
            }
            json["description"] = port.Description;
            json["constraints"] = ConstraintsJson(port.Constraints);
            json["nullable"] = port.Nullable;
            json["schema"] = described.Schema.ToToken();
            return json;
        }

        private static JToken ConstraintsJson(PortConstraints? constraints)
        {
            if (constraints is null)
            {
                return JValue.CreateNull();
            }
            var json = new JObject
            {
                ["constantOnly"] = constraints.ConstantOnly,
            };
            if (constraints.Enum is { } values)
            {
                json["enum"] = new JArray(values);
            }
            if (constraints.Pattern is { } pattern)
            {
                json["pattern"] = pattern;
            }
            if (constraints.Min is { } min)
            {
                json["min"] = min;
            }
            if (constraints.Max is { } max)
            {
                json["max"] = max;
            }
            if (constraints.MaxLength is { } maxLength)
            {
                json["maxLength"] = maxLength;
            }
            return json;
        }

        private static JObject RequirementJson(DescribedRequirement described)
        {
            var json = new JObject
            {
                ["kind"] = described.Requirement.Kind,
            };
            if (described.Requirement.Port is { } port)
            {
                json["port"] = port;
            }
            json["description"] = described.Description;
            return json;
        }

        private static JObject ExampleJson(OperationExample example)
        {
            var json = new JObject
            {
                ["title"] = example.Title,
                ["workflow"] = example.WorkflowJson.ToToken(),
            };
            if (example.FixtureManifest is not null)
            {
                json["fixtureManifest"] = example.FixtureManifest.ToToken();
            }
            return json;
        }

        private static JObject CompatibleMatchJson(CompatibleOperationMatch match)
        {
            var descriptor = match.Operation.Descriptor;
            return new JObject
            {
                ["operation"] = descriptor.Id,
                ["version"] = descriptor.Version,
                ["port"] = match.Port,
                ["match"] = JsonNaming.ToSnakeCase(match.Match.ToString()),
                ["origin"] = JsonNaming.ToSnakeCase(match.Operation.Origin.ToString()),
            };
        }

        private static JObject TypeEntryJson(TypeDefinition definition)
        {
            var entry = new JObject
            {
                ["id"] = definition.Id,
                ["kind"] = JsonNaming.ToSnakeCase(definition.Kind.ToString()),
                ["description"] = definition.Description,
                ["baseTypes"] = new JArray(definition.BaseTypes),
            };
            if (definition.GenericArity > 0)
            {
                entry["genericArity"] = definition.GenericArity;
            }
            if (definition.GenericConstraint is { } constraint)
            {
                entry["genericConstraint"] = constraint;
            }
            return entry;
        }

        private static JObject WorkflowEntryJson(WorkflowDocument document, string path)
        {
            var entry = new JObject
            {
                ["id"] = document.Id,
                ["path"] = path,
            };
            if (document.Title is { } title)
            {
                entry["title"] = title;
            }
            if (document.Description is { } description)
            {
                entry["description"] = description;
            }
            if (document.Tags.Count != 0)
            {
                entry["tags"] = new JArray(document.Tags);
            }
            return entry;
        }

        private static JObject OriginCountsJson(RegistrySnapshot? snapshot)
        {
            var standard = 0;
            var custom = 0;
            if (snapshot is not null)
            {
                var operations = snapshot.Operations;
                for (var i = 0; i < operations.Count; i++)
                {
                    if (operations[i].Origin == OperationOrigin.Standard)
                    {
                        standard++;
                    }
                    else
                    {
                        custom++;
                    }
                }
            }
            return new JObject
            {
                ["standard"] = standard,
                ["custom"] = custom,
            };
        }

        private static JArray CandidateFailuresJson(RegistrySnapshot? snapshot)
        {
            var failures = new JArray();
            if (snapshot is null)
            {
                return failures;
            }
            var candidates = snapshot.CandidateFailures;
            for (var i = 0; i < candidates.Count; i++)
            {
                var failure = candidates[i];
                var json = new JObject
                {
                    ["kind"] = JsonNaming.ToSnakeCase(failure.Kind.ToString()),
                    ["id"] = failure.CandidateId,
                    ["diagnostics"] = DiagnosticsJson(failure.Diagnostics),
                };
                if (failure.ProviderId is { } providerId)
                {
                    json["providerId"] = providerId;
                }
                if (failure.AssemblyName is { } assemblyName)
                {
                    json["assemblyName"] = assemblyName;
                }
                if (failure.SourceFile is { } sourceFile)
                {
                    json["sourceFile"] = sourceFile;
                }
                failures.Add(json);
            }
            return failures;
        }

        private static JArray TypeFailuresJson(RegistrySnapshot? snapshot)
        {
            var failures = new JArray();
            if (snapshot is null)
            {
                return failures;
            }
            var records = snapshot.TypeFailures;
            for (var i = 0; i < records.Count; i++)
            {
                var failure = records[i];
                var json = new JObject
                {
                    ["kind"] = JsonNaming.ToSnakeCase(failure.Kind.ToString()),
                    ["providerId"] = failure.ProviderId,
                    ["detail"] = failure.Detail,
                };
                if (failure.TypeId is { } typeId)
                {
                    json["typeId"] = typeId;
                }
                if (failure.AssemblyName is { } assemblyName)
                {
                    json["assemblyName"] = assemblyName;
                }
                failures.Add(json);
            }
            return failures;
        }

        private static JArray ExclusionsJson(RegistrySnapshot? snapshot)
        {
            var exclusions = new JArray();
            if (snapshot is null)
            {
                return exclusions;
            }
            var records = snapshot.Exclusions;
            for (var i = 0; i < records.Count; i++)
            {
                var exclusion = records[i];
                var json = new JObject
                {
                    ["kind"] = JsonNaming.ToSnakeCase(exclusion.Kind.ToString()),
                    ["id"] = exclusion.CandidateId,
                };
                if (exclusion.OperationVersion is { } version)
                {
                    json["version"] = version;
                }
                if (exclusion.PackageId is { } packageId)
                {
                    json["packageId"] = packageId;
                }
                if (exclusion.AssemblyName is { } assemblyName)
                {
                    json["assemblyName"] = assemblyName;
                }
                if (exclusion.SourceFile is { } sourceFile)
                {
                    json["sourceFile"] = sourceFile;
                }
                if (exclusion.Reason is { } reason)
                {
                    json["reason"] = reason;
                }
                exclusions.Add(json);
            }
            return exclusions;
        }

        // The §C3 rule-7 diff answer (see the command remarks for the
        // 'since' semantics): the recorded batch when it answers the
        // request, "unavailable" with a fixed English reason otherwise.
        private static JObject DiffJson(
            RegistrySnapshot? snapshot, RegistryDiffResult? diff, string? since)
        {
            if (since is null)
            {
                if (diff is null)
                {
                    return UnavailableDiff(
                        "no registry build has recorded a persistence pass yet");
                }
                var json = new JObject
                {
                    ["previousSnapshot"] = diff.PreviousSnapshot,
                    ["changes"] = ChangesJson(diff.Changes),
                };
                if (diff.UnavailableReason is { } reason)
                {
                    json["unavailableReason"] = reason;
                }
                if (diff.PersistenceError is { } persistenceError)
                {
                    json["persistenceError"] = persistenceError;
                }
                return json;
            }
            if (snapshot is not null && Names(snapshot.GenerationId, snapshot.RegistryHash, since))
            {
                return new JObject
                {
                    ["previousSnapshot"] = "available",
                    ["changes"] = new JArray(),
                };
            }
            if (diff?.Previous is { } previous &&
                Names(previous.GenerationId, previous.RegistryHash, since))
            {
                var json = new JObject
                {
                    ["previousSnapshot"] = "available",
                    ["changes"] = ChangesJson(diff.Changes),
                };
                if (diff.PersistenceError is { } persistenceError)
                {
                    json["persistenceError"] = persistenceError;
                }
                return json;
            }
            return UnavailableDiff(
                "the requested generation is neither the current generation nor " +
                "the persisted previous snapshot; no diff can be proven");
        }

        private static JObject UnavailableDiff(string reason) =>
            new JObject
            {
                ["previousSnapshot"] = "unavailable",
                ["unavailableReason"] = reason,
                ["changes"] = new JArray(),
            };

        private static bool Names(string generationId, string registryHash, string since) =>
            string.Equals(since, generationId, StringComparison.Ordinal) ||
            string.Equals(since, registryHash, StringComparison.Ordinal);

        private static JArray ChangesJson(IReadOnlyList<RegistryChange> changes)
        {
            var json = new JArray();
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                var entry = new JObject
                {
                    ["kind"] = change.Kind,
                    ["operationId"] = change.OperationId,
                    ["version"] = change.Version,
                    ["diagnostics"] = DiagnosticsJson(change.Diagnostics),
                };
                if (change.SourceFile is { } sourceFile)
                {
                    entry["sourceFile"] = sourceFile;
                }
                json.Add(entry);
            }
            return json;
        }

        private static JArray DiagnosticsJson(IReadOnlyList<Diagnostic> diagnostics)
        {
            var json = new JArray();
            for (var i = 0; i < diagnostics.Count; i++)
            {
                json.Add(AgentApiResponse.DiagnosticToJson(diagnostics[i]));
            }
            return json;
        }

        // ── list_workflows matching ──────────────────────────────────────

        private static IReadOnlyList<string> QueryTerms(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Array.Empty<string>();
            }
            return query!.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
        }

        // The §A8 search members: every query term must appear in at
        // least one of id/title/description/tags — OrdinalIgnoreCase so an
        // Agent's ASCII term still finds a Japanese title.
        private static bool MatchesTerms(IReadOnlyList<string> terms, WorkflowDocument document)
        {
            for (var i = 0; i < terms.Count; i++)
            {
                var term = terms[i];
                var found =
                    Contains(document.Id, term) ||
                    (document.Title is { } title && Contains(title, term)) ||
                    (document.Description is { } description && Contains(description, term));
                if (!found)
                {
                    var tags = document.Tags;
                    for (var t = 0; t < tags.Count && !found; t++)
                    {
                        found = Contains(tags[t], term);
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Contains(string text, string term) =>
            text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        // A file that cannot be read or parsed stays listed nowhere, but
        // never silently: one warning per file names the project-relative
        // path — OW-WF-001, the §J "cannot be read as a workflow document"
        // code the unreadable/unparseable cases share.
        private static Diagnostic UnreadableWorkflow(string path) =>
            ErrorCodes.Create(
                ErrorCodes.OW_WF_001,
                "The workflow file '" + path + "' could not be read as an OpWeave workflow document.");
    }
}
