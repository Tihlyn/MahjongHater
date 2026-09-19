using System.Numerics;
using Dalamud.Bindings.ImGui;
using MahjongHater.Core;

namespace MahjongHater.Windows;

internal static class Widgets
{
    private static readonly string[] TileLabels =
    [
        "1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m",
        "1p", "2p", "3p", "4p", "5p", "6p", "7p", "8p", "9p",
        "1s", "2s", "3s", "4s", "5s", "6s", "7s", "8s", "9s",
        "E", "S", "W", "N", "Wh", "G", "R",
    ];

    private static readonly string[] TileNames = CreateTileNames();

    public static float ContentWidth => Math.Max(Theme.Px(Theme.Hairline), ImGui.GetContentRegionAvail().X - Theme.Px(Theme.CardPadding));

    public static CardScope Card(bool headline = false) => new(headline);

    public static void Label(string text) => DisplayText(text, Theme.LabelScale, Theme.Muted, ContentWidth);

    public static void DisplayText(string text, float scale, Vector4 color, float width)
    {
        var size = ImGui.CalcTextSize(text, false, width / scale) * scale;
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
            ImGui.GetCursorScreenPos(), Theme.Color(color), text, width);
        ImGui.Dummy(new Vector2(Math.Min(width, size.X), size.Y));
    }

    public static void Wrapped(string text, bool muted = true)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, muted ? Theme.Muted : Theme.Text);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ContentWidth);
        try
        {
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
    }

    public static bool Pill(string label, bool selected = false, float width = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? Theme.AccentFill : Theme.Control);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Theme.Accent : Theme.Text);
        try
        {
            return ImGui.Button(label, new Vector2(width, Math.Max(Theme.Px(Theme.ControlHeight), ImGui.GetFrameHeight())));
        }
        finally
        {
            ImGui.PopStyleColor(2);
        }
    }

    public static bool Toggle(string id, bool value)
    {
        var pos = ImGui.GetCursorScreenPos();
        var size = Theme.Px(new Vector2(Theme.ToggleWidth, Theme.ToggleHeight));
        var clicked = ImGui.Button(id, size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, Theme.Color(value ? Theme.AccentFill : Theme.Control), size.Y / 2f);
        var edge = ImGui.IsItemFocused() ? Theme.Accent : ImGui.IsItemHovered() ? Theme.Highlight : Theme.Edge;
        draw.AddRect(pos, pos + size, Theme.Color(edge), size.Y / 2f);
        var radius = size.Y / 2f - Theme.Px(Theme.ToggleInset);
        var x = value ? size.X - size.Y / 2f : size.Y / 2f;
        draw.AddCircleFilled(pos + new Vector2(x, size.Y / 2f), radius, Theme.Color(value ? Theme.Accent : Theme.Muted));
        return clicked;
    }

    public static void Tooltip(string text)
    {
        ImGui.BeginTooltip();
        try
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Theme.Px(Theme.TooltipWidth));
            try
            {
                ImGui.TextUnformatted(text);
            }
            finally
            {
                ImGui.PopTextWrapPos();
            }
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    public static bool ToggleRow(string label, string id, bool value)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ContentWidth;
        var height = Math.Max(Theme.Px(Theme.ToggleHeight), ImGui.GetTextLineHeight());
        ImGui.GetWindowDrawList().AddText(pos + new Vector2(0f, (height - ImGui.GetTextLineHeight()) / 2f), Theme.Color(Theme.Text), label);
        ImGui.SetCursorScreenPos(pos + new Vector2(width - Theme.Px(Theme.ToggleWidth), 0f));
        var clicked = Toggle(id, value);
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(width, height));
        return clicked;
    }

    public static void Badge(string label, bool warning = false)
    {
        var size = ImGui.CalcTextSize(label) + Theme.Px(new Vector2(Theme.CardPadding * 2f, Theme.SmallGap * 2f));
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, Theme.Color(Theme.Control), size.Y / 2f);
        draw.AddText(pos + Theme.Px(new Vector2(Theme.CardPadding, Theme.SmallGap)), Theme.Color(warning ? Theme.Danger : Theme.Muted), label);
        ImGui.Dummy(size);
    }

    public static void TileChip(ImDrawListPtr draw, Vector2 pos, Tile tile, float scale)
    {
        var size = Theme.Px(new Vector2(Theme.TileWidth, Theme.TileHeight)) * scale;

        // The game's own tile face when it is loaded, fitted inside the chip; text otherwise.
        if (TileArt.TryGet(tile, out var texture, out var aspect))
        {
            var fitted = aspect >= size.X / size.Y
                ? new Vector2(size.X, size.X / aspect)
                : new Vector2(size.Y * aspect, size.Y);
            var offset = (size - fitted) / 2f;
            draw.AddImageRounded(texture, pos + offset, pos + offset + fitted, Vector2.Zero, Vector2.One, Theme.Color(Theme.Text), Theme.Px(Theme.TileRadius) * scale);
            return;
        }

        var face = tile.Suit switch
        {
            TileSuit.Man => Theme.ManFace,
            TileSuit.Pin => Theme.PinFace,
            TileSuit.Sou => Theme.SouFace,
            _ => Theme.HonorFace,
        };
        var label = TileLabels[TileHelpers.ToIndex(tile)];
        var textSize = ImGui.CalcTextSize(label) * scale;
        draw.AddRectFilled(pos, pos + size, Theme.Color(face), Theme.Px(Theme.TileRadius) * scale);
        draw.AddRect(pos, pos + size, Theme.Color(Theme.Highlight), Theme.Px(Theme.TileRadius) * scale);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, pos + (size - textSize) / 2f, Theme.Color(Theme.Text), label);
        if (tile.IsRedFive)
            draw.AddCircleFilled(pos + new Vector2(size.X - Theme.Px(Theme.RedDotInset) * scale, Theme.Px(Theme.RedDotInset) * scale), Theme.Px(Theme.RedDotRadius) * scale, Theme.Color(Theme.Danger));
    }

    public static void Tile(Tile tile)
    {
        TileChip(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), tile, Theme.BodyScale);
        ImGui.Dummy(Theme.Px(new Vector2(Theme.TileWidth, Theme.TileHeight)));
        if (ImGui.IsItemHovered())
            Tooltip(TileNames[TileHelpers.ToIndex(tile)]);
    }

    public static void Tiles(IReadOnlyList<Tile> tiles)
    {
        var width = ContentWidth;
        var used = 0f;
        var tileWidth = Theme.Px(Theme.TileWidth);
        var gap = Theme.Px(Theme.Gap);
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i > 0 && used + gap + tileWidth <= width)
            {
                ImGui.SameLine();
                used += gap;
            }
            else
            {
                used = 0f;
            }

            Tile(tiles[i]);
            used += tileWidth;
        }
    }

    public static void Risk(double risk)
    {
        var amount = (float)Math.Clamp(risk, 0d, 1d);
        var pos = ImGui.GetCursorScreenPos();
        var size = Theme.Px(new Vector2(Theme.RiskWidth, Theme.RiskHeight));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, Theme.Color(Theme.Control), size.Y / 2f);
        if (amount > 0f)
            draw.AddRectFilled(pos, pos + new Vector2(size.X * amount, size.Y), Theme.Color(Vector4.Lerp(Theme.Safe, Theme.Danger, amount)), size.Y / 2f);
        ImGui.Dummy(size);
    }

    private static string[] CreateTileNames()
    {
        var names = new string[TileLabels.Length];
        for (var i = 0; i < names.Length; i++)
            names[i] = TileHelpers.GetDisplayName(TileHelpers.FromIndex(i));
        return names;
    }

    public readonly struct CardScope : IDisposable
    {
        private readonly Vector2 origin;
        private readonly float width;
        private readonly bool headline;

        public CardScope(bool headline)
        {
            this.origin = ImGui.GetCursorScreenPos();
            this.width = ImGui.GetContentRegionAvail().X;
            this.headline = headline;
            ImGui.GetWindowDrawList().ChannelsSplit(2);
            ImGui.GetWindowDrawList().ChannelsSetCurrent(1);
            ImGui.BeginGroup();
            ImGui.SetCursorScreenPos(this.origin + new Vector2(Theme.Px(Theme.CardPadding)));
            ImGui.BeginGroup();
        }

        public void Dispose()
        {
            ImGui.EndGroup();
            var bottom = ImGui.GetCursorScreenPos().Y + Theme.Px(Theme.CardPadding - Theme.Gap);
            var draw = ImGui.GetWindowDrawList();
            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(this.origin, new Vector2(this.origin.X + this.width, bottom),
                Theme.Color(this.headline ? Theme.HeadlineCard : Theme.Card), Theme.Px(Theme.CardRadius));
            draw.ChannelsMerge();
            ImGui.SetCursorScreenPos(new Vector2(this.origin.X, bottom));
            ImGui.Dummy(new Vector2(this.width, 0f));
            ImGui.EndGroup();
        }
    }
}
