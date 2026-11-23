# Third-party notices — OpWeave Unity package

OpWeave-authored code is licensed under Apache License 2.0 (see `LICENSE.md`). The third-party components listed below are **not** relicensed under Apache-2.0.

## Vendored binary test dependencies (MIT)

The following nine DLLs under `Tests/Plugins/` are included **only for package testing** and remain subject to MIT. Their exact package versions, copyright owners, binary SHA-256 hashes, original license texts and additional transitive notices are preserved in:

- [Tests/Plugins/THIRD_PARTY_NOTICES.txt](Tests/Plugins/THIRD_PARTY_NOTICES.txt)
- [Tests/Plugins/Licenses/](Tests/Plugins/Licenses/)

| File | Original NuGet package | License |
| --- | --- | --- |
| Humanizer.dll | Humanizer.Core 2.14.1 | MIT |
| Json.More.dll | Json.More.Net 2.1.1 | MIT |
| JsonPointer.Net.dll | JsonPointer.Net 5.3.1 | MIT |
| JsonSchema.Net.dll | JsonSchema.Net 7.4.0 | MIT |
| Microsoft.Bcl.AsyncInterfaces.dll | Microsoft.Bcl.AsyncInterfaces 9.0.0 | MIT |
| System.IO.Pipelines.dll | System.IO.Pipelines 9.0.0 | MIT |
| System.Runtime.CompilerServices.Unsafe.dll | System.Runtime.CompilerServices.Unsafe 6.1.2 | MIT |
| System.Text.Encodings.Web.dll | System.Text.Encodings.Web 9.0.0 | MIT |
| System.Text.Json.dll | System.Text.Json 9.0.0 | MIT |

## UPM dependencies

The Unity package manifest depends on Unity's `com.unity.nuget.newtonsoft-json` and `com.unity.nuget.mono-cecil` packages. Their licenses are provided by their respective licensors and are **not** replaced by this project's Apache-2.0 license.

Any future public package distribution must preserve the corresponding files and verify licensing for all bundled code, assets and dependencies. Do not remove copyright or license notices during packaging.
