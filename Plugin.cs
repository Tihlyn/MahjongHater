using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
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
    private readonly EmjActuator actuator;
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
                // The config folder first (what a user drops in, and where a per-length model
                // goes), then the plugin folder, so a model shipped alongside the DLL works
                // without being copied anywhere.
                var path = LearnedModelPath(pluginInterface, (int)this.Configuration.GameLength);
                var model = MahjongHater.Core.Learning.LearnedModel.Load(path);
                if (model.Rules.Kuitan != this.Configuration.Kuitan || model.Rules.HandsInMatch != (int)this.Configuration.GameLength
                    || model.Rules.DoubleWindPairFu != this.Configuration.DoubleWindPairFu)
                    throw new InvalidDataException($"Model is for {model.Rules.HandsInMatch}-hand matches"
                        + $"{(model.Rules.Kuitan ? string.Empty : ", kuitan off")}; the plugin is configured for {(int)this.Configuration.GameLength}.");
                // "learned-guarded" (docs/research/EVALUATION_RUNS.md): the network's discard
                // ordering and tenpai head inside the measured danger budget; danger itself stays
                // on the Houou tables, riichi/calls/wins on the heuristic rules.
                var opponents = new MahjongHater.Core.Learning.LearnedOpponentModel(model, weights, useLearnedDanger: false);
                policy = new DecisionPolicy(opponents: opponents, discards: new MahjongHater.Core.Learning.LearnedDiscardPolicy(model, weights),
                    calls: new MahjongHater.Core.Learning.LearnedCallPolicy(model, weights), weights: weights);
                this.Reader.PolicyTag = "learned-guarded";
                this.LearnedPolicy = new LearnedPolicyState(true,
                    $"{Path.GetFileName(path)} ({model.Rules.HandsInMatch}-hand, {model.Schema switch { 1 => "schema 1", _ => "schema 2" }}, {model.Status})",
                    Path.GetDirectoryName(path) ?? string.Empty);
                pluginLog.Information($"Learned policy loaded from {path} ({model.Status}) as learned-guarded; exact cache remains first when enabled.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                this.LearnedPolicy = new LearnedPolicyState(false, ex.Message, string.Empty);
                pluginLog.Warning($"Learned policy unavailable; using existing policy: {ex.Message}");
            }
        }
        else
        {
            this.LearnedPolicy = new LearnedPolicyState(false, "Turned off in the settings.", string.Empty);
        }
        var simulationDirectory = FindOfflineFolder(pluginInterface, "simulation_policy");
        var precomputedFile = FindOfflineFile(pluginInterface, "precomputed_policy.json", "precomputed_policy.json.gz");
        if (this.Configuration.PrecomputedPolicyEnabled && simulationDirectory is null && precomputedFile is null)
        {
            // Nothing generated for this install: an optional component that was never fed is
            // not a failure, so it says so once at Information and shows in Diagnostics.
            this.PrecomputedPolicy = new OptionalComponentState(false, "No table installed (optional; generate one with tools/Precompute — docs/PRECOMPUTED_POLICY.md).");
            pluginLog.Information("Precomputed policy: no table installed; the heuristics and any learned policy decide. "
                                  + "tools/Precompute writes precomputed_policy.json / simulation_policy; drop either into the plugin config folder or ship it in resources/policy.");
        }
        else if (this.Configuration.PrecomputedPolicyEnabled)
        {
            try
            {
                var directory = simulationDirectory;
                if (directory is not null)
                {
                    var rules = new SimulationRules { Kuitan = this.Configuration.Kuitan,
                        HandsInMatch = (int)this.Configuration.GameLength, DoubleWindPairFu = this.Configuration.DoubleWindPairFu };
                    this.simulationDatabase = new SimulationDatabase(directory, rules.Profile(weights));
                    policy = new SimulationLookupPolicy(policy, this.simulationDatabase, weights);
                    this.PrecomputedPolicy = new OptionalComponentState(true, $"simulation_policy ({this.simulationDatabase.Manifest.InputRecords} records)");
                    pluginLog.Information($"Simulation policy loaded from {directory}: {this.simulationDatabase.Manifest.InputRecords} records on disk.");
                }
                else
                {
                    var table = PolicyTable.Load(precomputedFile!, BeliefState.Profile(weights));
                    policy = new PrecomputedPolicy(policy, table, weights);
                    this.PrecomputedPolicy = new OptionalComponentState(true, $"{Path.GetFileName(precomputedFile)} ({table.Count} belief states)");
                    pluginLog.Information($"Precomputed policy loaded from {precomputedFile}: {table.Count} belief states.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                this.PrecomputedPolicy = new OptionalComponentState(false, ex.Message);
                pluginLog.Warning($"Precomputed policy unavailable; using existing policy: {ex.Message}");
            }
        }
        else
        {
            this.PrecomputedPolicy = new OptionalComponentState(false, "Turned off in the settings.");
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

        this.actuator = new EmjActuator(gameGui, this.Reader);
        this.AutoPlayer = new AutoPlayer(this.Reader, this.AnalysisService, this.actuator,
            msg => pluginLog.Information(msg), msg => pluginLog.Warning(msg), () => this.StallLogPath)
        {
            Enabled = this.Configuration.AutoPlay,
            AllowHoverEscalation = this.Configuration.HoverEscalation,
        };
        this.IdleGuard = new IdleGuard(msg => pluginLog.Information(msg)) { Enabled = this.Configuration.AntiIdle };
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
            HelpMessage = "Toggle the Mahjong Hater overlay. '/mhater config' opens the settings, '/mhater focus' logs why the table may not be taking clicks.",
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

    public IdleGuard IdleGuard { get; }

    private int rankTicks;

    // What became of the learned policy at load: the Diagnostics tab shows it, because
    // "is the model actually in use?" is otherwise only answerable from the Dalamud log.
    public LearnedPolicyState LearnedPolicy { get; private set; } = new(false, "Turned off in the settings.", string.Empty);

    // Same idea for the offline lookup tables: optional, and its absence is not an error.
    public OptionalComponentState PrecomputedPolicy { get; private set; } = new(false, "Turned off in the settings.");

    public string StallLogPath => Path.Combine(this.pluginInterface.GetPluginConfigDirectory(), "autoplay_stalls.log");

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.IdleGuard.Reset();
        this.AnalysisService.Dispose();
        this.simulationDatabase?.Dispose();
        this.Reader.Dispose();
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
        var argument = arguments.Trim();
        if (argument.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            this.OpenConfigWindow();
            return;
        }

        if (argument.Equals("focus", StringComparison.OrdinalIgnoreCase) || argument.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            this.ReportInputState();
            return;
        }

        this.ToggleMainWindow();
    }

    // "/mhater focus", to be run WHILE the table refuses clicks. Everything it prints is a
    // read: focus holders, the table's own visibility and collision, any gating unit that is
    // open (especially one that is open but invisible), and whether Dalamud's ImGui layer is
    // taking the mouse before the game sees it.
    private void ReportInputState()
    {
        var lines = UiInputReport.Build(this.gameGui, this.Reader.Layout.AddonName);
        var io = ImGui.GetIO();
        lines.Add("-- Dalamud / ImGui --");
        lines.Add($"WantCaptureMouse={io.WantCaptureMouse} WantCaptureKeyboard={io.WantCaptureKeyboard} "
                  + $"anyItemActive={ImGui.IsAnyItemActive()} anyMouseDown={ImGui.IsAnyMouseDown()}");
        lines.Add($"our overlay open={this.MainWindow.IsOpen} config open={this.ConfigWindow.IsOpen} "
                  + $"autoPlay={this.AutoPlayer.Enabled} requeue={this.Queuer.Enabled} idleKeyDown={this.IdleGuard.IsKeyDown}");
        this.pluginLog.Information("[Input] ---- /mhater focus ----");
        foreach (var line in lines)
            this.pluginLog.Information($"[Input] {line}");
        this.pluginLog.Information("[Input] ---- end ----");
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
        this.IdleGuard.Reset();
        this.pluginLog.Information($"Mahjong Hater logout detected (type={type}, code={code}).");
        this.Reader.Reset();
    }

    // The offline tables are searched exactly like the learned model: the plugin config
    // folder first, so a table you generated yourself wins, then next to the DLL, then the
    // shipped resources/policy folder. resources/policy/*.json and
    // resources/policy/simulation_policy/** are copied into the package by the csproj, so a
    // generated table only has to be dropped there to ship and load with no further setup.
    private static IEnumerable<string> OfflineTableFolders(IDalamudPluginInterface pluginInterface)
    {
        var assembly = pluginInterface.AssemblyLocation.DirectoryName ?? ".";
        yield return pluginInterface.GetPluginConfigDirectory();
        yield return assembly;
        yield return Path.Combine(assembly, "resources", "policy");
    }

    private static string? FindOfflineFile(IDalamudPluginInterface pluginInterface, params string[] names)
        => OfflineTableFolders(pluginInterface)
            .SelectMany(folder => names.Select(name => Path.Combine(folder, name)))
            .FirstOrDefault(File.Exists);

    private static string? FindOfflineFolder(IDalamudPluginInterface pluginInterface, string name)
        => OfflineTableFolders(pluginInterface)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(Directory.Exists);

    // The models ship with the plugin as resources/models/learned_policy-<hands>.json.gz.
    // The config folder is searched first so a downloaded or self-trained model overrides the
    // shipped one, and the per-length name wins over the generic one.
    private static string LearnedModelPath(IDalamudPluginInterface pluginInterface, int hands)
    {
        string[] names =
        [
            $"learned_policy-{hands}.json", $"learned_policy-{hands}.json.gz",
            "learned_policy.json", "learned_policy.json.gz",
        ];
        var shipped = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName ?? ".", "resources", "models");
        string[] folders = [pluginInterface.GetPluginConfigDirectory(), pluginInterface.AssemblyLocation.DirectoryName ?? ".", shipped];
        foreach (var folder in folders)
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                    return candidate;
            }

        return Path.Combine(shipped, names[1]);   // the path an error message should name
    }

    // The Gold Saucer Info window is the only place the game shows rank and rating, so read
    // it whenever it happens to be open and keep the last value for the overlay.
    private void ReadMahjongRank()
    {
        if (++this.rankTicks % 30 != 0)
            return;
        if (MahjongRankReader.TryRead(this.gameGui) is not { } read)
            return;
        if (read.Rank == this.Configuration.MahjongRank && read.Rating == this.Configuration.MahjongRating
            && read.HighestRating == this.Configuration.MahjongHighestRating)
            return;
        this.Configuration.MahjongRank = read.Rank;
        this.Configuration.MahjongRating = read.Rating;
        this.Configuration.MahjongHighestRating = read.HighestRating;
        this.Configuration.Save();
        this.pluginLog.Information($"[State] Doman Mahjong rank read: {read.Rank}, rating {read.Rating} (highest {read.HighestRating}, {read.MatchesPlayed} matches).");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.Configuration.PluginEnabled)
        {
            this.IdleGuard.Tick(false, false, DateTime.UtcNow);
            return;
        }

        this.Reader.Tick();
        this.AnalysisService.Update(this.Reader.Current);
        this.ReadMahjongRank();
        // Read every frame so a comparison run needs no reload.
        this.AutoPlayer.AllowHoverEscalation = this.Configuration.HoverEscalation;
        // The operator acts first and the anti-idle press goes last, so a synthetic keystroke
        // is never delivered in the moments before a dispatch: both run on this thread, in this
        // order, every frame. The press is still skipped while automation is mid-action, and
        // automation still stands aside for a genuinely held key - but not for a release that
        // cannot be delivered, which used to freeze a match indefinitely.
        if (!this.IdleGuard.BlocksAutomation)
        {
            this.AutoPlayer.Tick(this.Reader.Current);
            this.Queuer.Tick(this.AutoPlayer.InMatch);
        }

        this.IdleGuard.Tick(this.AutoPlayer.Enabled, this.actuator.IsAddonOpen, DateTime.UtcNow);
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
