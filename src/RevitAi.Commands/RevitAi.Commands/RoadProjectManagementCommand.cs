using System;
using System.Linq;
using System.Reflection;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("RoadProjectManagementCommand", "路桥\n中心线管理", "管理多条路桥中心线项目，支持创建、编辑、删除、导入导出等功能", "AlwaysVisible", false, FeatureGroup.Infrastructure, "路桥", 100)]
public sealed class RoadProjectManagementCommand : IDynamicCommand
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
				ShowTaskDialog("错误", "无法获取 Revit 适配器");
				return CommandResult.Failed;
			}
			logger.Info("\ud83d\udd27 预创建 RoadCenterline ExternalEvent（在 API 上下文中）");
			try
			{
				MethodInfo method = revitAdapter.GetType().GetMethod("GetRoadCenterlineExternalEvent");
				if (method != null)
				{
					object obj = method.Invoke(revitAdapter, null);
					if (obj != null)
					{
						logger.Info("✅ 成功预创建 ExternalEvent: " + obj.GetType().FullName);
					}
					else
					{
						logger.Warning("⚠\ufe0f GetRoadCenterlineExternalEvent 返回 null，将在 UI 中尝试创建");
					}
				}
				else
				{
					logger.Warning("⚠\ufe0f 未找到 GetRoadCenterlineExternalEvent 方法");
				}
			}
			catch (Exception ex)
			{
				logger.Warning("⚠\ufe0f 预创建 ExternalEvent 失败: " + ex.Message + "，将在 UI 中尝试");
			}
			IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
			if (moduleLoader == null)
			{
				logger.Error("无法获取模块加载器");
				ShowTaskDialog("错误", "无法获取模块加载器");
				return CommandResult.Failed;
			}
			try
			{
				MethodInfo method2 = moduleLoader.GetType().GetMethod("ShowRoadProjectManagementWindow");
				if (method2 != null)
				{
					method2.Invoke(moduleLoader, null);
					logger.Info("路桥中心线管理窗口已打开");
					return CommandResult.Succeeded;
				}
				logger.Error("未找到 ShowRoadProjectManagementWindow 方法");
				ShowTaskDialog("错误", "UI 窗口尚未实现，请等待后续版本更新");
				return CommandResult.Failed;
			}
			catch (Exception ex2)
			{
				logger.Error("打开路桥中心线管理窗口失败", ex2);
				ShowTaskDialog("错误", "打开窗口失败: " + ex2.Message + "\n\n请确保 UI 模块已正确加载");
				return CommandResult.Failed;
			}
		}
		catch (Exception ex3)
		{
			try
			{
				ServiceProvider.GetLogger().Error("路桥中心线管理命令执行失败", ex3);
			}
			catch
			{
			}
			ShowTaskDialog("错误", "命令执行失败: " + ex3.Message);
			return CommandResult.Failed;
		}
	}

	private static void ShowTaskDialog(string title, string message)
	{
		try
		{
			Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => a.GetName().Name == "RevitAPI");
			if (assembly != null)
			{
				Type type = assembly.GetType("Autodesk.Revit.UI.TaskDialog");
				if (type != null)
				{
					type.GetMethod("Show", new Type[2]
					{
						typeof(string),
						typeof(string)
					})?.Invoke(null, new object[2] { title, message });
					return;
				}
			}
			Console.WriteLine(title + ": " + message);
		}
		catch
		{
			Console.WriteLine(title + ": " + message);
		}
	}
}
