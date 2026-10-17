using System;
using System.Reflection;
using System.Threading.Tasks;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Authentication;
using RevitAi.Abstractions.Commands;
using RevitAi.Abstractions.Common;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Main.Commands;

public sealed class CommandExecutor : ICommandExecutor
{
	private static ModuleLoaderAdapter? ModuleLoaderAdapter => ServiceProvider.GetModuleLoader() as ModuleLoaderAdapter;

	public CommandExecutionResult ExecuteCommand(string commandName, object commandData)
	{
		Logger.Info("===== 执行命令: " + commandName + " =====");
		try
		{
			RegisteredCommand registeredCommand = CommandRegistry.GetCommand(commandName);
			if (registeredCommand == null)
			{
				Logger.Error("命令未注册: " + commandName);
				return CommandExecutionResult.Failed;
			}
			(bool, string) tuple = CheckAuthorizationBeforeExecute(commandName, registeredCommand);
			if (!tuple.Item1)
			{
				Logger.Warning("命令未授权: " + commandName + " - " + tuple.Item2);
				string commandText = registeredCommand.Attribute.Text?.Replace("\r\n", " ").Replace("\n", " ") ?? "";
				ShowAuthorizationDialog(commandData, tuple.Item2, commandText);
				return CommandExecutionResult.Cancelled;
			}
			SaveUIApplication(commandData);
			EnsureAdapterServicesInitialized(commandData);
			IDynamicCommand dynamicCommand = registeredCommand.CreateInstance();
			CommandContext context = CreateCommandContext(commandData);
			if (dynamicCommand.Execute(context) == CommandResult.Succeeded)
			{
				if (!Array.Exists(new string[3] { "AboutCommand", "AICommand", "FeatureStoreCommand" }, (string c) => c.Equals(commandName, StringComparison.OrdinalIgnoreCase)))
				{
					string commandNameCopy = commandName;
					Task.Run(async delegate
					{
						try
						{
							IAuthManager authManager = ServiceProvider.AuthManager;
							if (authManager == null || !authManager.IsInitialized)
							{
								Logger.Warning("授权管理器未初始化，跳过记录使用");
							}
							else
							{
								AuthorizationState authorizationState = authManager.GetAuthorizationState();
								if (!authorizationState.HasValidLicense)
								{
									if (authorizationState.TrialUsage.TryGetValue(commandNameCopy, out var value))
									{
										_ = GetMaxTrialCount(commandNameCopy, registeredCommand.Attribute.Group, authManager) - value;
									}
									Result result = await authManager.RecordFeatureUsageAsync(commandNameCopy);
									if (!result.IsSuccess)
									{
										Logger.Warning("记录功能使用失败: " + commandNameCopy + " - " + result.Error);
									}
								}
							}
						}
						catch (Exception ex2)
						{
							Logger.Warning("记录功能使用失败: " + commandName, ex2);
						}
					});
				}
				return CommandExecutionResult.Succeeded;
			}
			Logger.Warning("命令执行失败: " + commandName);
			return CommandExecutionResult.Failed;
		}
		catch (Exception ex)
		{
			Logger.Error("命令执行异常: " + commandName, ex);
			return CommandExecutionResult.Failed;
		}
	}

	private (bool IsAuthorized, string Message) CheckAuthorizationBeforeExecute(string commandName, RegisteredCommand registeredCommand)
	{
		try
		{
			if (registeredCommand.Attribute.IsBasicFeature)
			{
				return (IsAuthorized: true, Message: "");
			}
			if (Array.Exists(new string[3] { "AboutCommand", "AICommand", "FeatureStoreCommand" }, (string c) => c.Equals(commandName, StringComparison.OrdinalIgnoreCase)))
			{
				return (IsAuthorized: true, Message: "");
			}
			IAuthManager authManager = ServiceProvider.AuthManager;
			if (authManager == null || !authManager.IsInitialized)
			{
				return (IsAuthorized: true, Message: "");
			}
			AuthorizationState authorizationState = authManager.GetAuthorizationState();
			if (authorizationState.HasValidLicense)
			{
				return (IsAuthorized: true, Message: "");
			}
			if (authorizationState.TrialUsage.TryGetValue(commandName, out var value))
			{
				int maxTrialCount = GetMaxTrialCount(commandName, registeredCommand.Attribute.Group, authManager);
				if (maxTrialCount - value > 0)
				{
					return (IsAuthorized: true, Message: "");
				}
				return (IsAuthorized: false, Message: $"试用次数已用完（{maxTrialCount}次），请购买许可证或充值");
			}
			if (!authManager.IsNetworkAvailable)
			{
				return (IsAuthorized: false, Message: "无法连接到授权服务器，请检查网络连接");
			}
			return (IsAuthorized: true, Message: "");
		}
		catch (Exception ex)
		{
			Logger.Warning("授权检查失败", ex);
			return (IsAuthorized: false, Message: "授权检查失败");
		}
	}

	private int GetMaxTrialCount(string commandName, FeatureGroup featureGroup, IAuthManager authManager)
	{
		try
		{
			MethodInfo method = authManager.GetType().GetMethod("GetMaxTrialCountFromConfig", BindingFlags.Instance | BindingFlags.NonPublic);
			if (method != null && method.Invoke(authManager, new object[1] { commandName }) is int num && num > 0)
			{
				return num;
			}
			string identifier = featureGroup.GetIdentifier();
			if (!string.IsNullOrEmpty(identifier))
			{
				MethodInfo method2 = authManager.GetType().GetMethod("GetMaxTrialCountFromFeatureGroup");
				if (method2 != null && method2.Invoke(authManager, new object[1] { identifier }) is int num2 && num2 > 0)
				{
					return num2;
				}
			}
			Logger.Warning("[Authorization] 功能 " + commandName + " 没有配置，使用默认值 10 次");
			return 10;
		}
		catch (Exception ex)
		{
			Logger.Warning("获取最大试用次数失败: " + commandName, ex);
			return 10;
		}
	}

