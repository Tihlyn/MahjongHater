using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHater.Core.State;

// Owns the struct reader, the event tracker and the snapshot builder; the only game-
// memory touching class the plugin talks to. Tick() runs on the framework thread and
// publishes Current (null while the Emj addon is closed).
public sealed unsafe partial class EmjStateReader : IDisposable
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

    // One-line summary of the current recommendation, wired by the plugin.
    public Func<string>? AnalysisSummaryProvider { get; set; }

    // Receives the tenpai samples of every finished hand (plugin appends them to CSV).
    public Action<IReadOnlyList<Policy.TenpaiSample>>? CalibrationSink { get; set; }

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

            this.Current = this.builder.Build(decoded, this.tracker, this.Layout, new RulesetOptions(this.configuration.Kuitan));
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

    public void SetRiichiDeclared(bool declared) => this.tracker.SetRiichiDeclared(declared);

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
        var roundText = this.Layout.RoundWind is null ? EmjScanner.ReadTextAtPath(addon, nodes.RoundWindText) : null;
        var leading = roundText?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (EmjScanner.ParseWindText(leading) is { } round)
            this.tracker.HintRoundWind(round);
    }

    // Draw screens label every seat "Tenpai!"/"Noten..." in the seat banners; a win names
    // the winner in type-32. Banners keep their last text (residue), so on a draw we wait
    // until all three opponent banners read Tenpai/Noten — up to ~5 s — before trusting them.
    private void RecordTenpaiGroundTruth(AtkUnitBase* addon, StateSnapshot snapshot)
    {
        if (snapshot.Phase != GamePhase.RoundEnd)
        {
            if (snapshot.Phase is not (GamePhase.NotInGame or GamePhase.Dealing) && snapshot.Hand.Count > 0)
                this.lastPlaySnapshot = snapshot;
            this.pendingCalibration = null;
            return;
        }

        if (this.pendingCalibration is null)
        {
            if (this.lastPlaySnapshot is null || this.CalibrationSink is null)
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
            this.CalibrationSink(samples);
    }

    // Visible texts of the call panel (Pon/Chi/Pass, Riichi/Tsumo/…). They persist after a
    // prompt closes and the list's item-table labels are always empty, so this is only the
    // tracker's fallback edge — the type-19/23 events are the real signal.
    private static List<string> PromptLabels(AtkUnitBase* addon) => EmjScanner.ScanCallButtonTexts(addon);

    private AtkUnitBase* GetAddon()
    {
        var ptr = this.gameGui.GetAddonByName(this.Layout.AddonName);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        return addon->RootNode == null ? null : addon;
    }
}
