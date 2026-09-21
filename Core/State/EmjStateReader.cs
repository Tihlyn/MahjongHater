using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.State;

// Owns the struct reader, the event tracker and the snapshot builder; the only game-
// memory touching class the plugin talks to. Tick() runs on the framework thread and
// publishes Current (null while the Emj addon is closed).
public sealed unsafe class EmjStateReader : IDisposable
{
    private const int WindScanInterval = 30;

    private readonly IGameGui gameGui;
    private readonly IPluginLog pluginLog;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly Configuration configuration;
    private readonly EmjStructReader structReader;
    private readonly EventTracker tracker;
    private readonly SnapshotBuilder builder = new();
    private readonly IAddonLifecycle.AddonEventDelegate onRefresh;
    private readonly IAddonLifecycle.AddonEventDelegate onReceiveEvent;
    private int ticks;
    private bool readFailureLogged;

    // Tenpai ground truth: the last in-play snapshot is frozen when the phase turns to
    // RoundEnd, then the seat banners are polled until the announcement has landed.
    private StateSnapshot? lastPlaySnapshot;
    private StateSnapshot? pendingCalibration;
    private int calibrationTicks;
    // Deal-in ground truth: our discards against threats, labelled at hand end.
    private readonly Policy.DealInRecorder dealIns = new();