	private void SaveUIApplication(object commandData)
	{
		try
		{
			PropertyInfo property = commandData.GetType().GetProperty("Application");
			if (property == null)
			{
				Logger.Warning("❌ SaveUIApplication: 无法获取 Application 属性");
				return;
			}
			object value = property.GetValue(commandData);
			if (value == null)
			{
				Logger.Warning("❌ SaveUIApplication: Application 为 null");
				return;
			}
			IRevitAdapter revitAdapter = ModuleLoaderAdapter?.RevitAdapter;
			if (revitAdapter == null)
			{
				Logger.Warning("❌ SaveUIApplication: RevitAdapter 未设置");
				return;
			}
			PropertyInfo property2 = revitAdapter.GetType().GetProperty("CurrentUIApplication", BindingFlags.Static | BindingFlags.Public);
			if (property2 != null)
			{
				property2.SetValue(null, value);
				property2.GetValue(null);
			}
			else
			{
				Logger.Warning("❌ 未找到 CurrentUIApplication 属性");
			}
		}
		catch (Exception ex)
		{
			Logger.Error("❌ 保存 UIApplication 失败", ex);
		}
	}

	private void EnsureAdapterServicesInitialized(object commandData)
	{
		try
		{
			IRevitAdapter revitAdapter = ModuleLoaderAdapter?.RevitAdapter;
			if (revitAdapter == null)
			{
				Logger.Warning("RevitAdapter 未设置");
			}
			else if (revitAdapter.DocumentService == null)
			{
				MethodInfo method = revitAdapter.GetType().GetMethod("InitializeForCommand");
				if (method != null)
				{
					method.Invoke(revitAdapter, new object[1] { commandData });
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Warning("初始化 RevitAdapter 服务失败", ex);
		}
	}

	private void ShowAuthorizationDialog(object commandData, string message, string commandText)
	{
		try
		{
			IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
			if (moduleLoader == null)
			{
				Logger.Warning("[CommandExecutor] ModuleLoader 为 null，使用备用提示方式");
				ShowFallbackAuthorizationDialog(commandData, message, commandText);
				return;
			}
			MethodInfo method = moduleLoader.GetType().GetMethod("ShowPaymentDialog");
			if (method != null)
			{
				method.Invoke(moduleLoader, new object[2] { message, commandText });
			}
			else
			{
				Logger.Warning("[CommandExecutor] 找不到 ShowPaymentDialog 方法，使用备用提示方式");
				ShowFallbackAuthorizationDialog(commandData, message, commandText);
			}
		}
		catch (Exception ex)
		{
			Logger.Error("显示授权对话框失败，尝试备用方式", ex);
			ShowFallbackAuthorizationDialog(commandData, message, commandText);
		}
	}

	private void ShowFallbackAuthorizationDialog(object commandData, string message, string commandText)
	{
		try
		{
			object obj = ((commandData?.GetType())?.GetProperty("Application"))?.GetValue(commandData);
			if (obj != null)
			{
				Assembly assembly = Assembly.GetAssembly(obj.GetType());
				if (assembly != null)
				{
					Type type = assembly.GetType("Autodesk.Revit.UI.TaskDialog");
					if (type != null)
					{
						string text = message + "\n\n功能：" + commandText + "\n\n请购买许可证或充值后继续使用。";
						MethodInfo method = type.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new Type[2]
						{
							typeof(string),
							typeof(string)
						}, null);
						if (method != null)
						{
							method.Invoke(null, new object[2] { "授权提示 - 功能需要授权", text });
							Logger.Info("[CommandExecutor] 已显示备用授权对话框（TaskDialog）");
							return;
						}
						string text2 = "功能需要授权";
						MethodInfo method2 = type.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new Type[3]
						{
							typeof(string),
							typeof(string),
							typeof(string)
						}, null);
						if (method2 != null)
						{
							method2.Invoke(null, new object[3] { "授权提示", text2, text });
							Logger.Info("[CommandExecutor] 已显示备用授权对话框（TaskDialog with MainInstruction）");
							return;
						}
					}
				}
			}
			Logger.Warning("[CommandExecutor] 授权提示（无法显示对话框）: " + message + " - 功能: " + commandText);
		}
		catch (Exception ex)
		{
			Logger.Error("[CommandExecutor] 显示备用授权对话框失败", ex);
		}
	}

	private async Task RecordFeatureUsageAsync(string commandName)
	{
		try
		{
			IAuthManager authManager = ServiceProvider.AuthManager;
			if (authManager == null)
			{
				Logger.Warning("无法记录功能使用：授权管理器未初始化");
			}
			else
			{
				await authManager.RecordFeatureUsageAsync(commandName);
			}
		}
		catch (Exception ex)
		{
			Logger.Warning("记录功能使用失败: " + commandName, ex);
		}
	}

	private CommandContext CreateCommandContext(object commandData)
	{
		CommandContext commandContext = new CommandContext();
		try
		{
			Type type = commandData.GetType();
			PropertyInfo property = type.GetProperty("Application");
			if (property != null)
			{
				commandContext.Application = property.GetValue(commandData);
			}
			PropertyInfo property2 = type.GetProperty("View");
			if (property2 != null)
			{
				commandContext.View = property2.GetValue(commandData);
			}
		}
		catch (Exception ex)
		{
			Logger.Warning("创建命令上下文失败（使用默认值）", ex);
		}
		return commandContext;
	}
}
