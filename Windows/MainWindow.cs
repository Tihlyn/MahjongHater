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
    private static readonly Vector4 Active = new(0.35f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Inactive = new(0.85f, 0.3f, 0.3f, 1f);

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly GameStateReader gameStateReader;
    private readonly HandAnalyzer handAnalyzer;
    private readonly YakuDetector yakuDetector;
    private readonly ScoringEngine scoringEngine;

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
        var hand = CreateAnalysisHand(state);
        var analysis = this.handAnalyzer.Analyze(hand);
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

        var hand = CreateScoringHand(state);
        var decomposition = HandDecomposer.GetBestDecomposition(hand);
        if (decomposition is null)
        {
            ImGui.TextUnformatted("Current tiles do not form a complete winning hand.");
            return;
        }

        var yaku = this.yakuDetector.Detect(hand, decomposition.Melds, decomposition.Pair, decomposition.Wait);
        if (yaku.Count == 0)
        {
            ImGui.TextUnformatted("No yaku detected.");
            return;
        }

        foreach (var entry in yaku)
        {
            ImGui.BulletText(entry.IsYakuman ? $"{entry.Name} (yakuman)" : $"{entry.Name} ({entry.Han} han)");
        }

        var fu = yaku.Any(entry => entry.Name == "Chiitoitsu") ? 25 : FuCalculator.Calculate(hand, decomposition.Melds, decomposition.Pair, decomposition.Wait, this.configuration.DoubleWindPairFu);
        var score = this.scoringEngine.Calculate(hand, yaku, fu, state.SeatWind == Wind.East);
        ImGui.TextUnformatted($"{score.Han} han / {score.Fu} fu");
        if (hand.WinMethod == WinMethod.Ron)
        {
            ImGui.TextColored(Gold, $"Ron: {score.RonPayment:N0}");
        }
        else if (state.SeatWind == Wind.East)
        {
            ImGui.TextColored(Gold, $"Tsumo: {score.TsumoPaymentDealer:N0} all");
        }
        else
        {
            ImGui.TextColored(Gold, $"Tsumo: {score.TsumoPaymentDealer:N0}/{score.TsumoPaymentNonDealer:N0}");
        }

        if (score.IsLimit)
        {
            ImGui.TextUnformatted($"Limit: {score.LimitName}");
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
