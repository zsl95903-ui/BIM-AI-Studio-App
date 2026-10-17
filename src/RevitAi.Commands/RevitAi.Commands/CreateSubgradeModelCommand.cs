using System;
using System.Linq;
using System.Reflection;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("CreateSubgradeModelCommand", "创建\n路基路面", "基于已有路线数据，定义路基层结构并创建路基三维模型", "AlwaysVisible", false, FeatureGroup.Infrastructure, "路桥", 110)]
public sealed class CreateSubgradeModelCommand : IDynamicCommand
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
				ShowTaskDialog("错误", "无法获取模块加载器");
				return CommandResult.Failed;
			}
			try
			{
				MethodInfo method = moduleLoader.GetType().GetMethod("ShowSubgradeModelWindow");
				if (method != null)
				{
					method.Invoke(moduleLoader, null);
					logger.Info("道路路基建模窗口已打开");
					return CommandResult.Succeeded;
				}
				logger.Error("未找到 ShowSubgradeModelWindow 方法");
				ShowTaskDialog("错误", "UI 窗口尚未实现，请等待后续版本更新");
				return CommandResult.Failed;
			}
			catch (Exception ex)
			{
				logger.Error("打开道路路基建模窗口失败", ex);
				ShowTaskDialog("错误", "打开窗口失败: " + ex.Message + "\n\n请确保 UI 模块已正确加载");
				return CommandResult.Failed;
			}
		}
		catch (Exception ex2)
		{
			try
			{
				ServiceProvider.GetLogger().Error("道路路基建模命令执行失败", ex2);
			}
			catch
			{
			}
			ShowTaskDialog("错误", "命令执行失败: " + ex2.Message);
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
