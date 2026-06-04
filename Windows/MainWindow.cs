using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using MahjongHater.Core;

namespace MahjongHater.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Gold = new(0.96f, 0.82f, 0.26f, 1f);
    private static readonly Vector4 Green = new(0.35f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Red = new(0.95f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Active = Green;
    private static readonly Vector4 Inactive = new(0.85f, 0.3f, 0.3f, 1f);

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly GameStateReader gameStateReader;
    private readonly HandAnalyzer handAnalyzer;
    private readonly YakuDetector yakuDetector;
    private readonly ScoringEngine scoringEngine;

    private string? cachedAnalysisKey;
    private AnalysisResult? cachedAnalysis;
    private string? cachedScoringKey;
    private (HandDecomposition? Decomposition, List<YakuResult> Yaku, int Fu, ScoreResult? Score)? cachedScoring;

    public MainWindow(Plugin plugin, Configuration configuration, GameStateReader gameStateReader, HandAnalyzer handAnalyzer)
        : base("Mahjong Hater", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.gameStateReader = gameStateReader;
        this.handAnalyzer = handAnalyzer;
        this.yakuDetector = new YakuDetector(configuration);
        this.scoringEngine = new ScoringEngine(configuration);
        this.IsOpen = configuration.ShowOverlay;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 320f),
            MaximumSize = new Vector2(900f, 900f),
        };
    }

    public override void Draw()
    {
        this.IsOpen = this.configuration.ShowOverlay;
        var state = this.gameStateReader.CurrentState;

        ImGui.SameLine(Math.Max(0f, ImGui.GetContentRegionAvail().X - 28f));
        if (ImGui.SmallButton("⚙"))
        {
            this.plugin.OpenConfigWindow();
        }

        DrawHeader("Status");
        var statusText = this.configuration.PluginEnabled ? "Active" : "Inactive";
        ImGui.PushStyleColor(ImGuiCol.Text, this.configuration.PluginEnabled ? Active : Inactive);
        ImGui.SetWindowFontScale(1.35f);
        ImGui.TextUnformatted(statusText);
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();
        if (ImGui.Button(this.configuration.PluginEnabled ? "Disable" : "Enable"))
        {
            this.configuration.PluginEnabled = !this.configuration.PluginEnabled;
            this.configuration.Save();
        }

        DrawHeader("Session");
        var wins = state?.WinsThisSession ?? 0;
        var losses = state?.LossesThisSession ?? 0;
        var totalGames = wins + losses;
        var winRate = totalGames == 0 ? 0d : (double)wins / totalGames;
        ImGui.TextColored(Gold, $"MGP Earned: {(state?.MgpEarned ?? 0):N0}");
        ImGui.TextColored(Green, $"Wins: {wins}");
        ImGui.TextColored(Red, $"Losses: {losses}");
        ImGui.TextUnformatted($"Win Rate: {winRate:P1}");

        DrawHeader("Current Hand");
        if (state is { InGame: true } && state.ClosedTiles.Count > 0)
        {
            DrawCurrentHand(state);
        }
        else
        {
            ImGui.TextUnformatted("Not currently seated at a Doman Mahjong table.");
        }

        DrawHeader("Scoring Info");
        DrawScoringInfo(state);

        this.configuration.ShowOverlay = this.IsOpen;
    }

    public override void OnClose()
    {
        this.configuration.ShowOverlay = false;
        this.configuration.Save();
    }

    public override void OnOpen()
    {
        this.configuration.ShowOverlay = true;
        this.configuration.Save();
    }

    private void DrawCurrentHand(GameState state)
    {
        var key = GetHandFingerprint(state);
        if (this.cachedAnalysisKey != key || this.cachedAnalysis is null)
        {
            var hand = CreateAnalysisHand(state);
            this.cachedAnalysis = this.handAnalyzer.Analyze(hand);
            this.cachedAnalysisKey = key;
        }

        var analysis = this.cachedAnalysis;
        ImGui.TextUnformatted($"Score: {state.CurrentScore:N0}");
        ImGui.TextUnformatted($"Seat Wind: {state.SeatWind}   Round Wind: {state.RoundWind}");
        if (analysis.ShantenAfterDiscard == 0)
        {
            ImGui.TextColored(Gold, "Tenpai!");
        }
        else
        {
            ImGui.TextUnformatted($"Shanten: {analysis.ShantenAfterDiscard}");
        }

        ImGui.TextColored(Gold, $"Best Discard: {TileHelpers.GetDisplayName(analysis.BestDiscard)}");
        if (analysis.ShantenAfterDiscard == 0)
        {
            ImGui.TextUnformatted($"Win Probability: {analysis.WinProbability:P1}");
        }

        ImGui.TextUnformatted($"Ukeire: {analysis.Ukeire}");
        if (analysis.TenpaiWaits.Count > 0)
        {
            ImGui.TextWrapped($"Waits: {string.Join(", ", analysis.TenpaiWaits.Select(TileHelpers.GetDisplayName))}");
        }

        ImGui.TextWrapped(analysis.Reasoning);
    }

    private void DrawScoringInfo(GameState? state)
    {
        if (state is not { InGame: true })
        {
            ImGui.TextUnformatted("No active round data.");
            return;
        }

        var key = GetHandFingerprint(state) + ":" + state.IsRiichi;
        if (this.cachedScoringKey != key || this.cachedScoring is null)
        {
            var hand = CreateScoringHand(state);
            var decomposition = HandDecomposer.GetBestDecomposition(hand);
            List<YakuResult> yaku = [];
            int fu = 0;
            ScoreResult? score = null;
            if (decomposition is not null)
            {
                yaku = this.yakuDetector.Detect(hand, decomposition.Melds, decomposition.Pair, decomposition.Wait);
                if (yaku.Count > 0)
                {
                    fu = yaku.Any(entry => entry.Name == "Chiitoitsu") ? 25 : FuCalculator.Calculate(hand, decomposition.Melds, decomposition.Pair, decomposition.Wait, this.configuration.DoubleWindPairFu);
                    score = this.scoringEngine.Calculate(hand, yaku, fu, state.SeatWind == Wind.East);
                }
            }

            this.cachedScoring = (decomposition, yaku, fu, score);
            this.cachedScoringKey = key;
        }

        var (cachedDecomposition, cachedYaku, cachedFu, cachedScore) = this.cachedScoring.Value;

        if (cachedDecomposition is null)
        {
            ImGui.TextUnformatted("Current tiles do not form a complete winning hand.");
            return;
        }

        if (cachedYaku.Count == 0)
        {
            ImGui.TextUnformatted("No yaku detected.");
            return;
        }

        foreach (var entry in cachedYaku)
        {
            ImGui.BulletText(entry.IsYakuman ? $"{entry.Name} (yakuman)" : $"{entry.Name} ({entry.Han} han)");
        }

        ImGui.TextUnformatted($"{cachedScore!.Han} han / {cachedFu} fu");
        if (cachedScore.RonPayment > 0)
        {
            ImGui.TextColored(Gold, $"Ron: {cachedScore.RonPayment:N0}");
        }
        else if (state.SeatWind == Wind.East)
        {
            ImGui.TextColored(Gold, $"Tsumo: {cachedScore.TsumoPaymentDealer:N0} all");
        }
        else
        {
            ImGui.TextColored(Gold, $"Tsumo: {cachedScore.TsumoPaymentDealer:N0}/{cachedScore.TsumoPaymentNonDealer:N0}");
        }

        if (cachedScore.IsLimit)
        {
            ImGui.TextUnformatted($"Limit: {cachedScore.LimitName}");
        }
    }

    private static Hand CreateAnalysisHand(GameState state)
    {
        var hand = new Hand
        {
            SeatWind = state.SeatWind,
            RoundWind = state.RoundWind,
        };
        hand.ClosedTiles.AddRange(state.ClosedTiles);
        hand.CalledMelds.AddRange(state.CalledMelds);
        return hand;
    }

    private static string GetHandFingerprint(GameState state)
    {
        var tiles = string.Join(",", state.ClosedTiles.Select(t => TileHelpers.ToIndex(TileHelpers.Normalize(t))));
        var melds = string.Join("|", state.CalledMelds.Select(m => string.Join(",", m.Tiles.Select(t => TileHelpers.ToIndex(t)))));
        return $"{tiles}:{melds}";
    }

    private static Hand CreateScoringHand(GameState state)
    {
        var hand = CreateAnalysisHand(state);
        hand.WinMethod = WinMethod.Tsumo;
        hand.WinningTile = state.ClosedTiles.LastOrDefault();
        hand.IsRiichi = state.IsRiichi;
        return hand;
    }

    private static void DrawHeader(string text)
    {
        ImGui.SeparatorText(text);
    }
}
