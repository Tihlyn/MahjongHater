using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace MahjongHater.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration configuration;
    private Theme.Scope theme;
    private bool saved;

    public ConfigWindow(Configuration configuration)
        : base("Mahjong Hater \u2013 Settings", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        // WindowSystem applies GlobalScale to window sizes and constraints.
        this.Size = Theme.ConfigSize;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Theme.MinimumSize,
            MaximumSize = Theme.MaximumSize,
        };
    }

    public override void PreDraw() => this.theme = new Theme.Scope();

    public override void PostDraw() => this.theme.Dispose();

    public override void OnOpen() => this.saved = false;

    public override void Draw()
    {
        Theme.Surface();
        var closeSize = Math.Max(Theme.Px(Theme.ControlHeight), ImGui.GetFrameHeight());
        var width = ImGui.GetContentRegionAvail().X;
        Widgets.DisplayText("Settings", Theme.HeadlineScale, Theme.Text, width - closeSize - Theme.Px(Theme.Gap));
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - closeSize);
        if (Widgets.Pill("x##close", width: closeSize))
            this.IsOpen = false;
        if (ImGui.IsItemHovered())
            Widgets.Tooltip("Close settings");

        using (Widgets.Card())
        {
            Widgets.Label("GENERAL");
            if (Widgets.ToggleRow("Enable Plugin", "##plugin", this.configuration.PluginEnabled))
            {
                this.configuration.PluginEnabled = !this.configuration.PluginEnabled;
                this.saved = false;
            }

            if (Widgets.ToggleRow("Show overlay", "##overlay", this.configuration.ShowOverlay))
            {
                MainWindow.SetOverlayVisibility(this.configuration, !this.configuration.ShowOverlay);
                this.saved = false;
            }
        }

        using (Widgets.Card())
        {
            Widgets.Label("TABLE RULES");
            if (Widgets.ToggleRow("Kuitan (open tanyao)", "##kuitan", this.configuration.Kuitan))
            {
                this.configuration.Kuitan = !this.configuration.Kuitan;
                this.saved = false;
            }

            Tooltip("Enable open tanyao except in the special kuitan-disabled room.");
            this.DrawGameLength();
            Widgets.Label("DOUBLE-WIND PAIR FU");
            var half = (Widgets.ContentWidth - Theme.Px(Theme.Gap)) / 2f;
            if (Widgets.Pill("2 fu", this.configuration.DoubleWindPairFu == 2, half))
            {
                this.configuration.DoubleWindPairFu = 2;
                this.saved = false;
            }

            Tooltip("FFXIV does not document whether seat + round wind pairs grant 2 or 4 fu. Default is 4.");
            ImGui.SameLine();
            if (Widgets.Pill("4 fu", this.configuration.DoubleWindPairFu == 4, half))
            {
                this.configuration.DoubleWindPairFu = 4;
                this.saved = false;
            }

            Tooltip("FFXIV does not document whether seat + round wind pairs grant 2 or 4 fu. Default is 4.");
        }

        using (Widgets.Card())
        {
            Widgets.Label("DORA DISPLAY");
            var half = (Widgets.ContentWidth - Theme.Px(Theme.Gap)) / 2f;
            if (Widgets.Pill("Doman", this.configuration.DoraDisplayMode == DoraDisplayMode.Doman, half))
            {
                this.configuration.DoraDisplayMode = DoraDisplayMode.Doman;
                this.saved = false;
            }

            ImGui.SameLine();
            if (Widgets.Pill("Traditional", this.configuration.DoraDisplayMode == DoraDisplayMode.Traditional, half))
            {
                this.configuration.DoraDisplayMode = DoraDisplayMode.Traditional;
                this.saved = false;
            }

            Widgets.Wrapped("Doman matches FFXIV's default display. Traditional shows dora indicators.");
        }

        using (Widgets.Card())
        {
            Widgets.Label("FIXED FFXIV RULES");
            Widgets.Wrapped("Atozuke on / Kuikae off / Double ron on\nKazoe yakuman on / No four-riichi abort\nTobi below 0 / Agariyame for the 1st-place dealer");
        }

        if (Widgets.Pill(this.saved ? "Saved##save" : "Save##save", selected: true, width: ImGui.GetContentRegionAvail().X))
        {
            this.configuration.Save();
            this.saved = true;
        }
    }

    private void DrawGameLength()
    {
        Widgets.Label("GAME LENGTH");
        var half = (Widgets.ContentWidth - Theme.Px(Theme.Gap)) / 2f;
        if (Widgets.Pill("Tonpuusen", this.configuration.GameLength == GameLength.Tonpuusen, half))
        {
            this.configuration.GameLength = GameLength.Tonpuusen;
            this.saved = false;
        }

        ImGui.SameLine();
        if (Widgets.Pill("Hanchan", this.configuration.GameLength == GameLength.Hanchan, half))
        {
            this.configuration.GameLength = GameLength.Hanchan;
            this.saved = false;
        }

        Widgets.Wrapped("Tonpuusen: East only. Hanchan: East + South.");
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
            Widgets.Tooltip(text);
    }
}