    public EmjStateReader(IGameGui gameGui, IPluginLog pluginLog, IAddonLifecycle addonLifecycle, Configuration configuration, EmjLayout layout)
    {
        this.gameGui = gameGui;
        this.pluginLog = pluginLog;
        this.addonLifecycle = addonLifecycle;
        this.configuration = configuration;
        this.Layout = layout;
        this.structReader = new EmjStructReader(layout);
        this.tracker = new EventTracker(msg => pluginLog.Information(msg));

        this.onRefresh = this.OnRefresh;
        this.onReceiveEvent = this.OnReceiveEvent;
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, layout.AddonName, this.onRefresh);
        this.addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, layout.AddonName, this.onReceiveEvent);
        pluginLog.Info($"[State] Layout '{layout.Name}' loaded: hand@0x{layout.HandArray:X4} base={layout.TileIconBase} read={this.structReader.ReadBytes} bytes.");
    }

    public EmjLayout Layout { get; }

    public EventTracker Tracker => this.tracker;

    public StateSnapshot? Current { get; private set; }

    public StructFrame? LastFrame { get; private set; }

    public DecodedStruct? LastDecoded { get; private set; }

    // Receives the tenpai samples of every finished hand (plugin appends them to CSV).
    public Action<IReadOnlyList<Policy.TenpaiSample>>? CalibrationSink { get; set; }

    // Receives the deal-in samples of every finished hand (plugin appends them to CSV).
    public Action<IReadOnlyList<Policy.DealInSample>>? DealInSink { get; set; }

    // Receives one result row per finished hand (plugin appends it to CSV).
    public Action<Policy.HandResult>? HandResultSink { get; set; }

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(this.onRefresh);
        this.addonLifecycle.UnregisterListener(this.onReceiveEvent);
    }

    public void Tick()
    {
        try
        {
            var addon = this.GetAddon();
            if (addon == null)
            {
                this.Current = null;
                this.LastFrame = null;
                this.LastDecoded = null;
                this.builder.BuildNotInGame();
                return;
            }

            if (!this.structReader.TryRead(addon, this.Layout, out var frame))
            {
                if (!this.readFailureLogged)
                {
                    this.readFailureLogged = true;
                    this.pluginLog.Warning("[State] Struct read failed — keeping the last snapshot.");
                }

                return;
            }

            this.readFailureLogged = false;
            var decoded = frame.Decode(this.Layout);
            this.LastFrame = frame;
            this.LastDecoded = decoded;

            this.tracker.OnTick(decoded, PromptLabels(addon));

            // Winds live in text nodes only (docs/EMJ_STRUCT.md, "Not in the struct").
            if (++this.ticks % WindScanInterval == 0)
                this.ScanWinds(addon);

            this.Current = this.builder.Build(decoded, this.tracker, this.Layout, new RulesetOptions(this.configuration.Kuitan, (int)this.configuration.GameLength));
            this.RecordTenpaiGroundTruth(addon, this.Current);
        }
        catch (Exception ex)
        {
            this.pluginLog.Error(ex, "[State] Tick failed.");
        }
    }

    public void Reset()
    {
        this.tracker.Reset();
        this.Current = null;
        this.builder.BuildNotInGame();
    }


    private void OnRefresh(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 22)
                return;
            this.tracker.OnRefresh(EmjStructReader.CopyAtkValues(addon));
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[State] OnRefresh failed.");
        }
    }

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonReceiveEventArgs ev)
                return;
            var atkType = (ushort)ev.AtkEventType;
            if (atkType != 74)
                return;
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null)
                return;
            this.tracker.OnReceiveEvent(atkType, EmjStructReader.CopyAtkValues(addon));
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[State] OnReceiveEvent failed.");
        }
    }

    // Seat winds from the four score-panel texts (dealer = "East"); round wind from the
    // optional hidden text node (else the tracker keeps the type-32 win-screen string).
    private void ScanWinds(AtkUnitBase* addon)
    {
        var nodes = this.Layout.Nodes;
        var winds = new Wind?[4];
        var any = false;
        for (var seat = 0; seat < 4; seat++)
        {
            winds[seat] = EmjScanner.ParseWindText(EmjScanner.ReadTextAtPath(addon, nodes.SeatWindTexts[seat]));
            any |= winds[seat] is not null;
        }

        if (any)
            this.tracker.HintSeatWinds(winds);

        // "South 4 South Wind": the round is the leading word; the string is win-screen
        // residue, so it lags by at most one hand and beats the East default on a
        // mid-session load (docs/EMJ_STRUCT.md, "Not in the struct").
        var roundText = EmjScanner.ReadTextAtPath(addon, nodes.RoundWindText);
        var leading = roundText?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (EmjScanner.ParseWindText(leading) is { } round)
        {
            if (this.Layout.RoundWind is null)
                this.tracker.HintRound(round, EventTracker.ParseHandNumber(roundText) ?? 0);
            else if (EventTracker.ParseHandNumber(roundText) is { } handNo)
                this.tracker.HintRound(this.tracker.RoundWind, handNo);
        }
    }

    // Draw screens label every seat "Tenpai!"/"Noten..." in the seat banners; a win names
    // the winner in type-32. Banners keep their last text (residue), so on a draw we wait
    // until all three opponent banners read Tenpai/Noten — up to ~5 s — before trusting them.
    private void RecordTenpaiGroundTruth(AtkUnitBase* addon, StateSnapshot snapshot)
    {
        if (snapshot.Phase != GamePhase.RoundEnd)
        {
            if (snapshot.Phase is not (GamePhase.NotInGame or GamePhase.Dealing) && snapshot.Hand.Count > 0)
            {
                this.lastPlaySnapshot = snapshot;
                this.dealIns.Population = this.configuration.CalibrationPopulation;
                this.dealIns.Model = this.configuration.DefenseV2 ? "V2" : "Legacy";
                this.dealIns.Observe(snapshot, DateTime.UtcNow);
            }

            this.pendingCalibration = null;
            return;
        }

        var sink = this.CalibrationSink;
        if (this.pendingCalibration is null)
        {
            if (this.lastPlaySnapshot is null || sink is null)
                return;
            this.pendingCalibration = this.lastPlaySnapshot;
            this.lastPlaySnapshot = null;
            this.calibrationTicks = 0;
        }

        var winner = this.tracker.LastWinnerSeat;
        var banners = new string?[4];
        var paths = this.Layout.Nodes.ResultBanners;
        for (var seat = 0; seat < 4 && seat < paths.Length; seat++)
            banners[seat] = EmjScanner.ReadTextAtPath(addon, paths[seat]);

        var complete = winner >= 0 || Policy.TenpaiCalibration.DrawBannersComplete(banners);
        if (!complete && ++this.calibrationTicks < 300)
            return;

        var samples = Policy.TenpaiCalibration.FromRoundEnd(this.pendingCalibration, banners, winner, Policy.PolicyWeights.Default, DateTime.UtcNow);
        this.pendingCalibration = null;
        this.tracker.Note($"tenpai calibration: {samples.Count} sample(s) (winner={winner}, banners=[{string.Join("|", banners.Select(b => b ?? "-"))}])");
        if (samples.Count > 0)
            sink?.Invoke(samples);

        var last = this.pendingCalibration;
        var dealIns = this.dealIns.Finish(winner, this.tracker.LastWinByRon, this.tracker.RonVictimSeat, this.tracker.RonTile);
        this.tracker.Note($"deal-in calibration: {dealIns.Count} row(s), ron={this.tracker.LastWinByRon} victim={this.tracker.RonVictimSeat} tile={this.tracker.RonTile?.ToString() ?? "-"}");
        if (dealIns.Count > 0)
            this.DealInSink?.Invoke(dealIns);

        var outcome = Policy.HandResult.Classify(winner, this.tracker.LastWinByRon, this.tracker.RonVictimSeat,
            winner >= 0 ? null : Policy.TenpaiCalibration.BannerMeansTenpai(banners[0]));
        this.HandResultSink?.Invoke(new Policy.HandResult(DateTime.UtcNow, last.RoundWind.ToString(), last.HandNumber,
            Math.Max(last.Us.Discards.Count, last.Us.DiscardCount), outcome, this.tracker.LastScoreDelta, last.OurRiichi,
            last.Seats.Count(s => s.Seat != 0 && s.Riichi), this.configuration.CalibrationPopulation,
            this.configuration.DefenseV2 ? "V2" : "Legacy"));
    }

    // Visible texts of the call panel (Pon/Chi/Pass, Riichi/Tsumo/…). They persist after a
    // prompt closes and the list's item-table labels are always empty, so this is only the
    // tracker's fallback edge — the type-19/23 events are the real signal.
    private static List<string> PromptLabels(AtkUnitBase* addon) => EmjScanner.ScanCallButtonTexts(addon);

    // Slot node holding a tile of the current hand, by struct semantics: the draw always
    // sits in the dedicated draw slot, closed tile i in the i-th non-draw slot by X.
    // After a meld the parked slots (1340010+) still scan as visible but carry nothing,
    // so "hand index == visual index" is wrong there. Exact tile first (red vs plain is
    // the policy's choice), then same kind; the draw wins ties as the natural discard.
    public AtkResNode* FindSlotNodeForTile(AtkUnitBase* addon, Tile wanted, List<ScannedTileSlot>? slots = null)
    {
        var decoded = this.LastDecoded;
        if (decoded == null)
            return null;

        slots ??= EmjScanner.ScanHandSlots(addon);
        var drawId = (uint)this.Layout.Nodes.HandSlotDraw;
        var drawPtr = slots.FirstOrDefault(sl => sl.NodeId == drawId).NodePtr; // 0 when absent
        var closedSlots = slots.Where(sl => sl.NodeId != drawId).ToList();
        var closed = decoded.ClosedTiles;

        AtkResNode* Closed(Func<Tile, bool> match)
        {
            for (var i = closed.Count - 1; i >= 0; i--)
                if (match(closed[i]) && i < closedSlots.Count)
                    return (AtkResNode*)closedSlots[i].NodePtr;
            return null;
        }

        var drawn = decoded.DrawnTile;
        if (drawn is { } d && d.Equals(wanted) && drawPtr != 0)
            return (AtkResNode*)drawPtr;
        var exact = Closed(t => t.Equals(wanted));
        if (exact != null)
            return exact;
        if (drawn is { } d2 && TileHelpers.SameKind(d2, wanted) && drawPtr != 0)
            return (AtkResNode*)drawPtr;
        return Closed(t => TileHelpers.SameKind(t, wanted));
    }

    private AtkUnitBase* GetAddon()
    {
        var ptr = this.gameGui.GetAddonByName(this.Layout.AddonName);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        return addon->RootNode == null ? null : addon;
    }
}
