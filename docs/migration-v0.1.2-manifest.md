# OpWeave v0.1.2 public package migration manifest

Transfer source: immutable OpWeave-dev annotated tag `v0.1.2` (tag object `6fa4123cbd70fdebeabe0074b6d796709faa85c1`), dereferencing to canonical commit `5dcaa944e12b0bdb01bbabf1855f9e5364cf17f3`.

## Product payload correspondence

- Included: the complete `Packages/com.opweave.workflow/**` product payload — 1,808 files — transferred byte-identically from the source tag.
- Preserved: core assemblies, Unity adapters, operations, Agent API, Pipeline integration, Graph Toolkit integration, Run Viewer, assembly definitions, schemas, samples, templates, package tests, dependencies, notices, license files, `.meta` files and Unity GUIDs.
- Required source package subtree: `7b61d5db082dfd65a615ea19f1bf6cf618bc0b24`.
- Required package manifest blob: `4d98c63599fe230082d2dd3f9a6ac4c576ed207a`, version `0.1.2`.
- Exact Git tree equality is the parity gate; filesystem comparison alone is not sufficient on Windows because of line-ending conversion.

## Provenance ledger reconciliation

The immutable tag-local `release-lock.json` cannot name its own canonical commit. It still records tested candidate `d39d4c8521a1c647928624cfbc091ef3de5ea25d` in `source_commit` and carries a note explaining that the canonical value must be completed post-tag.

The public ledger therefore uses the corrected post-tag provenance-completion content from OpWeave-dev `docs/v012-provenance-completion` head `c902f49ccfb9d1c3cfb90a2fb5912e08cb5bea54` (PR #661 at preparation time), rather than blindly copying the stale tag-local value. The package payload still comes only from immutable tag `v0.1.2`.

Corrected authoritative values:

- `source_commit`: `5dcaa944e12b0bdb01bbabf1855f9e5364cf17f3`
- `canonical_release_tree`: `638ce30cb5c8697965d0f0ff5c55349ca6edf6ff`
- verified candidate: `d39d4c8521a1c647928624cfbc091ef3de5ea25d`
- verified candidate tree: `8bda13a54c9562fa6bcf1ea44c79b51d7e2fc314`
- integration merge: `20b1163400c4130b3f27e9a4e2bc0e278ea55eb9`
- integration merge tree: `1b2f48d2873f779d7754a50a3b64b2f2db4d1296`
- evidence: OpWeave-dev Issue #584 comments `7152716024` and `7154082753`
- target platform Windows 11 x64; verified Unity `6000.5.11f1`; compatibility verification Unity `6000.0.85f1`; Apache-2.0

## Public metadata outside the package

Changed outside `Packages/com.opweave.workflow/`:

- `VERSION` → `0.1.2`
- corrected `release-lock.json`
- public English/Japanese README installation, version-history, support and release-delta documentation
- this migration manifest

Root `LICENSE`, `NOTICE`, and `LICENSING.md` are unchanged. The package license and third-party notices remain part of the byte-identical product payload.

## Excluded development-only material

Not transferred: coordinator/orchestrator infrastructure, autonomous development systems, agent configuration, `.agents/`, `.claude/`, `.devin/`, `.github/`, `AGENTS.md`, `CLAUDE.md`, `orca.yaml`, `tools/`, host-side `tests/`, `scripts/`, `dotnet/`, `workspace/`, `TestProject/`, internal docs/plans/review prompts/logs, machine-specific configuration, credentials, tokens, private environment files and development-only CI workflows.

No development-repository history was merged. The private development repository is not a public submodule or runtime dependency.

## Accepted byte-identical residuals

As in v0.1.0/v0.1.1, the exact package payload retains upstream product/test provenance comments, including internal ticket/SPEC references, synthetic credential security fixtures, product type names containing `Coordinator`, and test-only references to the private source repository in the UPM-resolution fixtures. These are not credentials and do not grant access. Removing or editing them would break exact package parity.

The package-local README also contains historical development wording inherited byte-identically from the immutable source package. The root English and Japanese READMEs are the current public user documentation.
