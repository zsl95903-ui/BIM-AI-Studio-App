using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;
using RevitAi.Abstractions.UI;

namespace RevitAi.Main;

public static class RealTimeRibbonManager
{
	private static Dictionary<FeatureGroup, IRibbonPanel>? _groupPanels;

	private static Dictionary<string, IRibbonPanel>? _basicPanels;

	private static Dictionary<string, IPushButton> _buttons = new Dictionary<string, IPushButton>();

	private static Dictionary<string, FeatureGroup> _commandToGroupMap = new Dictionary<string, FeatureGroup>();

	public static void CreateRealTimeRibbon(IUIApplication uiApp)
	{
		if (uiApp == null)
		{
			throw new ArgumentNullException("uiApp");
		}
		try
		{
			RibbonVisibilityManager.Initialize();
			uiApp.CreateRibbonTab("ToolLayer");
			_basicPanels = new Dictionary<string, IRibbonPanel>();
			_basicPanels["关于"] = uiApp.CreateRibbonPanel("ToolLayer", "关于");
			_basicPanels["核心功能"] = uiApp.CreateRibbonPanel("ToolLayer", "核心功能");
			_groupPanels = new Dictionary<FeatureGroup, IRibbonPanel>();
			foreach (FeatureGroup value3 in Enum.GetValues(typeof(FeatureGroup)))
			{
				if (value3 != FeatureGroup.CoreFeatures)
				{
					string displayName = value3.GetDisplayName();
					IRibbonPanel value = uiApp.CreateRibbonPanel("ToolLayer", displayName);
					_groupPanels[value3] = value;
				}
			}
			List<RegisteredCommand> list = (from c in CommandRegistry.GetAllCommands()
				orderby c.Attribute.Group
				select c).ThenBy<RegisteredCommand, string>((RegisteredCommand c) => c.Attribute.SubCategory, StringComparer.Ordinal).ThenBy((RegisteredCommand c) => c.Attribute.Order).ThenBy<RegisteredCommand, string>((RegisteredCommand c) => c.Attribute.Text, StringComparer.Ordinal)
				.ToList();
			int num = 0;
			Dictionary<string, int> dictionary = new Dictionary<string, int>();
			foreach (RegisteredCommand item in list)
			{
				IRibbonPanel ribbonPanel;
				if (item.Name.Equals("AboutCommand", StringComparison.OrdinalIgnoreCase))
				{
					ribbonPanel = _basicPanels["关于"];
				}
				else if (item.Attribute.Group == FeatureGroup.CoreFeatures)
				{
					ribbonPanel = _basicPanels["核心功能"];
				}
				else
				{
					FeatureGroup featureGroup2 = item.Attribute.Group;
					if (!_groupPanels.TryGetValue(featureGroup2, out IRibbonPanel value2))
					{
						Logger.Warning($"功能组 {featureGroup2} 没有对应的面板，跳过命令: {item.Name}");
						continue;
					}
					ribbonPanel = value2;
				}
				string availabilityClassName = GetAvailabilityClassName(item.Name);
				string iconPath = LoadIconFilePath(item.Name);
				IPushButton pushButton = ribbonPanel.AddPushButton(item.Name, item.Attribute.Text, iconPath, item.Attribute.Description);
				if (!string.IsNullOrEmpty(availabilityClassName))
				{
					pushButton.AvailabilityClassName = availabilityClassName;
				}
				else
				{
					Logger.Warning("命令 '" + item.Name + "' 没有 Availability 类，将始终可用");
				}
				bool commandVisibility = RibbonVisibilityManager.GetCommandVisibility(item.Name);
				pushButton.Visible = commandVisibility;
				_buttons[item.Name] = pushButton;
				_commandToGroupMap[item.Name] = item.Attribute.Group;
				num++;
				string name = ribbonPanel.Name;
				if (!dictionary.ContainsKey(name))
				{
					dictionary[name] = 0;
				}
				dictionary[name]++;
			}
			ApplyPanelVisibility();
		}
		catch (Exception ex)
		{
			Logger.Error("创建实时 Ribbon 失败", ex);
			throw;
		}
	}

