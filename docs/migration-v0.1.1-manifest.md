# v0.1.1 public migration manifest

Source: `pippirium/OpWeave-dev` annotated tag `v0.1.1` → commit `f53a00f77e1c6f38df618d4590ed0e20f3174f42` (tag object `5b7b272329eb84d72154a46642d517d14066a59d`). This is the canonical product source recorded by the finalized release handoff. The G-09 verified candidate was `b6d31dbee55970d228c4a7d732e5e79a321c6afc` (tree `469dc5ac0dd08b6c95457fe77c66e8d45e7714bf`); the publishable release-preparation merge is `f53a00f77e1c6f38df618d4590ed0e20f3174f42` (tree `a49c13842c075e144264b1314b37c84899f302c5`).

## Included (transferred byte-identically from the source tag)

| Pattern | Files | Notes |
| --- | --- | --- |
| `Packages/com.opweave.workflow/**` | 1808 | Complete UPM package: `Editor/` assemblies, `Tests/`, `Schemas~/`, `Samples~/`, `Editor/Templates~/`, `package.json` + all `.meta`/GUIDs, `README.md`, `LICENSE.md`, `Third Party Notices.md` |
| — package manifest | `Packages/com.opweave.workflow/package.json` | `com.opweave.workflow` `0.1.1`, `unity` `6000.0`; blob `2c7edb38cdd131018fa4f855d167f118ab9ead83` matches `release-lock.json` |
| — package tests | `Packages/com.opweave.workflow/Tests/**` | Test assemblies + vendored MIT-licensed plugin DLLs with `THIRD_PARTY_NOTICES.txt` + `Licenses/` texts (distributed per release policy) |

## Release-lock reconciliation

The `v0.1.1` development tag itself carries the first provenance ledger written during release preparation. That ledger incorrectly kept `source_commit` and `approved_merge_result_tree` pointed at the tested candidate revision even though the publishable 0.1.1 package manifest exists only in the release-preparation merge.

Development PR #640 (`d75cbe84b8f4947ca45d09efeedcc559da4401c4`) reviewed and merged the correction before publication. The public `release-lock.json` therefore uses the finalized authoritative ledger now present on development `main`:

- `source_commit`: `f53a00f77e1c6f38df618d4590ed0e20f3174f42`
- `verified_release_candidate_commit`: `b6d31dbee55970d228c4a7d732e5e79a321c6afc`
- `verified_release_candidate_tree`: `469dc5ac0dd08b6c95457fe77c66e8d45e7714bf`
- `approved_merge_result_tree`: `a49c13842c075e144264b1314b37c84899f302c5`
- `source_package_manifest_blob_sha`: `2c7edb38cdd131018fa4f855d167f118ab9ead83`
- acceptance evidence: `https://github.com/pippirium/OpWeave-dev/issues/584#issuecomment-7127776750`

This reconciles the stale tag-local metadata instead of blindly copying it. It does not change the product package payload, which is still transferred from the immutable annotated `v0.1.1` tag.

## Kept from the public repository (already present)

| File | Status |
| --- | --- |
| `LICENSE`, `NOTICE`, `LICENSING.md` | Owner-approved Apache-2.0 files; unchanged by this transfer |
| `.gitignore` | Public repository checkout hygiene; unchanged by this transfer |
| `docs/migration-v0.1.0-manifest.md` | Historical v0.1.0 migration record; unchanged by this transfer |

## Rewritten for publication (intentional difference)

| File | Effect |
| --- | --- |
| `README.md`, `README.ja.md` | Updated public product documentation for v0.1.1: installation, upgrade and old-version URLs, capabilities, supported environment boundary, standard operations, agent integration, extension model, recovery, limitations, version history, and the real 0.1.1 delta |
| `VERSION` | Updated `0.1.0` → `0.1.1` |
| `release-lock.json` | Updated to the corrected authoritative v0.1.1 provenance ledger described above |

## Added for the public repository (not from the source tag)

| File | Effect |
| --- | --- |
| `docs/migration-v0.1.1-manifest.md` | This manifest. Documentation only. |

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
| `docs/releases/v0.1.1.md`, `docs/verification/v0.1.1-release-handoff.md` | Source release decision and handoff records — live in the development repository; their public-safe substance is reflected here, in the README files, and in the GitHub Release |

No development-repository history was merged into the public repository, and the private development repository was not added as a Git dependency or submodule.

## Correspondence claim

`git rev-parse <public-release-commit>:Packages/com.opweave.workflow` must equal the source package subtree `482f746fc660d60697d04bacf176ed5001113c66` — byte-for-byte identical file contents, names and modes, including every `.meta` GUID. Any mismatch is a release blocker.

## Accepted residuals inside the byte-identical payload

- **Internal ticket IDs in code comments** (`T-W…`, `SPEC §…`): development provenance comments shipped verbatim inside the accepted product source. They reference the private development process but expose no secrets; the private repository name is already public via `release-lock.json`.
- **Product type names containing “Coordinator”** (for example run/JSON-save coordination): product implementation vocabulary, not the excluded autonomous-development coordinator infrastructure.
- **Dev-repo URL in two test files** (`Tests/Editor/GitUpmResolutionTests.cs`, `GitUpmResolutionEntry.cs`): the package's own UPM-resolution test fixtures name `github.com/pippirium/OpWeave-dev.git`. Test-only content; not executed by consumers; no credentials.

Neither residual is product-visible at runtime. Removing them would break the byte-identical correspondence gate.

## v0.1.1 product delta represented here

- `.opweavegraph` creation from the Unity Project Browser through **Assets > Create > OpWeave > Graph**.
- Runner active-lock handoff correction.
- Preflight pending-input handling corrections.
- Stability, regression-test, and acceptance improvements.

The authoritative source delta is the finalized development release record `docs/releases/v0.1.1.md`; the public summary is in `README.md`, `README.ja.md`, and the GitHub Release notes.
