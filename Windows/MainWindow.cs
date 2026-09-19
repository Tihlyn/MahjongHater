using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHater.Core;
using MahjongHater.Core.State;

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
    private readonly EmjStateReader reader;
    private readonly HandAnalyzer handAnalyzer;

    // One pon/chi evaluation per call window, not per frame.
    private (string Key, string Advice)? ponAdviceCache;
    private (string Key, string Advice)? chiAdviceCache;

    public MainWindow(Plugin plugin, Configuration configuration, EmjStateReader reader, HandAnalyzer handAnalyzer)
        : base("Mahjong Hater", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.reader = reader;
        this.handAnalyzer = handAnalyzer;
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
        var state = this.reader.Current;

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
        var wins = this.reader.Tracker.WinsThisSession;
        var losses = this.reader.Tracker.LossesThisSession;
        var totalGames = wins + losses;
        var winRate = totalGames == 0 ? 0d : (double)wins / totalGames;
        ImGui.TextColored(Gold, $"Score: {(state?.Us.Score ?? 0):N0}");
        ImGui.TextColored(Green, $"Wins: {wins}");
        ImGui.TextColored(Red, $"Losses: {losses}");
        ImGui.TextUnformatted($"Win Rate: {winRate:P1}");

        DrawHeader("Current Hand");
        if (state is { Phase: not GamePhase.NotInGame })
        {
            if (!state.LayoutHealthy)
                ImGui.TextColored(Red, "Layout check failed — hand read may be stale (see /struct).");
            foreach (var note in state.Notes)
                ImGui.TextDisabled(note);

            if (state.Hand.Count > 0)
                this.DrawCurrentHand(state);
            else if (state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare)
            {
                ImGui.TextUnformatted($"Seat Wind: {state.SeatWind}   Round Wind: {state.RoundWind}");
                this.DrawCallWindow(state);
            }
            else
                ImGui.TextUnformatted("Seated — waiting for hand tile data...");
        }
        else
        {
            ImGui.TextUnformatted("Not currently seated at a Doman Mahjong table.");
        }

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

    private void DrawCurrentHand(StateSnapshot state)
    {
        ImGui.TextUnformatted($"Seat Wind: {state.SeatWind}   Round Wind: {state.RoundWind}   Wall: {state.WallRemaining}");

        // Call advice renders on top, hand analysis stays visible below it.
        if (state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare)
            this.DrawCallWindow(state);

        // Analysis runs on a background thread (AnalysisService); Draw only renders
        // the latest publication and never blocks.
        var service = this.plugin.AnalysisService;
        var publication = service.Latest;
        if (publication is null)
        {
            ImGui.TextUnformatted("Analyzing hand...");
            return;
        }

        if (publication.Status != AnalysisStatus.Ready)
        {
            ImGui.TextColored(Red, publication.Status == AnalysisStatus.TimedOut
                ? "Analysis timed out — will retry on the next hand change."
                : $"Analysis failed: {publication.Error} — will retry on the next hand change.");
            return;
        }

        var analysis = publication.Result!;
        if (!analysis.IsValid)
        {
            ImGui.TextUnformatted(analysis.Reasoning);
            return;
        }

        var currentFingerprint = AnalysisSnapshot.ComputeFingerprint(state);
        var isFresh = publication.Fingerprint == currentFingerprint;
        if (!isFresh)
        {
            ImGui.TextDisabled(service.IsStalled
                ? "Analysis stalled — showing previous hand; will retry on the next hand change."
                : "Updating for the new hand...");
        }

        if (analysis.ShantenAfterDiscard <= 0)
            ImGui.TextColored(Gold, "Tenpai!");
        else
            ImGui.TextUnformatted($"Shanten: {analysis.ShantenAfterDiscard}");

        if (analysis.BestDiscard is { } bestDiscard)
            ImGui.TextColored(Gold, $"Best Discard: {TileHelpers.GetDisplayName(bestDiscard)}");

        if (analysis.ShantenAfterDiscard <= 0)
        {
            ImGui.TextUnformatted($"Win Probability: {analysis.WinProbability:P1}");
            if (analysis.RiichiRecommended)
                ImGui.TextColored(Gold, "Riichi recommended!");
        }

        ImGui.TextUnformatted($"Ukeire: {analysis.Ukeire}");
        if (analysis.TenpaiWaits.Count > 0)
            ImGui.TextWrapped($"Waits: {string.Join(", ", analysis.TenpaiWaits.Select(TileHelpers.GetDisplayName))}");

        ImGui.TextWrapped(analysis.Reasoning);
        DrawRankedOptions(analysis);

        // Never highlight a tile computed for a hand that has since changed.
        if (isFresh && analysis.BestDiscard is { } highlightTile)
            this.DrawBestDiscardHighlight(state, highlightTile);
    }

    private static void DrawRankedOptions(AnalysisResult analysis)
    {
        if (analysis.Ranked.Count < 2)
            return;

        ImGui.Spacing();
        ImGui.TextDisabled("Alternatives:");
        foreach (var option in analysis.Ranked.Take(3))
        {
            var line = $"  {TileHelpers.GetDisplayName(option.Eval.Discard)} — " +
                       (option.Eval.ShantenAfter == 0 ? "tenpai" : $"{option.Eval.ShantenAfter}-shanten") +
                       $", {option.Eval.Ukeire} tiles";
            if (option.OpenYakuRisk)
                line += " (yakuless!)";
            ImGui.TextDisabled(line);
        }
    }

    // Gold outline over the recommended tile's slot node. The struct hand is in visual
    // order (sorted slots, then the draw), so its index is the slot node index.
    private unsafe void DrawBestDiscardHighlight(StateSnapshot state, Tile bestDiscard)
    {
        var index = EmjStateReader.FindVisualIndex(state.Hand, bestDiscard);
        if (index < 0)
            return;

        var addonPtr = this.plugin.GameGui.GetAddonByName(this.reader.Layout.AddonName);
        if (addonPtr.IsNull)
            return;
        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->RootNode == null)
            return;

        var slots = EmjScanner.ScanHandSlots(addon);
        if (index >= slots.Count)
            return;

        var targetNode = (AtkResNode*)slots[index].NodePtr;
        if (targetNode == null || !targetNode->IsVisible())
            return;

        // Walk up the parent chain STOPPING before the RootNode (ParentNode == null):
        // RootNode->X/Y equals addon->X/Y, so including it would double-count the offset.
        float relX = 0, relY = 0;
        var cur = targetNode;
        for (var depth = 0; cur != null && cur->ParentNode != null && depth < 20; depth++, cur = cur->ParentNode)
        {
            relX += cur->X;
            relY += cur->Y;
        }

        var s = addon->Scale;
        var topLeft = new Vector2(addon->X + (relX * s), addon->Y + (relY * s));
        var botRight = topLeft + new Vector2(42 * s, 55 * s);

        ImGui.GetForegroundDrawList().AddRect(topLeft, botRight, ImGui.ColorConvertFloat4ToU32(Gold), 2f, ImDrawFlags.None, 3f);
    }

    private void DrawCallWindow(StateSnapshot state)
    {
        ImGui.Separator();
        ImGui.TextColored(Gold, "=== CALL WINDOW ===");

        if (state.Can(LegalAction.Tsumo))
        {
            ImGui.TextColored(Gold, ">>> TSUMO — Declare the win! <<<");
            ImGui.Separator();
            return;
        }

        if (state.Can(LegalAction.Ron))
        {
            ImGui.TextColored(Gold, ">>> RON — Declare the win! <<<");
            ImGui.Separator();
            return;
        }

        if (state.Can(LegalAction.Riichi))
        {
            var latest = this.plugin.AnalysisService.Latest?.Result;
            if (latest is { IsValid: true, RiichiRecommended: true })
                ImGui.TextColored(Gold, ">>> RIICHI — Declare! Tenpai with live waits. <<<");
            else if (latest is { IsValid: true, ShantenAfterDiscard: <= 0, Ukeire: 0 })
                ImGui.TextUnformatted("Riichi: waits are dead (0 live tiles) — consider passing.");
            else
                ImGui.TextUnformatted("Riichi: available — declaring locks the hand for +1 han.");
            ImGui.Separator();
            return;
        }

        if (state.CallTile is { } callTile)
        {
            ImGui.TextUnformatted($"Tile offered: {TileHelpers.GetDisplayName(callTile)}");
            if (state.Can(LegalAction.Pon))
                ImGui.TextUnformatted($"Pon: {this.AnalysePon(state, callTile)}");
            if (state.Can(LegalAction.Chi))
                ImGui.TextUnformatted($"Chi: {this.AnalyseChi(state, callTile)}");
            if (state.Can(LegalAction.MinKan))
                ImGui.TextUnformatted("Kan: Reveals new dora but locks your hand shape.");
        }
        else if (state.Can(LegalAction.AnKan))
        {
            ImGui.TextUnformatted("Kan (closed): keeps the hand closed; reveals a new dora.");
        }
        else if (state.CallOptions.Count > 0)
        {
            ImGui.TextUnformatted($"Options: {string.Join(", ", state.CallOptions)} — offered tile unknown, check manually.");
        }
        else
        {
            ImGui.TextUnformatted("Call window active — make your choice.");
        }

        ImGui.Separator();
    }

    // One-line pon recommendation, memoized per call window (hand fingerprint + tile).
    private string AnalysePon(StateSnapshot state, Tile callTile)
    {
        var cacheKey = $"{AnalysisSnapshot.ComputeFingerprint(state)}|{callTile}";
        if (this.ponAdviceCache is { } cached && cached.Key == cacheKey)
            return cached.Advice;

        var advice = this.ComputePonAdvice(state, callTile);
        this.ponAdviceCache = (cacheKey, advice);
        return advice;
    }

    private string ComputePonAdvice(StateSnapshot state, Tile callTile)
    {
        var matchCount = state.Hand.Count(t => TileHelpers.SameKind(t, callTile));
        if (matchCount < 2)
            return "Pass — no pon possible";

        var handAfterPon = new Hand { SeatWind = state.SeatWind, RoundWind = state.RoundWind };
        var removed = 0;
        foreach (var t in state.Hand)
        {
            if (removed < 2 && TileHelpers.SameKind(t, callTile)) { removed++; continue; }
            handAfterPon.ClosedTiles.Add(t);
        }

        handAfterPon.CalledMelds.AddRange(state.OurMelds);
        handAfterPon.CalledMelds.Add(new Meld(MeldType.Pon, [callTile, callTile, callTile], true));

        var ctx = BuildCallContext(state);
        var afterPonAnalysis = this.handAnalyzer.Analyze(handAfterPon, ctx);
        var currentAnalysis = this.handAnalyzer.Analyze(CreateAnalysisHand(state), ctx);
        if (!afterPonAnalysis.IsValid || !currentAnalysis.IsValid)
            return "Pass — hand data unstable";

        var ponDiscard = afterPonAnalysis.BestDiscard is { } d ? TileHelpers.GetDisplayName(d) : "?";
        if (afterPonAnalysis.Ranked.Count > 0 && afterPonAnalysis.Ranked[0].OpenYakuRisk)
            return "Pass — pon leads to a yakuless hand";
        if (afterPonAnalysis.ShantenAfterDiscard < currentAnalysis.ShantenAfterDiscard)
            return $"Pon recommended → discard {ponDiscard}";
        if (afterPonAnalysis.ShantenAfterDiscard == currentAnalysis.ShantenAfterDiscard
            && afterPonAnalysis.Ukeire > currentAnalysis.Ukeire)
            return $"Pon for more tiles → discard {ponDiscard}";

        return "Pass — pon does not improve hand";
    }

    private string AnalyseChi(StateSnapshot state, Tile callTile)
    {
        var cacheKey = $"chi|{AnalysisSnapshot.ComputeFingerprint(state)}|{callTile}";
        if (this.chiAdviceCache is { } cached && cached.Key == cacheKey)
            return cached.Advice;

        var advice = this.ComputeChiAdvice(state, callTile);
        this.chiAdviceCache = (cacheKey, advice);
        return advice;
    }

    // Tries every chi shape ((n-2,n-1), (n-1,n+1), (n+1,n+2)) available in hand and
    // compares the best resulting position against staying closed.
    private string ComputeChiAdvice(StateSnapshot state, Tile callTile)
    {
        if (callTile.IsHonor)
            return "Pass — honors cannot be called for chi";

        var ctx = BuildCallContext(state);
        var currentAnalysis = this.handAnalyzer.Analyze(CreateAnalysisHand(state), ctx);
        if (!currentAnalysis.IsValid)
            return "Pass — hand data unstable";

        AnalysisResult? bestAfter = null;
        string? bestShape = null;
        foreach (var (lo, hi) in new[] { (-2, -1), (-1, 1), (1, 2) })
        {
            var n1 = callTile.Number + lo;
            var n2 = callTile.Number + hi;
            if (n1 < 1 || n2 > 9)
                continue;

            var t1 = new Tile(callTile.Suit, n1);
            var t2 = new Tile(callTile.Suit, n2);
            if (TileHelpers.CountKind(state.Hand, t1) == 0 || TileHelpers.CountKind(state.Hand, t2) == 0)
                continue;

            var hand = new Hand { SeatWind = state.SeatWind, RoundWind = state.RoundWind };
            var used1 = false;
            var used2 = false;
            foreach (var t in state.Hand)
            {
                if (!used1 && TileHelpers.SameKind(t, t1)) { used1 = true; continue; }
                if (!used2 && TileHelpers.SameKind(t, t2)) { used2 = true; continue; }
                hand.ClosedTiles.Add(t);
            }

            hand.CalledMelds.AddRange(state.OurMelds);
            hand.CalledMelds.Add(new Meld(MeldType.Chi, [t1, TileHelpers.Normalize(callTile), t2], true));

            var analysis = this.handAnalyzer.Analyze(hand, ctx);
            if (!analysis.IsValid)
                continue;

            var better = bestAfter is null
                || analysis.ShantenAfterDiscard < bestAfter.ShantenAfterDiscard
                || (analysis.ShantenAfterDiscard == bestAfter.ShantenAfterDiscard && analysis.Ukeire > bestAfter.Ukeire);
            if (better)
            {
                bestAfter = analysis;
                bestShape = $"{t1}{TileHelpers.Normalize(callTile)}{t2}";
            }
        }

        if (bestAfter is null)
            return "Pass — no chi shape in hand";
        if (bestAfter.Ranked.Count > 0 && bestAfter.Ranked[0].OpenYakuRisk)
            return "Pass — chi leads to a yakuless hand";

        var chiDiscard = bestAfter.BestDiscard is { } d2 ? TileHelpers.GetDisplayName(d2) : "?";
        if (bestAfter.ShantenAfterDiscard < currentAnalysis.ShantenAfterDiscard)
            return $"Chi ({bestShape}) recommended → discard {chiDiscard}";
        if (bestAfter.ShantenAfterDiscard == currentAnalysis.ShantenAfterDiscard
            && bestAfter.Ukeire > currentAnalysis.Ukeire)
            return $"Chi ({bestShape}) for more tiles → discard {chiDiscard}";

        return "Pass — chi does not improve hand";
    }

    private static AnalysisContext BuildCallContext(StateSnapshot state)
    {
        return new AnalysisContext
        {
            SeenTiles = [.. state.SeenForAnalyzer()],
            DoraIndicators = [.. state.DoraIndicators],
            WallRemaining = state.WallRemaining,
            SeatWind = state.SeatWind,
            RoundWind = state.RoundWind,
            Ruleset = state.Ruleset,
        };
    }

    private static Hand CreateAnalysisHand(StateSnapshot state)
    {
        var hand = new Hand
        {
            SeatWind = state.SeatWind,
            RoundWind = state.RoundWind,
        };
        hand.ClosedTiles.AddRange(state.Hand);
        hand.CalledMelds.AddRange(state.OurMelds);
        return hand;
    }

    private static void DrawHeader(string text)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(text);
    }
}
