using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace RevitAi.Loader;

public sealed class ExternalApplication : IExternalApplication
{
	private static string? _pluginDirectory;

	private static string? _installPath;

	private object? _mainApplication;

	private Assembly? _mainAssembly;

	public Result OnStartup(UIControlledApplication application)
	{
		//IL_0317: Unknown result type (might be due to invalid IL or missing references)
		//IL_031b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_014c: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0302: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f1: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			WriteLog("========================================");
			WriteLog("RevitAi.Loader 启动中...");
			WriteLog("Revit 版本: " + application.ControlledApplication.VersionNumber);
			WriteLog("Revit Build: " + application.ControlledApplication.VersionBuild);
			_pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			WriteLog("插件目录: " + _pluginDirectory);
			_installPath = GetInstallPath();
			WriteLog("安装路径: " + _installPath);
			string versionPath = GetVersionPath(application);
			WriteLog("版本目录: " + versionPath);
			string text = Path.Combine(versionPath, "RevitAi.Main.dll");
			if (!File.Exists(text))
			{
				WriteLog("错误: 找不到主程序集: " + text);
				WriteLog("请确保已正确安装 RevitAi");
				return (Result)(-1);
			}
			WriteLog("加载主程序集: " + text);
			_mainAssembly = Assembly.LoadFrom(text);
			WriteLog("主程序集加载成功");
			WriteLog("创建主应用程序实例");
			Type type = _mainAssembly.GetType("RevitAi.MainApplication");
			if (type == null)
			{
				WriteLog("错误: 在主程序集中找不到 RevitAi.MainApplication 类型");
				return (Result)(-1);
			}
			_mainApplication = Activator.CreateInstance(type);
			WriteLog("调用主应用程序的 OnStartup");
			MethodInfo method = type.GetMethod("OnStartup", new Type[2]
			{
				typeof(object),
				typeof(object)
			});
			object obj;
			if (method != null)
			{
				obj = method.Invoke(_mainApplication, new object[2] { application, null });
			}
			else
			{
				method = type.GetMethod("OnStartup", new Type[1] { typeof(object) });
				if (method == null)
				{
					WriteLog("错误: 找不到 OnStartup 方法");
					return (Result)(-1);
				}
				obj = method.Invoke(_mainApplication, new object[1] { application });
			}
			Type type2 = obj?.GetType();
			if (type2 != null && type2.FullName == "RevitAi.Abstractions.Common.Result")
			{
				PropertyInfo property = type2.GetProperty("IsSuccess");
				if (property != null)
				{
					if ((bool)(property.GetValue(obj) ?? ((object)false)))
					{
						WriteLog("RevitAi 启动完成");
						WriteLog("========================================");
						return (Result)0;
					}
					string text2 = (type2.GetProperty("Error")?.GetValue(obj) as string) ?? "未知错误";
					WriteLog("错误: 主应用程序启动失败 - " + text2);
					return (Result)(-1);
				}
			}
			WriteLog("错误: 主应用程序启动失败 - 返回值类型不匹配");
			return (Result)(-1);
		}
		catch (Exception ex)
		{
			WriteLogError("启动失败", ex);
			return (Result)(-1);
		}
	}

	public Result OnShutdown(UIControlledApplication application)
	{
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			WriteLog("RevitAi.Loader 关闭中...");
			if (_mainApplication != null)
			{
				Type type = _mainApplication.GetType();
				MethodInfo method = type.GetMethod("OnShutdown", new Type[1] { typeof(object) });
				if (method != null)
				{
					WriteLog("调用主应用程序的 OnShutdown");
					method.Invoke(_mainApplication, new object[1] { application });
				}
			}
			WriteLog("RevitAi.Loader 已关闭");
			WriteLog("========================================");
			return (Result)0;
		}
		catch (Exception ex)
		{
			WriteLogError("关闭时发生错误", ex);
			return (Result)(-1);
		}
	}

	private static string? GetInstallPath()
	{
		string path = Path.Combine(_pluginDirectory, "config.json");
		if (File.Exists(path))
		{
			try
			{
				string text = File.ReadAllText(path);
				string[] array = text.Split('\n');
				string[] array2 = array;
				foreach (string text2 in array2)
				{
					if (!text2.Contains("\"InstallPath\""))
					{
						continue;
					}
					int num = text2.IndexOf("\"") + 1;
					int num2 = text2.LastIndexOf("\"");
					if (num > 0 && num2 > num)
					{
						string text3 = text2.Substring(num, num2 - num).Trim();
						if (Directory.Exists(text3))
						{
							WriteLog("从配置文件读取安装路径: " + text3);
							return text3;
						}
					}
				}
			}
			catch (Exception ex)
			{
				LoaderLogger.Error("读取配置文件失败", ex);
			}
		}
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\AS\\RevitAi");
			object obj = registryKey?.GetValue("InstallPath");
			string text4 = obj as string;
			if (!string.IsNullOrEmpty(text4) && Directory.Exists(text4))
			{
				WriteLog("从注册表读取安装路径: " + text4);
				return text4;
			}
		}
		catch
		{
		}
		string directoryName = Path.GetDirectoryName(_pluginDirectory);
		WriteLog("使用默认安装路径: " + directoryName);
		return directoryName;
	}

	private static string GetVersionPath(UIControlledApplication application)
	{
		string versionNumber = application.ControlledApplication.VersionNumber;
		string versionGroup = GetVersionGroup(versionNumber);
		return Path.Combine(_installPath, versionGroup);
	}

	private static string GetVersionGroup(string revitVersion)
	{
		if (int.TryParse(revitVersion, out var result))
		{
			if (result == 2027)
			{
				return "R2027";
			}
			if (result == 2026)
			{
				return "R2026";
			}
			if (result == 2025)
			{
				return "R2025";
			}
			if (result == 2024)
			{
				return "R2024";
			}
			if (result >= 2021)
			{
				return "R2021_R2023";
			}
			return "R2018_R2020";
		}
		return "R2018_R2020";
	}

	private static void WriteLog(string message)
	{
		LoaderLogger.Info(message);
	}

	private static void WriteLogError(string message, Exception? ex = null)
	{
		if (ex != null)
		{
			LoaderLogger.Error(message, ex);
		}
		else
		{
			LoaderLogger.Error(message);
		}
	}
}
