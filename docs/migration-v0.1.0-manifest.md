# v0.1.0 public migration manifest

Source: `pippirium/OpWeave-dev` annotated tag `v0.1.0` → commit `cacb94360f8a86cf65168d44e471282d4658f6b1` (tag object `e5d6ec489cba8ce97d92eef656ff698382e1edc3`). This commit is a descendant of the accepted baseline `8f3e0073af8129d37873c0bfe300d738b0ba5dcf` recorded in `release-lock.json`; the delta is the Apache-2.0 legal metadata PR (#578) which adds the package-level `LICENSE.md` and `Third Party Notices.md` required by the public-distribution gate.

## Included (transferred byte-identically from the source tag)

| Pattern | Files | Notes |
| --- | --- | --- |
| `Packages/com.opweave.workflow/**` | 1800 | Complete UPM package: `Editor/` (8 assemblies), `Tests/`, `Schemas~/`, `Samples~/`, `Editor/Templates~/`, `package.json` + all `.meta`/GUIDs, `README.md`, `LICENSE.md`, `Third Party Notices.md` |
| — package manifest | `Packages/com.opweave.workflow/package.json` | `com.opweave.workflow` `0.1.0`, `unity` `6000.0`; blob `864b7a817ac8b0912b5b3278a8dd6faf69cc9632` matches `release-lock.json` |
| — package tests | `Packages/com.opweave.workflow/Tests/**` | Test assemblies + 9 vendored MIT-licensed plugin DLLs with `THIRD_PARTY_NOTICES.txt` + `Licenses/` texts (distributed per release policy) |

## Kept from the public repository (already present, verified content-identical to source)

| File | Status |
| --- | --- |
| `LICENSE`, `NOTICE`, `LICENSING.md` | Owner-approved Apache-2.0 files (public PR #3); content-identical to source tag (line-ending style differs — CRLF vs LF — content equal) |
| `VERSION` | `0.1.0` — content-identical to source tag |
| `release-lock.json` | Owner-approved provenance lock (public PR #1); content-identical to source tag |

## Rewritten for publication (intentional difference)

| File | Effect |
| --- | --- |
| `README.md`, `README.ja.md` | Replaced planning-stage status text with implemented-product documentation: capabilities, requirements, UPM install URL, usage, package structure, license, known limitations |

## Added for the public repository (not from the source tag)

| File | Effect |
| --- | --- |
| `.gitignore` | Standard Unity/IDE ignore rules for anyone opening this repository inside a Unity project. No effect on package content. |
| `docs/migration-v0.1.0-manifest.md` | This manifest. Documentation only. |

## Excluded (development-only infrastructure, not part of the product)

| Source path | Reason |
| --- | --- |
| `.agents/`, `.claude/`, `.devin/`, `AGENTS.md`, `CLAUDE.md`, `orca.yaml` | Autonomous development orchestration/agent configuration |
| `tools/`, `tests/`, `scripts/`, `dotnet/` | Development coordinator, host-side test runner and internal tooling |
| `workspace/` | Internal ticket DAG, plans and session records |
| `TestProject/` | Development Unity test host project |
| `docs/` (all other files) | Internal specs, decisions, planning and verification records (normative development history, not product documentation) |
| `.github/` | Development CI/check workflows (references development-only tooling) |
| `.editorconfig`, `.gitattributes` | Development checkout hygiene; the package carries no attribute-dependent content |
| `docs/releases/v0.1.0.md` | Source freeze decision record — lives in the development repository; its content is reflected here and in Issue #2 |

## Correspondence claim

`git rev-parse <public-commit>:Packages/com.opweave.workflow` must equal the source subtree `9d650967ab17cf94407158d8a254b656d868767c` — byte-for-byte identical file contents, names and modes, including every `.meta` GUID. Any mismatch is a release blocker.

## Accepted residuals inside the byte-identical payload

- **Internal ticket IDs in code comments** (`T-W…`, `SPEC §…`): development provenance comments shipped verbatim inside the accepted product source. They reference the private development process but expose no secrets; the private repo name is already public via `release-lock.json`.
- **Dev-repo URL in two test files** (`Tests/Editor/GitUpmResolutionTests.cs`, `GitUpmResolutionEntry.cs`): the package's own UPM-resolution test fixtures name `github.com/pippirium/OpWeave-dev.git`. Test-only content; not executed by consumers; no credentials.

Neither residual is product-visible at runtime. Removing them would break the byte-identical correspondence gate.
