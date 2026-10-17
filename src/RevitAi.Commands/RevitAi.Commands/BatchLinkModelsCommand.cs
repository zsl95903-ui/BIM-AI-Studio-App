using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RevitAi.Abstractions.Adapters;
using RevitAi.Abstractions.LinkManagement;
using RevitAi.Abstractions.Loader;
using RevitAi.Abstractions.Logging;

namespace RevitAi.Commands;

[Command("BatchLinkModelsCommand", "批量链接", "批量选择模型文件，链接到当前模型中", "AlwaysVisible", false, FeatureGroup.BatchSplitAndMerge, "链接", 100)]
public sealed class BatchLinkModelsCommand : IDynamicCommand
{
	public CommandResult Execute(CommandContext context)
	{
		try
		{
			ILogger logger = ServiceProvider.GetLogger();
			IRevitAdapter revitAdapter = ServiceProvider.GetModuleLoader().RevitAdapter;
			if (revitAdapter == null)
			{
				logger.Error("[BatchLinkModelsCommand] 无法获取 Revit 适配器");
				ShowMessage("无法获取 Revit 适配器", success: false);
				return CommandResult.Failed;
			}
			object document = revitAdapter.GetActiveDocument();
			if (document == null)
			{
				logger.Error("[BatchLinkModelsCommand] 无法获取 Revit 文档");
				ShowMessage("无法获取 Revit 文档，请确保在 Revit 中运行此命令", success: false);
				return CommandResult.Failed;
			}
			object externalEvent = null;
			logger.Info("[BatchLinkModelsCommand] 获取批量链接 ExternalEvent");
			try
			{
				MethodInfo method = revitAdapter.GetType().GetMethod("GetBatchLinkModelsExternalEvent");
				if (!(method != null))
				{
					logger.Error("未找到 GetBatchLinkModelsExternalEvent 方法");
					ShowMessage("未找到批量链接功能，请确保 Revit 适配层已正确加载", success: false);
					return CommandResult.Failed;
				}
				externalEvent = method.Invoke(revitAdapter, null);
				if (externalEvent == null)
				{
					logger.Error("GetBatchLinkModelsExternalEvent 返回 null");
					ShowMessage("无法获取批量链接 ExternalEvent", success: false);
					return CommandResult.Failed;
				}
				logger.Info("✅ 成功获取 ExternalEvent");
			}
			catch (Exception ex)
			{
				logger.Error("获取 ExternalEvent 失败: " + ex.Message, ex);
				ShowMessage("获取批量链接 ExternalEvent 失败: " + ex.Message, success: false);
				return CommandResult.Failed;
			}
			Task.Run(delegate
			{
				ExecuteBatchLink(revitAdapter, document, externalEvent);
			});
			return CommandResult.Succeeded;
		}
		catch (Exception ex2)
		{
			try
			{
				ServiceProvider.GetLogger().Error("批量链接模型命令执行失败", ex2);
				ShowMessage("批量链接模型命令执行失败: " + ex2.Message, success: false);
			}
			catch
			{
			}
			return CommandResult.Failed;
		}
	}

