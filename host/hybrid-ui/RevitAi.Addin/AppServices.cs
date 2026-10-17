using Autodesk.Revit.UI;
using RevitAi.Revit;
using RevitAi.Revit.UI;
using System.IO;
using System.Text.Json.Nodes;
using RevitAi.Core.Tools;
using RevitAi.Addin.Services;
using RevitAi.Addin.Tools;
using RevitAi.Addin.UI;

namespace RevitAi.Addin;

internal static class AppServices
{
    public static readonly DockablePaneId PanelId =
        new(new Guid("a69e1f39-987d-4bf5-a55a-9a875d2c74dc"));

    public static UIApplication? UiApplication { get; private set; }

    public static RevitAdapter Adapter { get; private set; } = null!;

    public static IToolInvoker ToolRegistry { get; private set; } = null!;

    public static AgentRuntimeService Agent { get; private set; } = null!;

    public static AiPanelPage Panel { get; private set; } = null!;

    public static BridgePipeServer PipeServer { get; private set; } = null!;

    private static RuntimeBootstrap? _runtimeBootstrap;

    public static void Initialize(UIControlledApplication application)
    {
        _runtimeBootstrap = new RuntimeBootstrap();
        _runtimeBootstrap.InitializeBeforeAdapter();

        Adapter = RevitAdapterManager.CurrentAdapter as RevitAdapter
            ?? throw new InvalidOperationException(
                "RevitAi 4.1.0 adapter is not ready. The RevitAi loader must start first.");
        _runtimeBootstrap.AttachAdapter(Adapter);

        var externalEvent = Adapter.GetAIToolExternalEvent();
        LogStartup($"AI ExternalEvent: {(externalEvent is null ? "NULL" : "READY")}");

        ToolRegistry = new ToolInvokerBridge(Adapter);
        _runtimeBootstrap.RegisterLocalTools(Adapter);
        Agent = new AgentRuntimeService(ToolRegistry);
        Panel = new AiPanelPage();
        PipeServer = new BridgePipeServer(ToolRegistry, Agent);
    }

    public static void SetUiApplication(UIApplication uiApplication)
    {
        UiApplication = uiApplication;
    }

    public static void InitializeAdapter(ExternalCommandData commandData)
    {
        SetUiApplication(commandData.Application);
    }

    private static void LogStartup(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitAi");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void PostAgentStatus()
    {
        Panel?.Post(new JsonObject
        {
            ["type"] = "host.status",
            ["agent"] = Agent.GetStatus()
        });
    }

    public static void Dispose()
    {
        Panel?.Dispose();
        PipeServer?.Dispose();
        Agent?.Dispose();
        _runtimeBootstrap?.Dispose();
    }
}

