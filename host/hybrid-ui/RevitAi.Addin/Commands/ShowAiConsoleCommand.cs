using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAi.Addin.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ShowAiConsoleCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        try
        {
            AppServices.InitializeAdapter(commandData);
            var pane = commandData.Application.GetDockablePane(AppServices.PanelId);
            pane.Show();
            AppServices.PostAgentStatus();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.GetBaseException().Message;
            return Result.Failed;
        }
    }
}
