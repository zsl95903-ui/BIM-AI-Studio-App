using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitAi.Abstractions.AI;
using RevitAi.Abstractions.Adapters;
using RevitAi.Core.AI;
using RevitAi.Revit.AI;
using RevitAi.Revit.UI;

namespace RevitAi.CodexBridge;

public sealed class CodexBridgeApplication : IExternalApplication
{
    private static ExternalEvent _externalEvent;
    private static CodexPipeServer _server;
    private static string _discoveryPath;
    private static string _pidDiscoveryPath;
    private static bool _started;

    public Result OnStartup(UIControlledApplication application)
    {
        return Start(application);
    }

    public static Result Start(UIControlledApplication application)
    {
        if (_started)
        {
            return Result.Succeeded;
        }

        try
        {
            InstallAssemblyResolver();
            _externalEvent = ExternalEvent.Create(new AIToolExternalEventHandler());
            _server = new CodexPipeServer(_externalEvent);
            _server.Start();

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitAi");
            Directory.CreateDirectory(root);
            _discoveryPath = Path.Combine(root, "codex-bridge.json");
            _pidDiscoveryPath = Path.Combine(root, $"codex-bridge.{Environment.ProcessId}.json");
            WriteDiscovery();
            CodexBridgeLog("Codex bridge started.");
            _started = true;
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            CodexBridgeLog("Codex bridge startup failed: " + ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Stop();
    }

    public static Result Stop()
    {
        try
        {
            _server?.Stop();
            _externalEvent?.Dispose();
            DeleteIfExists(_discoveryPath);
            DeleteIfExists(_pidDiscoveryPath);
            CodexBridgeLog("Codex bridge stopped.");
            _started = false;
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            CodexBridgeLog("Codex bridge shutdown failed: " + ex);
            return Result.Failed;
        }
    }

    private static void WriteDiscovery()
    {
        var payload = new
        {
            service = "revitai-codex-bridge",
            pipeName = CodexPipeServer.PipeName,
            processId = Environment.ProcessId,
            startedAt = DateTimeOffset.Now.ToString("O")
        };
        var json = JsonConvert.SerializeObject(payload);
        File.WriteAllText(_discoveryPath, json, new UTF8Encoding(false));
        File.WriteAllText(_pidDiscoveryPath, json, new UTF8Encoding(false));
    }

    private static void DeleteIfExists(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static void CodexBridgeLog(string message)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitAi");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "codex-bridge.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static void InstallAssemblyResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var adapterRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "RevitAi",
            "app",
            "R2027");
        var candidate = Path.Combine(adapterRoot, name + ".dll");
        return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
    }
}

internal sealed class CodexPipeServer
{
    internal static string PipeName { get; } = "RevitAi.CodexBridge." + Environment.ProcessId;

    private readonly ExternalEvent _externalEvent;

    internal CodexPipeServer(ExternalEvent externalEvent)
    {
        _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
    }

    internal void Start()
    {
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "RevitAi.CodexBridge"
        };
        thread.Start();
    }

