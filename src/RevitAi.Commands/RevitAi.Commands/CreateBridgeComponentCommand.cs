using System;
using System.Reflection;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

public sealed class CreateBridgeComponentCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
			if (moduleLoader == null)
			{
				logger.Error("无法获取模块加载器");
				return CommandResult.Failed;
			}
			try
			{
				MethodInfo method = moduleLoader.GetType().GetMethod("ShowCreateBridgeComponentWindow");
				if (method != null)
				{
					method.Invoke(moduleLoader, null);
					logger.Info("创建桥梁部件窗口已打开");
					return CommandResult.Succeeded;
				}
				logger.Error("未找到 ShowCreateBridgeComponentWindow 方法");
				return CommandResult.Failed;
			}
			catch (Exception ex)
			{
				logger.Error("打开创建桥梁部件窗口失败", ex);
				return CommandResult.Failed;
			}
		}
		catch (Exception ex2)
		{
			try
			{
				ServiceProvider.GetLogger().Error("创建桥梁部件命令执行失败", ex2);
			}
			catch
			{
			}
			return CommandResult.Failed;
		}
	}
}
