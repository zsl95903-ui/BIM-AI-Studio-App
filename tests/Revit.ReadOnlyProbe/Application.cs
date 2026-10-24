using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BimAi.ReadOnlyProbe;

// Optional native test entry point. Never installed by build or package scripts.
// No transactions, document writes, saves, selection changes or model activation.
public sealed class Application : IExternalApplication
{
    private Settings? _settings;
    private UIControlledApplication? _host;
    private bool _finished;

    public Result OnStartup(UIControlledApplication application)
    {
        var directory = Path.GetDirectoryName(typeof(Application).Assembly.Location)!;
        var config = Path.Combine(directory, "probe-settings.json");
        if (!File.Exists(config)) return Result.Cancelled;
        _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(config));
        if (_settings is null || !Path.IsPathFullyQualified(_settings.ExpectedDocumentPath)
            || !Path.IsPathFullyQualified(_settings.BridgeDirectory)
            || !Path.IsPathFullyQualified(_settings.OutputPath)) return Result.Cancelled;
        _host = application;
        application.Idling += OnIdling;
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.Idling -= OnIdling;
        return Result.Succeeded;
    }

    private void OnIdling(object? sender, IdlingEventArgs args)
    {
        if (_finished || _settings is null || sender is not UIApplication app
            || app.ActiveUIDocument?.Document is not { } doc) return;
        if (!string.Equals(Path.GetFullPath(doc.PathName), Path.GetFullPath(_settings.ExpectedDocumentPath), StringComparison.OrdinalIgnoreCase)) return;
        _finished = true;
        _host!.Idling -= OnIdling;
        var results = new Dictionary<string, object?>();
        var context = new CandidateContext(_settings.BridgeDirectory);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(_settings.BridgeDirectory, "ReVitAI.Bridge.dll"));
            var type = assembly.GetType("ReVitAI.Bridge.ToolDispatcher", true)!;
            var dispatcher = Activator.CreateInstance(type, true)!;
            var commandType = assembly.GetType("ReVitAI.Bridge.PendingCommand", true)!;
            var execute = type.GetMethod("ExecuteOnRevitThread")!;
            foreach (var name in new[] { "get_all_levels", "get_all_grids", "get_all_views", "get_active_view" })
            {
                try
                {
                    var command = Activator.CreateInstance(commandType, true)!;
                    commandType.GetProperty("Tool")!.SetValue(command, name);
                    using var json = JsonDocument.Parse("{}");
                    commandType.GetProperty("Arguments")!.SetValue(command, json.RootElement.Clone());
                    var result = execute.Invoke(dispatcher, [app, command]);
                    results[name] = JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object));
                }
                catch (Exception ex) { results[name] = new { error = ex.GetBaseException().Message }; }
            }
        }
        catch (Exception ex) { results["startupError"] = ex.GetBaseException().Message; }
        finally { context.Unload(); }
        try
        {
            var output = new { document = doc.Title, documentGuid = doc.CreationGUID, results };
            File.WriteAllText(_settings.OutputPath, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { TaskDialog.Show("Read-only probe", ex.GetBaseException().Message); }
    }

    private sealed class CandidateContext(string directory) : AssemblyLoadContext("BimReadOnlyProbe", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("RevitAi.Engine.", StringComparison.Ordinal) == true
                || name.Name?.StartsWith("ExtToolLib", StringComparison.Ordinal) == true)
                return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
            return Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        }
    }

    private sealed class Settings
    {
        public string ExpectedDocumentPath { get; set; } = "";
        public string BridgeDirectory { get; set; } = "";
        public string OutputPath { get; set; } = "";
    }
}
