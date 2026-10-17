using System;
using RevitAi.Abstractions.Logging;
using RevitAi.Abstractions.Update.Models;
using RevitAi.Core.Update;

namespace RevitAi.Main;

public static class MainApplicationUpdateExtensions
{
	public static AutoUpdateManager InitializeAutoUpdate(this MainApplication mainApplication)
	{
		try
		{
			AutoUpdateManager autoUpdateManager = new AutoUpdateManager();
			autoUpdateManager.StartBackgroundUpdateCheck();
			return autoUpdateManager;
		}
		catch (Exception ex)
		{
			Logger.Error("初始化自动更新系统失败: " + ex.Message, ex);
			return new AutoUpdateManager();
		}
	}

	public static void CheckAndApplyPendingUpdate(this AutoUpdateManager updateManager)
	{
		try
		{
			Logger.Info("检查待安装的更新...");
			if (!updateManager.HasPendingUpdate())
			{
				Logger.Info("无待安装更新");
				return;
			}
			UpdateInfo pendingUpdateInfo = updateManager.GetPendingUpdateInfo();
			if (pendingUpdateInfo != null)
			{
				Logger.Info("发现待安装更新: " + pendingUpdateInfo.Version);
				Logger.Info("当前版本: " + pendingUpdateInfo.CurrentVersion);
				Logger.Info("更新说明: " + pendingUpdateInfo.ReleaseNotes);
			}
			updateManager.StartSilentInstaller();
			Logger.Info("✓ 更新将在后台自动应用");
		}
		catch (Exception ex)
		{
			Logger.Error("检查待安装更新失败: " + ex.Message, ex);
		}
	}
}
