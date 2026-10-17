using System;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("ExportFamilyMetadataCommand", "导出族\n元数据", "导出选中族的缩略图和参数信息到 JSON 文件，用于族库管理", "AlwaysVisible", false, FeatureGroup.FamilyAndParameter, "族管理", 110)]
public sealed class ExportFamilyMetadataCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		ILogger logger = ServiceProvider.GetLogger();
		IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
		logger.Info("[ExportFamilyMetadataCommand] 开始执行");
		try
		{
			(moduleLoader?.GetType().GetMethod("ShowExportFamilyMetadataWindow"))?.Invoke(moduleLoader, null);
			logger.Info("[ExportFamilyMetadataCommand] 已打开族元数据导出窗口");
			return CommandResult.Succeeded;
		}
		catch (Exception ex)
		{
			logger.Error("[ExportFamilyMetadataCommand] 打开窗口失败", ex);
			return CommandResult.Failed;
		}
	}
}
