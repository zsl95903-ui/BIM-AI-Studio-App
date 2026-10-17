using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using RevitAi.Abstractions.AI;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Authentication;
using RevitAi.Abstractions.Common;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;
using RevitAi.Abstractions.Network;
using RevitAi.Abstractions.Product;
using RevitAi.Abstractions.UI;
using RevitAi.Core;
using RevitAi.Core.AI;
using RevitAi.Core.Authentication;
using RevitAi.Core.Configuration;
using RevitAi.Core.Security;
using RevitAi.Core.Update;
using RevitAi.Main;
using RevitAi.Main.Commands;

namespace RevitAi;

public sealed class MainApplication
{
	private IRevitAdapter? _adapter;

	private Assembly? _adapterAssembly;

	private IAuthManager? _authManager;

	private INativeCryptoService? _cryptoService;

	private IRemoteProfileClient? _remoteProfileClient;

	private IConfigManager? _configManager;

	private IHttpClientFactory? _httpClientFactory;

	private string? _pluginDirectory;

	private ModuleLoaderAdapter? _moduleLoaderAdapter;

	private AutoUpdateManager? _autoUpdateManager;

	private SessionAIToolDataCache? _aiToolDataCache;

	private RevitDocumentChangeMonitor? _documentChangeMonitor;

	private bool _viewActivatedHandled;

	public static MainApplication? Instance { get; private set; }

	public Assembly? AdapterAssembly => _adapterAssembly;

	public static bool UIBootstrapperInitialized { get; private set; }

