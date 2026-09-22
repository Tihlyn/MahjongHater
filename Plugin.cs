using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MahjongHater.Core;
using MahjongHater.Core.Operate;
using MahjongHater.Core.Policy;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;
using MahjongHater.Windows;


namespace MahjongHater;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mhater";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog pluginLog;
    private readonly IFramework framework;
    private readonly SimulationDatabase? simulationDatabase;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameGui gameGui,
        IClientState clientState,
        ICommandManager commandManager,
        IPluginLog pluginLog,
        IFramework framework,
        IAddonLifecycle addonLifecycle,
        ITextureProvider textureProvider)
    {
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.pluginLog = pluginLog;
        this.framework = framework;

        this.Configuration = Configuration.Load(pluginInterface);
        var layout = EmjLayout.LoadDefault(pluginInterface.AssemblyLocation.DirectoryName);
        TileArt.Initialize(textureProvider, layout.TileIconBase);
        this.Reader = new EmjStateReader(gameGui, pluginLog, addonLifecycle, this.Configuration, layout);
        var weights = PolicyWeights.Default with
        {
            DefenseModel = this.Configuration.DefenseV2 ? DefenseModel.V2 : DefenseModel.Legacy,
        };
        IPolicy policy = new DecisionPolicy(weights: weights);
        this.Reader.PolicyTag = this.Configuration.DefenseV2 ? "V2" : "Legacy";
        if (this.Configuration.LearnedPolicyEnabled)
        {
            try
            {
                var model = MahjongHater.Core.Learning.LearnedModel.Load(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "learned_policy.json"));
                if (model.Rules.Kuitan != this.Configuration.Kuitan || model.Rules.HandsInMatch != (int)this.Configuration.GameLength
                    || model.Rules.DoubleWindPairFu != this.Configuration.DoubleWindPairFu)
                    throw new InvalidDataException("Learned model rule profile differs from plugin configuration.");
                // "learned-guarded" (docs/research/EVALUATION_RUNS.md): the network's discard
                // ordering and tenpai head inside the measured danger budget; danger itself stays
                // on the Houou tables, riichi/calls/wins on the heuristic rules.
                var opponents = new MahjongHater.Core.Learning.LearnedOpponentModel(model, weights, useLearnedDanger: false);
                policy = new DecisionPolicy(opponents: opponents, discards: new MahjongHater.Core.Learning.LearnedDiscardPolicy(model, weights),
                    calls: new MahjongHater.Core.Learning.LearnedCallPolicy(model, weights), weights: weights);
                this.Reader.PolicyTag = "learned-guarded";
                pluginLog.Information($"Learned policy loaded ({model.Status}) as learned-guarded; exact cache remains first when enabled.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                pluginLog.Warning($"Learned policy unavailable; using existing policy: {ex.Message}");
            }
        }
        if (this.Configuration.PrecomputedPolicyEnabled)
        {
            try
            {
                var directory = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "simulation_policy");
                if (Directory.Exists(directory))
                {
                    var rules = new SimulationRules { Kuitan = this.Configuration.Kuitan,
                        HandsInMatch = (int)this.Configuration.GameLength, DoubleWindPairFu = this.Configuration.DoubleWindPairFu };
                    this.simulationDatabase = new SimulationDatabase(directory, rules.Profile(weights));
                    policy = new SimulationLookupPolicy(policy, this.simulationDatabase, weights);
                    pluginLog.Information($"Simulation policy loaded: {this.simulationDatabase.Manifest.InputRecords} records on disk.");
                }
                else
                {
                    var path = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "precomputed_policy.json");
                    var table = PolicyTable.Load(path, BeliefState.Profile(weights));
                    policy = new PrecomputedPolicy(policy, table, weights);
                    pluginLog.Information($"Precomputed policy loaded: {table.Count} belief states.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                pluginLog.Warning($"Precomputed policy unavailable; using existing policy: {ex.Message}");
            }
        }
        if (this.Configuration.CapturePrecomputedSnapshots)
            policy = new SnapshotRecordingPolicy(policy,
                Path.Combine(pluginInterface.GetPluginConfigDirectory(), "precomputed_snapshots.jsonl"),
                ex => pluginLog.Warning($"Precomputed snapshot recording stopped: {ex.Message}"));
        this.Policy = policy;
        this.AnalysisService = new AnalysisService(this.Policy, (ex, msg) => pluginLog.Error(ex, msg));
        this.Reader.CalibrationSink = this.AppendCalibration;
        this.Reader.DealInSink = this.AppendDealIns;
        this.Reader.HandResultSink = this.AppendHandResult;

        var actuator = new EmjActuator(gameGui, this.Reader);
        this.AutoPlayer = new AutoPlayer(this.Reader, this.AnalysisService, actuator,
            msg => pluginLog.Information(msg), msg => pluginLog.Warning(msg), () => this.StallLogPath)
        {
            Enabled = this.Configuration.AutoPlay,
        };
        this.Queuer = new MatchQueuer(gameGui, clientState, msg => pluginLog.Information(msg))
        {
            Enabled = this.Configuration.Requeue,
            Duty = Enum.IsDefined(typeof(MahjongDuty), this.Configuration.RequeueDuty) ? (MahjongDuty)this.Configuration.RequeueDuty : MahjongDuty.NoviceQuick,
        };

        this.WindowSystem = new WindowSystem("MahjongHater");
        this.MainWindow = new MainWindow(this, this.Configuration, this.Reader);
        this.ConfigWindow = new ConfigWindow(this.Configuration);

        this.WindowSystem.AddWindow(this.MainWindow);
        this.WindowSystem.AddWindow(this.ConfigWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle the Mahjong Hater overlay. '/mhater config' opens the settings.",
            ShowInHelp = true,
        });

        this.pluginInterface.UiBuilder.Draw += this.DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;

        this.clientState.Login += this.OnLogin;
        this.clientState.Logout += this.OnLogout;
        this.framework.Update += this.OnFrameworkUpdate;

        this.MainWindow.IsOpen = this.Configuration.ShowOverlay;
    }

    public IGameGui GameGui => this.gameGui;

    public Configuration Configuration { get; }

    public MainWindow MainWindow { get; }

    public ConfigWindow ConfigWindow { get; }

    public WindowSystem WindowSystem { get; }

    public EmjStateReader Reader { get; }

    public IPolicy Policy { get; }

    public AnalysisService AnalysisService { get; }

    public AutoPlayer AutoPlayer { get; }

    public MatchQueuer Queuer { get; }

    public string StallLogPath => Path.Combine(this.pluginInterface.GetPluginConfigDirectory(), "autoplay_stalls.log");

    public void Dispose()
    {
        this.AnalysisService.Dispose();
        this.simulationDatabase?.Dispose();
        this.Reader.Dispose();
        this.framework.Update -= this.OnFrameworkUpdate;
        this.clientState.Login -= this.OnLogin;
        this.clientState.Logout -= this.OnLogout;
        this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        this.commandManager.RemoveHandler(CommandName);
        this.WindowSystem.RemoveAllWindows();
        this.Configuration.Save();
    }

    public void ToggleMainWindow()
    {
        this.MainWindow.Toggle();
        this.Configuration.ShowOverlay = this.MainWindow.IsOpen;
        this.Configuration.Save();
    }

    public void OpenConfigWindow()
    {
        this.ConfigWindow.IsOpen = true;
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            this.OpenConfigWindow();
            return;
        }

        this.ToggleMainWindow();
    }

    private void DrawUi()
    {
        this.MainWindow.IsOpen = this.Configuration.ShowOverlay;
        this.WindowSystem.Draw();
        this.Configuration.ShowOverlay = this.MainWindow.IsOpen;
    }

    private void OpenMainUi()
    {
        this.MainWindow.IsOpen = true;
        this.Configuration.ShowOverlay = true;
        this.Configuration.Save();
    }

    private void OpenConfigUi()
    {
        this.ConfigWindow.IsOpen = true;
    }

    private void OnLogin()
    {
        this.pluginLog.Information("Mahjong Hater login detected; refreshing Mahjong state.");
        this.Reader.Tick();
    }

    private void OnLogout(int type, int code)
    {
        this.pluginLog.Information($"Mahjong Hater logout detected (type={type}, code={code}).");
        this.Reader.Reset();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.Configuration.PluginEnabled)
        {
            return;
        }

        this.Reader.Tick();
        this.AnalysisService.Update(this.Reader.Current);
        this.AutoPlayer.Tick(this.Reader.Current);
        this.Queuer.Tick(this.AutoPlayer.InMatch);
    }

    private string CalibrationPath => Path.Combine(this.pluginInterface.GetPluginConfigDirectory(), "tenpai_calibration.csv");

    private string DealInPath => Path.Combine(this.pluginInterface.GetPluginConfigDirectory(), "dealin_calibration.csv");

    private string HandResultPath => Path.Combine(this.pluginInterface.GetPluginConfigDirectory(), "hand_results.csv");

    // One CSV row per finished hand (tools/ab_summary.py compares defense models).
    private void AppendHandResult(HandResult result)
    {
        try
        {
            var path = this.HandResultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>(2);
            if (!File.Exists(path))
                lines.Add(HandResult.CsvHeader);
            lines.Add(result.ToCsv());
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[Calibration] Failed to append the hand result.");
        }
    }

    // One CSV row per (own discard, threatening seat); the file grows across sessions.
    private void AppendDealIns(IReadOnlyList<DealInSample> samples)
    {
        try
        {
            var path = this.DealInPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>(samples.Count + 1);
            if (!File.Exists(path))
                lines.Add(DealInCalibration.CsvHeader);
            lines.AddRange(samples.Select(DealInCalibration.ToCsv));
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[Calibration] Failed to append deal-in samples.");
        }
    }

    // One CSV row per opponent per finished hand; the file grows across sessions.
    private void AppendCalibration(IReadOnlyList<TenpaiSample> samples)
    {
        try
        {
            var path = this.CalibrationPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>(samples.Count + 1);
            if (!File.Exists(path))
                lines.Add(TenpaiCalibration.CsvHeader);
            lines.AddRange(samples.Select(TenpaiCalibration.ToCsv));
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[Calibration] Failed to append tenpai samples.");
        }
    }
}
