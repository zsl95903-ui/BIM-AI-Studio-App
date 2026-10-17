using System;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("AboutCommand", "关于", "显示插件信息和授权管理", "AlwaysVisible", true, FeatureGroup.ToolAssistant, "基础功能", 100)]
public sealed class AboutCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			ServiceProvider.GetModuleLoader().ShowAboutWindow();
			logger.Info("关于窗口显示请求已发送");
			return CommandResult.Succeeded;
		}
		catch (Exception ex)
		{
			try
			{
				ServiceProvider.GetLogger().Error("关于命令执行失败", ex);
			}
			catch
			{
			}
			return CommandResult.Failed;
		}
	}
}
