using RevitAi.Abstractions.AI;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;
using RevitAi.Core.AI;
using RevitAi.Core.AI.Tools;
using RevitAi.Revit;
using RevitAi.Revit.UI;
using RevitAi.Addin.Tools;

namespace RevitAi.Addin.Services;

internal sealed class RuntimeBootstrap : IDisposable
{
    private readonly ModuleLoaderAdapter _moduleLoader = new();
    private readonly SessionAIToolDataCache _dataCache = new();
    private readonly RevitDocumentChangeMonitor _documentMonitor = new();
    private readonly SimpleHttpClientFactory _httpClientFactory = new();
    private ISkillManager? _skillManager;
    private bool _documentChangedSubscribed;

    public void InitializeBeforeAdapter()
    {
        ServiceProvider.Initialize(new LoggerAdapter(), _moduleLoader);
        ServiceProvider.RegisterService(typeof(IAIToolDataCache), _dataCache);
        ServiceProvider.RegisterService(
            typeof(RevitDocumentChangeMonitor),
            _documentMonitor);
        RevitAdapter.DocumentChanged += OnDocumentChanged;
        _documentChangedSubscribed = true;
    }

    public void AttachAdapter(RevitAdapter adapter)
    {
        _moduleLoader.RevitAdapter = adapter;
        RevitAdapterManager.SetAdapter(adapter);
    }

    public void RegisterLocalTools(RevitAdapter adapter)
    {
        var registry = AIToolRegistry.Instance;
        if (!registry.ContainsTool("save_memory"))
        {
            registry.RegisterTool(new SaveMemoryAITool());
        }

        if (!registry.ContainsTool("pdf_document_manager"))
        {
            registry.RegisterTool(new PdfDocumentManagerTool());
        }

        if (!registry.ContainsTool("cad_file_manager"))
        {
            registry.RegisterTool(new CadFileManagerTool());
        }

        RegisterSkillTools(registry);
        RegisterWebTools(registry);
    }

    private void RegisterSkillTools(IAIToolRegistry registry)
    {
        if (_skillManager is null)
        {
            _skillManager = new SkillManager(new DisabledUpdateService(), registry);
            ServiceProvider.RegisterService(typeof(ISkillManager), _skillManager);
            try
            {
                _skillManager.InitializeAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    "[RevitAi] SkillManager 初始化失败: " + ex.GetBaseException().Message);
            }
        }

        if (!registry.ContainsTool("execute_skill"))
        {
            registry.RegisterTool(new SkillExecutionTool(_skillManager));
        }
    }

    private void RegisterWebTools(IAIToolRegistry registry)
    {
        if (!registry.ContainsTool("web_fetch"))
        {
            registry.RegisterTool(new WebFetchTool(_httpClientFactory));
        }

    }

    public void Dispose()
    {
        if (_documentChangedSubscribed)
        {
            RevitAdapter.DocumentChanged -= OnDocumentChanged;
            _documentChangedSubscribed = false;
        }

        try
        {
            RevitAdapterManager.ClearAdapter();
        }
        catch
        {
        }

        _documentMonitor.Dispose();
        _dataCache.Dispose();
        _httpClientFactory.Dispose();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        _dataCache.ClearAll();
        _documentMonitor.OnDocumentChanged();
    }
}

