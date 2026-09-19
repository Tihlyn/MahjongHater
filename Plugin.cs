using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
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
        this.Policy = new DecisionPolicy();
        this.AnalysisService = new AnalysisService(this.Policy, (ex, msg) => pluginLog.Error(ex, msg));
        this.Reader.CalibrationSink = this.AppendCalibration;

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

    public void Dispose()
    {
        this.AnalysisService.Dispose();
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
    }

    private string CalibrationPath => Path.Combine(this.pluginInterface.GetPluginConfigDirectory(), "tenpai_calibration.csv");

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
