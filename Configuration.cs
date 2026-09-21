using Dalamud.Configuration;
using Dalamud.Plugin;

namespace MahjongHater;

public sealed class Configuration : IPluginConfiguration
{
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;

    public bool PluginEnabled { get; set; } = true;

    public bool Kuitan { get; set; } = true;

    public GameLength GameLength { get; set; } = GameLength.Hanchan;

    public DoraDisplayMode DoraDisplayMode { get; set; } = DoraDisplayMode.Doman;

    public int DoubleWindPairFu { get; set; } = 4;

    public bool ShowOverlay { get; set; } = true;

    // Defense v2 (docs/DEFENSE_PLAN.md): table-driven danger + push/fold budget. Off = the
    // v1.3 heuristics, kept for A/B auto-play runs.
    public bool DefenseV2 { get; set; } = true;

    // Experimental offline discard lookup and optional public-state corpus capture.
    // Both take effect after a plugin reload.
    public bool PrecomputedPolicyEnabled { get; set; }

    public bool LearnedPolicyEnabled { get; set; }

    public bool CapturePrecomputedSnapshots { get; set; }

    // Tag written into the calibration CSVs so human and NPC opponents are fitted apart.
    public string CalibrationPopulation { get; set; } = "human";

    // Auto play / requeue (dev tooling for unattended matches; both live in the main
    // window). Persisted so a hot reload mid-session picks up where it left off.
    public bool AutoPlay { get; set; }

    public bool Requeue { get; set; }

    public uint RequeueDuty { get; set; } = 766;   // Novice Mahjong (Quick Ranked Match)

    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);
        config.ClampValues();
        return config;
    }

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        this.ClampValues();
        this.pluginInterface?.SavePluginConfig(this);
    }

    private void ClampValues()
    {
        this.DoubleWindPairFu = this.DoubleWindPairFu is 2 or 4 ? this.DoubleWindPairFu : 4;
    }
}

public enum GameLength
{
    Tonpuusen = 4,
    Hanchan = 8,
}

public enum DoraDisplayMode
{
    Doman,
    Traditional,
}
