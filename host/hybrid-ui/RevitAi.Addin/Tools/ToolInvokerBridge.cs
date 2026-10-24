using System.Text.Json.Nodes;
using Autodesk.Revit.UI;
using ReVitAI.Bridge;
using RevitAi.Core.Tools;

namespace RevitAi.Addin.Tools;

internal sealed class ToolInvokerBridge : IToolInvoker
{
    private BridgeRuntime? _runtime;
    private IReadOnlyList<ToolDefinition> _definitions = [];
    public IReadOnlyList<ToolDefinition> Tools => _definitions;
    public void Initialize(UIApplication application)
    {
        if (_runtime is not null) return;
        _runtime = new BridgeRuntime();
        _definitions = _runtime.Catalog()["tools"]!.AsArray().Select(node => new ToolDefinition
        {
            Name = node!["name"]!.GetValue<string>(), Description = node["description"]!.GetValue<string>(),
            ToolSet = node["category"]!.GetValue<string>(), InputSchema = node["inputSchema"]!.DeepClone().AsObject(),
            Mutating = node["mutating"]!.GetValue<bool>(), RequiresTransaction = node["requiresTransaction"]!.GetValue<bool>(),
            SupportsDryRun = node["supportsDryRun"]!.GetValue<bool>()
        }).ToArray();
    }
    public async Task<ToolInvocationResult> InvokeAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        if (_runtime is null) return new() { ToolCallId = call.Id, ToolName = call.Name, ErrorCode = "revit_context_not_ready", ErrorMessage = "Open AI Console first." };
        var documentId = call.ExpectedDocumentGuid;
        if (documentId is null)
        {
            var snapshot = await _runtime.InvokeAsync("get_document_snapshot", new(), cancellationToken);
            if (snapshot["success"]?.GetValue<bool>() != true) return Map(snapshot);
            documentId = snapshot["payload"]?["documentGuid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(documentId)) return Map(new() { ["errorCode"] = "document_required", ["errorMessage"] = "No target document." });
        }
        return Map(await _runtime.InvokeAsync(call.Name, call.Arguments, cancellationToken, documentId));
        ToolInvocationResult Map(JsonObject response) => new()
        {
            ToolCallId = call.Id, ToolName = call.Name, Success = response["success"]?.GetValue<bool>() == true,
            Payload = response["payload"] as JsonObject ?? new JsonObject { ["data"] = response["payload"]?.DeepClone() },
            ErrorCode = response["errorCode"]?.GetValue<string>(), ErrorMessage = response["errorMessage"]?.GetValue<string>(),
            Duration = DateTimeOffset.UtcNow - started
        };
    }
}
