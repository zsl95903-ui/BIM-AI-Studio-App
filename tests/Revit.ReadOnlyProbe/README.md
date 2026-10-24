# Optional native read-only probe

Build on a machine with Revit 2027 API assemblies:

```powershell
dotnet build tests/Revit.ReadOnlyProbe -c Release
```

This project is not installed automatically. For an explicitly approved manual test, place `probe-settings.json` beside the built DLL with absolute `ExpectedDocumentPath`, `BridgeDirectory` and `OutputPath` values. BridgeDirectory must contain the candidate Bridge and tool dependencies. Register the public `BimAi.ReadOnlyProbe.Application` as an application add-in with a separate AddInId; preserve existing add-ins.

The probe runs once after that exact document becomes active. It calls only get_all_levels, get_all_grids, get_all_views and get_active_view through the candidate dispatcher in a separate collectible assembly context. It writes JSON evidence to OutputPath. It never opens a transaction, modifies the model, changes views, saves or closes documents. Remove the temporary manifest after testing.

This checks native tool implementation results. It does not exercise the transport, ExternalEvent queue, complete WebView2 UI, cancellation or model writes. Review errors in the JSON rather than treating output-file presence as success.
