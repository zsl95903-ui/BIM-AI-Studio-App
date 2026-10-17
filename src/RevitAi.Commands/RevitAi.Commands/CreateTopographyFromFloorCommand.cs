using System;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("CreateTopographyFromFloorCommand", "按楼板\n划分地形", "根据楼板轮廓从地形中裁剪出新地形，分隔地形为与楼板相同材质和范围的地形表面", "AlwaysVisible", false, FeatureGroup.Infrastructure, "场地地形", 120)]
public sealed class CreateTopographyFromFloorCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		ILogger logger = ServiceProvider.GetLogger();
		IRevitAdapter revitAdapter = ServiceProvider.GetModuleLoader().RevitAdapter;
		IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
		logger.Info("[CreateTopographyFromFloorCommand] 开始执行");
		logger.Info("[CreateTopographyFromFloorCommand] 预创建 TopographyOperation ExternalEvent");
		try
		{
			if ((revitAdapter?.GetType().GetMethod("GetTopographyOperationExternalEvent"))?.Invoke(revitAdapter, null) != null)
			{
				logger.Info("[CreateTopographyFromFloorCommand] 成功预创建 TopographyOperation ExternalEvent");
			}
			else
			{
				logger.Warning("[CreateTopographyFromFloorCommand] ExternalEvent 创建返回 null，将在 UI 中尝试");
			}
		}
		catch (Exception ex)
		{
			logger.Warning("[CreateTopographyFromFloorCommand] 预创建 ExternalEvent 失败: " + ex.Message);
		}
		try
		{
			(moduleLoader?.GetType().GetMethod("ShowTopographyFromFloorWindow"))?.Invoke(moduleLoader, null);
			logger.Info("[CreateTopographyFromFloorCommand] 已打开按楼板创建地形窗口");
			return CommandResult.Succeeded;
		}
		catch (Exception ex2)
		{
			logger.Error("[CreateTopographyFromFloorCommand] 打开窗口失败", ex2);
			return CommandResult.Failed;
		}
	}
}
