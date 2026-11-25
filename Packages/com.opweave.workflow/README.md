# OpWeave (`com.opweave.workflow`)

UPM package skeleton per SPEC §0.5. Folder layout and the assembly reference
table are normative there; this file only records implementation decisions made
by ticket T-W1.1-01.

## Mono.Cecil for `OpWeave.Testing`

`OpWeave.Testing` (SPEC §0.5, test-only, `defineConstraints:
["UNITY_INCLUDE_TESTS"]`) must reference NUnit and Mono.Cecil while no
production assembly may. This is enforced at two levels:

- `com.unity.nuget.mono-cecil` is declared in `package.json` `dependencies`, so
  the DLL is present whenever the package is installed — including consumer
  projects that add `com.opweave.workflow` to their manifest `testables`.
  (`nunit.framework.dll` comes from `com.unity.test-framework`, which any
  project running tests already has.)
- Every asmdef sets `"overrideReferences": true`, so precompiled plugin DLLs
  are referenced **only** via explicit `precompiledReferences`:
  `OpWeave.Core` lists `Newtonsoft.Json.dll` only; `OpWeave.Testing` lists
  `nunit.framework.dll` + `Mono.Cecil.dll`; the test assemblies list
  `nunit.framework.dll`; all other production assemblies list none. No
  production assembly references NUnit, Mono.Cecil or `OpWeave.Testing`.

## Empty-folder placeholders

Each `Editor/<Area>/` folder contains a single `internal static` marker type so
the asmdef produces a compiled assembly before real code lands. The
Unity-ignored folders `Editor/Templates~/`, `Schemas~/` and `Samples~/` carry
`.gitkeep` files so Git retains the layout required by SPEC §0.5 (no `.meta`
files are needed there since Unity never imports `~`-suffixed folders).

## Creating a graph asset

On Unity 6000.5+, use **Assets > Create > OpWeave > Graph** (also in the
Project window's right-click Create menu) to create a `*.opweavegraph` in
the selected folder; Unity shows the standard inline rename. Double-click
the asset to open it in the Graph Toolkit editor and save it with Ctrl+S
(SPEC §H6 item 6). A new graph is not linked to any `.opweave.json`:
creation, rename and Ctrl+S never write JSON.

## Saving workflow JSON

The `.opweave.json` document an `.opweavegraph` is linked to is written only by
the explicit **OpWeave > Save Graph JSON** command (Assets and Tools menus, plus
a user-bindable shortcut with no default chord). Graph Toolkit's own Save —
Ctrl+S and the window Save — saves only the `.opweavegraph` asset and never
writes JSON (SPEC §H7 item 3).

Before publishing, the command records a durable intent record keyed by the
graph asset GUID so an interrupted save reconciles instead of silently
overwriting. A save refused because it names an intent record needs a person to
inspect and remove that record; no agent API supersedes or deletes it.

Residual risks, by design: on Windows the durability of a rename across power
loss stays best-effort (Windows offers no directory-entry fsync), and a graph
deleted and re-created at the same path behind a stale intent record stays
refused until a person removes the named record.

## License and third-party notices

OpWeave-authored package code is licensed under the [Apache License, Version 2.0](LICENSE.md) (`Apache-2.0`). Third-party binaries and package dependencies retain their own terms. See [Third Party Notices.md](Third%20Party%20Notices.md), the test-only plugin inventory in `Tests/Plugins/THIRD_PARTY_NOTICES.txt`, and the accompanying `Tests/Plugins/Licenses/` texts. This is a legal-metadata addition to the accepted 0.1.0 code baseline; not a new product implementation or public release.
