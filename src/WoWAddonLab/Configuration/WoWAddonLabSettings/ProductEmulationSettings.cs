using System.Text.Json;

namespace WoWAddonLab.Configuration;

public sealed class ProductEmulationSettings
{
    public HashSet<string> EnabledAddons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledBlizzardModules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ProfileId { get; set; } = "clean";
    // Sync with WoW's WTF folder: newer WoW data is imported at startup, emulator data is written back on close
    public bool WriteBackSavedVariables { get; set; }
    public bool LoadBlizzardUi { get; set; } = true;
    public string GameType { get; set; } = AddonManifestContextFactory.StandardGameType;
    public int? SimulatedWidth { get; set; }
    public int? SimulatedHeight { get; set; }
    public bool? UseUiScale { get; set; }
    public float? UiScale { get; set; }
    public float? UiScaleMultiplier { get; set; }
}
