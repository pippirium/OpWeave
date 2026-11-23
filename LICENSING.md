# Licensing policy — OpWeave

**Project license:** [Apache License, Version 2.0](LICENSE) (`Apache-2.0`).

Original OpWeave-authored source code and documentation made available in this repository are licensed under Apache-2.0 unless the specific material explicitly carries different terms. The authoritative license text is the repository root `LICENSE`; the Unity package includes its own `Packages/com.opweave.workflow/LICENSE.md` when distributed. See `NOTICE` for project attribution.

## Third-party and Unity content

- **Not relicensed:** external dependencies, vendored binaries, examples/assets owned by others, Unity Editor/Engine and Unity-distributed components. These remain subject to their respective licenses.
- The nine vendored, test-only DLLs in the development package are recorded as MIT-licensed in `Packages/com.opweave.workflow/Tests/Plugins/THIRD_PARTY_NOTICES.txt` with individual notices and bundled license texts under `Tests/Plugins/Licenses/`. Those notices and their original copyright statements must accompany any distribution of those DLLs.
- The Unity package declares `com.unity.nuget.newtonsoft-json` and `com.unity.nuget.mono-cecil` as dependencies. Declaring package dependencies does not relicense them.
- The public package transfer must audit every redistributed binary, asset, sample and dependency for license compatibility and required notices; do not assume all third-party content is Apache-2.0.

## Development and publication

- The private `pippirium/OpWeave-dev` repository contains internal development tooling and release evidence that are **not** included in the intended public `pippirium/OpWeave` product distribution. Repository licensing does not itself publish these private contents.
- Windows v1 package version **0.1.0** is frozen in `VERSION` and `release-lock.json`. The addition of legal metadata is **not** a product implementation change or a new G-09 test run.
- Public package distribution, the `v0.1.0` Git tag, and GitHub Release are separate steps. At the time of adoption, the public repository does not yet contain the distributable package.
- Future contributors must only submit content they have rights to license under the declared project terms. Keep prior third-party copyright and attribution notices intact.

This document summarizes scope; if it conflicts with the full Apache-2.0 terms in `LICENSE`, the full license text governs.