	public static void ApplyPanelVisibility()
	{
		try
		{
			if (_groupPanels == null || _basicPanels == null)
			{
				Logger.Warning("面板未初始化，无法应用可见性");
				return;
			}
			UpdateGroupPanelVisibility();
			foreach (KeyValuePair<string, IRibbonPanel> basicPanel in _basicPanels)
			{
				basicPanel.Value.Visible = true;
			}
			int num = _groupPanels.Count<KeyValuePair<FeatureGroup, IRibbonPanel>>((KeyValuePair<FeatureGroup, IRibbonPanel> kvp) => kvp.Value.Visible);
			int value = _groupPanels.Count - num;
			Logger.Info($"面板可见性已应用：基础面板 {_basicPanels.Count} 个（始终可见），功能组面板 可见 {num} 个，隐藏 {value} 个");
		}
		catch (Exception ex)
		{
			Logger.Error("应用面板可见性失败", ex);
		}
	}

	private static string GetWrapperClassName(string commandName)
	{
		return "RevitAi.Main.Commands." + commandName + "Wrapper";
	}

	private static string GetAvailabilityClassName(string commandName)
	{
		try
		{
			string text = "RevitAi.Revit.UI.Availabilities." + commandName + "Availability";
			MainApplication instance = MainApplication.Instance;
			if (instance == null)
			{
				Logger.Debug("MainApplication.Instance 为 null，无法加载 Availability 类");
				return string.Empty;
			}
			Assembly adapterAssembly = instance.AdapterAssembly;
			if (adapterAssembly == null)
			{
				Logger.Debug("适配器程序集未加载");
				return string.Empty;
			}
			if (adapterAssembly.GetType(text) != null)
			{
				return text;
			}
			Logger.Debug("Availability 类不存在: " + text);
			return string.Empty;
		}
		catch (Exception ex)
		{
			Logger.Debug("获取 Availability 类名失败: " + commandName + " - " + ex.Message);
			return string.Empty;
		}
	}

	public static void NotifyRibbonChanged()
	{
		Logger.Info("========================================");
		Logger.Info("Ribbon 配置已更新");
		Logger.Info("提示: 点击任意按钮或切换文档后刷新可见性");
		Logger.Info("========================================");
		ApplyPanelVisibility();
	}

	private static string? LoadIconFilePath(string iconName)
	{
		try
		{
			string location = Assembly.GetExecutingAssembly().Location;
			if (string.IsNullOrEmpty(location))
			{
				return null;
			}
			string directoryName = Path.GetDirectoryName(location);
			if (string.IsNullOrEmpty(directoryName))
			{
				return null;
			}
			DirectoryInfo directoryInfo = new DirectoryInfo(directoryName);
			for (int i = 0; i <= 2; i++)
			{
				string text = Path.Combine(directoryInfo.FullName, "Resources", "Icons", iconName + ".png");
				if (File.Exists(text))
				{
					string text2 = CenterIcon(text);
					if (text2 != null)
					{
						return text2;
					}
					return text;
				}
				if (directoryInfo.Parent == null)
				{
					break;
				}
				directoryInfo = directoryInfo.Parent;
			}
			Logger.Debug("未找到图标文件: " + iconName);
			return null;
		}
		catch (Exception ex)
		{
			Logger.Warning("查找图标文件失败: " + iconName + " - " + ex.Message);
			return null;
		}
	}

	private static string? CenterIcon(string originalPath)
	{
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			using FileStream bitmapStream = new FileStream(originalPath, FileMode.Open, FileAccess.Read);
			BitmapFrame bitmapFrame = new PngBitmapDecoder(bitmapStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
			if (bitmapFrame.PixelWidth == 32 && bitmapFrame.PixelHeight == 32)
			{
				Int32Rect contentBounds = DetectContentBounds(bitmapFrame);
				if (((Int32Rect)contentBounds).IsEmpty)
				{
					return null;
				}
				double num = (double)((Int32Rect)contentBounds).X + (double)((Int32Rect)contentBounds).Width / 2.0;
				double num2 = (double)((Int32Rect)contentBounds).Y + (double)((Int32Rect)contentBounds).Height / 2.0;
				double num3 = 16.0;
				double num4 = 16.0;
				if (Math.Abs(num - num3) <= 2.0 && Math.Abs(num2 - num4) <= 2.0)
				{
					return null;
				}
				BitmapSource bitmapSource = CreateCenteredIcon(bitmapFrame, contentBounds);
				if (bitmapSource == null)
				{
					return null;
				}
				string text = Path.Combine(Path.GetTempPath(), $"RevitAi.Centered.{Guid.NewGuid()}.png");
				using FileStream stream = new FileStream(text, FileMode.Create);
				PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
				pngBitmapEncoder.Frames.Add(BitmapFrame.Create(bitmapSource));
				pngBitmapEncoder.Save(stream);
				return text;
			}
			return null;
		}
		catch (Exception ex)
		{
			Logger.Warning("居中图标失败: " + originalPath + " - " + ex.Message);
			return null;
		}
	}

