# Project Status

BIM-AI Studio is an open-source, local-first Revit automation project.
Project-authored source is licensed under Apache-2.0.

## Supported Environment

- Autodesk Revit 2027
- Windows x64
- .NET 10

Revit 2025 and Revit 2026 compatibility, multi-version side-by-side
installation, and concurrent multi-process coordination are not currently
guaranteed.

## Maintenance Scope

The project is maintained by an individual developer. The maintained scope is
the tool contract layer, Revit host adapter, OpenAI-compatible model client,
WebView2 application shell, build scripts, and documentation.

## Verification

- The toolset build completes against Revit 2027.
- The smoke suite currently reports 355 passed assertions and 0 failures.
- The application compatibility layer compiles against the toolset source.
- The MCP server and Revit bridge compile independently.

## Known Gaps

Automated tests use a simulated Revit adapter for most tool behavior. The
following areas require additional validation in representative projects:

- complex MEP systems and project-specific system families;
- roads, stairs, roofs, topography, and multi-stage modeling workflows;
- CAD attachments, satellite imagery, and large PDF attachments;
- batch family, parameter, schedule, and export operations;
- varying Revit document settings, localization, and template families;
- concurrent Revit processes and long-running agent sessions.

## Safety

Back up projects before model-writing operations. Validate write tools in a
test model first. Do not use generated code or batch mutations on production
models without review and a rollback plan.
