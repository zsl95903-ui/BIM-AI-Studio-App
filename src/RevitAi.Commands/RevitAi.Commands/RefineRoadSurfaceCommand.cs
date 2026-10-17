using System;
using System.Linq;
using System.Reflection;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("RefineRoadSurfaceCommand", "精修\n路基路面", "精修路基路面：1.绘制楼板 2.生成空心 3.批量剪切", "AlwaysVisible", false, FeatureGroup.Infrastructure, "路桥", 111)]
public sealed class RefineRoadSurfaceCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			IRevitAdapter revitAdapter = ServiceProvider.GetModuleLoader().RevitAdapter;
			IModuleLoader moduleLoader = ServiceProvider.GetModuleLoader();
			if (moduleLoader == null)
			{
				logger.Error("无法获取模块加载器");
				ShowTaskDialog("错误", "无法获取模块加载器");
				return CommandResult.Failed;
			}
			logger.Info("[RefineRoadSurfaceCommand] 预创建路面精修 ExternalEvent");
			try
			{
				if (revitAdapter != null)
				{
					object? obj = revitAdapter.GetType().GetMethod("GetRoadSurfaceSelectionExternalEvent")?.Invoke(revitAdapter, null);
					object obj2 = revitAdapter.GetType().GetMethod("GetRoadSurfaceVoidGenerationExternalEvent")?.Invoke(revitAdapter, null);
					object obj3 = revitAdapter.GetType().GetMethod("GetRoadSurfaceCutExternalEvent")?.Invoke(revitAdapter, null);
					if (obj != null && obj2 != null && obj3 != null)
					{
						logger.Info("[RefineRoadSurfaceCommand] 成功预创建路面精修 ExternalEvent");
					}
					else
					{
						logger.Warning("[RefineRoadSurfaceCommand] 部分 ExternalEvent 创建返回 null，将在 UI 中尝试");
					}
				}
			}
			catch (Exception ex)
			{
				logger.Warning("[RefineRoadSurfaceCommand] 预创建 ExternalEvent 失败: " + ex.Message);
			}
			try
			{
				MethodInfo method = moduleLoader.GetType().GetMethod("ShowRoadSurfaceRefinementWindow");
				if (method != null)
				{
					method.Invoke(moduleLoader, null);
					logger.Info("精修路基路面窗口已打开");
					return CommandResult.Succeeded;
				}
				logger.Error("未找到 ShowRoadSurfaceRefinementWindow 方法");
				ShowTaskDialog("错误", "UI 窗口尚未实现，请等待后续版本更新");
				return CommandResult.Failed;
			}
			catch (Exception ex2)
			{
				logger.Error("打开精修路基路面窗口失败", ex2);
				ShowTaskDialog("错误", "打开窗口失败: " + ex2.Message + "\n\n请确保 UI 模块已正确加载");
				return CommandResult.Failed;
			}
		}
		catch (Exception ex3)
		{
			try
			{
				ServiceProvider.GetLogger().Error("精修路基路面命令执行失败", ex3);
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
