using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MahjongHater.Core;
using MahjongHater.Core.Operate;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Windows;

public sealed class MainWindow : Window
{
    private static readonly ConditionalWeakTable<Configuration, MainWindow> Overlays = new();
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly EmjStateReader reader;
    private Theme.Scope theme;
    private ActionChoice? displayedChoice;
    private CandidateText[] candidateText = [];
    private string handText = string.Empty;
    private string scoreText = "0";
    private string recordText = "0 W / 0 L";
    private string winRateText = "0.0%";
    private string tableText = string.Empty;
    private (int Score, int Wins, int Losses)? sessionKey;
    private (Wind Seat, Wind Round, int Wall)? tableKey;
    private Tab tab = Tab.Play;

    public MainWindow(Plugin plugin, Configuration configuration, EmjStateReader reader)
        : base("Mahjong Hater", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.reader = reader;
        Overlays.AddOrUpdate(configuration, this);
        this.IsOpen = configuration.ShowOverlay;
        // WindowSystem applies GlobalScale to window sizes and constraints.
        this.Size = Theme.MainSize;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Theme.MinimumSize,
            MaximumSize = Theme.MaximumSize,
        };
    }

    public override void PreDraw() => this.theme = new Theme.Scope();

    public override void PostDraw() => this.theme.Dispose();

    internal static void SetOverlayVisibility(Configuration configuration, bool visible)
    {
        configuration.ShowOverlay = visible;
        // Plugin.DrawUi copies IsOpen back into configuration after both windows draw.
        if (Overlays.TryGetValue(configuration, out var window))
            window.IsOpen = visible;
    }

    public override void Draw()
    {
        this.IsOpen = this.configuration.ShowOverlay;
        Theme.Surface();
        this.DrawHeader();
        this.DrawTabs();
        var state = this.reader.Current;

        // The in-game tile highlight is drawn on the game window, not in a tab: keep it up
        // while the diagnostics tab is open.
        var publication = this.plugin.AnalysisService.Latest;
        if (state is { Phase: not GamePhase.NotInGame } && publication is { Status: AnalysisStatus.Ready, Choice.IsDiscard: true }
            && publication.Choice.Tile is { } highlightTile && publication.Fingerprint == AnalysisService.ComputeFingerprint(state))
            this.DrawBestDiscardHighlight(state, highlightTile);

        if (this.tab == Tab.Play)
            this.DrawPlay(state, publication);
        else
            this.DrawDiagnostics(state);

        this.configuration.ShowOverlay = this.IsOpen;
    }

    private void DrawTabs()
    {
        var half = (Widgets.ContentWidth - Theme.Px(Theme.Gap)) / 2f;
        if (Widgets.Pill("Play##tab", this.tab == Tab.Play, half))
            this.tab = Tab.Play;
        ImGui.SameLine();
        var stalls = this.plugin.AutoPlayer.StallsThisSession;
        if (Widgets.Pill(stalls > 0 ? $"Diagnostics ({stalls})##tab" : "Diagnostics##tab", this.tab == Tab.Diagnostics, half))
            this.tab = Tab.Diagnostics;
        if (ImGui.IsItemHovered())
            Widgets.Tooltip("Table tracking, tracker notes and the auto-play counters.");
    }

    private void DrawPlay(StateSnapshot? state, AnalysisPublication? publication)
    {
        this.DrawSession(state);
        this.DrawAutoPlay();
        if (state is not { Phase: not GamePhase.NotInGame })
        {
            DrawWaiting("Ready when you are", "Not currently seated at a Doman Mahjong table.");
            return;
        }

        // The table card moved to the diagnostics tab; this warning did not, because it says
        // the recommendation below may be built on a stale hand read.
        if (!state.LayoutHealthy)
        {
            using (Widgets.Card())
            {
                Widgets.Badge("Layout check failed", warning: true);
                Widgets.Wrapped("Hand read may be stale. See the diagnostics tab.");
            }
        }

        ActionChoice? choice = null;
        if (state.Hand.Count > 0 || state.Legal != LegalAction.None)
            choice = this.DrawDecision(state, publication);
        else
            DrawWaiting("Waiting for tiles", "Seated at the table. Waiting for hand tile data...");

        // Call options remain visible even while analysis is pending or failed.
        if (state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare && state.CallOptions.Count > 0)
        {
            using (Widgets.Card())
            {
                Widgets.Label("AVAILABLE CALLS");
                for (var i = 0; i < state.CallOptions.Count; i++)
                    Widgets.Wrapped(state.CallOptions[i]);
            }
        }

        if (choice is not null)
        {
            this.DrawCandidates(choice);
            DrawSteps(choice);
        }
    }

    // Everything that is read after the fact rather than during a decision: what the reader
    // makes of the table, the tracker's notes, and the unattended-run counters.
    private void DrawDiagnostics(StateSnapshot? state)
    {
        if (state is { Phase: not GamePhase.NotInGame })
            this.DrawTable(state);
        else
            DrawWaiting("No table", "Table tracking appears here once you are seated.");

        var player = this.plugin.AutoPlayer;
        var queuer = this.plugin.Queuer;
        using (Widgets.Card())
        {
            Widgets.Label("AUTO PLAY COUNTERS");
            var guard = this.plugin.IdleGuard;
            Widgets.Wrapped($"{player.DecisionsExecuted} decisions · {player.StallsThisSession} stalls · {player.RecoveriesThisSession} recoveries · {queuer.MatchesQueued} queued · {guard.NudgesThisSession} nudges", false);
            Widgets.Wrapped($"Anti-idle: {guard.Status}");
            if (guard.NudgesThisSession > 0 && !guard.LastNudgeReachedTheGame)
                Widgets.Badge("Last nudge went to a background window", warning: true);
            if (player.StallsThisSession > 0)
            {
                Widgets.Wrapped($"Stall log: {this.plugin.StallLogPath}");
                if (ImGui.IsItemHovered())
                    Widgets.Tooltip("Each stall appends the snapshot, the decision, the prompt rows, slot clickability and the tracker's recent events.");
            }

            var journal = player.Journal;
            if (journal.Count == 0)
                Widgets.Wrapped("No auto-play actions yet this session.");
            for (var i = Math.Max(0, journal.Count - 8); i < journal.Count; i++)
                Widgets.Wrapped(journal[i]);
        }
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

    private void DrawHeader()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var buttonSize = Math.Max(Theme.Px(Theme.ControlHeight), ImGui.GetFrameHeight());
        var stateWidth = ImGui.CalcTextSize("Disabled").X + Theme.Px(Theme.CardPadding * 2f);
        var titleWidth = width - stateWidth - buttonSize * 2f - Theme.Px(Theme.Gap * 3f);
        Widgets.DisplayText("Mahjong Hater", Theme.TitleScale, Theme.Text, titleWidth);
        ImGui.SameLine();
        ImGui.SetCursorScreenPos(origin + new Vector2(titleWidth + Theme.Px(Theme.Gap), 0f));
        if (Widgets.Pill(this.configuration.PluginEnabled ? "Enabled##enabled" : "Disabled##enabled", this.configuration.PluginEnabled, stateWidth))
        {
            this.configuration.PluginEnabled = !this.configuration.PluginEnabled;
            this.configuration.Save();
        }

        ImGui.SameLine();
        if (Widgets.Pill("\u2699##settings", width: buttonSize))
            this.plugin.OpenConfigWindow();
        if (ImGui.IsItemHovered())
            Widgets.Tooltip("Settings");
        ImGui.SameLine();
        if (Widgets.Pill("x##close", width: buttonSize))
            this.IsOpen = false;
        if (ImGui.IsItemHovered())
            Widgets.Tooltip("Hide overlay");
    }

    private void DrawSession(StateSnapshot? state)
    {
        var wins = this.reader.Tracker.WinsThisSession;
        var losses = this.reader.Tracker.LossesThisSession;
        var key = (state?.Us.Score ?? 0, wins, losses);
        if (this.sessionKey != key)
        {
            this.sessionKey = key;
            this.scoreText = $"{key.Item1:N0}";
            this.recordText = $"{wins} W / {losses} L";
            this.winRateText = $"{(wins + losses == 0 ? 0d : (double)wins / (wins + losses)):P1}";
        }

        using (Widgets.Card())
        {
            var origin = ImGui.GetCursorScreenPos();
            var column = Widgets.ContentWidth / 3f;
            DrawMetric("SCORE", this.scoreText, column);
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(origin + new Vector2(column, 0f));
            DrawMetric("SESSION W / L", this.recordText, column);
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(origin + new Vector2(column * 2f, 0f));
            DrawMetric("WIN RATE", this.winRateText, column);
        }
    }

    // Dev tooling for unattended matches: the auto player executes the overlay's own
    // decisions, the queuer keeps the Duty Finder fed. Both report one status line each;
    // a stall is called out loudly because it is what these runs are looking for.
    private void DrawAutoPlay()
    {
        var player = this.plugin.AutoPlayer;
        var queuer = this.plugin.Queuer;
        using (Widgets.Card())
        {
            Widgets.Label("AUTO PLAY");
            if (Widgets.ToggleRow("Play the recommended actions", "##autoplay", player.Enabled))
            {
                player.SetEnabled(!player.Enabled);
                this.configuration.AutoPlay = player.Enabled;
                this.configuration.Save();
            }

            if (player.IsStalled)
                Widgets.Badge($"STALLED {player.StalledFor.TotalSeconds:F0} s — dump #{player.StallsThisSession} written", warning: true);
            Widgets.Wrapped(player.Status);

            if (Widgets.ToggleRow("Requeue when a match ends", "##requeue", queuer.Enabled))
            {
                queuer.Enabled = !queuer.Enabled;
                this.configuration.Requeue = queuer.Enabled;
                this.configuration.Save();
            }

            Widgets.Wrapped(queuer.Status);

            var guard = this.plugin.IdleGuard;
            if (Widgets.ToggleRow("Anti-idle nudge (duties eject after ~5 min)", "##antiidle", guard.Enabled))
            {
                guard.Enabled = !guard.Enabled;
                guard.Reset();
                this.configuration.AntiIdle = guard.Enabled;
                this.configuration.Save();
            }

            if (ImGui.IsItemHovered())
                Widgets.Tooltip("While auto play runs a match, sends one harmless input every "
                    + $"{IdleGuard.Clamp(guard.Interval).TotalSeconds:F0} s — but only after the machine has been idle that long, "
                    + "so it never interferes while you are using it. The auto player's own clicks are addon events, which the duty timer does not see.");
            this.DrawDutyPicker(queuer);
        }
    }

    // Rank × length → the ContentFinderCondition the queuer registers for.
    private void DrawDutyPicker(MatchQueuer queuer)
    {
        var half = (Widgets.ContentWidth - Theme.Px(Theme.Gap)) / 2f;
        var advanced = queuer.Duty is MahjongDuty.AdvancedFull or MahjongDuty.AdvancedQuick;
        var full = queuer.Duty is MahjongDuty.NoviceFull or MahjongDuty.AdvancedFull;
        Widgets.Label("DUTY");
        if (Widgets.Pill("Novice##rank", !advanced, half))
            this.SetDuty(queuer, advanced: false, full);
        ImGui.SameLine();
        if (Widgets.Pill("Advanced##rank", advanced, half))
            this.SetDuty(queuer, advanced: true, full);
        if (ImGui.IsItemHovered())
            Widgets.Tooltip("Advanced Mahjong needs 1st dan or higher.");
        if (Widgets.Pill("Quick (East)##length", !full, half))
            this.SetDuty(queuer, advanced, full: false);
        ImGui.SameLine();
        if (Widgets.Pill("Full (East + South)##length", full, half))
            this.SetDuty(queuer, advanced, full: true);
        Widgets.Wrapped(MatchQueuer.Describe(queuer.Duty));
    }

    private void SetDuty(MatchQueuer queuer, bool advanced, bool full)
    {
        queuer.Duty = (advanced, full) switch
        {
            (false, false) => MahjongDuty.NoviceQuick,
            (false, true) => MahjongDuty.NoviceFull,
            (true, false) => MahjongDuty.AdvancedQuick,
            (true, true) => MahjongDuty.AdvancedFull,
        };
        this.configuration.RequeueDuty = (uint)queuer.Duty;
        this.configuration.Save();
    }

    private static void DrawMetric(string label, string value, float width)
    {
        ImGui.BeginGroup();
        try
        {
            Widgets.DisplayText(label, Theme.LabelScale, Theme.Muted, width);
            Widgets.DisplayText(value, Theme.BodyScale, Theme.Text, width);
        }
        finally
        {
            ImGui.EndGroup();
        }
    }

    private void DrawTable(StateSnapshot state)
    {
        var key = (state.SeatWind, state.RoundWind, state.WallRemaining);
        if (this.tableKey != key)
        {
            this.tableKey = key;
            this.tableText = $"Seat {state.SeatWind} / Round {state.RoundWind} / Wall {state.WallRemaining}";
        }

        using (Widgets.Card())
        {
            Widgets.Label("TABLE");
            Widgets.Wrapped(this.tableText, false);
            if (!state.LayoutHealthy)
            {
                Widgets.Badge("Layout check failed", warning: true);
                Widgets.Wrapped("Hand read may be stale.");
            }

            for (var i = 0; i < state.Notes.Count; i++)
                Widgets.Wrapped(state.Notes[i]);
        }
    }

    private ActionChoice? DrawDecision(StateSnapshot state, AnalysisPublication? publication)
    {
        // One immutable publication, read by Draw; the render thread never runs policy.
        var service = this.plugin.AnalysisService;
        if (publication is null)
        {
            DrawWaiting(service.IsStalled ? "Analysis stalled" : "Analyzing hand...",
                service.IsStalled ? "Will retry on the next hand change." : "Your recommendation will appear here.");
            return null;
        }

        if (publication.Status != AnalysisStatus.Ready || publication.Choice is null)
        {
            using (Widgets.Card())
            {
                Widgets.Badge(publication.Status == AnalysisStatus.TimedOut ? "Analysis timed out" : "Analysis failed", warning: true);
                if (!string.IsNullOrEmpty(publication.Error))
                    Widgets.Wrapped(publication.Error);
                Widgets.Wrapped("Will retry on the next hand change.");
            }

            return null;
        }

        var choice = publication.Choice;
        var isFresh = publication.Fingerprint == AnalysisService.ComputeFingerprint(state);
        this.CacheChoice(choice);
        using (Widgets.Card(headline: true))
        {
            Widgets.Label(isFresh ? "RECOMMENDED ACTION" : "PREVIOUS HAND");
            if (!isFresh || service.IsStalled)
                Widgets.Wrapped(service.IsStalled
                    ? "Analysis stalled. Showing the previous hand; will retry on the next hand change."
                    : "Updating for the new hand...");
            DrawAction(choice);
            this.DrawHandSummary(choice.Hand);
        }

        return choice;
    }

    private void CacheChoice(ActionChoice choice)
    {
        if (ReferenceEquals(this.displayedChoice, choice))
            return;
        this.displayedChoice = choice;
        this.handText = choice.Hand is { } hand
            ? $"{(hand.Shanten <= 0 ? "Tenpai!" : $"{hand.Shanten}-shanten")}   /   Ukeire {hand.Ukeire}"
            : string.Empty;
        this.candidateText = new CandidateText[Math.Min(Theme.CandidateLimit, choice.Candidates.Count)];
        for (var i = 0; i < this.candidateText.Length; i++)
        {
            var candidate = choice.Candidates[i];
            // "Risk" is the aggregate deal-in chance; the rank letter is the tile's conditional
            // danger against the primary threat (docs/DEFENSE_PLAN.md §2).
            var rank = candidate.DangerSeat >= 1
                ? $"  ·  {RankLabel(candidate.DangerRank)} {candidate.Danger:P1} vs seat {candidate.DangerSeat}"
                : string.Empty;
            var dangerLine = candidate.DangerSeat >= 1 && !string.IsNullOrEmpty(candidate.DangerNote)
                ? $"\nDanger: {candidate.DangerNote}" : string.Empty;
            this.candidateText[i] = new CandidateText(
                $"{(candidate.ShantenAfter <= 0 ? "Tenpai" : $"{candidate.ShantenAfter}-shanten")} / {candidate.Ukeire} tiles",
                $"Risk {candidate.DealInRisk:P0}{rank}",
                $"Two-step ukeire: {candidate.Ukeire2}\nValue: {candidate.Value:0.##}\nRanking score: {candidate.Score:0.##}{dangerLine}");
        }
    }

    private static void DrawAction(ActionChoice choice)
    {
        var headline = choice.Kind switch
        {
            ActionKind.Tsumo => "Tsumo!",
            ActionKind.Ron => "Ron!",
            ActionKind.Riichi => "Riichi!",
            ActionKind.Discard => "Discard",
            ActionKind.Pon => "Pon",
            ActionKind.Chi => "Chi",
            ActionKind.MinKan => "Open kan",
            ActionKind.AnKan => "Closed kan",
            ActionKind.ShouMinKan => "Added kan",
            ActionKind.Pass => "Pass",
            _ => "Standing by",
        };
        // Calls and declarations read green (take it), declining reads yellow (let it go);
        // an ordinary discard keeps the neutral text colour.
        var color = choice.IsWin || choice.IsCall || choice.Kind == ActionKind.Riichi ? Theme.Positive
            : choice.Kind == ActionKind.Pass ? Theme.Decline
            : choice.Kind == ActionKind.None ? Theme.Muted : Theme.Text;
        Widgets.DisplayText(headline, Theme.HeadlineScale, color, Widgets.ContentWidth);
        if (choice.IsCall && choice.Call is { } meld)
            Widgets.Tiles(meld.Tiles);
        else if (choice.Tile is { } tile)
        {
            if (choice.IsDiscard)
                ImGui.SameLine();
            Widgets.Tile(tile);
        }
        if (!string.IsNullOrEmpty(choice.Summary))
            Widgets.Wrapped(choice.Summary);
    }

    private void DrawHandSummary(HandSummary? hand)
    {
        if (hand is null)
            return;
        Widgets.Wrapped(this.handText, false);
        if (hand.Waits.Count > 0)
        {
            Widgets.Label("WAITS");
            Widgets.Tiles(hand.Waits);
        }
    }

    private void DrawCandidates(ActionChoice choice)
    {
        if (this.candidateText.Length == 0)
            return;
        using (Widgets.Card())
        {
            Widgets.Label("DISCARD CANDIDATES");
            for (var i = 0; i < this.candidateText.Length; i++)
            {
                var candidate = choice.Candidates[i];
                var text = this.candidateText[i];
                Widgets.Tile(candidate.Tile);
                ImGui.SameLine();
                ImGui.BeginGroup();
                try
                {
                    Widgets.Wrapped(text.Summary, false);
                    if (ImGui.IsItemHovered())
                        Widgets.Tooltip(text.Details);
                    Widgets.DisplayText(text.Risk, Theme.LabelScale, Theme.Muted, Widgets.ContentWidth);
                    ImGui.SameLine();
                    Widgets.Risk(candidate.DealInRisk);
                    if (!string.IsNullOrEmpty(candidate.Note))
                        Widgets.Wrapped(candidate.Note);
                    if (candidate.Waits.Count > 0)
                    {
                        Widgets.Label("WAITS");
                        Widgets.Tiles(candidate.Waits);
                    }
                }
                finally
                {
                    ImGui.EndGroup();
                }
            }
        }
    }

    private static void DrawSteps(ActionChoice choice)
    {
        if (choice.Steps.Count == 0)
            return;
        using (Widgets.Card())
        {
            Widgets.Label("REASONING");
            for (var i = 0; i < choice.Steps.Count; i++)
            {
                var step = choice.Steps[i];
                Widgets.Label(step.Stage);
                Widgets.Wrapped(step.Display);
            }
        }
    }

    private static void DrawWaiting(string title, string description)
    {
        using (Widgets.Card(headline: true))
        {
            Widgets.Label("CURRENT HAND");
            Widgets.DisplayText(title, Theme.HeadlineScale, Theme.Text, Widgets.ContentWidth);
            Widgets.Wrapped(description);
        }
    }

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

        // RootNode already includes addon X/Y; stop before it to avoid double offsets.
        float relX = 0, relY = 0;
        var cur = targetNode;
        for (var depth = 0; cur != null && cur->ParentNode != null && depth < 20; depth++, cur = cur->ParentNode)
        {
            relX += cur->X;
            relY += cur->Y;
        }

        // Game geometry follows addon scale; only the glow uses Dalamud UI scale.
        var s = addon->Scale;
        var topLeft = new Vector2(addon->X + (relX * s), addon->Y + (relY * s));
        var botRight = topLeft + new Vector2(Theme.GameTileWidth * s, Theme.GameTileHeight * s);
        Theme.DiscardGlow(ImGui.GetForegroundDrawList(), topLeft, botRight);
    }

    private enum Tab { Play, Diagnostics }

    private readonly record struct CandidateText(string Summary, string Risk, string Details);

    private static string RankLabel(DangerRank rank) => rank switch
    {
        DangerRank.S => "S",
        DangerRank.APlus => "A+",
        _ => rank.ToString(),
    };
}
