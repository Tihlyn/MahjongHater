using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.Operate;

// The solo Doman Mahjong duties (ContentFinderCondition rows; Duty Finder → Gold Saucer).
public enum MahjongDuty : uint
{
    NoviceFull = 643,       // Novice Mahjong (Full Ranked Match)
    NoviceQuick = 766,      // Novice Mahjong (Quick Ranked Match)
    AdvancedFull = 644,     // Advanced Mahjong (Full Ranked Match) — 1st dan or higher
    AdvancedQuick = 767,    // Advanced Mahjong (Quick Ranked Match)
}

// Keeps a player in back-to-back matches: registers for the chosen duty through the
// Duty Finder queue whenever no match is open, then answers the queue pop. The game's
// own queue state machine (None → Pending → Queued → Ready → Accepted → InContent) is
// the only truth used here. Registration goes straight through the queue packet
// (QueueDuties); if that does not move the state, the Duty Finder window is opened on
// the duty and its Join button clicked instead. Framework-thread only.
public sealed unsafe class MatchQueuer
{
    // The Duty Finder pop window. Its typed CommenceButton is clicked through the
    // registered ButtonClick chain (what a real click delivers); callback 8 is the
    // fallback when the typed pointer is missing.
    private const string ConfirmAddon = "ContentsFinderConfirm";
    private const int CommenceCallback = 8;

    private const string FinderAddon = "ContentsFinder";

    // Let the results screen and the content exit settle before queueing again.
    private static readonly TimeSpan SettleAfterMatch = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryQueueEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommenceEvery = TimeSpan.FromSeconds(3);
    // The direct request gets this long to show up as Pending/Queued before the window path.
    private static readonly TimeSpan DirectRequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan JoinClickEvery = TimeSpan.FromSeconds(4);

    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly Action<string> log;

    private bool wasInMatch;
    private DateTime matchClosedUtc = DateTime.MinValue;
    private DateTime lastQueueAttemptUtc = DateTime.MinValue;
    private DateTime lastCommenceUtc = DateTime.MinValue;
    private DateTime lastJoinClickUtc = DateTime.MinValue;
    private ContentsFinderQueueState lastState = ContentsFinderQueueState.None;
    // Registration in flight: 0 = idle, 1 = direct packet sent, 2 = Duty Finder window path.
    private int registerStage;
    private bool preferWindow;

    public MatchQueuer(IGameGui gameGui, IClientState clientState, Action<string> log)
    {
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.log = log;
    }

    public bool Enabled { get; set; }

    public MahjongDuty Duty { get; set; } = MahjongDuty.NoviceQuick;

    public string Status { get; private set; } = "Off";

    public int MatchesQueued { get; private set; }

    public static string Describe(MahjongDuty duty) => duty switch
    {
        MahjongDuty.NoviceFull => "Novice Mahjong (Full Ranked Match)",
        MahjongDuty.NoviceQuick => "Novice Mahjong (Quick Ranked Match)",
        MahjongDuty.AdvancedFull => "Advanced Mahjong (Full Ranked Match)",
        MahjongDuty.AdvancedQuick => "Advanced Mahjong (Quick Ranked Match)",
        _ => $"duty {(uint)duty}",
    };

    public void Tick(bool inMatch)
    {
        var now = DateTime.UtcNow;
        if (this.wasInMatch && !inMatch)
            this.matchClosedUtc = now;
        this.wasInMatch = inMatch;

        var finder = ContentsFinder.Instance();
        if (finder == null || !this.clientState.IsLoggedIn)
        {
            this.Status = this.Enabled ? "Not logged in" : "Off";
            return;
        }

        var queue = &finder->QueueInfo;
        var state = queue->QueueState;
        if (state != this.lastState)
        {
            this.log($"[Queue] state {this.lastState} → {state}");
            this.lastState = state;
        }

        if (!this.Enabled)
        {
            this.Status = state == ContentsFinderQueueState.None ? "Off" : $"Off ({state})";
            return;
        }

        switch (state)
        {
            case ContentsFinderQueueState.None:
                if (inMatch)
                {
                    this.Status = "In a match";
                    return;
                }

                var settle = SettleAfterMatch - (now - this.matchClosedUtc);
                if (settle > TimeSpan.Zero)
                {
                    this.Status = $"Match over, queueing in {settle.TotalSeconds:F0} s";
                    return;
                }

                this.Register(queue, now);
                return;

            case ContentsFinderQueueState.Pending:
            case ContentsFinderQueueState.Queued:
                if (this.registerStage != 0)
                {
                    this.log($"[Queue] registered via {(this.registerStage == 2 ? "the Duty Finder window" : "QueueDuties")} → {state}");
                    if (this.registerStage == 2)
                        this.HideFinder();
                    this.registerStage = 0;
                }

                var waited = now - queue->GetEnteredQueueDateTime();
                var position = queue->PositionInQueue;
                this.Status = $"In queue {(waited.TotalHours < 1 && waited > TimeSpan.Zero ? waited.ToString(@"mm\:ss") : "")}{(position > 0 ? $" · position {position}" : "")}";
                return;

            case ContentsFinderQueueState.Ready:
                if (now - this.lastCommenceUtc < CommenceEvery)
                {
                    this.Status = "Match found, commencing…";
                    return;
                }

                this.lastCommenceUtc = now;
                this.Status = this.Commence() ? "Match found → Commence" : "Match found, waiting for the Duty Finder window";
                return;

            case ContentsFinderQueueState.Accepted:
                this.Status = "Commenced, waiting for the table";
                return;

            case ContentsFinderQueueState.InContent:
                this.Status = inMatch ? "In a match" : "In content, waiting for the table";
                return;

            default:
                this.Status = state.ToString();
                return;
        }
    }

