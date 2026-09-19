using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MahjongHater.Core;
using MahjongHater.Core.Policy;
using MahjongHater.Core.State;
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
    private readonly IPluginLog pluginLog;
    private readonly IFramework framework;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameGui gameGui,
        IClientState clientState,
        ICommandManager commandManager,
        IChatGui chatGui,
        IPluginLog pluginLog,
        IFramework framework,
        IAddonLifecycle addonLifecycle)
    {
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.pluginLog = pluginLog;
        this.framework = framework;

        this.Configuration = Configuration.Load(pluginInterface);
        var layout = EmjLayout.LoadDefault(pluginInterface.AssemblyLocation.DirectoryName);
        this.Reader = new EmjStateReader(gameGui, pluginLog, addonLifecycle, this.Configuration, layout);
        this.Policy = new DecisionPolicy();
        this.AnalysisService = new AnalysisService(this.Policy, (ex, msg) => pluginLog.Error(ex, msg));
        this.Reader.AnalysisSummaryProvider = this.DescribeCurrentRecommendation;

        this.WindowSystem = new WindowSystem("MahjongHater");
        this.MainWindow = new MainWindow(this, this.Configuration, this.Reader);
        this.ConfigWindow = new ConfigWindow(this.Configuration);

        this.WindowSystem.AddWindow(this.MainWindow);
        this.WindowSystem.AddWindow(this.ConfigWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle the Mahjong Hater overlay. Args: 'dump' = struct + AtkValues snapshot to file, 'debug [port]' = localhost debug API.",
            ShowInHelp = true,
        });

        this.pluginInterface.UiBuilder.Draw += this.DrawUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;

        this.clientState.Login += this.OnLogin;
        this.clientState.Logout += this.OnLogout;
        this.framework.Update += this.OnFrameworkUpdate;

        // Dev-loaded builds always expose the loopback API so a terminal can drive tests
        // right after a hot reload; release users opt in with /mhater debug.
        if (this.Configuration.DebugServerAutoStart || pluginInterface.IsDev)
            this.StartDebugServer(this.Configuration.DebugServerPort);

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

    private DebugServer? debugServer;

    public void Dispose()
    {
        this.debugServer?.Dispose();
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
        var arg = arguments.Trim();

        if (arg.Equals("dump", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(this.pluginInterface.GetPluginConfigDirectory(), $"emj_dump_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            this.DumpToFile(path);
            this.chatGui.Print($"[MahjongHater] Dump → {path}");
            return;
        }

        if (arg.StartsWith("debug", StringComparison.OrdinalIgnoreCase))
        {
            this.ToggleDebugServer(arg);
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

    // /mhater dump: struct decode + AtkValues + node tree, for in-game checks without the API.
    private void DumpToFile(string path)
    {
        var lines = new List<string>(4096) { $"# MahjongHater dump {DateTime.Now:O}" };
        lines.Add("=== /struct ===");
        lines.AddRange(Flatten(this.Reader.BuildDebugStruct(includeHex: true)));
        lines.Add("=== /status ===");
        lines.AddRange(Flatten(this.Reader.BuildDebugStatus()));
        lines.Add("=== /frame ===");
        lines.AddRange(Flatten(this.Reader.BuildDebugFrame(this.Reader.Layout.AddonName)));
        lines.Add("=== /tree ===");
        lines.AddRange(this.Reader.BuildDebugTree(null, this.Reader.Layout.AddonName));
        File.WriteAllLines(path, lines);

        static IEnumerable<string> Flatten(Dictionary<string, object?> dict)
        {
            foreach (var (k, v) in dict)
            {
                if (v is IEnumerable<string> list)
                {
                    yield return $"  {k}:";
                    foreach (var item in list)
                        yield return $"    {item}";
                }
                else
                {
                    yield return $"  {k} = {System.Text.Json.JsonSerializer.Serialize(v)}";
                }
            }
        }
    }

    // ───────────────────────────── DEBUG API (/mhater debug) ─────────────────────────────

    private void ToggleDebugServer(string arg)
    {
        if (this.debugServer is { IsRunning: true })
        {
            this.debugServer.Dispose();
            this.debugServer = null;
            this.Configuration.DebugServerAutoStart = false;
            this.Configuration.Save();
            this.chatGui.Print("[MahjongHater] Debug API stopped.");
            return;
        }

        var port = this.Configuration.DebugServerPort;
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && int.TryParse(parts[1], out var customPort))
            port = customPort;

        if (this.StartDebugServer(port))
        {
            this.Configuration.DebugServerAutoStart = true;
            this.Configuration.DebugServerPort = port;
            this.Configuration.Save();
        }
    }

    private bool StartDebugServer(int port)
    {
        try
        {
            this.debugServer = new DebugServer(port, this.RouteDebugRequest, (ex, msg) => this.pluginLog.Warning(ex, msg));
            this.debugServer.Start();
            this.chatGui.Print($"[MahjongHater] Debug API on http://127.0.0.1:{port}/  " +
                               "(read: /status /state /struct /hand /piles /frame /prompt /reco /events /tree /nodes /addons — " +
                               "operate: /discard /hover /call /click /listclick /fire /callback /riichi /act /enable)");
            return true;
        }
        catch (Exception ex)
        {
            this.pluginLog.Error(ex, "[Debug] Failed to start debug server.");
            this.chatGui.Print($"[MahjongHater] Debug API failed to start: {ex.Message}");
            this.debugServer = null;
            return false;
        }
    }

    // All game-memory reads are marshaled onto the framework thread here — the debug
    // server itself runs on thread-pool threads.
    private Task<object?> RouteDebugRequest(string path, Dictionary<string, string> query)
    {
        switch (path)
        {
            case "/":
                return Task.FromResult<object?>(new Dictionary<string, object?>
                {
                    ["plugin"] = "MahjongHater debug API (two-way)",
                    ["read"] = new[]
                    {
                        "/status — current StateSnapshot summary + analysis one-liner",
                        "/state — status + prompt + reco merged (one round-trip)",
                        "/struct[?hex=1] — decoded AddonEmj struct frame (layout verification)",
                        "/hand — struct hand vs visible slot nodes (click targets)",
                        "/piles — per-seat discards, riichi, scores",
                        "/frame[?addon=Emj] — full AtkValues table",
                        "/prompt — call window state + raw button texts + list rows",
                        "/reco — latest policy decision: action, hand summary, ranked discards, reasoning steps",
                        "/events[?tail=200] — raw event + tracker decision + operate timeline",
                        "/tree[?node=133][&addon=Emj] — node tree (text)",
                        "/nodes[?addon=Emj] — event-bearing nodes (operate targets)",
                        "/addons — all loaded addons",
                    },
                    ["operate"] = new[]
                    {
                        "/discard?slot=3 | ?tile=8s — click a hand slot",
                        "/hover?slot=3 — MouseOver a slot, returns the addon's tile name",
                        "/call?option=Chi[&addon=Emj] — click a button by visible label",
                        "/click?node=0x…|<nodeId>[&param=][&addon=] — full click on a node",
                        "/listclick?node=<list>&index=0[&addon=] — select a list row (addon-bound ListItemClick)",
                        "/fire?node=…&type=9[&param=][&addon=] — one precise AtkEvent",
                        "/callback?values=3,0[&addon=] — FireCallback with int values",
                        "/riichi?declared=true|false — manual riichi-lock override",
                        "/act — execute the latest policy decision once (discard / call / pass / win), only if it is fresh",
                        "/enable?on=true|false — plugin on/off (framework tick)   /overlay?on=true|false — show/hide the window",
                    },
                });
            case "/status":
                return this.OnFramework(() => this.Reader.BuildDebugStatus());
            case "/state":
                return this.OnFramework(() => new Dictionary<string, object?>
                {
                    ["status"] = this.Reader.BuildDebugStatus(),
                    ["prompt"] = this.Reader.BuildDebugPrompt(),
                    ["reco"] = this.BuildRecoDebug(),
                });
            case "/struct":
            {
                var hex = query.TryGetValue("hex", out var h) && h is "1" or "true";
                return this.OnFramework(() => this.Reader.BuildDebugStruct(hex));
            }

            case "/hand":
                return this.OnFramework(() => this.Reader.BuildDebugHand());
            case "/piles":
                return this.OnFramework(() => this.Reader.BuildDebugPiles());
            case "/frame":
                return this.OnFramework(() => this.Reader.BuildDebugFrame(Addon(query)));
            case "/prompt":
                return this.OnFramework(() => this.Reader.BuildDebugPrompt());
            case "/events":
            {
                var tail = query.TryGetValue("tail", out var t) && int.TryParse(t, out var n) ? n : 200;
                return this.OnFramework(() => this.Reader.BuildDebugEvents(tail));
            }

            case "/tree":
            {
                uint? nodeId = query.TryGetValue("node", out var n) && uint.TryParse(n, out var id) ? id : null;
                return this.OnFramework(() => this.Reader.BuildDebugTree(nodeId, Addon(query)));
            }

            case "/nodes":
                return this.OnFramework(() => this.Reader.DebugListNodes(Addon(query)));
            case "/addons":
                return this.OnFramework(this.Reader.DebugListAddons);
            case "/reco":
                return this.OnFramework(this.BuildRecoDebug);

            // ── operate (two-way): actions are framework-thread marshaled like reads ──
            case "/discard":
            {
                int? slot = query.TryGetValue("slot", out var s) && int.TryParse(s, out var si) ? si : null;
                var tile = query.TryGetValue("tile", out var tv) ? tv : null;
                if (slot is null && tile is null)
                    return Error("need slot= or tile=");
                return this.OnFramework(() => this.Reader.DebugDiscard(slot, tile));
            }

            case "/hover":
            {
                if (!query.TryGetValue("slot", out var s) || !int.TryParse(s, out var slot))
                    return Error("need slot=");
                return this.OnFramework(() => this.Reader.DebugHoverSlot(slot));
            }

            case "/call":
            {
                if (!query.TryGetValue("option", out var label) || string.IsNullOrWhiteSpace(label))
                    return Error("need option=<visible label>");
                return this.OnFramework(() => this.Reader.DebugClickLabel(Addon(query), label));
            }

            case "/click":
            {
                if (!query.TryGetValue("node", out var spec) || string.IsNullOrWhiteSpace(spec))
                    return Error("need node=0x…|<nodeId>");
                int? param = query.TryGetValue("param", out var p) && int.TryParse(p, out var pi) ? pi : null;
                return this.OnFramework(() => this.Reader.DebugClickNode(Addon(query), spec, param));
            }

            case "/listclick":
            {
                if (!query.TryGetValue("node", out var spec) || string.IsNullOrWhiteSpace(spec)
                    || !query.TryGetValue("index", out var ix) || !int.TryParse(ix, out var index))
                    return Error("need node=<list ptr|nodeId> and index=<row>");
                return this.OnFramework(() => this.Reader.DebugListClick(Addon(query), spec, index));
            }

            case "/fire":
            {
                if (!query.TryGetValue("node", out var spec) || string.IsNullOrWhiteSpace(spec)
                    || !query.TryGetValue("type", out var ts) || !int.TryParse(ts, out var eventType))
                    return Error("need node=0x…|<nodeId> and type=<AtkEventType int>");
                int? param = query.TryGetValue("param", out var p) && int.TryParse(p, out var pi) ? pi : null;
                return this.OnFramework(() => this.Reader.DebugFireEvent(Addon(query), spec, eventType, param));
            }

            case "/callback":
            {
                if (!query.TryGetValue("values", out var vs) || string.IsNullOrWhiteSpace(vs))
                    return Error("need values=<int,int,…>");
                var parts = vs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var values = new int[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!int.TryParse(parts[i], out values[i]))
                        return Error($"'{parts[i]}' is not an int");
                }

                if (values.Length == 0)
                    return Error("values is empty");
                return this.OnFramework(() => this.Reader.DebugFireCallback(Addon(query), values));
            }

            // Manual riichi-lock override until a riichi signal is mapped in the struct.
            case "/act":
                return this.OnFramework(this.ExecuteLatestChoice);

            case "/enable":
            case "/overlay":
            {
                if (!query.TryGetValue("on", out var ov) || !bool.TryParse(ov, out var on))
                    return Error("need on=true|false");
                return this.OnFramework(() =>
                {
                    if (path == "/enable")
                        this.Configuration.PluginEnabled = on;
                    else
                        this.Configuration.ShowOverlay = on;
                    this.Configuration.Save();
                    return new Dictionary<string, object?>
                    {
                        ["pluginEnabled"] = this.Configuration.PluginEnabled,
                        ["showOverlay"] = this.Configuration.ShowOverlay,
                    };
                });
            }

            case "/riichi":
            {
                if (!query.TryGetValue("declared", out var dv) || !bool.TryParse(dv, out var declared))
                    return Error("need declared=true|false");
                return this.OnFramework(() =>
                {
                    this.Reader.SetRiichiDeclared(declared);
                    return new Dictionary<string, object?> { ["isRiichiDeclared"] = declared };
                });
            }

            default:
                return Task.FromResult<object?>(null);
        }

        static string Addon(Dictionary<string, string> query)
            => query.TryGetValue("addon", out var a) && !string.IsNullOrWhiteSpace(a) ? a : "Emj";

        static Task<object?> Error(string message)
            => Task.FromResult<object?>(new Dictionary<string, object?> { ["error"] = message });
    }

    private Task<object?> OnFramework(Func<object?> read) => this.framework.RunOnFrameworkThread(read);

    private object? BuildRecoDebug()
    {
        var publication = this.AnalysisService.Latest;
        if (publication is null)
            return new Dictionary<string, object?> { ["status"] = "no publication yet" };

        var state = this.Reader.Current;
        var result = new Dictionary<string, object?>
        {
            ["status"] = publication.Status.ToString(),
            ["fresh"] = state is not null && publication.Fingerprint == AnalysisService.ComputeFingerprint(state),
            ["completedUtc"] = publication.CompletedUtc.ToString("O"),
            ["error"] = publication.Error,
            ["computing"] = this.AnalysisService.IsComputing,
        };

        if (publication.Choice is { } choice)
        {
            result["action"] = choice.Kind.ToString();
            result["tile"] = choice.Tile?.ToString();
            result["call"] = choice.Call is { } m ? string.Join(",", m.Tiles.Select(t => t.ToString())) : null;
            result["summary"] = choice.Summary;
            result["shanten"] = choice.Hand?.Shanten;
            result["ukeire"] = choice.Hand?.Ukeire;
            result["waits"] = choice.Hand?.Waits.Select(t => t.ToString()).ToList();
            result["steps"] = choice.Steps.Select(r => $"[{r.Stage}] {r.Display}").ToList();
            result["ranked"] = choice.Candidates.Take(6).Select(c => new Dictionary<string, object?>
            {
                ["tile"] = c.Tile.ToString(),
                ["shanten"] = c.ShantenAfter,
                ["ukeire"] = c.Ukeire,
                ["ukeire2"] = c.Ukeire2,
                ["score"] = Math.Round(c.Score, 1),
                ["value"] = c.Value,
                ["risk"] = Math.Round(c.DealInRisk, 3),
                ["note"] = c.Note,
            }).ToList();
        }

        return result;
    }

    // One-line recommendation summary for /status: what the plugin is advising, whether
    // the advised tile is in the hand, and whether the result matches the live fingerprint.
    private string DescribeCurrentRecommendation()
    {
        var publication = this.AnalysisService.Latest;
        if (publication is null)
            return "no publication yet";

        var state = this.Reader.Current;
        var fresh = state is not null && publication.Fingerprint == AnalysisService.ComputeFingerprint(state);
        if (publication.Status != AnalysisStatus.Ready || publication.Choice is null)
            return $"status={publication.Status}  fresh={fresh}  err={publication.Error}";

        var choice = publication.Choice;
        var tile = choice.Tile?.ToString() ?? "-";
        var tileInHand = choice.Tile is { } t && state is not null && state.Hand.Any(h => TileHelpers.SameKind(h, t));
        return $"status=Ready  action={choice.Kind}  tile={tile}  tileInHand={tileInHand}  " +
               $"shanten={choice.Hand?.Shanten.ToString() ?? "-"}  ukeire={choice.Hand?.Ukeire.ToString() ?? "-"}  fresh={fresh}";
    }

    // Manual, one-shot actuator for the latest decision — the debug-API side of "do what
    // the overlay says". Refuses stale publications so it never acts on a previous hand.
    private object? ExecuteLatestChoice()
    {
        var publication = this.AnalysisService.Latest;
        var state = this.Reader.Current;
        if (publication?.Choice is not { } choice || state is null)
            return new Dictionary<string, object?> { ["error"] = "no decision yet" };
        if (publication.Fingerprint != AnalysisService.ComputeFingerprint(state))
            return new Dictionary<string, object?> { ["error"] = "decision is stale for the current state", ["action"] = choice.Kind.ToString() };

        const string addon = "Emj";
        var chooser = state.CallShapes.Count > 0;
        object? outcome = choice.Kind switch
        {
            ActionKind.Discard => this.Reader.DebugDiscard(null, choice.Tile?.ToString()),
            ActionKind.Riichi => this.Reader.DebugClickLabel(addon, "Riichi"),
            ActionKind.Tsumo => this.Reader.DebugClickLabel(addon, "Tsumo"),
            ActionKind.Ron => this.Reader.DebugClickLabel(addon, "Ron"),
            ActionKind.Pon => this.Reader.DebugClickLabel(addon, "Pon"),
            ActionKind.Chi when chooser => this.ClickChiShape(state, choice),
            ActionKind.Chi => this.Reader.DebugClickLabel(addon, "Chi"),
            ActionKind.MinKan or ActionKind.AnKan or ActionKind.ShouMinKan => this.Reader.DebugClickLabel(addon, "Kan"),
            ActionKind.Pass when chooser => this.Reader.DebugClickPath(this.Reader.Layout.Nodes.ChiShapeCancel ?? string.Empty),
            ActionKind.Pass when state.Phase is GamePhase.CallPrompt or GamePhase.SelfDeclare => this.Reader.DebugClickLabel(addon, "Pass"),
            _ => new Dictionary<string, object?> { ["skipped"] = "nothing to execute", ["action"] = choice.Kind.ToString() },
        };

        // A list answer (call / pass / win / riichi) is final for that window; the game echoes
        // it as another type-19, which the tracker must not treat as a new prompt.
        var answered = outcome is Dictionary<string, object?> o && (o.ContainsKey("clicked") || o.ContainsKey("path")) && !o.ContainsKey("error");
        if (answered && choice.Kind != ActionKind.Discard)
            this.Reader.Tracker.MarkCallAnswered(choice.IsWin);

        return new Dictionary<string, object?>
        {
            ["action"] = choice.Kind.ToString(),
            ["tile"] = choice.Tile?.ToString(),
            ["meld"] = choice.Call is { } meld ? string.Join(" ", meld.Tiles.Select(t => t.ToString())) : null,
            ["summary"] = choice.Summary,
            ["outcome"] = outcome,
            ["answered"] = answered,
        };
    }

    // State 25: pick the option whose three tiles are the policy's meld (button order =
    // AtkValues order); a meld the game did not offer is a policy bug, not a click.
    private object? ClickChiShape(StateSnapshot state, ActionChoice choice)
    {
        if (choice.Call is not { } wanted)
            return new Dictionary<string, object?> { ["error"] = "chi decision carries no meld" };
        var index = -1;
        for (var i = 0; i < state.CallShapes.Count; i++)
        {
            var offered = state.CallShapes[i].Tiles.Select(TileHelpers.ToIndex).Order();
            if (offered.SequenceEqual(wanted.Tiles.Select(TileHelpers.ToIndex).Order()))
            {
                index = i;
                break;
            }
        }

        var buttons = this.Reader.Layout.Nodes.ChiShapeButtons;
        if (index < 0 || index >= buttons.Length)
            return new Dictionary<string, object?> { ["error"] = $"shape {string.Join(" ", wanted.Tiles)} is not among the offered {state.CallShapes.Count} shapes" };
        var result = this.Reader.DebugClickPath(buttons[index]);
        result["shapeIndex"] = index;
        return result;
    }
}
