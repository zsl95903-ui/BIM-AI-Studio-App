# Third-Party Notices

## Project license

Project-authored application source is licensed under Apache-2.0. The `toolset`
source referenced through the Git submodule is separately licensed under
Apache-2.0 by the BIM-AI-Studio repository.

## NuGet dependencies

The application uses the following NuGet packages. Each package remains under
its own license and is restored from NuGet rather than committed to this
repository.

- `PdfPig` 0.1.16, used for PDF text extraction.
- `PDFtoImage` 5.4.0, used by the PDF Worker for page rendering.
- `Microsoft.Web.WebView2` 1.0.2903.40, used for the embedded UI.

The authoritative license text for each package is included in the package or
available from its NuGet package page.

## Tool-layer dependencies

The referenced `toolset` source uses `ACadSharp` 3.4.9 under the MIT license.
See the BIM-AI-Studio repository's `THIRD_PARTY_NOTICES.md`.

## Autodesk Revit API

`RevitAPI.dll` and `RevitAPIUI.dll` are proprietary Autodesk components
provided by a local Revit installation. They are not distributed by this
repository.