	private static Int32Rect DetectContentBounds(BitmapSource source)
	{
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		int num = source.PixelWidth * 4;
		byte[] array = new byte[source.PixelHeight * num];
		source.CopyPixels(array, num, 0);
		int num2 = source.PixelWidth;
		int num3 = source.PixelHeight;
		int num4 = 0;
		int num5 = 0;
		bool flag = false;
		for (int i = 0; i < source.PixelHeight; i++)
		{
			for (int j = 0; j < source.PixelWidth; j++)
			{
				int num6 = i * num + j * 4;
				if (array[num6 + 3] > 10)
				{
					if (j < num2)
					{
						num2 = j;
					}
					if (j > num4)
					{
						num4 = j;
					}
					if (i < num3)
					{
						num3 = i;
					}
					if (i > num5)
					{
						num5 = i;
					}
					flag = true;
				}
			}
		}
		if (!flag)
		{
			return Int32Rect.Empty;
		}
		return new Int32Rect(num2, num3, num4 - num2 + 1, num5 - num3 + 1);
	}

	private static BitmapSource? CreateCenteredIcon(BitmapSource source, Int32Rect contentBounds)
	{
		try
		{
			int num = 32;
			int num2 = 32;
			int num3 = (num - ((Int32Rect)contentBounds).Width) / 2;
			int num4 = (num2 - ((Int32Rect)contentBounds).Height) / 2;
			int num5 = source.PixelWidth * 4;
			byte[] array = new byte[source.PixelHeight * num5];
			source.CopyPixels(array, num5, 0);
			int num6 = num * 4;
			byte[] array2 = new byte[num2 * num6];
			for (int i = 0; i < ((Int32Rect)contentBounds).Height; i++)
			{
				for (int j = 0; j < ((Int32Rect)contentBounds).Width; j++)
				{
					int num7 = ((Int32Rect)contentBounds).X + j;
					int num8 = ((Int32Rect)contentBounds).Y + i;
					int num9 = num3 + j;
					int num10 = num4 + i;
					if (num9 >= 0 && num9 < num && num10 >= 0 && num10 < num2)
					{
						int num11 = num8 * num5 + num7 * 4;
						int num12 = num10 * num6 + num9 * 4;
						for (int k = 0; k < 4; k++)
						{
							array2[num12 + k] = array[num11 + k];
						}
					}
				}
			}
			return BitmapSource.Create(num, num2, 96.0, 96.0, PixelFormats.Bgra32, null, array2, num6);
		}
		catch (Exception ex)
		{
			Logger.Warning("创建居中图标失败: " + ex.Message);
			return null;
		}
	}

	private static void UpdateGroupPanelVisibility()
	{
		if (_groupPanels == null)
		{
			return;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AboutCommand", "AICommand", "OpenAIChatCommand", "FeatureStoreCommand", "FamilyLibraryCommand" };
		foreach (KeyValuePair<FeatureGroup, IRibbonPanel> groupPanel in _groupPanels)
		{
			FeatureGroup key = groupPanel.Key;
			IRibbonPanel value = groupPanel.Value;
			bool visible = false;
			foreach (KeyValuePair<string, IPushButton> button in _buttons)
			{
				if (!hashSet.Contains(button.Key) && _commandToGroupMap.TryGetValue(button.Key, out var value2) && value2 == key && button.Value.Visible)
				{
					visible = true;
					break;
				}
			}
			value.Visible = visible;
		}
	}

	public static void SetButtonVisible(string commandName, bool visible)
	{
		try
		{
			if (_buttons.TryGetValue(commandName, out IPushButton value))
			{
				value.Visible = visible;
				UpdateGroupPanelVisibility();
			}
			else
			{
				Logger.Warning("找不到按钮: " + commandName);
			}
		}
		catch (Exception ex)
		{
			Logger.Error("设置按钮可见性失败: " + commandName, ex);
		}
	}
}
