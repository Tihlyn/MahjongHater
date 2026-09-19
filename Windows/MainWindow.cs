using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
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

    public MainWindow(Plugin plugin, Configuration configuration, EmjStateReader reader)
        : base("Mahjong Hater", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.reader = reader;
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

            if (state.Hand.Count > 0 || state.Legal != LegalAction.None)
                this.DrawDecision(state);
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


    private void DrawDecision(StateSnapshot state)
    {
        ImGui.TextUnformatted($"Seat Wind: {state.SeatWind}   Round Wind: {state.RoundWind}   Wall: {state.WallRemaining}");

        // The policy runs on a background thread (AnalysisService); Draw only renders
        // the latest publication and never blocks.
        var service = this.plugin.AnalysisService;
        var publication = service.Latest;
        if (publication is null)
        {
            ImGui.TextUnformatted("Analyzing hand...");
            return;
        }

        if (publication.Status != AnalysisStatus.Ready || publication.Choice is null)
        {
            ImGui.TextColored(Red, publication.Status == AnalysisStatus.TimedOut
                ? "Analysis timed out — will retry on the next hand change."
                : $"Analysis failed: {publication.Error} — will retry on the next hand change.");
            return;
        }

        var choice = publication.Choice;
        var isFresh = publication.Fingerprint == AnalysisService.ComputeFingerprint(state);
        if (!isFresh)
        {
            ImGui.TextDisabled(service.IsStalled
                ? "Analysis stalled — showing previous hand; will retry on the next hand change."
                : "Updating for the new hand...");
        }

        DrawHandSummary(choice.Hand);
        DrawAction(choice, state);
        DrawCandidates(choice);
        DrawSteps(choice);

        // Never highlight a tile computed for a hand that has since changed.
        if (isFresh && choice.IsDiscard && choice.Tile is { } highlightTile)
            this.DrawBestDiscardHighlight(state, highlightTile);
    }

    private static void DrawHandSummary(HandSummary? hand)
    {
        if (hand is null)
            return;
        if (hand.Shanten <= 0)
            ImGui.TextColored(Gold, "Tenpai!");
        else
            ImGui.TextUnformatted($"Shanten: {hand.Shanten}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"   Ukeire: {hand.Ukeire}");
        if (hand.Waits.Count > 0)
            ImGui.TextWrapped($"Waits: {string.Join(", ", hand.Waits.Select(TileHelpers.GetDisplayName))}");
    }

    // The headline: what to do right now, coloured by how loud it should be.
    private static void DrawAction(ActionChoice choice, StateSnapshot state)
    {
        switch (choice.Kind)
        {
            case ActionKind.Tsumo:
            case ActionKind.Ron:
                ImGui.TextColored(Gold, $">>> {choice.Kind.ToString().ToUpperInvariant()} — declare the win! <<<");
                break;
            case ActionKind.Riichi:
                ImGui.TextColored(Gold, $"Riichi! Discard {Name(choice.Tile)}");
                break;
            case ActionKind.Discard:
                ImGui.TextColored(Gold, $"Best Discard: {Name(choice.Tile)}");
                break;
            case ActionKind.Pon:
            case ActionKind.Chi:
            case ActionKind.MinKan:
            case ActionKind.AnKan:
            case ActionKind.ShouMinKan:
                var tiles = choice.Call is { } meld ? string.Join(" ", meld.Tiles.Select(TileHelpers.GetDisplayName)) : Name(choice.Tile);
                ImGui.TextColored(Gold, $"Call {choice.Kind}: {tiles}");
                break;
            case ActionKind.Pass:
                if (state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare)
                    ImGui.TextColored(Green, $"Pass — {choice.Summary}");
                else
                    ImGui.TextDisabled(choice.Summary);
                break;
            default:
                ImGui.TextDisabled(choice.Summary);
                break;
        }

        if (state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare && state.CallOptions.Count > 0)
            ImGui.TextDisabled($"Options: {string.Join(", ", state.CallOptions)}");
    }

    private static void DrawCandidates(ActionChoice choice)
    {
        if (choice.Candidates.Count < 2)
            return;

        ImGui.Spacing();
        ImGui.TextDisabled("Alternatives:");
        foreach (var c in choice.Candidates.Take(4))
        {
            var line = $"  {TileHelpers.GetDisplayName(c.Tile)} — " +
                       (c.ShantenAfter == 0 ? "tenpai" : $"{c.ShantenAfter}-shanten") +
                       $", {c.Ukeire} tiles, risk {c.DealInRisk:P0}";
            if (c.Note.Length > 0)
                line += $" ({c.Note})";
            ImGui.TextDisabled(line);
        }
    }

    private static void DrawSteps(ActionChoice choice)
    {
        if (choice.Steps.Count == 0)
            return;
        ImGui.Spacing();
        foreach (var step in choice.Steps)
            ImGui.TextWrapped($"[{step.Stage}] {step.Display}");
    }

    private static string Name(Tile? tile) => tile is { } t ? TileHelpers.GetDisplayName(t) : "?";

    private unsafe void DrawBestDiscardHighlight(StateSnapshot state, Tile bestDiscard)
    {
        var addonPtr = this.plugin.GameGui.GetAddonByName(this.reader.Layout.AddonName);
        if (addonPtr.IsNull)
            return;
        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->RootNode == null)
            return;

        var targetNode = this.reader.FindSlotNodeForTile(addon, bestDiscard);
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


    private static void DrawHeader(string text)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(text);
    }
}
