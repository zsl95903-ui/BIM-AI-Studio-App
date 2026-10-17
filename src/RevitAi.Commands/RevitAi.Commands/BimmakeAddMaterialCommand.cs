using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;
using RevitAi.Abstractions.Services;

namespace RevitAi.Commands;

[Command("BimmakeAddMaterialCommand", "添加\n默认材质", "为BIMMAKE等导入的模型添加材质，支持墙、楼板、结构柱、结构框架、门、窗等元素", "AlwaysVisible", false, FeatureGroup.WallBeamColumn, "编辑", 200)]
public sealed class BimmakeAddMaterialCommand : IDynamicCommand
{
	private static class ReflectionCache
	{
		public static Type? ElementType { get; }

		public static Type? ElementIdType { get; }

		public static Type? TransactionType { get; }

		public static Type? DocumentType { get; }

		static ReflectionCache()
		{
			Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => a.GetName().Name == "RevitAPI");
			if (assembly != null)
			{
				ElementType = assembly.GetType("Autodesk.Revit.DB.ElementType");
				ElementIdType = assembly.GetType("Autodesk.Revit.DB.ElementId");
				TransactionType = assembly.GetType("Autodesk.Revit.DB.Transaction");
				DocumentType = assembly.GetType("Autodesk.Revit.DB.Document");
			}
		}
	}

	private static HashSet<object> GetElementTypesWithInstances(object document, IElementService elementService, string categoryName, ILogger? logger = null)
	{
		HashSet<object> hashSet = new HashSet<object>();
		Dictionary<int, object> dictionary = new Dictionary<int, object>();
		HashSet<int> hashSet2 = new HashSet<int>();
		try
		{
			if (ReflectionCache.ElementType == null || ReflectionCache.ElementIdType == null)
			{
				return hashSet;
			}
			PropertyInfo property = ReflectionCache.ElementIdType.GetProperty("IntegerValue");
			MethodInfo method = ReflectionCache.ElementType.GetMethod("GetTypeId");
			if (property == null || method == null)
			{
				return hashSet;
			}
			foreach (object item2 in elementService.GetElementsByCategory(document, categoryName))
			{
				try
				{
					if (ReflectionCache.ElementType.IsInstanceOfType(item2))
					{
						object obj = item2.GetType().GetProperty("Id")?.GetValue(item2);
						if (obj != null && ReflectionCache.ElementIdType.IsInstanceOfType(obj) && property.GetValue(obj) is int key)
						{
							dictionary[key] = item2;
						}
					}
					else
					{
						object obj2 = method.Invoke(item2, null);
						if (obj2 != null && ReflectionCache.ElementIdType.IsInstanceOfType(obj2) && property.GetValue(obj2) is int item)
						{
							hashSet2.Add(item);
						}
					}
				}
				catch
				{
				}
			}
			foreach (int item3 in hashSet2)
			{
				if (dictionary.ContainsKey(item3))
				{
					hashSet.Add(dictionary[item3]);
					continue;
				}
				try
				{
					object elementById = elementService.GetElementById(document, item3);
					if (elementById != null && ReflectionCache.ElementType.IsInstanceOfType(elementById))
					{
						hashSet.Add(elementById);
					}
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			logger?.Error("获取类别 '" + categoryName + "' 的有实例类型失败: " + ex.Message);
		}
		return hashSet;
	}

	public CommandResult Execute(CommandContext context)
	{
		object obj = null;
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
			IMaterialService materialService = revitAdapter.MaterialService;
			IElementService elementService = revitAdapter.ElementService;
			object activeDocument = revitAdapter.GetActiveDocument();
			if (materialService == null || elementService == null || activeDocument == null)
			{
				logger.Error("无法获取必要的服务或文档");
				ShowTaskDialog("错误", "无法获取必要的服务或文档");
				return CommandResult.Failed;
			}
			if (ReflectionCache.TransactionType == null || ReflectionCache.DocumentType == null)
			{
				logger.Error("反射缓存未初始化");
				ShowTaskDialog("错误", "反射缓存未初始化");
				return CommandResult.Failed;
			}
			obj = ReflectionCache.TransactionType.GetConstructor(new Type[2]
			{
				ReflectionCache.DocumentType,
				typeof(string)
			})?.Invoke(new object[2] { activeDocument, "BIMMAKE添加材质" });
			if (obj == null)
			{
				logger.Error("无法创建 Transaction");
				return CommandResult.Failed;
			}
			ReflectionCache.TransactionType.GetMethod("Start", Type.EmptyTypes)?.Invoke(obj, null);
			logger.Info("✅ 事务已启动");
			try
			{
				int num = 0;
				int num2 = 0;
				(int success, int failed) tuple = ProcessFloors(materialService, activeDocument, elementService, logger);
				int item = tuple.success;
				int item2 = tuple.failed;
				num += item;
				num2 += item2;
				(int success, int failed) tuple2 = ProcessStructureElements(materialService, activeDocument, elementService, logger);
				int item3 = tuple2.success;
				int item4 = tuple2.failed;
				num += item3;
				num2 += item4;
				(int success, int failed) tuple3 = ProcessWalls(materialService, activeDocument, elementService, logger);
				int item5 = tuple3.success;
				int item6 = tuple3.failed;
				num += item5;
				num2 += item6;
				(int success, int failed) tuple4 = ProcessDoors(materialService, activeDocument, elementService, logger);
				int item7 = tuple4.success;
				int item8 = tuple4.failed;
				num += item7;
				num2 += item8;
				(int success, int failed) tuple5 = ProcessWindows(materialService, activeDocument, elementService, logger);
				int item9 = tuple5.success;
				int item10 = tuple5.failed;
				num += item9;
				num2 += item10;
				ReflectionCache.TransactionType.GetMethod("Commit", Type.EmptyTypes)?.Invoke(obj, null);
				logger.Info("✅ 事务已提交");
				logger.Info($"✅ 材质添加完成！成功: {num}, 失败: {num2}");
				ShowTaskDialog("成功", $"材质添加完成！\n成功: {num}\n失败: {num2}");
				return CommandResult.Succeeded;
			}
			catch (Exception ex)
			{
				ReflectionCache.TransactionType.GetMethod("RollBack", Type.EmptyTypes)?.Invoke(obj, null);
				logger.Error("❌ 事务已回滚", ex);
				logger.Error("材质添加失败", ex);
				ShowTaskDialog("错误", "材质添加失败: " + ex.Message);
				return CommandResult.Failed;
			}
		}
		catch (Exception ex2)
		{
			if (obj != null && ReflectionCache.TransactionType != null)
			{
				try
				{
					ReflectionCache.TransactionType.GetMethod("RollBack", Type.EmptyTypes)?.Invoke(obj, null);
				}
				catch
				{
				}
			}
			try
			{
				ServiceProvider.GetLogger().Error("添加材质命令执行失败", ex2);
			}
			catch
			{
			}
			ShowTaskDialog("错误", "命令执行失败: " + ex2.Message);
			return CommandResult.Failed;
		}
	}

	private (int success, int failed) ProcessFloors(IMaterialService materialService, object document, IElementService elementService, ILogger logger)
	{
		int num = 0;
		int num2 = 0;
		try
		{
			HashSet<object> elementTypesWithInstances = GetElementTypesWithInstances(document, elementService, "楼板", logger);
			object materialByName = materialService.GetMaterialByName(document, "混凝土 - 现场浇注混凝土");
			if (materialByName != null)
			{
				int? materialId = GetMaterialId(materialByName);
				if (materialId.HasValue)
				{
					(int successCount, int failedCount) tuple = materialService.SetElementMaterialsBatch(elementTypesWithInstances, materialId.Value, document, MaterialSetStrategy.CompoundStructureLayer);
					int item = tuple.successCount;
					int item2 = tuple.failedCount;
					num += item;
					num2 += item2;
				}
			}
		}
		catch (Exception ex)
		{
			logger.Error("处理楼板失败: " + ex.Message);
			num2++;
		}
		return (success: num, failed: num2);
	}

	private (int success, int failed) ProcessStructureElements(IMaterialService materialService, object document, IElementService elementService, ILogger logger)
	{
		int num = 0;
		int num2 = 0;
		foreach (KeyValuePair<string, string> item3 in new Dictionary<string, string>
		{
			{ "结构柱", "混凝土 - 现场浇注混凝土" },
			{ "结构框架", "混凝土 - 现场浇注混凝土" },
			{ "结构基础", "混凝土砌块" },
			{ "常规模型", "混凝土 - 现场浇注混凝土" }
		})
		{
			try
			{
				HashSet<object> elementTypesWithInstances = GetElementTypesWithInstances(document, elementService, item3.Key, logger);
				object materialByName = materialService.GetMaterialByName(document, item3.Value);
				if (materialByName != null)
				{
					int? materialId = GetMaterialId(materialByName);
					if (materialId.HasValue)
					{
						(int successCount, int failedCount) tuple = materialService.SetElementMaterialsBatch(elementTypesWithInstances, materialId.Value, document, MaterialSetStrategy.TypeParameter, "材质");
						int item = tuple.successCount;
						int item2 = tuple.failedCount;
						num += item;
						num2 += item2;
					}
				}
			}
			catch (Exception ex)
			{
				logger.Error("处理" + item3.Key + "失败: " + ex.Message);
				num2++;
			}
		}
		return (success: num, failed: num2);
	}

	private (int success, int failed) ProcessWalls(IMaterialService materialService, object document, IElementService elementService, ILogger logger)
	{
		int num = 0;
		int num2 = 0;
		try
		{
			HashSet<object> elementTypesWithInstances = GetElementTypesWithInstances(document, elementService, "墙", logger);
			Dictionary<string, List<object>> dictionary = new Dictionary<string, List<object>>();
			foreach (object item3 in elementTypesWithInstances)
			{
				try
				{
					string elementName = elementService.GetElementName(item3);
					string key = (string.IsNullOrEmpty(elementName) ? "混凝土 - 现场浇注混凝土" : ((elementName.IndexOf("QTQ", StringComparison.OrdinalIgnoreCase) < 0 && elementName.IndexOf("砌体墙", StringComparison.OrdinalIgnoreCase) < 0) ? ((elementName.IndexOf("MQ", StringComparison.OrdinalIgnoreCase) < 0 && elementName.IndexOf("幕墙", StringComparison.OrdinalIgnoreCase) < 0) ? "混凝土 - 现场浇注混凝土" : "玻璃") : "混凝土砌块"));
					if (!dictionary.ContainsKey(key))
					{
						dictionary[key] = new List<object>();
					}
					dictionary[key].Add(item3);
				}
				catch (Exception ex)
				{
					logger.Error("处理墙类型失败: " + ex.Message);
				}
			}
			foreach (KeyValuePair<string, List<object>> item4 in dictionary)
			{
				if (item4.Value.Count == 0)
				{
					continue;
				}
				object materialByName = materialService.GetMaterialByName(document, item4.Key);
				if (materialByName != null)
				{
					int? materialId = GetMaterialId(materialByName);
					if (materialId.HasValue)
					{
						(int successCount, int failedCount) tuple = materialService.SetElementMaterialsBatch(item4.Value, materialId.Value, document, MaterialSetStrategy.CompoundStructureLayer);
						int item = tuple.successCount;
						int item2 = tuple.failedCount;
						num += item;
						num2 += item2;
					}
				}
			}
		}
		catch (Exception ex2)
		{
			logger.Error("处理墙失败: " + ex2.Message);
			num2++;
		}
		return (success: num, failed: num2);
	}

	private (int success, int failed) ProcessDoors(IMaterialService materialService, object document, IElementService elementService, ILogger logger)
	{
		int num = 0;
		int num2 = 0;
		try
		{
			foreach (object elementTypesWithInstance in GetElementTypesWithInstances(document, elementService, "门", logger))
			{
				try
				{
					bool num3 = materialService.SetElementMaterialByName(elementTypesWithInstance, "门 - 框架", document, MaterialSetStrategy.TypeParameter, "门框材质");
					bool flag = materialService.SetElementMaterialByName(elementTypesWithInstance, "门 - 嵌板", document, MaterialSetStrategy.TypeParameter, "门板材质");
					if (num3 | flag)
					{
						num++;
					}
					else
					{
						num2++;
					}
				}
				catch (Exception ex)
				{
					logger.Error("处理门类型失败: " + ex.Message);
					num2++;
				}
			}
		}
		catch (Exception ex2)
		{
			logger.Error("处理门失败: " + ex2.Message);
			num2++;
		}
		return (success: num, failed: num2);
	}

	private (int success, int failed) ProcessWindows(IMaterialService materialService, object document, IElementService elementService, ILogger logger)
	{
		int num = 0;
		int num2 = 0;
		try
		{
			foreach (object elementTypesWithInstance in GetElementTypesWithInstances(document, elementService, "窗", logger))
			{
				try
				{
					bool num3 = materialService.SetElementMaterialByName(elementTypesWithInstance, "玻璃", document, MaterialSetStrategy.TypeParameter, "玻璃材质");
					bool flag = materialService.SetElementMaterialByName(elementTypesWithInstance, "铝 1", document, MaterialSetStrategy.TypeParameter, "窗框材质");
					if (num3 | flag)
					{
						num++;
					}
					else
					{
						num2++;
					}
				}
				catch (Exception ex)
				{
					logger.Error("处理窗类型失败: " + ex.Message);
					num2++;
				}
			}
		}
		catch (Exception ex2)
		{
			logger.Error("处理窗失败: " + ex2.Message);
			num2++;
		}
		return (success: num, failed: num2);
	}

	private static int? GetMaterialId(object material)
	{
		try
		{
			if (ReflectionCache.ElementIdType == null)
			{
				return null;
			}
			PropertyInfo property = material.GetType().GetProperty("Id");
			if (property == null)
			{
				return null;
			}
			object value = property.GetValue(material);
			if (value is int value2)
			{
				return value2;
			}
			if (value != null && ReflectionCache.ElementIdType.IsInstanceOfType(value))
			{
				PropertyInfo property2 = ReflectionCache.ElementIdType.GetProperty("IntegerValue");
				if (property2 != null && property2.GetValue(value) is int value3)
				{
					return value3;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static void ShowTaskDialog(string title, string message)
	{
		try
		{
			if (ReflectionCache.TransactionType == null)
			{
				Console.WriteLine(title + ": " + message);
				return;
			}
			Type type = ReflectionCache.TransactionType.Assembly.GetType("Autodesk.Revit.UI.TaskDialog");
			if (type != null)
			{
				type.GetMethod("Show", new Type[2]
				{
					typeof(string),
					typeof(string)
				})?.Invoke(null, new object[2] { title, message });
			}
			else
			{
				Console.WriteLine(title + ": " + message);
			}
		}
		catch
		{
			Console.WriteLine(title + ": " + message);
		}
	}
}
