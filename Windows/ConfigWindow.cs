using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace MahjongHater.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration configuration;

    public ConfigWindow(Configuration configuration)
        : base("Mahjong Hater – Settings")
    {
        this.configuration = configuration;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(420f, 320f),
            MaximumSize = new System.Numerics.Vector2(700f, 700f),
        };
    }

    public override void Draw()
    {
        var pluginEnabled = this.configuration.PluginEnabled;
        if (ImGui.Checkbox("Enable Plugin", ref pluginEnabled))
        {
            this.configuration.PluginEnabled = pluginEnabled;
        }

        ImGui.SeparatorText("Rules");

        var kuitan = this.configuration.Kuitan;
        if (ImGui.Checkbox("Kuitan (Open Tanyao)", ref kuitan))
        {
            this.configuration.Kuitan = kuitan;
        }
        Tooltip("Enable open tanyao except in the special kuitan-disabled room.");

        DrawGameLengthCombo();
        Tooltip("Tonpuusen is East-only; Hanchan is East+South.");

        DrawDoraDisplayCombo();
        Tooltip("Doman matches FFXIV's default dora display; Traditional shows indicators.");

        var fuMode = this.configuration.DoubleWindPairFu;
        ImGui.TextUnformatted("Double-Wind Pair Fu");
        if (ImGui.RadioButton("2 fu", fuMode == 2))
        {
            this.configuration.DoubleWindPairFu = 2;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("4 fu", fuMode == 4))
        {
            this.configuration.DoubleWindPairFu = 4;
        }
        Tooltip("FFXIV does not document whether seat+round wind pairs grant 2 or 4 fu. Default is 4.");

        ImGui.Separator();
        ImGui.TextUnformatted("FFXIV Fixed Rules (not configurable):");
        ImGui.BulletText("atozuke on");
        ImGui.BulletText("kuikae off");
        ImGui.BulletText("double ron on");
        ImGui.BulletText("kazoe yakuman on");
        ImGui.BulletText("no four-riichi abort");
        ImGui.BulletText("tobi on <0");
        ImGui.BulletText("agariyame for 1st-place dealer");

        if (ImGui.Button("Save"))
        {
            this.configuration.Save();
        }
    }

    private void DrawGameLengthCombo()
    {
        var current = this.configuration.GameLength;
        if (!ImGui.BeginCombo("Game Length", current.ToString()))
        {
            return;
        }

        foreach (var value in Enum.GetValues<GameLength>())
        {
            var selected = value == current;
            if (ImGui.Selectable(value.ToString(), selected))
            {
                this.configuration.GameLength = value;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private void DrawDoraDisplayCombo()
    {
        var current = this.configuration.DoraDisplayMode;
        if (!ImGui.BeginCombo("Dora Display Mode", current.ToString()))
        {
            return;
        }

        foreach (var value in Enum.GetValues<DoraDisplayMode>())
        {
            var selected = value == current;
            if (ImGui.Selectable(value.ToString(), selected))
            {
                this.configuration.DoraDisplayMode = value;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.TextWrapped(text);
        ImGui.EndTooltip();
    }
}
