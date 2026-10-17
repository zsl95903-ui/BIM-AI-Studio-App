using System;
using System.IO;

namespace RevitAi.Loader;

internal static class LoaderLogger
{
	private static readonly string LogDirectory;

	private static readonly string LogFile;

	private static readonly object _lock;

	static LoaderLogger()
	{
		LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAi", "Logs");
		LogFile = Path.Combine(LogDirectory, "loader.log");
		_lock = new object();
		try
		{
			if (!Directory.Exists(LogDirectory))
			{
				Directory.CreateDirectory(LogDirectory);
			}
		}
		catch
		{
		}
	}

	public static void Info(string message)
	{
		Log("INFO", message);
	}

	public static void Warning(string message)
	{
		Log("WARN", message);
	}

	public static void Warning(string message, Exception ex)
	{
		Log("WARN", $"{message}: {ex.GetType().Name} - {ex.Message}");
	}

	public static void Error(string message)
	{
		Log("ERROR", message);
	}

	public static void Error(string message, Exception ex)
	{
		Log("ERROR", $"{message}: {ex.GetType().Name} - {ex.Message}");
		Log("ERROR", "堆栈跟踪: " + ex.StackTrace);
	}

	private static void Log(string level, string message)
	{
		try
		{
			lock (_lock)
			{
				string value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
				string text = $"[{value}] [{level}] [Loader] {message}";
				File.AppendAllText(LogFile, text + Environment.NewLine);
			}
		}
		catch
		{
		}
	}
}