	private void ExecuteBatchLink(object revitAdapter, object document, object externalEvent)
	{
		ILogger logger = ServiceProvider.GetLogger();
		try
		{
			List<string> selectedFiles = ShowFileDialog();
			if (selectedFiles == null || selectedFiles.Count == 0)
			{
				logger.Info("[BatchLinkModelsCommand] 用户取消文件选择");
				return;
			}
			logger.Info($"[BatchLinkModelsCommand] 用户选择了 {selectedFiles.Count} 个文件");
			Type? typeFromHandle = typeof(BatchLinkModelsExternalEventRequest);
			object obj = Activator.CreateInstance(typeFromHandle);
			PropertyInfo property = typeFromHandle.GetProperty("FilePaths");
			PropertyInfo property2 = typeFromHandle.GetProperty("Document");
			PropertyInfo? property3 = typeFromHandle.GetProperty("OnCompleted");
			property?.SetValue(obj, selectedFiles);
			property2?.SetValue(obj, document);
			TaskCompletionSource<(int successCount, int totalCount, Exception? exception)> tcs = new TaskCompletionSource<(int, int, Exception)>();
			property3?.SetValue(obj, (Action<Exception, int>)delegate(Exception? ex4, int count)
			{
				if (ex4 != null)
				{
					tcs.SetResult((0, selectedFiles.Count, ex4));
				}
				else
				{
					tcs.SetResult((count, selectedFiles.Count, null));
				}
			});
			logger.Info("[BatchLinkModelsCommand] 查找 BatchLinkModelsExternalEventHandler 类型");
			Type type = null;
			try
			{
				type = revitAdapter.GetType().Assembly.GetType("RevitAi.Revit.LinkManagement.BatchLinkModelsExternalEventHandler");
				if (type == null)
				{
					Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
					foreach (Assembly assembly in assemblies)
					{
						try
						{
							type = assembly.GetType("RevitAi.Revit.LinkManagement.BatchLinkModelsExternalEventHandler");
							if (type != null)
							{
								logger.Info("找到 Handler 在程序集: " + assembly.GetName().Name);
								break;
							}
						}
						catch
						{
						}
					}
				}
			}
			catch (Exception ex)
			{
				logger.Error("查找 Handler 类型失败: " + ex.Message);
			}
			if (type == null)
			{
				logger.Error("无法找到 BatchLinkModelsExternalEventHandler 类型");
				ShowMessage("无法找到批量链接处理器，请确保 Revit 适配层已正确加载", success: false);
				return;
			}
			logger.Info("[BatchLinkModelsCommand] 设置 CurrentRequest 静态属性");
			PropertyInfo property4 = type.GetProperty("CurrentRequest", BindingFlags.Static | BindingFlags.Public);
			if (property4 != null)
			{
				property4.SetValue(null, obj);
				logger.Info("✅ CurrentRequest 已设置");
				logger.Info("[BatchLinkModelsCommand] 触发 ExternalEvent");
				MethodInfo methodInfo = externalEvent?.GetType().GetMethod("Raise");
				if (methodInfo != null)
				{
					methodInfo.Invoke(externalEvent, null);
					logger.Info("✅ ExternalEvent 已触发，等待操作完成...");
					var (num2, num3, ex2) = tcs.Task.Result;
					logger.Info($"[BatchLinkModelsCommand] 批量链接完成：成功 {num2}/{num3}");
					if (ex2 != null)
					{
						ShowMessage("链接失败: " + ex2.Message, success: false);
					}
					else if (num2 == num3)
					{
						ShowMessage($"✅ 成功链接 {num2}/{num3} 个模型文件", success: true);
					}
					else if (num2 > 0)
					{
						ShowMessage($"⚠\ufe0f 部分链接成功：{num2}/{num3} 个模型文件\n\n请检查失败的文件路径和权限后重试", success: false);
					}
					else
					{
						ShowMessage("❌ 链接失败，请检查文件路径和权限", success: false);
					}
				}
				else
				{
					logger.Error("未找到 Raise 方法");
					ShowMessage("无法触发批量链接操作", success: false);
				}
			}
			else
			{
				logger.Error("无法找到 CurrentRequest 属性");
				ShowMessage("无法设置批量链接请求", success: false);
			}
		}
		catch (Exception ex3)
		{
			logger.Error("[BatchLinkModelsCommand] 执行失败", ex3);
			ShowMessage("操作失败: " + ex3.Message, success: false);
		}
	}

