using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using RevitAi.Addin.Commands;
using RevitAi.CodexBridge;

namespace RevitAi.Addin;

public sealed class RevitAiApplication : IExternalApplication
{
    private UIControlledApplication? _application;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            _application = application;
            AppServices.Initialize(application);

            application.RegisterDockablePane(
                AppServices.PanelId,
                "Revit AI",
                AppServices.Panel);

            CreateRibbon(application);
            AppServices.PipeServer.Start();
            CodexBridgeApplication.Start(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Revit AI startup error", ex.ToString());
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        CodexBridgeApplication.Stop();
        AppServices.Dispose();
        _application = null;
        return Result.Succeeded;
    }

    private static void CreateRibbon(UIControlledApplication application)
    {
        var panel = application.CreateRibbonPanel(Tab.AddIns, "Revit AI");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var buttonData = new PushButtonData(
            "RevitAi.ShowConsole",
            "AI Console",
            assemblyPath,
            typeof(ShowAiConsoleCommand).FullName)
        {
            ToolTip = "Open the local Revit AI execution console.",
            LongDescription = "Chat with an external model API and execute typed Revit tools."
        };

        panel.AddItem(buttonData);
    }
}