	public Result OnStartup(object application)
	{
		try
		{
			Instance = this;
			Logger.Info("========================================");
			Logger.Info("RevitAi.Main 启动中...");
			Assembly callingAssembly = Assembly.GetCallingAssembly();
			_pluginDirectory = Path.GetDirectoryName(callingAssembly.Location);
			Logger.Info("版本目录: " + _pluginDirectory);
			SetupAssemblyResolver();
			try
			{
				CoreServicesFactory.CreateCoreServices(out _authManager, out _cryptoService, out _remoteProfileClient, out _configManager, out _httpClientFactory);
			}
			catch (Exception ex)
			{
				Logger.Error("核心服务初始化失败", ex);
				return Result.Failure("初始化失败");
			}
			try
			{
				Task.Run(async delegate
				{
					await _remoteProfileClient.WarmUpConnectionAsync();
				}).Wait(TimeSpan.FromSeconds(2L));
			}
			catch
			{
			}
			try
			{
				string hardwareFingerprintV = _cryptoService.GetHardwareFingerprintV2();
				Logger.Info("[MainApplication] 硬件指纹 (V2): " + hardwareFingerprintV.Substring(0, 8) + "...");
			}
			catch (Exception ex2)
			{
				Logger.Warning("获取设备信息失败（继续运行）", ex2);
			}
			try
			{
				LoggerAdapter logger = new LoggerAdapter();
				_moduleLoaderAdapter = new ModuleLoaderAdapter();
				ServiceProvider.Initialize(logger, _moduleLoaderAdapter);
				ServiceProvider.RegisterService(typeof(IAuthManager), _authManager);
				ServiceProvider.RegisterService(typeof(IRemoteProfileClient), _remoteProfileClient);
				ServiceProvider.RegisterService(typeof(INativeCryptoService), _cryptoService);
				ServiceProvider.RegisterService(typeof(IConfigManager), _configManager);
				if (_httpClientFactory != null)
				{
					ServiceProvider.RegisterService(typeof(IHttpClientFactory), _httpClientFactory);
				}
				Logger.Info("核心服务已注册到 ServiceProvider");
			}
			catch (Exception ex3)
			{
				Logger.Error("服务定位器初始化失败", ex3);
				return Result.Failure("初始化失败");
			}
			try
			{
				CommandDiscovery.DiscoverAndRegisterFromPluginDirectory();
			}
			catch (Exception ex4)
			{
				Logger.Warning("外部命令发现失败（将继续运行）: " + ex4.Message);
			}
#if ENABLE_DEFAULT_UI
			try
			{
				RibbonConfigManager.Initialize(_pluginDirectory);
				RibbonVisibilityManager.Initialize();
				RibbonConfigurationData currentConfig = RibbonConfigManager.GetCurrentConfig();
				Logger.Info($"Ribbon 配置已加载（最后更新: {currentConfig?.LastUpdated:yyyy-MM-dd HH:mm:ss}）");
			}
			catch (Exception ex5)
			{
				Logger.Warning("Ribbon 管理器初始化失败（将使用默认配置）: " + ex5.Message);
			}
#endif
			Result result = Task.Run(() => _authManager.InitializeAsync()).GetAwaiter().GetResult();
			if (result.IsFailure)
			{
				Logger.Warning("授权管理器初始化警告: " + result.Error);
			}
			else
			{
				ServiceProvider.SetAuthManager(_authManager);
				try
				{
					_aiToolDataCache = new SessionAIToolDataCache();
					_documentChangeMonitor = new RevitDocumentChangeMonitor();
					ServiceProvider.RegisterService(typeof(IAIToolDataCache), _aiToolDataCache);
					ServiceProvider.RegisterService(typeof(RevitDocumentChangeMonitor), _documentChangeMonitor);
					_documentChangeMonitor.DocumentChanged += delegate
					{
						_aiToolDataCache?.ClearAll();
						Logger.Info("[AI Cache] 文档已变更，已清理所有缓存");
					};
					SubscribeToRevitDocumentChanged();
				}
				catch (Exception ex6)
				{
					Logger.Warning("AI 工具数据缓存服务初始化失败（将不使用缓存功能）: " + ex6.Message);
				}
			}
			Task.Run(async delegate
			{
				_ = 1;
				try
				{
					await _authManager.RegisterOrFindDeviceAsync();
					try
					{
						Result<IUserIdentity> result2 = await _authManager.AutoSignInAsync();
						if (result2.IsSuccess)
						{
							IUserIdentity value = result2.Value;
							if (value != null)
							{
								Logger.Info("自动登录成功: " + value.Email);
							}
						}
						else
						{
							Logger.Debug("自动登录失败（设备未绑定或网络问题）: " + result2.Error);
						}
					}
					catch (Exception ex17)
					{
						Logger.Warning("自动登录异常（已忽略）: " + ex17.Message);
					}
				}
				catch (Exception ex18)
				{
					Logger.Warning("设备注册异常（已忽略）: " + ex18.Message);
				}
			});
#if ENABLE_DEFAULT_UI
			try
			{
				if (UIBootstrapperInitialized = ModuleLoader.InitializeUIModule(_authManager, _cryptoService, _remoteProfileClient))
				{
					Logger.Info("UI 层初始化完成（动态加载成功）");
				}
				else
				{
					Logger.Warning("UI 层初始化失败");
				}
			}
			catch (Exception ex7)
			{
				Logger.Warning("UI 层初始化异常: " + ex7.Message);
				UIBootstrapperInitialized = false;
			}
#else
			// Revit AI WebView2 UI owns the upper-layer interface.
			UIBootstrapperInitialized = false;
#endif
			RevitVersionInfo currentVersion = RevitVersionDetector.GetCurrentVersion();
			_authManager.SetCurrentRevitVersion(currentVersion.VersionYear.ToString());
			_adapterAssembly = AssemblyLoader.LoadAdapterAssembly(currentVersion);
			_adapter = AssemblyLoader.CreateRevitAdapter(_adapterAssembly);
			if (_adapter == null)
			{
				Logger.Error("无法创建适配器实例，插件初始化失败");
				return Result.Failure("初始化失败");
			}
			_adapter.AuthManager = _authManager;
			if (_moduleLoaderAdapter != null)
			{
				_moduleLoaderAdapter.RevitAdapter = _adapter;
			}
			try
			{
				CommandExecutorRegistry.RegisterExecutor(new CommandExecutor());
			}
			catch (Exception ex8)
			{
				Logger.Warning("命令执行器注册失败", ex8);
			}
			try
			{
				Type type = _adapterAssembly.GetType("RevitAi.Revit.UI.RevitAdapterManager");
				if (type != null)
				{
					MethodInfo method = type.GetMethod("SetAdapter", BindingFlags.Static | BindingFlags.Public);
					if (method != null)
					{
						method.Invoke(null, new object[1] { _adapter });
					}
					else
					{
						Logger.Warning("未找到 RevitAdapterManager.SetAdapter 方法");
					}
				}
				else
				{
					Logger.Info("RevitAdapterManager 类型未找到（可能尚未集成）");
				}
			}
			catch (Exception ex9)
			{
				Logger.Warning("设置 RevitAdapterManager 失败（AI 工具可能无法正常工作）", ex9);
			}
			if (!_adapter.InitializeForUI(application))
			{
				Logger.Error("适配器初始化失败");
				return Result.Failure("初始化失败");
			}
#if ENABLE_DEFAULT_UI
			IUIApplication iUIApplication = null;
			try
			{
				iUIApplication = _adapter.CreateUIApplication(application);
				Logger.Info("UI 应用程序适配器已创建");
			}
			catch (Exception ex10)
			{
				Logger.Warning("创建 UI 应用程序适配器失败（Ribbon 界面将无法创建）: " + ex10.Message);
			}
#else
			IUIApplication iUIApplication = null;
#endif
			try
			{
				AIToolRegistry instance = AIToolRegistry.Instance;
				if (_adapterAssembly != null)
				{
					instance.SetAdapterAssembly(_adapterAssembly);
				}
				if (_adapter != null)
				{
					instance.SetRevitAdapter(_adapter);
				}
			}
			catch (Exception ex11)
			{
				Logger.Warning("设置工具注册表失败（将继续运行）: " + ex11.Message);
			}
#if ENABLE_DEFAULT_UI
			if (iUIApplication != null)
			{
				try
				{
					RealTimeRibbonManager.CreateRealTimeRibbon(iUIApplication);
				}
				catch (Exception ex12)
				{
					Logger.Warning("Ribbon 界面创建失败: " + ex12.Message + "，插件仍可正常运行");
				}
			}
			if (iUIApplication != null)
			{
				try
				{
					RegisterAIDockablePane(iUIApplication);
					try
					{
						EnsureAIPaneInitialized(iUIApplication);
					}
					catch (Exception ex13)
					{
						Logger.Warning("AI 面板初始化失败（将在首次使用时重试）: " + ex13.Message);
					}
				}
				catch (Exception ex14)
				{
					Logger.Warning("AI 对话 DockablePane 注册失败（将使用独立窗口）: " + ex14.Message);
				}
			}
#endif
			try
			{
				Logger.Info("初始化自动更新系统...");
				_autoUpdateManager = this.InitializeAutoUpdate();
			}
			catch (Exception ex15)
			{
				Logger.Warning("自动更新系统初始化失败（将继续运行）: " + ex15.Message);
			}
			Logger.Info("========================================");
			Logger.Info("RevitAi.Main 启动完成");
			if (UIBootstrapperInitialized)
			{
				Task.Run(async delegate
				{
					_ = 1;
					try
					{
						await Task.Delay(2000);
						await CheckAndShowUpdateNotificationAsync();
					}
					catch (Exception ex17)
					{
						Logger.Warning("显示更新通知失败（已忽略）: " + ex17.Message);
					}
				});
			}
			if (UIBootstrapperInitialized)
			{
				Task.Run(async delegate
				{
					_ = 1;
					try
					{
						await Task.Delay(30000);
						await CheckAndShowFeedbackNotificationAsync();
					}
					catch (Exception ex17)
					{
						Logger.Warning("显示反馈通知失败（已忽略）: " + ex17.Message);
					}
				});
			}
			return Result.Success();
		}
		catch (Exception ex16)
		{
			Logger.Error("启动失败", ex16);
			return Result.Failure("启动失败");
		}
	}