	private List<string>? ShowFileDialog()
	{
		try
		{
			List<string> selectedFiles = null;
			Thread thread = new Thread((ThreadStart)delegate
			{
				try
				{
					Assembly assembly = null;
					try
					{
						assembly = Assembly.Load("PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
					}
					catch
					{
						try
						{
							assembly = Assembly.Load("PresentationFramework, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
						}
						catch
						{
							assembly = Assembly.Load("PresentationFramework");
						}
					}
					if (assembly == null)
					{
						ServiceProvider.GetLogger().Error("无法加载 PresentationFramework 程序集");
					}
					else
					{
						Type type = assembly.GetType("Microsoft.Win32.OpenFileDialog");
						if (type == null)
						{
							ServiceProvider.GetLogger().Error("无法获取 Microsoft.Win32.OpenFileDialog 类型");
						}
						else
						{
							object obj3 = Activator.CreateInstance(type);
							type.GetProperty("Title")?.SetValue(obj3, "选择 Revit 模型文件");
							type.GetProperty("Filter")?.SetValue(obj3, "Revit 模型文件 (*.rvt)|*.rvt|所有文件 (*.*)|*.*");
							type.GetProperty("Multiselect")?.SetValue(obj3, true);
							type.GetProperty("RestoreDirectory")?.SetValue(obj3, true);
							object obj4 = type.GetMethod("ShowDialog", Type.EmptyTypes)?.Invoke(obj3, null);
							if (obj4 != null && obj4 is bool && (bool)obj4 && type.GetProperty("FileNames")?.GetValue(obj3) is string[] source)
							{
								selectedFiles = source.ToList();
							}
						}
					}
				}
				catch (Exception ex2)
				{
					ServiceProvider.GetLogger().Error("文件选择对话框失败: " + ex2.Message, ex2);
				}
			});
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			thread.Join();
			return selectedFiles;
		}
		catch (Exception ex)
		{
			ServiceProvider.GetLogger().Error("显示文件对话框失败", ex);
			return null;
		}
	}

	private void ShowMessage(string message, bool success)
	{
		try
		{
			Thread thread = new Thread((ThreadStart)delegate
			{
				try
				{
					Assembly assembly = null;
					try
					{
						assembly = Assembly.Load("PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
					}
					catch
					{
						try
						{
							assembly = Assembly.Load("PresentationFramework, Version=8.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
						}
						catch
						{
							assembly = Assembly.Load("PresentationFramework");
						}
					}
					if (assembly == null)
					{
						ServiceProvider.GetLogger().Error("无法加载 PresentationFramework 程序集");
					}
					else
					{
						Type type = assembly.GetType("System.Windows.MessageBox");
						if (type == null)
						{
							ServiceProvider.GetLogger().Error("无法获取 System.Windows.MessageBox 类型");
						}
						else
						{
							Type type2 = assembly.GetType("System.Windows.MessageBoxButton");
							if (type2 == null)
							{
								ServiceProvider.GetLogger().Error("无法获取 MessageBoxButton 类型");
							}
							else
							{
								Type type3 = assembly.GetType("System.Windows.MessageBoxImage");
								if (type3 == null)
								{
									ServiceProvider.GetLogger().Error("无法获取 MessageBoxImage 类型");
								}
								else
								{
									MethodInfo method = type.GetMethod("Show", new Type[4]
									{
										typeof(string),
										typeof(string),
										type2,
										type3
									});
									if (method != null)
									{
										object obj3 = Enum.Parse(type2, "OK");
										object obj4 = (success ? Enum.Parse(type3, "Information") : Enum.Parse(type3, "Error"));
										method.Invoke(null, new object[4] { message, "批量链接", obj3, obj4 });
									}
									else
									{
										type.GetMethod("Show", new Type[2]
										{
											typeof(string),
											typeof(string)
										})?.Invoke(null, new object[2] { message, "批量链接" });
									}
								}
							}
						}
					}
				}
				catch (Exception ex2)
				{
					ServiceProvider.GetLogger().Error("显示消息框失败: " + ex2.Message);
				}
			});
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
		}
		catch (Exception ex)
		{
			ServiceProvider.GetLogger().Error("显示消息失败", ex);
		}
	}
}
