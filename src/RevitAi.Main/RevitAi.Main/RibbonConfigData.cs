using System.Collections.Generic;

namespace RevitAi.Main;

public class RibbonConfigData
{
	public string TabName { get; set; } = "RevitAi";

	public List<PanelConfig> Panels { get; set; } = new List<PanelConfig>();

	public List<string> HiddenCommands { get; set; } = new List<string>();
}
