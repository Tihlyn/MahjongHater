using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MahjongHater.Core;
using MahjongHater.Windows;

[assembly: AssemblyVersion("1.0.0.0")]

namespace MahjongHater;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mhater";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IDataManager dataManager;
    private readonly IPluginLog pluginLog;
    private readonly ITextureProvider textureProvider;
    private readonly IFramework framework;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameGui gameGui,
        IClientState clientState,
        ICommandManager commandManager,
        IChatGui chatGui,
        IDataManager dataManager,
        IPluginLog pluginLog,
        ITextureProvider textureProvider,
        IFramework framework)
    {
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.dataManager = dataManager;
        this.pluginLog = pluginLog;
        this.textureProvider = textureProvider;
        this.framework = framework;

        this.Configuration = Configuration.Load(pluginInterface);
        this.GameStateReader = new GameStateReader(gameGui, pluginLog, this.Configuration);
        this.HandAnalyzer = new HandAnalyzer();

        this.WindowSystem = new WindowSystem("MahjongHater");
        this.MainWindow = new MainWindow(this, this.Configuration, this.GameStateReader, this.HandAnalyzer);
        this.ConfigWindow = new ConfigWindow(this.Configuration);

        this.WindowSystem.AddWindow(this.MainWindow);
        this.WindowSystem.AddWindow(this.ConfigWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle the Mahjong Hater overlay.",
            ShowInHelp = true,
        });

        this.pluginInterface.UiBuilder.Draw += this.DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;

        this.clientState.Login += this.OnLogin;
        this.clientState.Logout += this.OnLogout;
        this.framework.Update += this.OnFrameworkUpdate;

        this.MainWindow.IsOpen = this.Configuration.ShowOverlay;
    }

    public Configuration Configuration { get; }

    public MainWindow MainWindow { get; }

    public ConfigWindow ConfigWindow { get; }

    public WindowSystem WindowSystem { get; }

    public GameStateReader GameStateReader { get; }

    public HandAnalyzer HandAnalyzer { get; }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.clientState.Login -= this.OnLogin;
        this.clientState.Logout -= this.OnLogout;
        this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
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
        this.ToggleMainWindow();
    }

    private void DrawUi()
    {
        this.MainWindow.IsOpen = this.Configuration.ShowOverlay;
        this.WindowSystem.Draw();
        this.Configuration.ShowOverlay = this.MainWindow.IsOpen;
    }

    private void OpenConfigUi()
    {
        this.ConfigWindow.IsOpen = true;
    }

    private void OnLogin()
    {
        this.pluginLog.Information("Mahjong Hater login detected; refreshing Mahjong state.");
        this.GameStateReader.Tick();
    }

    private void OnLogout(int type, int code)
    {
        this.pluginLog.Information($"Mahjong Hater logout detected (type={type}, code={code}).");
        this.GameStateReader.Reset();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.Configuration.PluginEnabled)
        {
            return;
        }

        this.GameStateReader.Tick();
    }
}