    internal void Stop()
    {
        try
        {
            using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                client.Connect(250);
            }
        }
        catch
        {
        }
    }

    private void Run()
    {
        while (true)
        {
            try
            {
                using (var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    8,
                    PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly))
                {
                    pipe.WaitForConnection();
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, true))
                    {
                        writer.AutoFlush = true;
                        var line = reader.ReadLine();
                        var response = Handle(line);
                        writer.WriteLine(response);
                    }
                }
            }
            catch (Exception ex)
            {
                CodexBridgeApplication.CodexBridgeLog("Pipe loop error: " + ex);
            }
        }
    }

    private string Handle(string requestJson)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = string.IsNullOrWhiteSpace(requestJson)
                ? new JObject()
                : JObject.Parse(requestJson);
            var type = (string)request["type"] ?? "invoke";
            var requestId = (string)request["requestId"];

            if (string.Equals(type, "ping", StringComparison.OrdinalIgnoreCase))
            {
                stopwatch.Stop();
                return JsonConvert.SerializeObject(new
                {
                    type = "pong",
                    requestId,
                    success = true,
                    payload = new
                    {
                        processId = Environment.ProcessId,
                        pipeName = PipeName,
                        toolCount = GetTools().Count
                    },
                    durationMs = stopwatch.Elapsed.TotalMilliseconds
                }, SerializerSettings);
            }

            if (string.Equals(type, "tools.list", StringComparison.OrdinalIgnoreCase))
            {
                var tools = GetTools().Select(tool =>
                {
                    var attribute = GetToolAttribute(tool);
                    return new
                    {
                        name = tool.Name,
                        category = tool.Category,
                        description = tool.Description,
                        requiresTransaction = attribute?.RequiresTransaction ?? false,
                        requiresModification = attribute?.RequiresModification ?? false,
                        requiresActiveDocument = attribute?.RequiresActiveDocument ?? true,
                        parametersSchema = tool.ParametersSchema
                    };
                }).ToArray();
                stopwatch.Stop();
                return JsonConvert.SerializeObject(new
                {
                    type = "tools.list",
                    requestId,
                    success = true,
                    tools,
                    count = tools.Length,
                    durationMs = stopwatch.Elapsed.TotalMilliseconds
                }, SerializerSettings);
            }

            if (!string.Equals(type, "invoke", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unsupported request type: " + type);
            }

            var toolName = (string)request["tool"];
            if (string.IsNullOrWhiteSpace(toolName))
            {
                throw new InvalidOperationException("The invoke request requires a tool name.");
            }

            var adapter = RevitAdapterManager.CurrentAdapter as IRevitAdapter;
            if (adapter == null)
            {
                throw new InvalidOperationException("Revit adapter is not ready.");
            }

            var parameters = new Dictionary<string, object>();
            if (request["arguments"] is JObject args)
            {
                foreach (var property in args.Properties())
                {
                    parameters[property.Name] = ToPlainObject(property.Value);
                }
            }

            AIToolRegistry.Instance.SetAdapterAssembly(adapter.GetType().Assembly);
            AIToolRegistry.Instance.SetRevitAdapter(adapter);
            AIToolRegistry.Instance.EnsureToolsDiscovered();

            var context = new AIToolContext
            {
                Document = adapter.GetActiveDocument(),
                RevitAdapter = adapter,
                ExternalEvent = _externalEvent,
                SessionId = "codex-bridge"
            };

            var handler = new AIToolInvocationHandler(AIToolRegistry.Instance);
            var result = handler.HandleToolCallAsync(
                new ToolCallInfo
                {
                    ToolName = toolName,
                    Parameters = parameters,
                    CallId = requestId ?? Guid.NewGuid().ToString("N")
                },
                context).GetAwaiter().GetResult();
            stopwatch.Stop();

            return JsonConvert.SerializeObject(new
            {
                type = "invoke",
                requestId,
                success = result.Success,
                payload = new
                {
                    tool = toolName,
                    elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                    success = result.Success,
                    message = result.Message,
                    errorMessage = result.Error,
                    data = result.Data
                },
                durationMs = stopwatch.Elapsed.TotalMilliseconds
            }, SerializerSettings);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            CodexBridgeApplication.CodexBridgeLog("Request failed: " + ex);
            return JsonConvert.SerializeObject(new
            {
                type = "error",
                success = false,
                errorCode = "bridge_error",
                errorMessage = ex.Message,
                durationMs = stopwatch.Elapsed.TotalMilliseconds
            }, SerializerSettings);
        }
    }

    private static IReadOnlyList<IAITool> GetTools()
    {
        var adapter = RevitAdapterManager.CurrentAdapter as IRevitAdapter;
        if (adapter == null)
        {
            throw new InvalidOperationException("Revit adapter is not ready.");
        }

        AIToolRegistry.Instance.SetAdapterAssembly(adapter.GetType().Assembly);
        AIToolRegistry.Instance.SetRevitAdapter(adapter);
        AIToolRegistry.Instance.EnsureToolsDiscovered();
        return AIToolRegistry.Instance.GetAllTools().ToList();
    }

    private static AIToolAttribute GetToolAttribute(IAITool tool)
    {
        return tool.GetType()
            .GetCustomAttributes(typeof(AIToolAttribute), false)
            .Cast<AIToolAttribute>()
            .FirstOrDefault();
    }

    private static object ToPlainObject(JToken token)
    {
        if (token == null)
        {
            return null;
        }

        switch (token.Type)
        {
            case JTokenType.Object:
                var dictionary = new Dictionary<string, object>();
                foreach (var property in ((JObject)token).Properties())
                {
                    dictionary[property.Name] = ToPlainObject(property.Value);
                }
                return dictionary;
            case JTokenType.Array:
                return ((JArray)token).Select(ToPlainObject).ToList();
            case JTokenType.Integer:
                return ((JValue)token).Value;
            case JTokenType.Float:
                return ((JValue)token).Value;
            case JTokenType.Boolean:
                return ((JValue)token).Value;
            case JTokenType.Null:
            case JTokenType.Undefined:
                return null;
            default:
                return ((JValue)token).Value;
        }
    }

    private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.None,
        MaxDepth = 64,
        Converters =
        {
            new JsonNodeJsonConverter()
        }
    };

    private sealed class JsonNodeJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return typeof(JsonNode).IsAssignableFrom(objectType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteRawValue(((JsonNode)value).ToJsonString());
        }

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object existingValue,
            JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
