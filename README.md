# OpWeave

[日本語](README.ja.md)

OpWeave is a planned workflow tool for discovering, connecting, and executing typed operations in the Unity Editor. It aims to let people and AI agents compose repeatable editing tasks while validating their inputs, execution order, and completion conditions.

## Status

**OpWeave is in the planning and early development stage.** There is no released package or supported installation procedure yet. The features described below are goals, not implemented functionality.

## Planned capabilities

- Search an operation catalog by type, compatibility, and side effects.
- Build workflows with a visual node editor and separate control and data connections.
- Validate workflows and execute operations sequentially, waiting for their declared completion conditions.
- Track execution state and errors, with operation-specific information about Undo and reruns.
- Provide an interface for AI agents to discover operations and validate workflows.

The initial scope is Unity Editor editing operations. Runtime visual scripting, other applications, and unrestricted execution of arbitrary C# are outside the initial scope.

## Releases and development

This repository is intended for release updates. No releases are available yet. Development and design work take place in [OpWeave-dev](https://github.com/pippirium/OpWeave-dev).

Supported Unity versions, installation instructions, distribution details, and licensing have not yet been decided.
