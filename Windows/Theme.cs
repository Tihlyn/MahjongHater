/*
 * Glass palette: charcoal layers, white text, 55% secondary text, cool blue accent.
 * Corners: 12px panels, 10px cards, fully rounded controls; 16px outer padding.
 * Add a content card with `using (Widgets.Card()) { ... }`; cards size to content.
 * Dimensions are logical pixels. Px applies global UI scale to drawing;
 * Dalamud's WindowSystem scales window sizes and constraints itself.
 */
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace MahjongHater.Windows;

internal static class Theme
{
    public static readonly Vector4 Text = new(0.94f, 0.96f, 1f, 1f);
    public static readonly Vector4 Muted = new(1f, 1f, 1f, 0.55f);
    public static readonly Vector4 Accent = new(0.353f, 0.784f, 0.98f, 1f);
    public static readonly Vector4 Background = new(0.055f, 0.065f, 0.085f, 0.90f);
    public static readonly Vector4 Glass = new(0.7f, 0.8f, 1f, 0.025f);
    public static readonly Vector4 Card = new(0.75f, 0.82f, 0.94f, 0.055f);
    public static readonly Vector4 HeadlineCard = new(0.353f, 0.784f, 0.98f, 0.075f);
    public static readonly Vector4 Edge = new(1f, 1f, 1f, 0.07f);
    public static readonly Vector4 Highlight = new(1f, 1f, 1f, 0.15f);
    public static readonly Vector4 Control = new(1f, 1f, 1f, 0.075f);
    public static readonly Vector4 Hover = new(1f, 1f, 1f, 0.13f);
    public static readonly Vector4 Pressed = new(1f, 1f, 1f, 0.19f);
    public static readonly Vector4 AccentFill = new(0.353f, 0.784f, 0.98f, 0.17f);
    public static readonly Vector4 Safe = new(0.42f, 0.78f, 0.61f, 1f);
    public static readonly Vector4 Danger = new(0.98f, 0.40f, 0.43f, 1f);
    public static readonly Vector4 Positive = new(0.30f, 0.85f, 0.39f, 1f);   // take the call / declare
    public static readonly Vector4 Decline = new(1f, 0.80f, 0.20f, 1f);       // pass / stand down
    public static readonly Vector4 ManFace = new(0.28f, 0.16f, 0.18f, 1f);
    public static readonly Vector4 PinFace = new(0.14f, 0.23f, 0.32f, 1f);
    public static readonly Vector4 SouFace = new(0.13f, 0.26f, 0.22f, 1f);
    public static readonly Vector4 HonorFace = new(0.23f, 0.24f, 0.28f, 1f);
    public static readonly Vector4 Shadow = new(0f, 0f, 0f, 0.12f);

    public const float WindowRadius = 12f;
    public const float CardRadius = 10f;
    public const float TileRadius = 5f;
    public const float PillRadius = 100f;
    public const float Padding = 16f;
    public const float CardPadding = 12f;
    public const float Gap = 8f;
    public const float SmallGap = 4f;
    public const float Hairline = 1f;
    public const float ScrollbarWidth = 5f;
    public const float ControlHeight = 28f;
    public const float ToggleWidth = 42f;
    public const float ToggleHeight = 24f;
    public const float ToggleInset = 3f;
    public const float TileWidth = 26f;
    public const float TileHeight = 36f;
    public const float RedDotRadius = 2f;
    public const float RedDotInset = 5f;
    public const float RiskWidth = 62f;
    public const float RiskHeight = 4f;
    public const float TooltipWidth = 300f;
    public const float ShadowSpread = 3f;
    public const int ShadowLayers = 3;
    public const float ShadowFalloff = 0.025f;
    public const float GlowOpacity = 0.55f;      // outer halo; the tile sits on a busy table
    public const float GlowCoreOpacity = 1f;
    public const float GlowThickness = 2.5f;
    public const float GlowSpread = 2f;
    public const float HeadlineScale = 1.65f;
    public const float TitleScale = 1.1f;
    public const float LabelScale = 0.8f;
    public const float BodyScale = 1f;
    public const int CandidateLimit = 4;
    public const float GameTileWidth = 42f;
    public const float GameTileHeight = 55f;
    public static readonly Vector2 MainSize = new(460f, 760f);
    public static readonly Vector2 ConfigSize = new(460f, 690f);
    public static readonly Vector2 MinimumSize = new(420f, 320f);
    public static readonly Vector2 MaximumSize = new(900f, 1000f);

    public static float Px(float value) => value * ImGuiHelpers.GlobalScale;

    public static Vector2 Px(Vector2 value) => value * ImGuiHelpers.GlobalScale;

    public static uint Color(Vector4 value)
    {
        value.W *= ImGui.GetStyle().Alpha;
        return ImGui.ColorConvertFloat4ToU32(value);
    }