	public Result OnShutdown(object application)
	{
		try
		{
			Logger.Info("========================================");
			Logger.Info("RevitAi.Main 关闭中...");
			try
			{
				if (_autoUpdateManager != null)
				{
					_autoUpdateManager.CheckAndApplyPendingUpdate();
				}
			}
			catch (Exception ex)
			{
				Logger.Warning("检查待安装更新失败（将继续关闭）: " + ex.Message);
			}
			if (_pluginDirectory != null)
			{
				AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
			}
			if (ModuleLoader.IsUIModuleLoaded)
			{
				ModuleLoader.CloseAllWindows();
			}
			try
			{
				if (!Task.Run(async delegate
				{
					try
					{
						await ModuleLoader.ShutdownAsync();
					}
					catch (Exception ex7)
					{
						Logger.Warning("UI 层清理异常: " + ex7.Message);
					}
				}).Wait(TimeSpan.FromSeconds(2L)))
				{
					Logger.Warning("UI 层清理超时（2 秒），强制继续关闭流程");
				}
			}
			catch (Exception ex2)
			{
				Logger.Warning("UI 层清理失败（将继续关闭）: " + ex2.Message);
			}
			if (_adapter != null)
			{
				if (_adapter.Shutdown())
				{
					Logger.Info("适配器已安全关闭");
				}
				else
				{
					Logger.Warning("适配器关闭时发生错误");
				}
			}
			if (_authManager != null && _authManager is IDisposable disposable)
			{
				disposable.Dispose();
			}
			if (_cryptoService is IDisposable disposable2)
			{
				disposable2.Dispose();
			}
			try
			{
				_autoUpdateManager?.Dispose();
			}
			catch (Exception ex3)
			{
				Logger.Warning("清理自动更新管理器失败: " + ex3.Message);
			}
			try
			{
				if (_configManager is IDisposable disposable3)
				{
					disposable3.Dispose();
					Logger.Info("配置管理器已清理");
				}
			}
			catch (Exception ex4)
			{
				Logger.Warning("清理配置管理器失败: " + ex4.Message);
			}
			try
			{
				_documentChangeMonitor?.Dispose();
				_aiToolDataCache?.Dispose();
				Logger.Info("AI 工具数据缓存服务已清理");
			}
			catch (Exception ex5)
			{
				Logger.Warning("清理 AI 工具数据缓存服务失败: " + ex5.Message);
			}
			Logger.Info("RevitAi.Main 已安全关闭");
			Logger.Info("========================================");
			return Result.Success();
		}
		catch (Exception ex6)
		{
			Logger.Error("关闭时发生错误", ex6);
			return Result.Failure("启动失败");
		}
	}

