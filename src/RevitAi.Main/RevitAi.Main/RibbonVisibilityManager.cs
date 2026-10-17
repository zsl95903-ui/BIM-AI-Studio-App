using System;
using System.Collections.Generic;
using System.Linq;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Main;

public static class RibbonVisibilityManager
{
	private static readonly Dictionary<string, bool> _commandVisibility = new Dictionary<string, bool>();

	private static readonly object _lock = new object();

	public static void Initialize()
	{
		lock (_lock)
		{
			_commandVisibility.Clear();
			RibbonConfigurationData currentConfig = RibbonConfigManager.GetCurrentConfig();
			if (currentConfig != null)
			{
				HashSet<string> hashSet = new HashSet<string>();
				foreach (PanelConfig panel in currentConfig.RibbonConfiguration.Panels)
				{
					foreach (string command in panel.Commands)
					{
						hashSet.Add(command);
					}
				}
				foreach (RegisteredCommand allCommand in CommandRegistry.GetAllCommands())
				{
					bool value = hashSet.Contains(allCommand.Name) && !currentConfig.RibbonConfiguration.HiddenCommands.Contains(allCommand.Name);
					_commandVisibility[allCommand.Name] = value;
				}
				int num = _commandVisibility.Values.Count((bool v) => v);
				int value2 = _commandVisibility.Count - num;
				Logger.Info($"可见性管理器已初始化：{_commandVisibility.Count} 个命令（可见: {num}, 隐藏: {value2}）");
			}
			else
			{
				Logger.Warning("无法加载配置，所有命令将默认可见");
			}
		}
	}

	public static void SetCommandVisibility(string commandName, bool isVisible)
	{
		lock (_lock)
		{
			if (!_commandVisibility.ContainsKey(commandName))
			{
				Logger.Warning("命令不存在: " + commandName);
				return;
			}
			_ = _commandVisibility[commandName];
			_commandVisibility[commandName] = isVisible;
		}
	}

	public static void SetCommandVisibilityAndSave(string commandName, bool isVisible)
	{
		SetCommandVisibility(commandName, isVisible);
		RealTimeRibbonManager.SetButtonVisible(commandName, isVisible);
		try
		{
			if (isVisible)
			{
				string commandTargetPanel = RibbonConfigManager.GetCommandTargetPanel(commandName);
				RibbonConfigManager.ShowCommand(commandName, commandTargetPanel);
			}
			else
			{
				RibbonConfigManager.HideCommand(commandName);
			}
		}
		catch (Exception ex)
		{
			Logger.Error("保存命令可见性失败: " + commandName, ex);
		}
	}

	public static bool GetCommandVisibility(string commandName)
	{
		lock (_lock)
		{
			if (_commandVisibility.TryGetValue(commandName, out var value))
			{
				return value;
			}
			return true;
		}
	}

	public static bool ToggleCommandVisibility(string commandName)
	{
		lock (_lock)
		{
			if (!_commandVisibility.ContainsKey(commandName))
			{
				Logger.Warning("命令不存在: " + commandName);
				return false;
			}
			bool flag = _commandVisibility[commandName];
			bool flag2 = !flag;
			_commandVisibility[commandName] = flag2;
			Logger.Info($"命令 '{commandName}' 可见性已切换: {flag} → {flag2}");
			return flag2;
		}
	}

	public static Dictionary<string, bool> GetAllVisibility()
	{
		lock (_lock)
		{
			return new Dictionary<string, bool>(_commandVisibility);
		}
	}

	public static void SaveVisibilityToConfig()
	{
		try
		{
			RibbonConfigurationData currentConfig = RibbonConfigManager.GetCurrentConfig();
			if (currentConfig == null)
			{
				Logger.Warning("无法保存可见性：配置为空");
				return;
			}
			lock (_lock)
			{
				foreach (PanelConfig panel in currentConfig.RibbonConfiguration.Panels)
				{
					panel.Commands.Clear();
				}
				Dictionary<string, PanelConfig> dictionary = currentConfig.RibbonConfiguration.Panels.ToDictionary<PanelConfig, string, PanelConfig>((PanelConfig p) => p.PanelName, (PanelConfig p) => p, StringComparer.OrdinalIgnoreCase);
				foreach (KeyValuePair<string, bool> item in _commandVisibility)
				{
					if (!item.Value)
					{
						continue;
					}
					string commandTargetPanel = RibbonConfigManager.GetCommandTargetPanel(item.Key);
					if (dictionary.TryGetValue(commandTargetPanel, out var value))
					{
						if (!value.Commands.Contains(item.Key))
						{
							value.Commands.Add(item.Key);
						}
					}
					else
					{
						Logger.Warning("找不到目标面板: " + commandTargetPanel);
					}
				}
				RibbonConfigManager.SaveConfig();
				Logger.Info("可见性状态已保存到配置文件");
			}
		}
		catch (Exception ex)
		{
			Logger.Error("保存可见性状态失败", ex);
		}
	}

	public static void RefreshRibbon()
	{
		Logger.Info("Ribbon 可见性已更新，点击任意按钮后生效");
	}
}