    public static void Surface()
    {
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var extent = new Vector2(Px(ShadowSpread * (ShadowLayers + 1)));
        draw.PushClipRect(min - extent, max + extent, false);
        try
        {
            for (var layer = ShadowLayers; layer > 0; layer--)
            {
                var spread = new Vector2(Px(ShadowSpread * layer));
                var shadow = Shadow;
                shadow.W -= layer * ShadowFalloff;
                draw.AddRect(min - spread, max + spread, Color(shadow), Px(WindowRadius + ShadowSpread * layer), ImDrawFlags.None, Px(ShadowSpread));
            }

            var inset = new Vector2(Px(Hairline));
            draw.AddRectFilled(min + inset, max - inset, Color(Glass), Px(WindowRadius));
            draw.AddRect(min + inset, max - inset, Color(Edge), Px(WindowRadius), ImDrawFlags.None, Px(Hairline));
            draw.AddLine(min + new Vector2(Px(WindowRadius), Px(Hairline)),
                new Vector2(max.X - Px(WindowRadius), min.Y + Px(Hairline)), Color(Highlight), Px(Hairline));
        }
        finally
        {
            draw.PopClipRect();
        }
    }

    public static void DiscardGlow(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        for (var layer = ShadowLayers; layer > 0; layer--)
        {
            var spread = new Vector2(Px(layer * GlowSpread));
            var color = Accent;
            color.W = GlowOpacity / layer;
            draw.AddRect(min - spread, max + spread, Color(color), Px(TileRadius + layer), ImDrawFlags.None, Px(ShadowSpread));
        }

        var core = Accent;
        core.W = GlowCoreOpacity;
        draw.AddRect(min, max, Color(core), Px(TileRadius), ImDrawFlags.None, Px(GlowThickness));
    }

    // Stored between PreDraw and PostDraw so styles also apply to ImGui.Begin.
    public struct Scope : IDisposable
    {
        private int colors;
        private int variables;

        public Scope()
        {
            this.colors = 0;
            this.variables = 0;
            this.Push(ImGuiStyleVar.WindowPadding, new Vector2(Px(Padding)));
            this.Push(ImGuiStyleVar.WindowRounding, Px(WindowRadius));
            this.Push(ImGuiStyleVar.WindowBorderSize, 0f);
            this.Push(ImGuiStyleVar.PopupRounding, Px(CardRadius));
            this.Push(ImGuiStyleVar.PopupBorderSize, 0f);
            this.Push(ImGuiStyleVar.FramePadding, new Vector2(Px(CardPadding), Px(SmallGap)));
            this.Push(ImGuiStyleVar.FrameRounding, Px(PillRadius));
            this.Push(ImGuiStyleVar.FrameBorderSize, 0f);
            this.Push(ImGuiStyleVar.ItemSpacing, new Vector2(Px(Gap)));
            this.Push(ImGuiStyleVar.ItemInnerSpacing, new Vector2(Px(SmallGap)));
            this.Push(ImGuiStyleVar.ScrollbarSize, Px(ScrollbarWidth));
            this.Push(ImGuiStyleVar.ScrollbarRounding, Px(PillRadius));
            this.Push(ImGuiStyleVar.GrabRounding, Px(PillRadius));
            this.Push(ImGuiCol.Text, Text);
            this.Push(ImGuiCol.TextDisabled, Muted);
            this.Push(ImGuiCol.WindowBg, Background);
            this.Push(ImGuiCol.PopupBg, Background);
            this.Push(ImGuiCol.Border, Edge);
            this.Push(ImGuiCol.Button, Control);
            this.Push(ImGuiCol.ButtonHovered, Hover);
            this.Push(ImGuiCol.ButtonActive, Pressed);
            this.Push(ImGuiCol.FrameBg, Control);
            this.Push(ImGuiCol.FrameBgHovered, Hover);
            this.Push(ImGuiCol.FrameBgActive, Pressed);
            this.Push(ImGuiCol.CheckMark, Accent);
            this.Push(ImGuiCol.ScrollbarBg, Vector4.Zero);
            this.Push(ImGuiCol.ScrollbarGrab, Highlight);
            this.Push(ImGuiCol.ScrollbarGrabHovered, Muted);
            this.Push(ImGuiCol.ScrollbarGrabActive, Accent);
            this.Push(ImGuiCol.ResizeGrip, Vector4.Zero);
            this.Push(ImGuiCol.ResizeGripHovered, Highlight);
            this.Push(ImGuiCol.ResizeGripActive, Accent);
            this.Push(ImGuiCol.NavHighlight, Accent);
        }

        public void Dispose()
        {
            if (this.colors > 0)
                ImGui.PopStyleColor(this.colors);
            if (this.variables > 0)
                ImGui.PopStyleVar(this.variables);
            this.colors = 0;
            this.variables = 0;
        }

        private void Push(ImGuiCol name, Vector4 value)
        {
            ImGui.PushStyleColor(name, value);
            this.colors++;
        }

        private void Push(ImGuiStyleVar name, float value)
        {
            ImGui.PushStyleVar(name, value);
            this.variables++;
        }

        private void Push(ImGuiStyleVar name, Vector2 value)
        {
            ImGui.PushStyleVar(name, value);
            this.variables++;
        }
    }
}
