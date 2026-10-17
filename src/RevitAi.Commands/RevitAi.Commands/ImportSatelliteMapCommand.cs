using System;
using System.Reflection;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;
using RevitAi.Abstractions.Models;
using RevitAi.Abstractions.Revit;

namespace RevitAi.Commands;

[Command("ImportSatelliteMapCommand", "导入\n卫星图像", "打开地图浏览器选择卫星图范围并导入到 Revit", "AlwaysVisible", false, FeatureGroup.Infrastructure, "场地地形", 150)]
public sealed class ImportSatelliteMapCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		ILogger logger = ServiceProvider.GetLogger();
		try
		{
			ISatelliteMapImportService importService = ServiceProvider.GetService<ISatelliteMapImportService>();
			if (importService == null)
			{
				logger.Error("[ImportSatelliteMapCommand] 无法获取卫星图导入服务");
				return CommandResult.Failed;
			}
			importService.PrepareImport();
			Type uiBootstrapperType = Type.GetType("RevitAi.UI.Services.UIBootstrapper, RevitAi.UI");
			Action<MapSelectionCompletedEventArgs> action = delegate(MapSelectionCompletedEventArgs args)
			{
				try
				{
					importService.StartImport(args.BoundingBox, args.MapSource, 18, args.TiandituToken, args.GoogleMapsApiKey, delegate
					{
						try
						{
							if (!(uiBootstrapperType == null))
							{
								uiBootstrapperType.GetMethod("CloseMapSelectionWindow", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
							}
						}
						catch (Exception ex4)
						{
							logger.Warning("[ImportSatelliteMapCommand] 关闭窗口失败: " + ex4.Message);
						}
					});
				}
				catch (Exception ex3)
				{
					logger.Error("[ImportSatelliteMapCommand] 处理用户选择失败: " + ex3.Message);
				}
			};
			double? num = null;
			double? num2 = null;
			try
			{
				object obj = null;
				if (context.Application != null)
				{
					object obj2 = context.Application.GetType().GetProperty("ActiveUIDocument")?.GetValue(context.Application);
					if (obj2 != null)
					{
						obj = obj2.GetType().GetProperty("Document")?.GetValue(obj2);
					}
				}
				if (obj != null)
				{
					GeoReferencePoint referencePoint = importService.GetReferencePoint(obj);
					if (referencePoint != null)
					{
						num = referencePoint.CenterLat;
						num2 = referencePoint.CenterLon;
						logger.Info($"[ImportSatelliteMapCommand] 使用上次导入位置: ({num}, {num2})");
					}
				}
			}
			catch (Exception ex)
			{
				logger.Debug("[ImportSatelliteMapCommand] 获取上次导入参考点失败，将使用 IP 定位: " + ex.Message);
			}
			if (uiBootstrapperType == null)
			{
				logger.Error("[ImportSatelliteMapCommand] 无法找到 UIBootstrapper 类型");
				return CommandResult.Failed;
			}
			MethodInfo method = uiBootstrapperType.GetMethod("ShowMapSelectionWindowWithCallback", BindingFlags.Static | BindingFlags.Public);
			if (method == null)
			{
				logger.Error("[ImportSatelliteMapCommand] ShowMapSelectionWindowWithCallback 方法未找到");
				return CommandResult.Failed;
			}
			method.Invoke(null, new object[3] { action, num, num2 });
			return CommandResult.Succeeded;
		}
		catch (Exception ex2)
		{
			logger.Error("[ImportSatelliteMapCommand] 打开窗口失败", ex2);
			return CommandResult.Failed;
		}
	}
}
