using System;
using System.Reflection;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("FeatureStoreCommand", "应用仓库", "管理插件功能和自定义 Ribbon 界面", "AlwaysVisible", true, FeatureGroup.CoreFeatures, "基础功能", 120)]
public sealed class FeatureStoreCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			try
			{
				IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
				MethodInfo method = moduleLoader.GetType().GetMethod("ShowFeatureStoreWindow");
				if (method != null)
				{
					method.Invoke(moduleLoader, null);
					logger.Info("应用仓库窗口已打开");
				}
				else
				{
					logger.Warning("ModuleLoader 未找到 ShowFeatureStoreWindow 方法");
				}
			}
			catch (Exception ex)
			{
				logger.Error("打开应用仓库窗口失败", ex);
			}
			return CommandResult.Succeeded;
		}
		catch (Exception ex2)
		{
			try
			{
				ServiceProvider.GetLogger().Error("应用仓库命令执行失败", ex2);
			}
			catch
			{
			}
			return CommandResult.Failed;
		}
	}
}
