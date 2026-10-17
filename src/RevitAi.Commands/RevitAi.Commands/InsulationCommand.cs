using System;
using System.Reflection;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("InsulationCommand", "保温工具", "管理管道和风管的保温层，支持添加、删除、修改和查询操作", "AlwaysVisible", false, FeatureGroup.MEPPiping, "MEP操作", 105)]
public sealed class InsulationCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			IRevitAdapter revitAdapter = ServiceProvider.GetModuleLoader().RevitAdapter;
			if (revitAdapter == null)
			{
				logger.Error("无法获取 Revit 适配器");
				return CommandResult.Failed;
			}
			logger.Info("\ud83d\udd27 预创建保温工具 ExternalEvent（在 API 上下文中）");
			try
			{
				MethodInfo method = revitAdapter.GetType().GetMethod("GetInsulationExternalEvent");
				if (method != null)
				{
					if (method.Invoke(revitAdapter, null) != null)
					{
						logger.Info("✅ 成功预创建保温工具 ExternalEvent");
					}
					else
					{
						logger.Warning("⚠\ufe0f GetInsulationExternalEvent 返回 null，将在 UI 中尝试创建");
					}
				}
				else
				{
					logger.Warning("⚠\ufe0f 未找到 GetInsulationExternalEvent 方法");
				}
			}
			catch (Exception ex)
			{
				logger.Warning("⚠\ufe0f 预创建保温工具 ExternalEvent 失败: " + ex.Message + "，将在 UI 中尝试");
			}
			ServiceProvider.GetModuleLoader().ShowInsulationWindow();
			return CommandResult.Succeeded;
		}
		catch (Exception ex2)
		{
			Logger.Error("[InsulationCommand] 执行失败: " + ex2.Message);
			return CommandResult.Failed;
		}
	}
}
