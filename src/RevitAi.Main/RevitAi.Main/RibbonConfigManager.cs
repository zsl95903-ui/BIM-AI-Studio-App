using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;
using Newtonsoft.Json;

namespace RevitAi.Main;

public static class RibbonConfigManager
{
	private static readonly string ConfigFileName = "user-ribbon-config.json";

	private static string? _configFilePath;

	private static RibbonConfigurationData? _currentConfig;

	private static readonly object _lock = new object();

	public static void Initialize(string pluginDirectory)
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAi");
		if (!Directory.Exists(text))
		{
			Directory.CreateDirectory(text);
		}
		_configFilePath = Path.Combine(text, ConfigFileName);
		LoadOrCreateConfig();
	}

	private static void LoadOrCreateConfig()
	{
		lock (_lock)
		{
			try
			{
				bool flag = false;
				if (File.Exists(_configFilePath))
				{
					_currentConfig = JsonConvert.DeserializeObject<RibbonConfigurationData>(File.ReadAllText(_configFilePath));
					if (_currentConfig != null && _currentConfig.RibbonConfiguration != null)
					{
						flag = NeedsConfigMigration(_currentConfig);
					}
				}
				else
				{
					flag = true;
				}
				if (flag)
				{
					_currentConfig = MigrateConfig(_currentConfig);
					SaveConfig();
					Logger.Info("Ribbon 配置已更新至最新版本（保留用户配置）");
				}
				else if (_currentConfig != null)
				{
					List<string> list = EnsureBasicFeatureCommands(_currentConfig);
					if (list.Count > 0)
					{
						SaveConfig();
						Logger.Info($"已自动添加 {list.Count} 个基础功能命令到 Ribbon: {string.Join(", ", list)}");
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Error("加载 Ribbon 配置失败，使用默认配置", ex);
				_currentConfig = CreateDefaultConfig();
			}
		}
	}

	private static List<string> EnsureBasicFeatureCommands(RibbonConfigurationData config)
	{
		List<string> list = new List<string>();
		if (config?.RibbonConfiguration?.Panels == null)
		{
			return list;
		}
		IReadOnlyList<RegisteredCommand> allCommands = CommandRegistry.GetAllCommands();
		PanelConfig panelConfig = config.RibbonConfiguration.Panels.FirstOrDefault((PanelConfig p) => p.PanelName == "核心功能");
		if (panelConfig == null)
		{
			panelConfig = new PanelConfig
			{
				PanelName = "核心功能",
				Commands = new List<string>()
			};
			config.RibbonConfiguration.Panels.Add(panelConfig);
		}
		HashSet<string> hashSet = new HashSet<string>(panelConfig.Commands, StringComparer.OrdinalIgnoreCase);
		foreach (RegisteredCommand item in allCommands)
		{
			if (item.Attribute.IsBasicFeature && item.Attribute.Group == FeatureGroup.CoreFeatures)
			{
				if (!hashSet.Contains(item.Name) && !config.RibbonConfiguration.HiddenCommands.Contains<string>(item.Name, StringComparer.OrdinalIgnoreCase))
				{
					panelConfig.Commands.Add(item.Name);
					list.Add(item.Name);
					hashSet.Add(item.Name);
				}
				else if (config.RibbonConfiguration.HiddenCommands.Contains<string>(item.Name, StringComparer.OrdinalIgnoreCase))
				{
					config.RibbonConfiguration.HiddenCommands.Remove(item.Name);
					panelConfig.Commands.Add(item.Name);
					list.Add(item.Name);
					hashSet.Add(item.Name);
				}
			}
		}
		return list;
	}

	private static bool NeedsConfigMigration(RibbonConfigurationData config)
	{
		if (config?.RibbonConfiguration?.Panels == null || config.RibbonConfiguration.Panels.Count == 0)
		{
			return true;
		}
		List<PanelConfig> panels = config.RibbonConfiguration.Panels;
		HashSet<string> hashSet = new HashSet<string>(panels.Select((PanelConfig p) => p.PanelName), StringComparer.OrdinalIgnoreCase);
		if (panels.Count == 1 && panels[0].PanelName == "基础功能")
		{
			Logger.Info("检测到旧版本 Ribbon 配置（基础功能模式），将自动迁移到新版本");
			return true;
		}
		string[] array = new string[3] { "AI助手", "在线族库", "应用仓库" };
		foreach (string text in array)
		{
			if (hashSet.Contains(text))
			{
				Logger.Info("检测到旧版本 Ribbon 配置（包含旧面板 '" + text + "'），将自动迁移到新版本");
				return true;
			}
		}
		array = new string[2] { "关于", "核心功能" };
		foreach (string text2 in array)
		{
			if (!hashSet.Contains(text2))
			{
				Logger.Info("Ribbon 配置缺少面板 '" + text2 + "'，将重新生成配置");
				return true;
			}
		}
		PanelConfig panelConfig = panels.FirstOrDefault((PanelConfig p) => p.PanelName == "核心功能");
		if (panelConfig != null)
		{
			IReadOnlyList<RegisteredCommand> allCommands = CommandRegistry.GetAllCommands();
			HashSet<string> hashSet2 = new HashSet<string>(panelConfig.Commands, StringComparer.OrdinalIgnoreCase);
			foreach (RegisteredCommand item in allCommands)
			{
				if (item.Attribute.IsBasicFeature && item.Attribute.Group == FeatureGroup.CoreFeatures && !hashSet2.Contains(item.Name))
				{
					Logger.Info("核心功能面板缺少基础功能命令 '" + item.Name + "'，将重新生成配置");
					return true;
				}
			}
		}
		return false;
	}

	private static RibbonConfigurationData MigrateConfig(RibbonConfigurationData? oldConfig)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (oldConfig?.RibbonConfiguration != null)
		{
			foreach (PanelConfig panel in oldConfig.RibbonConfiguration.Panels)
			{
				foreach (string command in panel.Commands)
				{
					hashSet.Add(command);
				}
			}
			foreach (string hiddenCommand in oldConfig.RibbonConfiguration.HiddenCommands)
			{
				hashSet2.Add(hiddenCommand);
			}
		}
		IReadOnlyList<RegisteredCommand> allCommands = CommandRegistry.GetAllCommands();
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		List<string> list3 = new List<string>();
		List<string> list4 = new List<string>();
		foreach (RegisteredCommand item in allCommands)
		{
			bool flag = hashSet.Contains(item.Name);
			if (hashSet2.Contains(item.Name))
			{
				list3.Add(item.Name);
			}
			else if (item.Name.Equals("AboutCommand", StringComparison.OrdinalIgnoreCase))
			{
				if (flag || item.Attribute.IsBasicFeature)
				{
					list.Add(item.Name);
				}
			}
			else if (item.Attribute.Group == FeatureGroup.CoreFeatures)
			{
				if (flag || item.Attribute.IsBasicFeature)
				{
					list2.Add(item.Name);
				}
			}
			else if (flag)
			{
				list4.Add(item.Name);
			}
			else if (item.Attribute.IsBasicFeature)
			{
				list2.Add(item.Name);
			}
			else
			{
				list3.Add(item.Name);
			}
		}
		List<PanelConfig> list5 = new List<PanelConfig>();
		if (list.Count > 0)
		{
			list5.Add(new PanelConfig
			{
				PanelName = "关于",
				Commands = list
			});
		}
		if (list2.Count > 0)
		{
			list5.Add(new PanelConfig
			{
				PanelName = "核心功能",
				Commands = list2
			});
		}
		Dictionary<string, RegisteredCommand> dictionary = allCommands.ToDictionary((RegisteredCommand c) => c.Name, (RegisteredCommand c) => c);
		foreach (string item2 in list4)
		{
			if (dictionary.TryGetValue(item2, out var value))
			{
				FeatureGroup featureGroup = value.Attribute.Group;
				string panelName = featureGroup.GetDisplayName();
				PanelConfig panelConfig = list5.FirstOrDefault((PanelConfig p) => p.PanelName == panelName);
				if (panelConfig != null)
				{
					panelConfig.Commands.Add(item2);
					continue;
				}
				list5.Add(new PanelConfig
				{
					PanelName = panelName,
					Commands = new List<string> { item2 }
				});
			}
		}
		return new RibbonConfigurationData
		{
			Version = "3.0",
			LastUpdated = DateTime.Now,
			RibbonConfiguration = new RibbonConfigData
			{
				TabName = "RevitAi",
				Panels = list5,
				HiddenCommands = list3
			}
		};
	}

	private static RibbonConfigurationData CreateDefaultConfig()
	{
		IReadOnlyList<RegisteredCommand> allCommands = CommandRegistry.GetAllCommands();
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		List<string> list3 = new List<string>();
		foreach (RegisteredCommand item in allCommands)
		{
			if (item.Attribute.IsBasicFeature)
			{
				if (item.Name.Equals("AboutCommand", StringComparison.OrdinalIgnoreCase))
				{
					list2.Add(item.Name);
				}
				else if (item.Attribute.Group == FeatureGroup.CoreFeatures)
				{
					list3.Add(item.Name);
				}
				else
				{
					list3.Add(item.Name);
				}
			}
			else
			{
				list.Add(item.Name);
			}
		}
		List<PanelConfig> list4 = new List<PanelConfig>();
		if (list2.Count > 0)
		{
			list4.Add(new PanelConfig
			{
				PanelName = "关于",
				Commands = list2
			});
		}
		if (list3.Count > 0)
		{
			list4.Add(new PanelConfig
			{
				PanelName = "核心功能",
				Commands = list3
			});
		}
		return new RibbonConfigurationData
		{
			Version = "3.0",
			LastUpdated = DateTime.Now,
			RibbonConfiguration = new RibbonConfigData
			{
				TabName = "RevitAi",
				Panels = list4,
				HiddenCommands = new List<string>(list)
			}
		};
	}

	public static void SaveConfig()
	{
		lock (_lock)
		{
			try
			{
				if (_currentConfig != null && _configFilePath != null)
				{
					_currentConfig.LastUpdated = DateTime.Now;
					_currentConfig.Version = "3.0";
					string contents = JsonConvert.SerializeObject(_currentConfig, Formatting.Indented);
					File.WriteAllText(_configFilePath, contents);
				}
			}
			catch (Exception ex)
			{
				Logger.Error("保存 Ribbon 配置失败", ex);
			}
		}
	}

	public static RibbonConfigurationData? GetCurrentConfig()
	{
		lock (_lock)
		{
			return _currentConfig;
		}
	}

	public static void HideCommand(string commandName)
	{
		lock (_lock)
		{
			if (_currentConfig == null)
			{
				return;
			}
			foreach (PanelConfig panel in _currentConfig.RibbonConfiguration.Panels)
			{
				panel.Commands.Remove(commandName);
			}
			if (!_currentConfig.RibbonConfiguration.HiddenCommands.Contains(commandName))
			{
				_currentConfig.RibbonConfiguration.HiddenCommands.Add(commandName);
			}
			SaveConfig();
		}
	}

	public static string GetCommandTargetPanel(string commandName)
	{
		if (commandName.Equals("AboutCommand", StringComparison.OrdinalIgnoreCase))
		{
			return "关于";
		}
		if (commandName.Equals("AICommand", StringComparison.OrdinalIgnoreCase) || commandName.Equals("OpenAIChatCommand", StringComparison.OrdinalIgnoreCase))
		{
			return "核心功能";
		}
		if (commandName.Equals("FeatureStoreCommand", StringComparison.OrdinalIgnoreCase))
		{
			return "核心功能";
		}
		if (commandName.Equals("FamilyLibraryCommand", StringComparison.OrdinalIgnoreCase))
		{
			return "核心功能";
		}
		RegisteredCommand command = CommandRegistry.GetCommand(commandName);
		if (command != null)
		{
			return command.Attribute.Group.GetDisplayName();
		}
		return "核心功能";
	}

	public static void ShowCommand(string commandName, string targetPanelName)
	{
		lock (_lock)
		{
			if (_currentConfig == null)
			{
				return;
			}
			_currentConfig.RibbonConfiguration.HiddenCommands.Remove(commandName);
			PanelConfig panelConfig = _currentConfig.RibbonConfiguration.Panels.FirstOrDefault((PanelConfig p) => p.PanelName == targetPanelName);
			if (panelConfig != null)
			{
				if (!panelConfig.Commands.Contains(commandName))
				{
					panelConfig.Commands.Add(commandName);
				}
			}
			else
			{
				Logger.Info("创建新面板: " + targetPanelName);
				PanelConfig item = new PanelConfig
				{
					PanelName = targetPanelName,
					Commands = new List<string> { commandName }
				};
				_currentConfig.RibbonConfiguration.Panels.Add(item);
			}
			SaveConfig();
		}
	}

	public static string? GetConfigFilePath()
	{
		return _configFilePath;
	}
}
