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
    private const int RoundWindScanInterval = 60;

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

            var labels = EmjScanner.ScanCallButtonTexts(addon);
            this.tracker.OnTick(decoded, labels);

            if (++this.ticks % RoundWindScanInterval == 0 && this.Layout.RoundWind is null
                && EmjScanner.ScanRoundWindText(addon) is { } wind)
                this.tracker.HintRoundWind(wind);

            this.Current = this.builder.Build(decoded, this.tracker, this.Layout, new RulesetOptions(this.configuration.Kuitan));
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

    private AtkUnitBase* GetAddon()
    {
        var ptr = this.gameGui.GetAddonByName(this.Layout.AddonName);
        if (ptr.IsNull)
            return null;
        var addon = (AtkUnitBase*)ptr.Address;
        return addon->RootNode == null ? null : addon;
    }
}
