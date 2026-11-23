# OpWeave

[日本語](README.ja.md)

OpWeave is a Unity Editor workflow tool for discovering, connecting, and executing typed operations. Windows v1 has completed development acceptance; public package distribution is being prepared.

## Status

**Windows v1 development is complete and verified, but this public repository does not yet contain the distributable Unity package.** The agreed version is **0.1.0**, recorded in [VERSION](VERSION) and [release-lock.json](release-lock.json). No public release tag or supported installation procedure exists yet.

## Windows v1 capabilities (verified development build)

- Search an operation catalog by type, compatibility, and side effects.
- Build workflows with a visual node editor and separate control and data connections.
- Validate workflows and execute operations sequentially, waiting for their declared completion conditions.
- Track execution state and errors, with operation-specific information about Undo and reruns.
- Provide an interface for AI agents to discover operations and validate workflows.

The initial scope is Unity Editor editing operations. Runtime visual scripting, other applications, and unrestricted execution of arbitrary C# are outside the initial scope.

## Releases and development

This repository is the intended public distribution destination. The Windows v1 development baseline is version **0.1.0** (`com.opweave.workflow`), mapped to the verified source commit in [release-lock.json](release-lock.json). The development repository is [OpWeave-dev](https://github.com/pippirium/OpWeave-dev) (private; not available to all visitors).

**Verified development target:** Windows 11 x64, Unity 6000.5.11f1. The public package, installation URL, release tag `v0.1.0`, and distribution/license notices are **not published yet**. The version record is not a release announcement; publication requires an audited transfer of the product package and a separate release decision.