    // None state, no match open: send the registration, escalating to the window path
    // when the direct packet did not move the state in time.
    private void Register(ContentsFinderQueueInfo* queue, DateTime now)
    {
        var id = (uint)this.Duty;
        var sinceAttempt = now - this.lastQueueAttemptUtc;
        switch (this.registerStage)
        {
            case 0:
                if (sinceAttempt < RetryQueueEvery)
                {
                    this.Status = $"Queue request had no effect, retrying in {(RetryQueueEvery - sinceAttempt).TotalSeconds:F0} s";
                    return;
                }

                this.lastQueueAttemptUtc = now;
                this.MatchesQueued++;
                if (this.preferWindow)
                {
                    this.OpenFinder(id);
                    return;
                }

                queue->QueueDuties(&id, 1);
                this.registerStage = 1;
                this.log($"[Queue] QueueDuties({id}) — {Describe(this.Duty)}");
                this.Status = $"Queue request sent for {Describe(this.Duty)}";
                return;

            case 1:
                if (sinceAttempt < DirectRequestTimeout)
                {
                    this.Status = $"Queue request sent, waiting for the game ({sinceAttempt.TotalSeconds:F0} s)";
                    return;
                }

                this.log("[Queue] QueueDuties did not change the queue state; using the Duty Finder window instead");
                this.preferWindow = true;
                this.OpenFinder(id);
                return;

            case 2:
                if (sinceAttempt > RetryQueueEvery)
                {
                    this.log("[Queue] Duty Finder window path did not register either; giving up this attempt");
                    this.registerStage = 0;
                    this.Status = "Registration failed (see log)";
                    return;
                }

                if (now - this.lastJoinClickUtc < JoinClickEvery)
                    return;
                this.lastJoinClickUtc = now;
                this.Status = this.ClickJoin() ? "Duty Finder → Join" : "Waiting for the Duty Finder window";
                return;
        }
    }

    private void OpenFinder(uint id)
    {
        var agent = AgentContentsFinder.Instance();
        if (agent == null)
        {
            this.log("[Queue] AgentContentsFinder unavailable");
            this.registerStage = 0;
            return;
        }

        agent->OpenRegularDuty(id, false);
        this.registerStage = 2;
        this.lastJoinClickUtc = DateTime.UtcNow;   // let the window build before the first Join
        this.log($"[Queue] OpenRegularDuty({id}) — {Describe(this.Duty)}");
        this.Status = "Duty Finder opened on the duty";
    }

    private bool ClickJoin()
    {
        var ptr = this.gameGui.GetAddonByName(FinderAddon);
        if (ptr.IsNull)
            return false;
        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible || addon->RootNode == null)
            return false;

        var finder = (AddonContentsFinder*)addon;
        var button = finder->JoinButton;
        if (button == null || button->OwnerNode == null || !button->IsEnabled || !button->OwnerNode->AtkResNode.IsVisible())
        {
            this.log($"[Queue] {FinderAddon} Join button not clickable (button={(button == null ? "null" : button->IsEnabled ? "enabled" : "disabled")}) — is the duty selected?");
            return false;
        }

        var dispatch = EmjOperator.ClickNode(addon, &button->OwnerNode->AtkResNode);
        this.log($"[Queue] {FinderAddon} Join: {dispatch.Detail}");
        return dispatch.Sent;
    }

    private void HideFinder()
    {
        var agent = AgentContentsFinder.Instance();
        if (agent != null && agent->IsAgentActive())
            agent->Hide();
    }

    private bool Commence()
    {
        var ptr = this.gameGui.GetAddonByName(ConfirmAddon);
        if (ptr.IsNull)
        {
            this.log($"[Queue] pop is Ready but {ConfirmAddon} is not open yet");
            return false;
        }

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible || addon->RootNode == null)
        {
            this.log($"[Queue] {ConfirmAddon} exists but is not visible yet");
            return false;
        }

        var confirm = (AddonContentsFinderConfirm*)addon;
        var button = confirm->CommenceButton;
        if (button != null && button->OwnerNode != null && button->IsEnabled && button->OwnerNode->AtkResNode.IsVisible())
        {
            var dispatch = EmjOperator.ClickNode(addon, &button->OwnerNode->AtkResNode);
            if (dispatch.Sent)
            {
                this.log($"[Queue] {ConfirmAddon} Commence button: {dispatch.Detail}");
                return true;
            }

            this.log($"[Queue] {ConfirmAddon} Commence button not clickable ({dispatch.Detail}); falling back to the callback");
        }

        var callback = EmjOperator.FireCallback(addon, CommenceCallback);
        this.log($"[Queue] {ConfirmAddon} Commence button unavailable (button={(button == null ? "null" : button->IsEnabled ? "enabled" : "disabled")}); {callback}");
        return true;
    }
}
