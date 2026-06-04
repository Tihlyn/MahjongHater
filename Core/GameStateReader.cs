using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core;

public unsafe sealed class GameStateReader
{
    private static readonly Regex DigitsRegex = new(@"-?\d[\d,]*", RegexOptions.Compiled);

    private readonly IGameGui gameGui;
    private readonly IPluginLog pluginLog;
    private readonly Configuration configuration;
    private readonly SortedSet<uint> observedTextureIds = [];
    private readonly Dictionary<uint, Tile> textureToTileMap = [];
    private int? previousScore;
    private string? lastOutcomeFingerprint;
    private int winsThisSession;
    private int lossesThisSession;

    public GameStateReader(IGameGui gameGui, IPluginLog pluginLog, Configuration configuration)
    {
        this.gameGui = gameGui;
        this.pluginLog = pluginLog;
        this.configuration = configuration;
    }

    public GameState? CurrentState { get; private set; }

    public void Reset()
    {
        this.CurrentState = null;
        this.previousScore = null;
        this.lastOutcomeFingerprint = null;
        this.winsThisSession = 0;
        this.lossesThisSession = 0;
        this.observedTextureIds.Clear();
        this.textureToTileMap.Clear();
    }

    public void Tick()
    {
        try
        {
            var addonPtr = this.gameGui.GetAddonByName("Emj");
            if (addonPtr.IsNull)
            {
                this.CurrentState = null;
                return;
            }

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon->RootNode == null)
            {
                this.CurrentState = null;
                return;
            }

            var imageNodes = new List<ImageNodeSnapshot>();
            var textNodes = new List<TextNodeSnapshot>();
            var nodes = new List<nint>();
            CollectNodes(addon->RootNode, nodes);
            foreach (var nodeAddress in nodes)
            {
                var node = (AtkResNode*)nodeAddress;
                if (node == null || !node->IsVisible())
                {
                    continue;
                }

                var imageNode = node->GetAsAtkImageNode();
                if (imageNode != null && TryReadImage(imageNode, out var imageSnapshot))
                {
                    imageNodes.Add(imageSnapshot);
                }

                var textNode = node->GetAsAtkTextNode();
                if (textNode != null && TryReadText(textNode, out var textSnapshot))
                {
                    textNodes.Add(textSnapshot);
                }
            }

            // TODO(WIP): If AgentEmj gets mapped in FFXIVClientStructs, prefer reading the canonical Mahjong agent state directly.
            var tileNodes = InferTileNodes(imageNodes);
            var inferredHand = PartitionTiles(tileNodes);
            var score = ExtractScore(textNodes);
            var mgp = ExtractMgpEarned(textNodes);
            var seatWind = ExtractWind(textNodes, "seat") ?? Wind.East;
            var roundWind = ExtractWind(textNodes, "round") ?? Wind.East;
            var honba = ExtractTaggedValue(textNodes, "honba") ?? 0;
            var riichiSticks = ExtractTaggedValue(textNodes, "riichi") ?? 0;
            var isRiichi = textNodes.Any(t => t.Text.Contains("riichi", StringComparison.OrdinalIgnoreCase) || t.Text.Contains('立'));

            var state = new GameState
            {
                InGame = true,
                ClosedTiles = inferredHand.ClosedTiles,
                CalledMelds = inferredHand.CalledMelds,
                SeatWind = seatWind,
                RoundWind = roundWind,
                CurrentScore = score,
                HonbaCount = honba,
                RiichiSticksCount = riichiSticks,
                MgpEarned = mgp,
                WinsThisSession = this.winsThisSession,
                LossesThisSession = this.lossesThisSession,
                IsRiichi = isRiichi,
                TilesRemainingInWall = Math.Max(0, 70 - inferredHand.ClosedTiles.Count - inferredHand.CalledMelds.Sum(m => m.Tiles.Length) - inferredHand.DiscardPile.Count),
                DiscardPile = inferredHand.DiscardPile,
                DoraIndicators = inferredHand.DoraIndicators,
                UradoraIndicators = inferredHand.UradoraIndicators,
            };

            UpdateSessionRecord(state);
            state.WinsThisSession = this.winsThisSession;
            state.LossesThisSession = this.lossesThisSession;
            this.CurrentState = state;
        }
        catch (Exception ex)
        {
            this.pluginLog.Error(ex, "Mahjong Hater failed to read the Emj addon.");
        }
    }

    private void UpdateSessionRecord(GameState state)
    {
        if (!this.previousScore.HasValue)
        {
            this.previousScore = state.CurrentScore;
            return;
        }

        var delta = state.CurrentScore - this.previousScore.Value;
        var fingerprint = $"{state.CurrentScore}:{state.ClosedTiles.Count}:{state.CalledMelds.Count}:{state.MgpEarned}";
        if (Math.Abs(delta) >= 1000 && !string.Equals(this.lastOutcomeFingerprint, fingerprint, StringComparison.Ordinal))
        {
            if (delta > 0)
            {
                this.winsThisSession++;
            }
            else
            {
                this.lossesThisSession++;
            }

            this.lastOutcomeFingerprint = fingerprint;
        }

        this.previousScore = state.CurrentScore;
    }

    private static void CollectNodes(AtkResNode* node, List<nint> nodes)
    {
        if (node == null)
        {
            return;
        }

        nodes.Add((nint)node);
        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
        {
            CollectNodes(child, nodes);
        }
    }

    private static bool TryReadImage(AtkImageNode* imageNode, out ImageNodeSnapshot snapshot)
    {
        snapshot = default;
        if (imageNode->PartsList == null || imageNode->PartId >= imageNode->PartsList->PartCount)
        {
            return false;
        }

        var asset = imageNode->PartsList->Parts[imageNode->PartId].UldAsset;
        if (asset == null)
        {
            return false;
        }

        snapshot = new ImageNodeSnapshot(
            imageNode->NodeId,
            asset->Id,
            imageNode->X,
            imageNode->Y,
            imageNode->Width,
            imageNode->Height);
        return snapshot.AssetId != 0;
    }

    private static bool TryReadText(AtkTextNode* textNode, out TextNodeSnapshot snapshot)
    {
        snapshot = default;
        var raw = Marshal.PtrToStringUTF8((nint)(byte*)textNode->NodeText.StringPtr);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        snapshot = new TextNodeSnapshot(textNode->NodeId, raw.Trim(), textNode->X, textNode->Y);
        return true;
    }

    private List<TileNodeSnapshot> InferTileNodes(IEnumerable<ImageNodeSnapshot> images)
    {
        var filtered = images
            .Where(image => image.Width is >= 16 and <= 96 && image.Height is >= 16 and <= 128)
            .OrderBy(image => image.Y)
            .ThenBy(image => image.X)
            .ToList();

        foreach (var image in filtered)
        {
            this.observedTextureIds.Add(image.AssetId);
        }

        RefreshTextureMap();

        var nodes = new List<TileNodeSnapshot>();
        foreach (var image in filtered)
        {
            if (this.textureToTileMap.TryGetValue(image.AssetId, out var tile))
            {
                nodes.Add(new TileNodeSnapshot(image, tile));
            }
        }

        return nodes;
    }

    private void RefreshTextureMap()
    {
        this.textureToTileMap.Clear();
        var ordered = this.observedTextureIds.OrderBy(id => id).ToList();
        var tileOrder = BuildHeuristicTileOrder(ordered.Count);
        for (var i = 0; i < Math.Min(ordered.Count, tileOrder.Count); i++)
        {
            this.textureToTileMap[ordered[i]] = tileOrder[i];
        }
    }

    private static List<Tile> BuildHeuristicTileOrder(int observedCount)
    {
        var baseOrder = new List<Tile>
        {
            new(TileSuit.Man, 1), new(TileSuit.Man, 2), new(TileSuit.Man, 3), new(TileSuit.Man, 4), new(TileSuit.Man, 5), new(TileSuit.Man, 5, true), new(TileSuit.Man, 6), new(TileSuit.Man, 7), new(TileSuit.Man, 8), new(TileSuit.Man, 9),
            new(TileSuit.Pin, 1), new(TileSuit.Pin, 2), new(TileSuit.Pin, 3), new(TileSuit.Pin, 4), new(TileSuit.Pin, 5), new(TileSuit.Pin, 5, true), new(TileSuit.Pin, 6), new(TileSuit.Pin, 7), new(TileSuit.Pin, 8), new(TileSuit.Pin, 9),
            new(TileSuit.Sou, 1), new(TileSuit.Sou, 2), new(TileSuit.Sou, 3), new(TileSuit.Sou, 4), new(TileSuit.Sou, 5), new(TileSuit.Sou, 5, true), new(TileSuit.Sou, 6), new(TileSuit.Sou, 7), new(TileSuit.Sou, 8), new(TileSuit.Sou, 9),
            new(TileSuit.Wind, 1), new(TileSuit.Wind, 2), new(TileSuit.Wind, 3), new(TileSuit.Wind, 4), new(TileSuit.Dragon, 1), new(TileSuit.Dragon, 2), new(TileSuit.Dragon, 3),
        };

        if (observedCount <= 34)
        {
            return TileHelpers.AllTileTypes.ToList();
        }

        return baseOrder;
    }

    private static PartitionedTiles PartitionTiles(List<TileNodeSnapshot> tileNodes)
    {
        if (tileNodes.Count == 0)
        {
            return new PartitionedTiles([], [], [], [], []);
        }

        var maxY = tileNodes.Max(node => node.Image.Y);
        var minY = tileNodes.Min(node => node.Image.Y);
        var playerBand = tileNodes.Where(node => Math.Abs(node.Image.Y - maxY) < 32f).OrderBy(node => node.Image.X).ToList();
        var closedTiles = playerBand.TakeLast(Math.Min(14, playerBand.Count)).Select(node => node.Tile).ToList();
        var calledCandidates = playerBand.Take(Math.Max(0, playerBand.Count - closedTiles.Count)).ToList();
        var calledMelds = GroupCalledMelds(calledCandidates.Select(candidate => candidate.Tile).ToList());

        var discardBand = tileNodes
            .Where(node => node.Image.Y > minY + 40f && node.Image.Y < maxY - 40f)
            .OrderBy(node => node.Image.Y)
            .ThenBy(node => node.Image.X)
            .Take(24)
            .Select(node => node.Tile)
            .ToList();

        var doraIndicators = tileNodes
            .Where(node => node.Image.Y <= minY + 32f)
            .OrderByDescending(node => node.Image.X)
            .Take(5)
            .Select(node => node.Tile)
            .ToList();

        return new PartitionedTiles(closedTiles, calledMelds, discardBand, doraIndicators, []);
    }

    private static List<Meld> GroupCalledMelds(List<Tile> calledTiles)
    {
        var melds = new List<Meld>();
        for (var i = 0; i < calledTiles.Count;)
        {
            var remaining = calledTiles.Count - i;
            if (remaining >= 4 && AreSameKind(calledTiles, i, 4))
            {
                melds.Add(Meld.MakeKan(calledTiles[i], MeldType.Daiminkan));
                i += 4;
                continue;
            }

            if (remaining >= 3 && AreSameKind(calledTiles, i, 3))
            {
                melds.Add(new Meld(MeldType.Pon, [calledTiles[i], calledTiles[i], calledTiles[i]], true));
                i += 3;
                continue;
            }

            if (remaining >= 3 && IsSequence(calledTiles[i], calledTiles[i + 1], calledTiles[i + 2]))
            {
                melds.Add(new Meld(MeldType.Chi, [calledTiles[i], calledTiles[i + 1], calledTiles[i + 2]], true));
                i += 3;
                continue;
            }

            i++;
        }

        return melds;
    }

    private static bool AreSameKind(IReadOnlyList<Tile> tiles, int startIndex, int length)
    {
        for (var i = 1; i < length; i++)
        {
            if (!TileHelpers.SameKind(tiles[startIndex], tiles[startIndex + i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSequence(Tile first, Tile second, Tile third)
    {
        var sorted = new[] { TileHelpers.Normalize(first), TileHelpers.Normalize(second), TileHelpers.Normalize(third) }.OrderBy(tile => tile.Number).ToArray();
        return sorted.All(tile => tile.Suit == sorted[0].Suit && tile.Suit <= TileSuit.Sou)
               && sorted[0].Number + 1 == sorted[1].Number
               && sorted[1].Number + 1 == sorted[2].Number;
    }

    private static int ExtractScore(IEnumerable<TextNodeSnapshot> textNodes)
    {
        return textNodes
            .SelectMany(ExtractNumbers)
            .Where(value => value is >= -50000 and <= 200000)
            .OrderByDescending(value => Math.Abs(value))
            .FirstOrDefault();
    }

    private static long ExtractMgpEarned(IEnumerable<TextNodeSnapshot> textNodes)
    {
        var tagged = textNodes.Where(t => t.Text.Contains("mgp", StringComparison.OrdinalIgnoreCase));
        return tagged.SelectMany(ExtractNumbers).Select(value => (long)value).DefaultIfEmpty().Max();
    }

    private static Wind? ExtractWind(IEnumerable<TextNodeSnapshot> textNodes, string labelHint)
    {
        foreach (var text in textNodes.OrderBy(t => t.Y).ThenBy(t => t.X))
        {
            if (!text.Text.Contains(labelHint, StringComparison.OrdinalIgnoreCase)
                && !text.Text.Contains("東")
                && !text.Text.Contains("南")
                && !text.Text.Contains("西")
                && !text.Text.Contains("北"))
            {
                continue;
            }

            if (text.Text.Contains("East", StringComparison.OrdinalIgnoreCase) || text.Text.Contains("東")) return Wind.East;
            if (text.Text.Contains("South", StringComparison.OrdinalIgnoreCase) || text.Text.Contains("南")) return Wind.South;
            if (text.Text.Contains("West", StringComparison.OrdinalIgnoreCase) || text.Text.Contains("西")) return Wind.West;
            if (text.Text.Contains("North", StringComparison.OrdinalIgnoreCase) || text.Text.Contains("北")) return Wind.North;
        }

        return null;
    }

    private static int? ExtractTaggedValue(IEnumerable<TextNodeSnapshot> textNodes, string hint)
    {
        return textNodes
            .Where(t => t.Text.Contains(hint, StringComparison.OrdinalIgnoreCase))
            .SelectMany(ExtractNumbers)
            .FirstOrDefault();
    }

    private static IEnumerable<int> ExtractNumbers(TextNodeSnapshot text)
    {
        foreach (Match match in DigitsRegex.Matches(text.Text))
        {
            if (int.TryParse(match.Value.Replace(",", string.Empty, StringComparison.Ordinal), out var value))
            {
                yield return value;
            }
        }
    }

    private readonly record struct ImageNodeSnapshot(uint NodeId, uint AssetId, float X, float Y, ushort Width, ushort Height);

    private readonly record struct TextNodeSnapshot(uint NodeId, string Text, float X, float Y);

    private readonly record struct TileNodeSnapshot(ImageNodeSnapshot Image, Tile Tile);

    private readonly record struct PartitionedTiles(
        List<Tile> ClosedTiles,
        List<Meld> CalledMelds,
        List<Tile> DiscardPile,
        List<Tile> DoraIndicators,
        List<Tile> UradoraIndicators);
}

public sealed class GameState
{
    public bool InGame { get; set; }

    public List<Tile> ClosedTiles { get; set; } = [];

    public List<Meld> CalledMelds { get; set; } = [];

    public Wind SeatWind { get; set; } = Wind.East;

    public Wind RoundWind { get; set; } = Wind.East;

    public int CurrentScore { get; set; }

    public int HonbaCount { get; set; }

    public int RiichiSticksCount { get; set; }

    public long MgpEarned { get; set; }

    public int WinsThisSession { get; set; }

    public int LossesThisSession { get; set; }

    public bool IsRiichi { get; set; }

    public int TilesRemainingInWall { get; set; }

    public List<Tile> DiscardPile { get; set; } = [];

    public List<Tile> DoraIndicators { get; set; } = [];

    public List<Tile> UradoraIndicators { get; set; } = [];
}
