using System;

namespace RevitAi.Main;

public sealed class RibbonConfigurationData
{
	public string Version { get; set; } = "3.0";

	public DateTime LastUpdated { get; set; } = DateTime.Now;

	public RibbonConfigData RibbonConfiguration { get; set; } = new RibbonConfigData();
}
