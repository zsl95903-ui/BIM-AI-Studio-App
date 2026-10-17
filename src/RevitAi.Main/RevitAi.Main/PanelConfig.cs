using System.Collections.Generic;

namespace RevitAi.Main;

public class PanelConfig
{
	public string PanelName { get; set; } = string.Empty;

	public List<string> Commands { get; set; } = new List<string>();
}
