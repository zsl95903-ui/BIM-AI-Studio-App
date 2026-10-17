using System;
using System.Reflection;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("AICommand", "AI对话", "AI 助手对话窗口", "AlwaysVisible", true, FeatureGroup.CoreFeatures, "基础功能", 100)]
public sealed class AICommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			ToggleAIChatWindow(ServiceProvider.GetModuleLoader(), logger);
			return CommandResult.Succeeded;
		}
		catch (Exception ex)
		{
			try
			{
				ServiceProvider.GetLogger().Error("AI 命令执行失败", ex);
			}
			catch
			{
			}
			return CommandResult.Failed;
		}
	}

	private static void ToggleAIChatWindow(IModuleLoader _moduleLoader, ILogger logger)
	{
		try
		{
			Assembly assembly = Assembly.Load("RevitAi.UI");
			if (assembly == null)
			{
				logger.Warning("无法加载 RevitAi.UI 程序集");
				return;
			}
			Type type = assembly.GetType("RevitAi.UI.Services.UIBootstrapper");
			if (type == null)
			{
				logger.Warning("未找到 UIBootstrapper 类型");
				return;
			}
			MethodInfo method = type.GetMethod("GetService");
			if (method == null)
			{
				logger.Warning("未找到 GetService 方法");
				return;
			}
			Type type2 = assembly.GetType("RevitAi.UI.Services.IWindowManager");
			if (type2 == null)
			{
				logger.Warning("未找到 IWindowManager 接口类型");
				return;
			}
			object obj = method.MakeGenericMethod(type2).Invoke(null, null);
			if (obj == null)
			{
				logger.Warning("无法获取 IWindowManager 实例");
				return;
			}
			MethodInfo method2 = type2.GetMethod("ToggleAIChatWindow");
			if (method2 == null)
			{
				logger.Warning("未找到 ToggleAIChatWindow 方法");
				return;
			}
			method2.Invoke(obj, null);
			logger.Info("AI 窗口状态已切换");
		}
		catch (Exception ex)
		{
			logger.Error("切换 AI 窗口状态失败: " + ex.Message, ex);
		}
	}
}