	private void SetupAssemblyResolver()
	{
		try
		{
			AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
		}
		catch (Exception ex)
		{
			Logger.Warning("设置程序集解析器失败", ex);
		}
	}

	private void SubscribeToRevitDocumentChanged()
	{
		try
		{
			if (_adapterAssembly == null)
			{
				return;
			}
			Type type = _adapterAssembly.GetType("RevitAi.Revit.RevitAdapter") ?? _adapterAssembly.GetType("RevitAi.Revit.Net8.RevitAdapter");
			if (type == null)
			{
				Logger.Warning("[AI Cache] 未找到 RevitAdapter 类型");
				return;
			}
			EventInfo eventInfo = type.GetEvent("DocumentChanged");
			if (eventInfo == null)
			{
				Logger.Warning("[AI Cache] 未找到 RevitAdapter.DocumentChanged 事件");
				return;
			}
			EventHandler handler = delegate
			{
				_documentChangeMonitor?.OnDocumentChanged();
			};
			eventInfo.AddEventHandler(null, handler);
		}
		catch (Exception ex)
		{
			Logger.Warning("[AI Cache] 订阅文档变更事件失败: " + ex.Message);
		}
	}

	private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
	{
		try
		{
			if (_pluginDirectory == null || string.IsNullOrEmpty(args.Name))
			{
				return null;
			}
			string name = new AssemblyName(args.Name).Name;
			if (string.IsNullOrEmpty(name))
			{
				return null;
			}
			if (!name.StartsWith("RevitAi.", StringComparison.OrdinalIgnoreCase) && !name.Equals("MaterialDesignThemes.Wpf", StringComparison.OrdinalIgnoreCase) && !name.Equals("MaterialDesignColors", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("MaterialDesign", StringComparison.OrdinalIgnoreCase) && !name.Equals("Emoji.Wpf", StringComparison.OrdinalIgnoreCase) && !name.Equals("Stfu", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Memory", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Runtime.CompilerServices.Unsafe", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Buffers", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Threading.Tasks.Extensions", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Numerics.Vectors", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Text.Encodings.Web", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Text.Json", StringComparison.OrdinalIgnoreCase) && !name.Equals("System.Memory.Data", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			string text = Path.Combine(_pluginDirectory, name + ".dll");
			if (File.Exists(text))
			{
				return Assembly.LoadFrom(text);
			}
			string[] array = new string[4] { "R_Legacy", "R_Modern", "R2024", "R_Next" };
			foreach (string path in array)
			{
				string text2 = Path.Combine(_pluginDirectory, path, name + ".dll");
				if (File.Exists(text2))
				{
					return Assembly.LoadFrom(text2);
				}
			}
			return null;
		}
		catch (Exception ex)
		{
			Logger.Warning("解析程序集时出错: " + args.Name + " - " + ex.Message);
			return null;
		}
	}

	private void RegisterAIDockablePane(IUIApplication uiApp)
	{
		if (_adapterAssembly == null)
		{
			Logger.Warning("适配器程序集未加载，跳过 DockablePane 注册");
			return;
		}
		try
		{
			Type type = _adapterAssembly.GetType("RevitAi.Revit.UI.AIDockablePaneRegistrar");
			if (type == null)
			{
				Logger.Warning("未找到 AIDockablePaneRegistrar 类型，跳过 DockablePane 注册");
				return;
			}
			MethodInfo method = type.GetMethod("Register", BindingFlags.Static | BindingFlags.Public);
			if (method == null)
			{
				Logger.Warning("未找到 Register 方法，跳过 DockablePane 注册");
				return;
			}
			object underlyingObject = uiApp.GetUnderlyingObject();
			if (underlyingObject == null)
			{
				Logger.Warning("无法获取 UIControlledApplication，跳过 DockablePane 注册");
				return;
			}
			method.Invoke(null, new object[1] { underlyingObject });
		}
		catch (Exception ex)
		{
			Logger.Warning("DockablePane 注册失败: " + ex.Message);
		}
	}

	public void EnsureAdapterServicesInitialized(object externalCommandData)
	{
		if (_adapter == null)
		{
			Logger.Warning("RevitAdapter 未初始化");
		}
		else
		{
			if (_adapter.ElementService != null && _adapter.DocumentService != null)
			{
				return;
			}
			try
			{
				if (_adapter.InitializeForCommand(externalCommandData))
				{
					Logger.Info("✅ RevitAdapter 服务重新初始化成功");
				}
				else
				{
					Logger.Warning("RevitAdapter 服务重新初始化失败");
				}
			}
			catch (Exception ex)
			{
				Logger.Error("RevitAdapter 服务重新初始化异常", ex);
			}
		}
	}

	private void OnViewActivated(object? sender, object e)
	{
		try
		{
			if (!_viewActivatedHandled)
			{
				_viewActivatedHandled = true;
				if (sender == null)
				{
					Logger.Warning("ViewActivated 事件的 sender 为 null，跳过处理");
				}
				else if (sender == null)
				{
					Logger.Warning("UIApplication 为 null，跳过处理");
				}
				else if (_adapterAssembly == null)
				{
					Logger.Warning("适配器程序集未加载，跳过 AI 面板重置");
				}
				else if (UIBootstrapperInitialized)
				{
					ResetAIPanel(sender);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Warning("处理 ViewActivated 事件失败", ex);
		}
	}

	private void ResetAIPanel(object? uiApp)
	{
		try
		{
			if (uiApp == null || _adapterAssembly == null)
			{
				return;
			}
			Type type = _adapterAssembly.GetType("RevitAi.Revit.UI.AIDockablePaneHelper");
			if (type == null)
			{
				return;
			}
			MethodInfo method = type.GetMethod("ShowPane");
			if (method == null)
			{
				Logger.Warning("未找到 ShowPane 方法");
				return;
			}
			try
			{
				object value = method.Invoke(null, new object[1] { uiApp });
				Logger.Info($"✅ AI 面板已重新初始化（ViewModel 和 RevitAdapter 已设置），结果: {value}");
			}
			catch (Exception ex)
			{
				Logger.Warning("调用 ShowPane 方法失败: " + ex.Message);
			}
		}
		catch (Exception ex2)
		{
			Logger.Warning("重置 AI 面板失败: " + ex2.Message);
		}
	}

	private void EnsureAIPaneInitialized(IUIApplication uiApp)
	{
		try
		{
			if (uiApp == null || _adapterAssembly == null)
			{
				return;
			}
			object underlyingObject = uiApp.GetUnderlyingObject();
			if (underlyingObject == null)
			{
				Logger.Warning("无法获取底层的 UIControlledApplication 对象");
				return;
			}
			object obj = TryExtractUIApplicationFromControlled(underlyingObject);
			if (obj != null)
			{
				SetCurrentUIApplication(obj);
			}
			else
			{
				Logger.Warning("⚠\ufe0f 未能从 UIControlledApplication 提取 UIApplication");
			}
			Type type = _adapterAssembly.GetType("RevitAi.Revit.UI.AIDockablePaneHelper");
			if (type == null)
			{
				return;
			}
			MethodInfo method = type.GetMethod("ShowPane", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
			if (method == null)
			{
				Logger.Warning("未找到 ShowPane() 无参方法");
				return;
			}
			try
			{
				method.Invoke(null, null);
			}
			catch (Exception ex)
			{
				Logger.Warning("调用 ShowPane 方法失败: " + ex.Message);
			}
		}
		catch (Exception ex2)
		{
			Logger.Warning("AI 面板初始化异常: " + ex2.Message);
		}
	}

	private object? TryExtractUIApplicationFromControlled(object controlledApplication)
	{
		try
		{
			if (controlledApplication == null)
			{
				return null;
			}
			Type type = controlledApplication.GetType();
			string[] array = new string[8] { "m_uiapplication", "m_application", "UIApplication", "_application", "m_uiApplication", "m_app", "_app", "application" };
			foreach (string text in array)
			{
				try
				{
					FieldInfo field = type.GetField(text, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					if (field != null)
					{
						object value = field.GetValue(controlledApplication);
						if (value != null)
						{
							return value;
						}
					}
				}
				catch (Exception ex)
				{
					Logger.Debug("访问字段 '" + text + "' 失败: " + ex.Message);
				}
			}
			try
			{
				FieldInfo field2 = type.GetField("m_proxy", BindingFlags.Instance | BindingFlags.NonPublic);
				if (field2 != null)
				{
					object value2 = field2.GetValue(controlledApplication);
					if (value2 != null)
					{
						FieldInfo field3 = value2.GetType().GetField("m_uiapplication", BindingFlags.Instance | BindingFlags.NonPublic);
						if (field3 != null)
						{
							object value3 = field3.GetValue(value2);
							if (value3 != null)
							{
								return value3;
							}
						}
					}
				}
			}
			catch (Exception ex2)
			{
				Logger.Debug("通过 m_proxy 获取失败: " + ex2.Message);
			}
			try
			{
				FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				Logger.Warning("⚠\ufe0f 反射提取失败。可用字段: " + string.Join(", ", fields.Select((FieldInfo f) => f.Name)));
				Logger.Warning("⚠\ufe0f 可用属性: " + string.Join(", ", properties.Select((PropertyInfo p) => p.Name)));
			}
			catch
			{
			}
			return null;
		}
		catch (Exception ex3)
		{
			Logger.Warning("提取 UIApplication 失败: " + ex3.Message);
			return null;
		}
	}

	private void SetCurrentUIApplication(object uiApplication)
	{
		try
		{
			if (_adapterAssembly == null)
			{
				return;
			}
			Type type = _adapterAssembly.GetType("RevitAi.Revit.RevitAdapter");
			if (!(type == null))
			{
				PropertyInfo property = type.GetProperty("CurrentUIApplication", BindingFlags.Static | BindingFlags.Public);
				if (property != null)
				{
					property.SetValue(null, uiApplication);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Warning("设置 CurrentUIApplication 失败: " + ex.Message);
		}
	}

	private async Task CheckAndShowUpdateNotificationAsync()
	{
		_ = 3;
		try
		{
			if (_configManager == null)
			{
				Logger.Warning("[UpdateNotification] ConfigManager 不可用，跳过更新通知检查");
				return;
			}
			UpdateNotificationService updateNotificationService = new UpdateNotificationService(_configManager);
			if (await updateNotificationService.ShouldShowUpdateNotificationAsync())
			{
				string releaseNotes = updateNotificationService.GetCurrentVersionReleaseNotes();
				if (string.IsNullOrWhiteSpace(releaseNotes))
				{
					Logger.Warning("[UpdateNotification] 当前版本 " + ProductInfo.Version + " 没有更新日志，跳过显示");
					Logger.Warning("[UpdateNotification] 请在 ProductInfo.cs 的 ReleaseNotes 字典中添加该版本的更新日志");
					return;
				}
				await Task.Delay(2000);
				ShowUpdateNotificationToast(ProductInfo.Version, releaseNotes);
				await updateNotificationService.MarkVersionAsShownAsync();
				await _configManager.SaveAsync();
			}
		}
		catch (Exception ex)
		{
			Logger.Error("[UpdateNotification] 显示更新通知失败: " + ex.Message, ex);
		}
	}

	private void ShowUpdateNotificationToast(string version, string releaseNotes)
	{
		try
		{
			Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => a.GetName().Name == "RevitAi.UI");
			if (assembly == null)
			{
				Logger.Warning("未找到 RevitAi.UI 程序集，跳过更新通知 Toast");
				return;
			}
			Type type = assembly.GetType("RevitAi.UI.Services.UserNotificationService");
			if (type == null)
			{
				Logger.Warning("未找到 UserNotificationService 类型，跳过更新通知 Toast");
				return;
			}
			PropertyInfo property = type.GetProperty("Toast", BindingFlags.Static | BindingFlags.Public);
			if (property == null)
			{
				Logger.Warning("未找到 UserNotificationService.Toast 属性，跳过更新通知 Toast");
				return;
			}
			object value = property.GetValue(null);
			if (value == null)
			{
				Logger.Warning("Toast 服务实例为 null，跳过更新通知 Toast");
				return;
			}
			MethodInfo method = value.GetType().GetMethod("ShowToast", new Type[3]
			{
				typeof(string),
				typeof(string),
				typeof(int)
			});
			if (method == null)
			{
				Logger.Warning("未找到 Toast.ShowToast 方法，跳过更新通知 Toast");
				return;
			}
			string text = "RevitAi";
			method.Invoke(value, new object[3] { text, releaseNotes, 10 });
		}
		catch (Exception ex)
		{
			Logger.Warning("显示更新通知 Toast 失败（已忽略）: " + ex.Message);
		}
	}

	private async Task CheckAndShowFeedbackNotificationAsync()
	{
		try
		{
			if (_remoteProfileClient == null)
			{
				Logger.Warning("[FeedbackNotification] RemoteProfileClient 不可用，跳过反馈通知检查");
				return;
			}
			IDeviceInfo deviceInfo = _authManager?.CurrentDevice;
			if (deviceInfo == null)
			{
				Logger.Warning("[FeedbackNotification] 设备信息不可用，跳过反馈通知检查");
				return;
			}
			IUserIdentity userIdentity = _authManager?.CurrentUser;
			Result<int> result = await _remoteProfileClient.GetUnreadReplyCountAsync(deviceInfo.DeviceId ?? string.Empty, userIdentity?.UserId);
			if (!result.IsSuccess)
			{
				Logger.Warning("[FeedbackNotification] 获取未读反馈数量失败: " + result.Error);
				return;
			}
			int value = result.Value;
			if (value > 0)
			{
				Logger.Info($"[FeedbackNotification] 发现 {value} 条未读反馈，显示通知");
				ShowFeedbackNotificationToast(value);
			}
			else
			{
				Logger.Info("[FeedbackNotification] 没有未读反馈");
			}
		}
		catch (Exception ex)
		{
			Logger.Error("[FeedbackNotification] 检查未读反馈失败: " + ex.Message, ex);
		}
	}

	private void ShowFeedbackNotificationToast(int unreadCount)
	{
		try
		{
			Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => a.GetName().Name == "RevitAi.UI");
			if (assembly == null)
			{
				Logger.Warning("未找到 RevitAi.UI 程序集，跳过反馈通知 Toast");
				return;
			}
			Type type = assembly.GetType("RevitAi.UI.Services.UserNotificationService");
			if (type == null)
			{
				Logger.Warning("未找到 UserNotificationService 类型，跳过反馈通知 Toast");
				return;
			}
			PropertyInfo property = type.GetProperty("Toast", BindingFlags.Static | BindingFlags.Public);
			if (property == null)
			{
				Logger.Warning("未找到 UserNotificationService.Toast 属性，跳过反馈通知 Toast");
				return;
			}
			object value = property.GetValue(null);
			if (value == null)
			{
				Logger.Warning("Toast 服务实例为 null，跳过反馈通知 Toast");
				return;
			}
			MethodInfo method = value.GetType().GetMethod("ShowToast", new Type[3]
			{
				typeof(string),
				typeof(string),
				typeof(int)
			});
			if (method == null)
			{
				Logger.Warning("未找到 Toast.ShowToast 方法，跳过反馈通知 Toast");
				return;
			}
			string text = "\ud83d\udcac ToolLayer 反馈新回复";
			string text2 = ((unreadCount == 1) ? "您有 1 条来自管理员的新回复，请点击【关于】按钮查看" : $"您有 {unreadCount} 条来自管理员的新回复，请点击【关于】按钮查看");
			method.Invoke(value, new object[3] { text, text2, 10 });
		}
		catch (Exception ex)
		{
			Logger.Warning("显示反馈通知 Toast 失败（已忽略）: " + ex.Message);
		}
	}
}
