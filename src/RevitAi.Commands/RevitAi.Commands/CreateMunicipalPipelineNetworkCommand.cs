using System;
using System.Linq;
using System.Reflection;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("CreateMunicipalPipelineNetworkCommand", "创建\n市政管网", "根据CAD图纸和表格文件创建地下给排水管道和检查井等模型", "AlwaysVisible", false, FeatureGroup.Infrastructure, "市政管网", 110)]
public sealed class CreateMunicipalPipelineNetworkCommand : IDynamicCommand
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
			logger.Info("\ud83d\udd27 预创建 CAD 图元选择 ExternalEvent（在 API 上下文中）");
			try
			{
				MethodInfo method = revitAdapter.GetType().GetMethod("GetCADElementSelectionExternalEvent");
				if (method != null)
				{
					if (method.Invoke(revitAdapter, null) != null)
					{
						logger.Info("✅ 成功预创建 CAD 图元选择 ExternalEvent");
					}
					else
					{
						logger.Warning("⚠\ufe0f GetCADElementSelectionExternalEvent 返回 null，将在 UI 中尝试创建");
					}
				}
				else
				{
					logger.Warning("⚠\ufe0f 未找到 GetCADElementSelectionExternalEvent 方法");
				}
			}
			catch (Exception ex)
			{
				logger.Warning("⚠\ufe0f 预创建 CAD 图元选择 ExternalEvent 失败: " + ex.Message + "，将在 UI 中尝试");
			}
			logger.Info("\ud83d\udd27 预创建管网建模 ExternalEvent（在 API 上下文中）");
			try
			{
				MethodInfo method2 = revitAdapter.GetType().GetMethod("GetPipeNetworkModelingExternalEvent");
				if (method2 != null)
				{
					if (method2.Invoke(revitAdapter, null) != null)
					{
						logger.Info("✅ 成功预创建管网建模 ExternalEvent");
					}
					else
					{
						logger.Warning("⚠\ufe0f GetPipeNetworkModelingExternalEvent 返回 null，将在 UI 中尝试创建");
					}
				}
				else
				{
					logger.Warning("⚠\ufe0f 未找到 GetPipeNetworkModelingExternalEvent 方法");
				}
			}
			catch (Exception ex2)
			{
				logger.Warning("⚠\ufe0f 预创建管网建模 ExternalEvent 失败: " + ex2.Message + "，将在 UI 中尝试");
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
				MethodInfo method3 = moduleLoader.GetType().GetMethod("ShowMunicipalPipelineNetworkWindow");
				if (method3 != null)
				{
					method3.Invoke(moduleLoader, null);
					logger.Info("市政管网窗口已打开");
					return CommandResult.Succeeded;
				}
				logger.Error("未找到 ShowMunicipalPipelineNetworkWindow 方法");
				ShowTaskDialog("错误", "UI 窗口尚未实现，请等待后续版本更新");
				return CommandResult.Failed;
			}
			catch (Exception ex3)
			{
				logger.Error("打开市政管网窗口失败", ex3);
				ShowTaskDialog("错误", "打开窗口失败: " + ex3.Message + "\n\n请确保 UI 模块已正确加载");
				return CommandResult.Failed;
			}
		}
		catch (Exception ex4)
		{
			try
			{
				ServiceProvider.GetLogger().Error("创建市政管网命令执行失败", ex4);
			}
			catch
			{
			}
			ShowTaskDialog("错误", "命令执行失败: " + ex4.Message);
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
