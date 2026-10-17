using System;
using System.Reflection;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("ManageModelLinksCommand", "管理链接", "管理当前模型中的链接文件，支持快速修改坐标、旋转、高程等", "AlwaysVisible", false, FeatureGroup.BatchSplitAndMerge, "链接", 110)]
public sealed class ManageModelLinksCommand : IDynamicCommand
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
			try
			{
				MethodInfo method = revitAdapter.GetType().GetMethod("GetLinkManagementExternalEvent");
				if (method != null)
				{
					if (method.Invoke(revitAdapter, null) == null)
					{
						logger.Warning("GetLinkManagementExternalEvent 返回 null，将在 UI 中尝试创建");
					}
				}
				else
				{
					logger.Warning("未找到 GetLinkManagementExternalEvent 方法");
				}
			}
			catch (Exception ex)
			{
				logger.Warning("预创建 ExternalEvent 失败: " + ex.Message + "，将在 UI 中尝试");
			}
			try
			{
				IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
				MethodInfo method2 = moduleLoader.GetType().GetMethod("ShowManageModelLinksWindow");
				if (method2 != null)
				{
					method2.Invoke(moduleLoader, null);
					logger.Info("链接管理窗口已打开");
				}
				else
				{
					logger.Warning("ModuleLoader 未找到 ShowManageModelLinksWindow 方法");
				}
			}
			catch (Exception ex2)
			{
				logger.Error("打开链接管理窗口失败", ex2);
			}
			return CommandResult.Succeeded;
		}
		catch (Exception ex3)
		{
			try
			{
				ServiceProvider.GetLogger().Error("管理链接命令执行失败", ex3);
			}
			catch
			{
			}
			return CommandResult.Failed;
		}
	}
}
