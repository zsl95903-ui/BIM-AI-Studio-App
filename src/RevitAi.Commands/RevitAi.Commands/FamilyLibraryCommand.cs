using System;
using System.Reflection;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("FamilyLibraryCommand", "在线族库", "在线族库管理", "AlwaysVisible", true, FeatureGroup.CoreFeatures, "基础功能", 110)]
public sealed class FamilyLibraryCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			ShowFamilyLibraryWindow(ServiceProvider.GetModuleLoader(), logger);
			return CommandResult.Succeeded;
		}
		catch (Exception ex)
		{
			try
			{
				ServiceProvider.GetLogger().Error("族库命令执行失败", ex);
			}
			catch
			{
			}
			return CommandResult.Failed;
		}
	}

	private static void ShowFamilyLibraryWindow(IModuleLoader moduleLoader, ILogger logger)
	{
		try
		{
			Assembly assembly = Assembly.Load("RevitAi.UI");
			if (assembly == null)
			{
				logger.Warning("[FamilyLibraryCommand] 无法加载 RevitAi.UI 程序集");
				return;
			}
			Type type = assembly.GetType("RevitAi.UI.Services.UIBootstrapper");
			if (type == null)
			{
				logger.Warning("[FamilyLibraryCommand] 未找到 UIBootstrapper 类型");
				return;
			}
			MethodInfo method = type.GetMethod("GetService");
			if (method == null)
			{
				logger.Warning("[FamilyLibraryCommand] 未找到 GetService 方法");
				return;
			}
			Type type2 = assembly.GetType("RevitAi.UI.Services.IWindowManager");
			if (type2 == null)
			{
				logger.Warning("[FamilyLibraryCommand] 未找到 IWindowManager 接口类型");
				return;
			}
			object obj = method.MakeGenericMethod(type2).Invoke(null, null);
			if (obj == null)
			{
				logger.Warning("[FamilyLibraryCommand] 无法获取 IWindowManager 实例");
				return;
			}
			MethodInfo method2 = type2.GetMethod("ShowFamilyLibraryWindow");
			if (method2 == null)
			{
				logger.Warning("[FamilyLibraryCommand] 未找到 ShowFamilyLibraryWindow 方法");
			}
			else
			{
				method2.Invoke(obj, null);
			}
		}
		catch (Exception ex)
		{
			logger.Error("[FamilyLibraryCommand] 显示族库窗口失败: " + ex.Message, ex);
		}
	}
}
