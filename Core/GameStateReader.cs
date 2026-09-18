using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
namespace MahjongHater.Core;

public unsafe sealed class GameStateReader : IDisposable
{
    private static readonly Regex DigitsRegex = new(@"-?\d[\d,]*", RegexOptions.Compiled);

    private readonly IGameGui gameGui;
    private readonly IPluginLog pluginLog;
    private readonly Configuration configuration;
    private readonly IAddonLifecycle addonLifecycle;

    // Session tracking
    private string? lastOutcomeFingerprint;
    private int winsThisSession;
    private int lossesThisSession;
    // Running score: starts at 25000 (standard Doman Mahjong starting points per player).
    // Accumulated via type-29 score-delta events ([1] × 100).
    private int currentScore = 25000;

    // Direct hand tile reading.
    //
    // Layout 2 (count=50, [14]>0 — has open melds; 2=Chi, 3=Pon, 7=Tsumo, etc.):
    //   [17]           Int  -1 sentinel
    //   [18]           Int  closed tile count (1-13)
    //   [19..18+count] Int  closed tile icon IDs in hand order
    //   Readable every Tick() and on type-21 NEW_DEAL.
    //
    // Layout 1 (count=50, [14]=0 — no open melds):
    //   [16-21] Int  dora indicator icon IDs (NO tile data here)
    //   [22]    Int  -1 sentinel
    //   Tiles are NOT in AtkValues — populated only via hover events (atkType=6).
    //
    // count=109 layout (post-round / win screen):
    //   [24-36] Int  13 sorted closed tile icon IDs
    //   [37]    Int  14th tile (drawn) during draw phase
    private List<Tile> directHandTiles = [];

    // Open melds declared this round (Pon/Chi/Kan), tracked from atkType=74 events.
    // Cleared when a new round starts (Layout 1 type-21 NEW_DEAL).
    private readonly List<Meld> trackedCalledMelds = [];
    // Tile signature of the last meld actually added — atkType=74 fires repeatedly
    // while its payload is present (2026-07-06 live: this duplicated the SAME meld
    // every subsequent firing, unboundedly growing trackedCalledMelds past the real
    // max of 4 and eventually driving HandTracking.MaxClosedTiles negative — see
    // HandleMeldAccepted).
    private string? lastAddedMeldSignature;

    // Dora indicators cached from type-19 (call window) events where [16-21] = all 6 doras.
    // In count=50 non-call events [19-21] are hand tiles — cannot read doras from there.
    private List<Tile> trackedDoras = [];
    private readonly Dictionary<int, Tile> hoverHandSlots = [];
    private string? staleFaceWarnSig;
    private Tile? lastLocalDiscardTile;
    private DateTime lastLocalDiscardUtc = DateTime.MinValue;
    private DateTime lastDiscardEventUtc = DateTime.MinValue;
    private Tile? lastDiscardEventKind;
    // True when lastDiscardEventKind came from a type-8 event ([1]=seat, [2]=REAL tile
    // icon for ALL seats — discovered live 2026-07-05). Type-5's pile-diff/placeholder
    // guesses must not overwrite a fresh authoritative kind.
    private bool lastDiscardKindAuthoritative;
    private DateTime lastLocalDrawUtc = DateTime.MinValue;
    // Truth for the drawn (far-right) slot, whose pooled face can be stale in EITHER
    // direction (2026-07-05: once the node face was stale and type-6 was right; once
    // the node face was correct — hover-confirmed — and type-6 itself was wrong/stale).
    // Priority: type-5's [3] on a seat==0 own-draw turn-advance (the client always
    // knows its own tile, unlike a placeholder for opponents — matched a live hover
    // confirmation exactly) outranks type-6, which is a weaker fallback for draws
    // where the type-5 reading wasn't captured. Cleared when the drawn tile leaves
    // the hand (discard/meld/new deal).
    private Tile? lastDrawnTile;
    private bool lastDrawnTileAuthoritative;
    private DateTime slotJumpEdgeConsumedUtc = DateTime.MinValue;
    private readonly IAddonLifecycle.AddonEventDelegate? onHoverDelegate;

    // Call window state: SET only by HandleCallWindow (type-19), CLEARED by post-call events.
    // NodeID=104 (call window panel) is NOT used — the node is visible during normal gameplay,
    // so node-visibility-based detection produces false positives. Pure event-based is correct.
    private bool isCallWindowActive;
    private List<string> callWindowOptions = []; // non-Pass entries from AtkValues[6/7/8]
    private Tile? callOpportunityTile;            // last opponent discard at time window opened

    // Last opponent discard: source for callOpportunityTile when window opens.
    private Tile? lastOpponentDiscard;

    // Set once per round when Tick() detects an oversized tracked hand.
    private bool oversizeHandWarned;

    // True after a score/win screen (or at load): the next type-21 Layout-1 deal is a real
    // round start. Type-21 Layout-1 ALSO fires mid-round around local turns (recorded
    // 2026-07-04) — those must NOT wipe tracking state (hand/melds/discards/wall).
    private bool roundEnded = true;

    // True once the local player has declared riichi this hand — they are then locked
    // into discarding whatever they draw (tsumogiri) every turn. No reliable automatic
    // detection signal was found live 2026-07-06 (node color/alpha/rotation were
    // identical between a locked-out tile and the one selectable drawn tile; no
    // distinct "REACH" banner/token was visible either) — set manually via the debug
    // API (`/riichi?declared=true`) until a real signal is discovered. Reset on a
    // genuine new deal.
    private bool isRiichiDeclared;

    // ── Node-scan engine state ──
    // Learned face-key → tile map; persisted across sessions. Once every hand slot's
    // face resolves, tiles are read straight from the node tree and hover is obsolete.
    private readonly TileFaceMap tileFaceMap = new();
    private readonly string faceMapPath;
    private bool nodeReadActive;
    // Pile face snapshot from the previous Tick, diffed on type-5 to learn discards.
    private Dictionary<nint, string> pileFaceBaseline = [];
    // Tiles decoded from the pile scan this Tick (icon modes); empty when unreadable.
    private List<Tile> scannedPileTiles = [];
    // Edge-triggered prompt detection state (see Tick).
    private string lastPromptSignature = string.Empty;
    private DateTime lastPromptActiveUtc = DateTime.MinValue;
    // Rolling pre-freeze hand snapshots: the ghost claimable-tile slot can render a few
    // frames BEFORE the prompt is detectable, so the freeze alone can lock a ghost in.
    private readonly Queue<List<Tile>> recentHands = new();
    // Last ordered-read pairing taught to the face map (frame-rate dedupe).
    private string? lastOrderedLearnSignature;
    // Deep-scan analysis session (/mhater analyze): recording + node-scan sections.
    private bool isDeepScan;
    private DateTime lastDeepSample;

    // Wall count and discard history from type-5 discard events.
    private int trackedWallRemaining = 70;
    private readonly List<Tile> eventDiscardPile = [];
    private (int Wall, int Seat, int TileId) lastDiscardKey;

    // Round wind tracked from type-32 win-screen string "[East/South] N …".
    private Wind trackedRoundWind = Wind.East;

    private readonly IAddonLifecycle.AddonEventDelegate onGameRefreshDelegate;

    // Recording state
    private bool isRecording;
    private string recordingOutputPath = string.Empty;
    private DateTime recordingStart;
    private readonly List<string> recordLog = [];
    private IAddonLifecycle.AddonEventDelegate? onSetupDelegate;
    private IAddonLifecycle.AddonEventDelegate? onRefreshDelegate;
    private IAddonLifecycle.AddonEventDelegate? onReceiveEventDelegate;
    private IAddonLifecycle.AddonEventDelegate? onFinalizeDelegate;

    // Debug API event timeline: raw addon events, tracker decisions and operate actions
    // interleaved in arrival order (all appended on the framework thread). Served by
    // /events so a debug session can reconstruct what happened without a human report.
    private const int DebugEventRingCap = 600;
    private readonly Queue<string> debugEventRing = new(DebugEventRingCap);

    public GameStateReader(IGameGui gameGui, IPluginLog pluginLog, Configuration configuration, IAddonLifecycle addonLifecycle, string configDirectory)
    {
        this.gameGui = gameGui;
        this.pluginLog = pluginLog;
        this.configuration = configuration;
        this.addonLifecycle = addonLifecycle;

        this.faceMapPath = System.IO.Path.Combine(configDirectory, "tile_face_map.json");
        this.tileFaceMap.Load(this.faceMapPath);
        if (this.tileFaceMap.ResolvedCount > 0)
            this.pluginLog.Info($"[FaceMap] Loaded {this.tileFaceMap.ResolvedCount}/{this.tileFaceMap.KeyCount} resolved tile faces.");

        this.onGameRefreshDelegate = this.OnGameRefresh;
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Emj", this.onGameRefreshDelegate);

        this.onHoverDelegate = this.OnHoverEvent;
        this.addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "Emj", this.onHoverDelegate);
    }

    public GameState? CurrentState { get; private set; }

    public bool IsRecording => this.isRecording;

    public bool IsAnalyzing => this.isRecording && this.isDeepScan;

    // Learned tile-face identities; the render thread reads Resolved for the highlight.
    public TileFaceMap TileFaceMap => this.tileFaceMap;

    // One-line summary of the current recommendation, wired by the plugin (the reader
    // must not depend on AnalysisService). Logged per deep-scan frame so recommendation
    // failures can be correlated against the tracked hand.
    public Func<string>? AnalysisSummaryProvider { get; set; }

    public void Dispose()
    {
        if (this.isRecording)
            this.StopRecording();
        this.addonLifecycle.UnregisterListener(this.onGameRefreshDelegate);
        if (this.onHoverDelegate != null)
            this.addonLifecycle.UnregisterListener(this.onHoverDelegate);
        this.tileFaceMap.Save(this.faceMapPath);
    }

    // ──────────────────────────────────────────── RECORDING ─────────────────────────────────────────────

    public void StartRecording(string outputPath)
    {
        if (this.isRecording)
        {
            this.pluginLog.Info("[Recorder] Already recording — stop first.");
            return;
        }

        this.recordingOutputPath = outputPath;
        this.recordLog.Clear();
        this.recordingStart = DateTime.UtcNow;
        this.isRecording = true;

        this.onSetupDelegate        = this.OnAddonSetup;
        this.onRefreshDelegate      = this.OnAddonRefresh;
        this.onReceiveEventDelegate = this.OnAddonReceiveEvent;
        this.onFinalizeDelegate     = this.OnAddonFinalize;

        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup,        "Emj", this.onSetupDelegate);
        this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh,      "Emj", this.onRefreshDelegate);
        this.addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "Emj", this.onReceiveEventDelegate);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize,      "Emj", this.onFinalizeDelegate);

        this.pluginLog.Info($"[Recorder] Started → {outputPath}");
    }

    public void StopRecording()
    {
        if (!this.isRecording) return;
        this.isRecording = false;
        this.isDeepScan = false;

        this.addonLifecycle.UnregisterListener(
            this.onSetupDelegate!,
            this.onRefreshDelegate!,
            this.onReceiveEventDelegate!,
            this.onFinalizeDelegate!);

        var elapsed = (DateTime.UtcNow - this.recordingStart).TotalSeconds;
        this.recordLog.Insert(0, $"# MahjongHater Emj Recording  start={this.recordingStart:O}  duration={elapsed:F1}s  frames={this.recordLog.Count}");
        System.IO.File.WriteAllLines(this.recordingOutputPath, this.recordLog);
        this.pluginLog.Info($"[Recorder] Saved {this.recordLog.Count} entries → {this.recordingOutputPath}");
    }

    // ──────────────────────────────────────── GAME STATE EVENTS ─────────────────────────────────────────

    // PostRefresh listener dispatches on AtkValues[0] event type.
    //
    // Confirmed AtkValues layout (count=109 as of 2026-06-05):
    //   [0]     Int   event type
    //   [2]     Int   seat-wind icon (76068-76071) for non-5/6 events
    //   [16-21] Int   dora indicator icon IDs (76041-76074; 0 = unrevealed)
    //   [22]    Int   -1 sentinel during active game; 4 on win/score screen
    //   [24-36] Int   local player's 13 sorted closed-tile icon IDs
    //   [37]    Int   14th tile icon ID during draw phase (else 0/Undefined)
    //
    //   Type  5 (Discard):   [1]=wall, [2]=seat(0=local), [3]=discarded tile icon
    //   Type  6 (Draw):      [2]=drawn tile icon (or seat-wind icon if notification only)
    //   Type 15 (Turn):      [2]=seat-wind, [6]="Discard"
    //   Type 19 (Call wnd):  [6]=Chi opt, [7]=Pon opt, [8]=Ron opt ("Pass"=unavailable)
    //   Type 21 (New deal):  [1]=tile count(13), [2]=seat(0=local)
    //   Type 29 (Score):     [1]=seat-0 delta×100, [2-4]=seats 1-3 delta×100
    //   Type 30 (Hover):     [1]=tile display name, [2]=seat-wind
    //   Type 32 (Win screen):[2]=round+seat string e.g."East 3 South Wind", [5]=win method,
    //                         [6]="X Fu Y Han", [7]=score÷100
    private void OnGameRefresh(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 22) return;

            var eventType = addon->AtkValues[0].Int;
            this.NoteRefreshEvent(addon, eventType);

            // Call window lifecycle (see field comment above):
            //   SET:   type-19 → HandleCallWindow
            //   CLEAR: type-5 (a seat draws — play continued past the window)
            //          type-8 (a discard resolved the window)
            //          type-21 (new deal — after meld)
            //          type-29/32 (round end / win screen)
            //          atkType=74 (meld accepted, OnHoverEvent)
            //          label disappearance (Tick)
            //          ResetRoundState() (new round)
            //   NOT type-15: it fires ~20 ms after a window OPENS (part of the open
            //   flow — live 2026-07-05 it killed a freshly-activated real chi window;
            //   the old "own discard turn" reading was wrong — it fires after every
            //   seat's cycle).
            switch (eventType)
            {
                case 5:
                    this.HandleDiscard(addon);
                    isCallWindowActive = false;
                    callWindowOptions  = [];
                    break;
                case 6:  this.HandleDraw(addon); break;
                case 8:
                    this.HandleDiscardTruth(addon);
                    isCallWindowActive = false;
                    callWindowOptions  = [];
                    break;
                case 19: this.HandleCallWindow(addon); break; // sets isCallWindowActive = true
                case 21:
                    if (this.HandleNewDeal(addon))
                    {
                        isCallWindowActive = false;
                        callWindowOptions  = [];
                    }

                    break;
                case 29:
                    this.HandleScoreDelta(addon);
                    isCallWindowActive = false;
                    callWindowOptions  = [];
                    break;
                case 32:
                    this.HandleWinScreen(addon);
                    isCallWindowActive = false;
                    callWindowOptions  = [];
                    break;
            }
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[GameStateReader] OnGameRefresh error.");
        }
    }

    // Type 5: TURN ADVANCE, not a discard (reinterpreted live 2026-07-05 — see the
    // comment below). [1]=wall remaining, [2]=seat that draws next, [3]=constant
    // placeholder icon (76041), NOT tile identity. True discards are type-8
    // (HandleDiscardTruth). This handler keeps wall tracking, pile-face-diff learning,
    // and prompt-window offered-tile metadata for the seat-0 "announcement" case.
    private void HandleDiscard(AtkUnitBase* addon)
    {
        var wall   = addon->AtkValues[1].Int;
        var seat   = addon->AtkValues[2].Int;
        var tileId = addon->AtkValues[3].Int;

        if (wall is > 0 and <= 70)
            this.trackedWallRemaining = wall;

        var key = (wall, seat, tileId);
        if (key == this.lastDiscardKey) return;
        this.lastDiscardKey = key;

        (int Changes, string? NewKey) pileDiff;
        try
        {
            pileDiff = this.DiffPilesAndUpdateBaseline(addon);
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[FaceMap] Pile diff failed.");
            pileDiff = (0, null);
        }

        // The pile-slot face that just changed is ground truth for the discarded tile —
        // [3] can be a placeholder even for seat 0 (recorded: a "local discarded 1m"
        // type-5 fired 90 ms after a call-window click with the 76041 placeholder).
        Tile? pileTile = null;
        if (pileDiff.Changes == 1 && pileDiff.NewKey is not null)
        {
            if (FaceKeys.TryDecodeIcon(pileDiff.NewKey, out var decoded))
                pileTile = decoded;
            else if (this.tileFaceMap.Resolved.TryGetValue(pileDiff.NewKey, out var learned))
                pileTile = learned;
        }

        // Reinterpreted live 2026-07-05: type-5 is the TURN ADVANCE (seat [2] draws;
        // wall [1] decrements with it), NOT a discard — the [3] icon is a constant
        // placeholder (76041). The old "seat-0 type-5 = local discard" reading booked a
        // phantom placeholder discard on every local draw (the historical "not found in
        // tracked hand" warnings and the pre-prompt announcement mystery). Discards are
        // booked by type-8 (HandleDiscardTruth); this event keeps wall tracking, the
        // pile-face diff (the previous discard's face lands around this event — paired
        // with the fresh type-8 kind it teaches unresolved faces for ALL seats), and
        // the prompt-window offered-tile metadata.
        var authoritativeFresh = this.lastDiscardKindAuthoritative
            && (DateTime.UtcNow - this.lastDiscardEventUtc).TotalMilliseconds < 1500;

        if (pileDiff.Changes == 1 && pileDiff.NewKey is not null && pileTile is null
            && authoritativeFresh && this.lastDiscardEventKind is { } authKind)
        {
            this.tileFaceMap.Observe(pileDiff.NewKey, authKind);
            this.pluginLog.Debug($"[FaceMap] Learned {pileDiff.NewKey} → {TileHelpers.Normalize(authKind)} via type-8 (resolved {this.tileFaceMap.ResolvedCount}/{this.tileFaceMap.KeyCount})");
        }

        // Legacy fallback for modes where type-8 has not been observed: arm the claim
        // detector from the resolved pile face only (never the placeholder icon).
        if (!authoritativeFresh)
        {
            this.lastDiscardEventUtc = DateTime.UtcNow;
            this.lastDiscardEventKind = pileTile;
            this.lastDiscardKindAuthoritative = false;
            if (pileTile is { } legacy && seat != 0)
                this.lastOpponentDiscard = legacy;
        }

        // While a call prompt is up (or within a grace window of one resolving), a
        // seat-0 type-5 names the OFFERED tile / resume of play (recorded: "local
        // discarded 1s" during a 1s chi window, 90 ms after the click) — metadata only.
        if (seat == 0 && (this.isCallWindowActive || (DateTime.UtcNow - this.lastPromptActiveUtc).TotalMilliseconds < 600))
        {
            if (TryTileFromIconId(tileId, out var offered))
            {
                this.lastOpponentDiscard = offered;
                this.callOpportunityTile = offered;
            }

            this.LogNote($"[HandTrack] Prompt-window type-5 treated as offered tile: icon={tileId}");
            return;
        }

        // Own draw: [3] carries the REAL drawn tile here (the client always knows its
        // own tile; only opponent turn-advances get the placeholder) — outranks type-6.
        if (seat == 0 && TryTileFromIconId(tileId, out var ownDraw))
        {
            this.lastDrawnTile = ownDraw;
            this.lastDrawnTileAuthoritative = true;
        }

        this.LogNote($"[HandTrack] Turn advance (type-5): seat={seat} draws  wall={wall}{(pileTile is { } p ? $"  pileFace={p}" : string.Empty)}");
    }

    // Type 8: THE authoritative discard event — [1]=seat, [2]=real tile icon for every
    // seat (live 2026-07-05: seat 1's type-5 placeholder "1m" was really 9m per type-8;
    // an API-fired local discard produced type-8 but NO seat-0 type-5 at all). Type-5
    // appears to be the following seat's DRAW ([1]=wall decrements with it); the type-5
    // pile-diff machinery stays as-is, but kind metadata and local booking live here.
    private void HandleDiscardTruth(AtkUnitBase* addon)
    {
        var seat = addon->AtkValues[1].Int;
        var icon = addon->AtkValues[2].Int;
        if (seat is < 0 or > 3 || !TryTileFromIconId(icon, out var tile))
            return;

        this.lastDiscardEventUtc = DateTime.UtcNow;
        this.lastDiscardEventKind = tile;
        this.lastDiscardKindAuthoritative = true;

        if (seat != 0)
        {
            this.lastOpponentDiscard = tile;
            this.eventDiscardPile.Add(tile);
            this.LogNote($"[HandTrack] Discard truth (type-8): seat={seat} {tile}");
            return;
        }

        // Local discard. Book it here unless the type-5 path already did (same kind
        // within 2 s) — API-driven discards produce no seat-0 type-5 to book from.
        if (this.lastLocalDiscardTile is { } already
            && TileHelpers.SameKind(already, tile)
            && (DateTime.UtcNow - this.lastLocalDiscardUtc).TotalMilliseconds < 2000)
            return;

        this.eventDiscardPile.Add(tile);
        this.lastLocalDiscardTile = tile;
        this.lastLocalDiscardUtc = DateTime.UtcNow;
        this.lastDrawnTile = null; // drawn tile left the hand (or was itself discarded)
        this.lastDrawnTileAuthoritative = false;
        this.hoverHandSlots.Clear();
        if (!HandTracking.RemoveOneTile(this.directHandTiles, tile) && this.directHandTiles.Count > 0)
            this.pluginLog.Warning($"[HandTrack] Type-8 local discard {tile} not found in tracked hand ({this.directHandTiles.Count} tiles).");
        this.LogNote($"[HandTrack] Local discard (type-8): {tile}  handCount={this.directHandTiles.Count}");
        this.isCallWindowActive = false;
        this.callWindowOptions = [];
    }

    // A prompt just appeared: freeze onset. The ghost claimable-tile slot may already
    // have leaked into the tracked hand (it renders slightly before the prompt is
    // detectable — recorded: a frozen hand containing the offered tile for a whole
    // round), so roll back to the oldest buffered pre-prompt hand.
    private void OnPromptDetected()
    {
        if (!this.isCallWindowActive && this.recentHands.Count > 0)
        {
            var rollback = this.recentHands.Peek();
            if (rollback.Count >= 1 && rollback.Count != this.directHandTiles.Count)
            {
                this.LogNote($"[HandTrack] Prompt edge: rolled hand back {this.directHandTiles.Count}→{rollback.Count} tiles (ghost-slot guard).");
                this.directHandTiles = [.. rollback];
            }
        }

        this.isCallWindowActive = true;
    }

    // A chi/pon claim window just appeared (edge). Recovers the offered tile and
    // repairs the two artifacts the pre-prompt announcement leaves behind:
    //   1. The claimable discard renders as an extra far-right hand slot (the ghost) —
    //      node-tree truth for the offered tile, read directly.
    //   2. The game announces the claimable discard as a seat-0 type-5 that can fire
    //      BEFORE the prompt is detectable (2026-07-05: "local discard 7m" booked while
    //      the chi window offered that 7m, offered tile unknown) — un-book it.
    //   3. The rollback snapshot may still contain the ghost (it renders well before
    //      the prompt labels) — an oversize frozen hand sheds its far-right tile.
    private void OnClaimPromptEdge(AtkUnitBase* addon, List<string> options)
    {
        var isClaim = options.Any(o => o.Equals("Chi", StringComparison.OrdinalIgnoreCase)
                                    || o.Equals("Pon", StringComparison.OrdinalIgnoreCase));
        if (!isClaim)
            return;

        // Claiming happens on an opponent's turn: the hand holds one tile fewer than
        // after a draw, so exactly one extra slot marks the ghost.
        var expected = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count) - 1;
        Tile? offered = null;
        try
        {
            var slots = EmjScanner.ScanHandSlots(addon);
            if (slots.Count == expected + 1 && this.TryDecodeSlot(slots[^1], out var ghostTile))
                offered = ghostTile;
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[HandTrack] Claim-edge ghost-slot read failed.");
        }

        // The ghost slot's face can be STALE (live 2026-07-05: it decoded the drawn 4p
        // while displaying the claimable 1p). A type-8 discard kind is the true
        // claimable tile — it outranks a disagreeing ghost decode. No elapsed-time gate
        // here: the flag itself already tracks validity correctly (every type-8 event
        // unconditionally refreshes it; HandleDiscard's type-5 path is what downgrades
        // it once a genuinely new turn-advance passes without its own type-8). An
        // ADDITIONAL wall-clock re-check here was actively wrong (live 2026-07-06): our
        // own meld-tracking bug delayed detecting a real window by several minutes, and
        // by the time detection finally ran the still-100%-correct authoritative kind
        // (5s) got discarded as "stale" purely by elapsed time, falling back to a wrong
        // ghost-face read (5z) for a window that had been open and unresolved the whole
        // time — nothing had actually superseded the original truth.
        if (this.lastDiscardKindAuthoritative && this.lastDiscardEventKind is { } authKind)
        {
            if (offered is { } ghostRead && !TileHelpers.SameKind(ghostRead, authKind))
                this.LogNote($"[HandTrack] Claim edge: ghost decoded {ghostRead} but type-8 says {authKind} — trusting type-8 (stale ghost face).");
            offered = authKind;
        }

        // No legal call for the offered tile → the window belongs to another seat
        // (call announcement / mirrored decision) or is a stale panel — the game only
        // opens local windows for legal calls. Deactivate instead of advising.
        if (offered is { } claimable && !this.HasAnyLegalLocalCall(claimable))
        {
            this.LogNote($"[HandTrack] Claim window for {claimable}: no legal local call — not ours; ignoring.");
            this.isCallWindowActive = false;
            this.callWindowOptions = [];
            this.callOpportunityTile = null;
            return;
        }

        var msSinceLocalDiscard = (DateTime.UtcNow - this.lastLocalDiscardUtc).TotalMilliseconds;
        if (this.eventDiscardPile.Count > 0 && this.lastLocalDiscardTile is { } phantom
            && TileHelpers.SameKind(this.eventDiscardPile[^1], phantom)
            && HandTracking.IsPrePromptAnnouncement(phantom, msSinceLocalDiscard, offered)
            && this.HasAnyLegalLocalCall(phantom))
        {
            this.eventDiscardPile.RemoveAt(this.eventDiscardPile.Count - 1);
            this.lastLocalDiscardTile = null;
            offered ??= phantom;
            this.LogNote($"[HandTrack] Claim edge: reattributed pre-prompt \"local discard\" {phantom} as the offered tile.");
        }

        if (offered is { } tile)
        {
            this.callOpportunityTile = tile;
            this.lastOpponentDiscard = tile;
            this.LogNote($"[HandTrack] Claim edge: offered tile = {tile}.");
        }

        if (this.directHandTiles.Count == expected + 1)
        {
            var dropped = this.directHandTiles[^1];
            this.directHandTiles.RemoveAt(this.directHandTiles.Count - 1);
            this.LogNote($"[HandTrack] Claim edge: dropped ghost {dropped} from frozen hand ({this.directHandTiles.Count} tiles).");
        }
    }

    // Legality gate for every claim-window activation: evaluates HasAnyLegalCall
    // against the tracked hand, excluding a trailing ghost/drawn 14th slot so the
    // claimed tile is judged against the 13 tiles the player actually holds.
    //
    // Fails OPEN (returns true, i.e. does not suppress) when the tracked hand's size
    // doesn't match either expected size for this meld count — tracking is not in sync
    // (e.g. just after a plugin reload, before the first deal/hover populates it) and
    // an under-sized hand would spuriously fail every legality check. The gate exists
    // to suppress OTHER seats' actions and stale panels, never to hide a real window
    // from advice — a false "not ours" is worse than a false "maybe ours" here, since
    // the plugin only recommends, it never acts on the player's behalf.
    private bool HasAnyLegalLocalCall(Tile claimed)
    {
        var hand = this.directHandTiles;
        var claimTimeSize = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count) - 1;
        if (hand.Count != claimTimeSize && hand.Count != claimTimeSize + 1)
            return true;

        IReadOnlyList<Tile> closed = hand.Count == claimTimeSize + 1 ? hand.GetRange(0, hand.Count - 1) : hand;
        return HandTracking.HasAnyLegalCall(closed, claimed, this.trackedCalledMelds.Count);
    }

    // Decodes one scanned slot's face: icons directly, learned map as fallback.
    private bool TryDecodeSlot(ScannedTileSlot slot, out Tile tile)
    {
        if (FaceKeys.TryDecodeIcon(slot.FaceKey, out tile))
            return true;

        if (slot.FaceKey is not null && this.tileFaceMap.Resolved.TryGetValue(slot.FaceKey, out var learned))
        {
            tile = learned;
            return true;
        }

        return false;
    }

    // Type 6: Local draw notification. [2] = drawn tile icon ID.
    // directHandTiles is refreshed by Tick() from AtkValues[18-19..] on the next frame.
    private void HandleDraw(AtkUnitBase* addon)
    {
        // Any type-6 marks local-turn activity — stamped BEFORE the seat-wind filter so
        // the ghost-slot claim detector never mistakes an own-turn 14th tile for a
        // claim-window ghost (a suppressed detection just falls back to old behavior;
        // a false one would freeze the hand and strip a real drawn tile).
        this.lastLocalDrawUtc = DateTime.UtcNow;

        var val = addon->AtkValues[2].Int;
        if (val is >= 76068 and <= 76071) return; // seat-wind-only notification

        // Weaker than type-5's seat==0 [3] reading (2026-07-05: type-6 itself was the
        // stale one in a case where the face AND a hover confirmation both agreed) —
        // only fills in when this draw cycle has no authoritative reading yet.
        if (this.lastDrawnTileAuthoritative)
        {
            this.LogNote($"[HandTrack] Draw (type-6): icon={val} (ignored — authoritative reading already set).");
            return;
        }

        this.lastDrawnTile = TryTileFromIconId(val, out var drawn) ? drawn : null;
        this.LogNote($"[HandTrack] Draw (type-6): icon={val}{(this.lastDrawnTile is { } d ? $" → {d}" : string.Empty)}");
    }

    // Type 19: Call opportunity window (Pon / Chi / Kan / Ron).
    // [6]=Chi slot, [7]=Pon slot, [8]=Ron slot. "Pass" means that option is unavailable.
    // In type-19, [16-21] contain all 6 dora indicators (unlike other count=50 events where
    // [19-21] are hand tiles). Cache them so Tick() can show accurate doras.
    private void HandleCallWindow(AtkUnitBase* addon)
    {
        this.OnPromptDetected();
        this.lastPromptActiveUtc = DateTime.UtcNow;

        var opts = new List<string>(3);
        for (var i = 6; i <= 8 && i < addon->AtkValuesCount; i++)
        {
            var s = SafeString(ref addon->AtkValues[i]);
            if (!string.IsNullOrEmpty(s) && !s.Equals("Pass", StringComparison.OrdinalIgnoreCase))
                opts.Add(s);
        }
        this.callWindowOptions = opts;

        // Callable tile: prefer the event-tracked last opponent discard (fresh); fall
        // back to [4], which carries the called tile icon on announcement-flavored
        // type-19 frames (2026-07-04: [4]=4s on the pon) but PERSISTS afterwards —
        // a stale [4] must not override a fresh discard.
        if (this.lastOpponentDiscard is { } fresh)
            this.callOpportunityTile = fresh;
        else if (addon->AtkValuesCount > 4 && TryTileFromIconId(addon->AtkValues[4].Int, out var called))
            this.callOpportunityTile = called;
        else
            this.callOpportunityTile = null;

        // Announcement flavor: type-19 also fires when OTHER seats call (Pon!/Chi!
        // banners) with the called tile at [4] and stale option strings at [6-8].
        // A tile no local call is legal for cannot be a local window — don't activate.
        if (this.callOpportunityTile is { } claimTile && !this.HasAnyLegalLocalCall(claimTile))
        {
            this.LogNote($"[CallWindow] type-19 for {claimTile}: no legal local call (another seat's action) — not activating.");
            this.isCallWindowActive = false;
            this.callWindowOptions = [];
            this.callOpportunityTile = null;
        }
        else
        {
            this.OnClaimPromptEdge(addon, opts);
        }

        // Dora indicators: Layout 1 exposes up to six at [16-21]; Layout 2 ([14]>0)
        // only [16] — [17] is a -1 sentinel and [18+] are HAND tiles (never doras).
        var layout = addon->AtkValuesCount >= 15 ? addon->AtkValues[14].Int : 0;
        var doraEnd = layout > 0 ? 16 : 21;
        var doras = new List<Tile>(6);
        for (var i = 16; i <= doraEnd && i < addon->AtkValuesCount; i++)
            if (TryTileFromIconId(addon->AtkValues[i].Int, out var d)) doras.Add(d);
        if (doras.Count > 0) this.trackedDoras = doras;

        this.pluginLog.Debug($"[CallWindow] opts=[{string.Join(",", opts)}]  tile={this.callOpportunityTile}  doras=[{string.Join(" ", this.trackedDoras)}]");
    }

    // Type 21: New hand dealt.
    // Layout 1 ([14]=0): start-of-round deal — reset all per-round state.
    //   Tiles are NOT in AtkValues; directHandTiles stays [] until hover or Layout 2 kicks in.
    // Layout 2 ([14]>0): within-round update after a meld — update directHandTiles only.
    // Returns true only when this type-21 was a genuine state-changing reset/update
    // (a real new deal, or a real post-meld hand refresh) — the caller uses this to
    // decide whether clearing the call-window flag is warranted. A "not our deal" or
    // "mid-round, ignored" type-21 must NOT clear it: a real claim window can be
    // active (e.g. just activated by the slot-jump detector) when an UNRELATED
    // type-21 happens to arrive in the same batch, and blindly clearing on every
    // type-21 regardless of outcome silently kills it before it's ever actable —
    // live 2026-07-06: the ghost-slot-jump detector correctly activated a chi window
    // (offered=5s) one event before a mid-round type-21 fired and immediately wiped
    // isCallWindowActive back to false at the switch level, even though HandleNewDeal
    // itself correctly recognized the type-21 as a no-op. Since the stuck labels never
    // produce a fresh edge and the slot-jump fallback won't refire for an
    // already-consumed discard event, the window became permanently invisible —
    // exactly the reported "visible Chi/Pass window the plugin never recognized."
    private bool HandleNewDeal(AtkUnitBase* addon)
    {
        if (addon->AtkValues[2].Int != 0) return false; // local player's deal only (seat=0)

        var layout = addon->AtkValuesCount >= 15 ? addon->AtkValues[14].Int : 0;
        if (layout == 0)
        {
            if (!this.roundEnded)
            {
                // Mid-round refresh, not a deal — wiping here loses the hand, melds,
                // discard pile and wall count (this was the post-call-window corruption).
                this.LogNote("[HandTrack] Mid-round type-21 Layout1 ignored (no round boundary seen).");
                return false;
            }

            this.roundEnded = false;
            this.directHandTiles = [];
            this.trackedCalledMelds.Clear();
            this.lastAddedMeldSignature = null;
            this.eventDiscardPile.Clear();
            this.callOpportunityTile = null;
            this.lastOpponentDiscard = null;
            this.trackedWallRemaining = 70;
            this.lastDiscardKey = default;
            this.hoverHandSlots.Clear();
            this.lastDrawnTile = null;
            this.lastDrawnTileAuthoritative = false;
            this.oversizeHandWarned = false;
            this.isRiichiDeclared = false;
            this.tileFaceMap.Save(this.faceMapPath); // round boundary = cheap persistence point

            // count=109 deal frames expose the fresh 13-tile hand at [24-36] — grab it
            // so recommendations start immediately, no hover needed.
            var dealt = ReadHandTilesFromAtkValues(addon);
            if (dealt.Count >= 13)
                this.directHandTiles = dealt;

            this.LogNote($"[HandTrack] New deal Layout1 (type-21): round state reset (deal read: {this.directHandTiles.Count} tiles).");
            return true;
        }
        else
        {
            // Post-meld state update: read tiles from Layout 2 AtkValues.
            var newTiles = ReadHandTilesFromAtkValues(addon);
            if (newTiles.Count > 0)
            {
                this.directHandTiles = newTiles;
                // Clear stale hover data from the pre-meld hand. Without this, old
                // hoverHandSlots entries (e.g. 3p from 13-tile hand) survive into the
                // reduced hand and trigger spurious adds via countInDirect < countInHover.
                this.hoverHandSlots.Clear();
                this.lastDrawnTile = null;
                this.lastDrawnTileAuthoritative = false;
                this.LogNote($"[HandTrack] New deal Layout2 (type-21): {newTiles.Count} tiles from AtkValues.");
                return true;
            }

            return false;
        }
    }

    // Reads closed hand tile icons from AtkValues.
    //
    // LOCAL Layout 2 requires BOTH [14]>0 AND the [17]==-1 sentinel. [14] also flips
    // when an OPPONENT melds (recorded 2026-07-04 round 2: seat-2 pon set [14]=2/3
    // while [16-21] still held the six dora indicators — reading [19..] then poisoned
    // the hand with doras). With the sentinel, tile icons run from [19] until the
    // first non-tile; [18] claims a count but UNDER-REPORTS after melds ([18]=8 with
    // 11 icons at [19..29]) — never trust it, read the run.
    //
    // [24-37] hold a 13/14-tile hand ONLY on type-21 deal frames. They PERSIST across
    // later events — after a win screen they contain the WINNER's hand (recorded:
    // opponent tsumo hand adopted as our own for minutes) — so gate on [0]==21.
    private static List<Tile> ReadHandTilesFromAtkValues(AtkUnitBase* addon)
    {
        if (addon->AtkValues == null || addon->AtkValuesCount < 20) return [];

        // Local Layout 2: [14]>0 (2=Chi, 3=Pon, 5/6=Pon variants, 7=Tsumo) + [17]==-1.
        // A type-30 (hover/tooltip) refresh's AtkValues reuses these same slot
        // positions for unrelated data and can coincidentally satisfy this shape too
        // (live 2026-07-06: [14]=3, [17]=-1, [18]=8 on a pure hover event, with [19..21]
        // happening to hold 3 real tile icons before hitting a ConstString — produced a
        // plausible-looking but truncated 3-tile "hand" that got trusted every tick).
        // Require the read count to match the claimed closed-tile count at [18] exactly
        // — a genuine Layout2 frame always satisfies this by construction; a spurious
        // match breaking early on unrelated reused data generally won't.
        if (addon->AtkValuesCount >= 20
            && addon->AtkValues[14].Int > 0
            && addon->AtkValues[17].Type == AtkValueType.Int
            && addon->AtkValues[17].Int == -1
            && addon->AtkValues[18].Type == AtkValueType.Int)
        {
            var claimedCount = addon->AtkValues[18].Int;
            var tiles = new List<Tile>(14);
            for (var i = 19; i < addon->AtkValuesCount && tiles.Count < 14; i++)
            {
                ref var v = ref addon->AtkValues[i];
                if (v.Type != AtkValueType.Int || !TryTileFromIconId(v.Int, out var t)) break;
                tiles.Add(t);
            }
            if (tiles.Count >= 1 && tiles.Count == claimedCount) return tiles;
        }

        // Deal frames only: [24-36] = sorted closed tiles, [37] = draw tile.
        if (addon->AtkValuesCount >= 37 && addon->AtkValues[0].Int == 21)
        {
            var tiles = new List<Tile>(14);
            for (var i = 24; i <= 37 && i < addon->AtkValuesCount; i++)
            {
                ref var v = ref addon->AtkValues[i];
                if (v.Type != AtkValueType.Int) break;
                if (TryTileFromIconId(v.Int, out var t)) tiles.Add(t);
            }
            if (tiles.Count >= 13) return tiles;
        }

        return [];
    }

    // Type 29: Post-round score summary. [1]=seat-0 delta×100.
    // Score is accumulated into currentScore; fingerprinting prevents duplicate processing.
    private void HandleScoreDelta(AtkUnitBase* addon)
    {
        this.roundEnded = true; // next Layout-1 type-21 is a genuine deal
        // Clear so the just-ended round's hand can't be mistaken for still-legit-sized
        // state by the Tick() populate loop's roundEnded=false correction, which would
        // otherwise re-arm on stale data before the next genuine deal fires.
        this.directHandTiles = [];
        if (addon->AtkValuesCount < 2) return;
        var delta = addon->AtkValues[1].Int * 100;
        this.currentScore += delta;

        var fingerprint = $"type29:{delta}:{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (string.Equals(this.lastOutcomeFingerprint, fingerprint, StringComparison.Ordinal)) return;
        this.lastOutcomeFingerprint = fingerprint;

        if (Math.Abs(delta) < 100) return; // zero-delta draws or noise

        if (delta > 0)
        {
            this.winsThisSession++;
            this.pluginLog.Debug($"[Session] Win delta={delta} runningScore={this.currentScore}");
        }
        else
        {
            this.lossesThisSession++;
            this.pluginLog.Debug($"[Session] Loss delta={delta} runningScore={this.currentScore}");
        }
    }

    // Type 32: Win screen. Extract round wind from [2] e.g. "East 3 South Wind".
    private void HandleWinScreen(AtkUnitBase* addon)
    {
        this.roundEnded = true; // next Layout-1 type-21 is a genuine deal
        // Same rationale as HandleScoreDelta: prevent the just-ended round's stale
        // legit-sized hand from re-arming roundEnded=false before the next real deal.
        this.directHandTiles = [];
        if (addon->AtkValuesCount < 3) return;
        var roundStr = SafeString(ref addon->AtkValues[2]);
        if (roundStr.StartsWith("East",  StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.East;
        else if (roundStr.StartsWith("South", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.South;
        else if (roundStr.StartsWith("West",  StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.West;
        else if (roundStr.StartsWith("North", StringComparison.OrdinalIgnoreCase)) this.trackedRoundWind = Wind.North;
    }

    // atkType=74: meld accepted (player clicked Pon/Chi/Kan in the call window).
    // [8..] = meld tile icon IDs (3 tiles for Pon/Chi, 4 for Kan).
    // AtkValues are already Layout 2 at this point ([14]>0: 2=Chi, 3=Pon, 7=Tsumo).
    // Returns true only when a real meld payload was present and tracked.
    private bool HandleMeldAccepted(AtkUnitBase* addon)
    {
        if (addon->AtkValuesCount < 11) return false;
        var meldTiles = new List<Tile>(4);
        for (var i = 8; i < Math.Min(12, (int)addon->AtkValuesCount); i++)
        {
            ref var v = ref addon->AtkValues[i];
            if (v.Type != AtkValueType.Int || !TryTileFromIconId(v.Int, out var t)) break;
            meldTiles.Add(t);
        }
        if (meldTiles.Count < 3) return false;
        var meldType = meldTiles.Count == 4 ? MeldType.Daiminkan
            : TileHelpers.SameKind(meldTiles[0], meldTiles[1]) ? MeldType.Pon
            : MeldType.Chi;
        var meldArr = meldTiles.ToArray();

        // atkType=74 fires repeatedly while its payload tiles are still present, not
        // just once — dedupe consecutive identical payloads so the same meld isn't
        // added over and over. A hand can have at most 4 melds total; refuse past
        // that regardless (defensive — MaxClosedTiles(5+) goes negative and crashed
        // the truncation logic live 2026-07-06 when this dedupe didn't exist yet).
        var signature = $"{meldType}:{string.Join(",", meldArr.Select(TileHelpers.ToIndex).OrderBy(i => i))}";
        if (signature == this.lastAddedMeldSignature || this.trackedCalledMelds.Count >= 4)
            return false;

        this.lastAddedMeldSignature = signature;
        this.trackedCalledMelds.Add(new Meld(meldType, meldArr, true));
        this.pluginLog.Debug($"[Meld] atkType=74: {meldType} [{string.Join(" ", meldArr)}]");
        return true;
    }

    // PostReceiveEvent atkType=6 (mouse-enter on a tile button).
    // EventParam = slot index (0-based, 0-13). AtkValues[1] = ConstString tile display name.
    // Used as fallback for players who load the plugin mid-round (before the next type-21 deal).
    private void OnHoverEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonReceiveEventArgs ev) return;
            var atkType = (ushort)ev.AtkEventType;
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null) return;

            if (atkType == 74)
            {
                // atkType=74 fires constantly WITHOUT payload (noise, recorded 2026-07-04);
                // only a real meld acceptance (payload tiles present) resolves a prompt.
                if (this.HandleMeldAccepted(addon))
                {
                    this.DebugNote("meld accepted (atkType=74) — call window cleared");
                    this.lastDrawnTile = null;
                    this.lastDrawnTileAuthoritative = false;
                    isCallWindowActive = false;
                    callWindowOptions  = [];
                }

                return;
            }
            if (atkType != 6) return;
            if (addon->AtkValuesCount < 2) return;

            var slot = (int)ev.EventParam;
            if (slot < 0 || slot > 13) return;

            var tileName = SafeString(ref addon->AtkValues[1]);
            if (!TryParseTileName(tileName, out var tile)) return;

            this.hoverHandSlots[slot] = tile;
            this.DebugNote($"hover slot={slot} \"{tileName}\" → {tile}");

            // Face learning: the hover names the tile in a specific visual slot — the
            // highest-confidence observation source for that slot's rendered face.
            // Icon-decodable keys need no learning (and hover/slot index mismatches
            // would only poison them — recorded: a 1m/7s conflict blocked node reads).
            var scanSlots = EmjScanner.ScanHandSlots(addon);
            if (slot < scanSlots.Count && scanSlots[slot].FaceKey is { } hoverFaceKey
                && !FaceKeys.TryDecodeIcon(hoverFaceKey, out _))
                this.tileFaceMap.Observe(hoverFaceKey, tile);

            // Sync to directHandTiles: if hover reveals a tile we don't have yet
            // (common in count=50 where the deal only exposes 5 of 13 tiles via AtkValues),
            // add it so recommendations work without requiring a full re-hover.
            var countInDirect = this.directHandTiles.Count(t => TileHelpers.SameKind(t, tile));
            var countInHover  = this.hoverHandSlots.Values.Count(t => TileHelpers.SameKind(t, tile));
            var maxClosed     = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count);
            if (countInDirect < countInHover && this.directHandTiles.Count < maxClosed)
                this.directHandTiles.Add(tile);
        }
        catch (Exception ex)
        {
            this.pluginLog.Warning(ex, "[GameStateReader] OnHoverEvent error.");
        }
    }

    // Parses English tile display names from AtkValues[1] during hover events.
    private static bool TryParseTileName(string name, out Tile tile)
    {
        tile = default;
        if (string.IsNullOrWhiteSpace(name) || name == "-") return false;

        if (name.Equals("East Wind",    StringComparison.OrdinalIgnoreCase)) { tile = new Tile(TileSuit.Wind, 1); return true; }
        if (name.Equals("South Wind",   StringComparison.OrdinalIgnoreCase)) { tile = new Tile(TileSuit.Wind, 2); return true; }
        if (name.Equals("West Wind",    StringComparison.OrdinalIgnoreCase)) { tile = new Tile(TileSuit.Wind, 3); return true; }
        if (name.Equals("North Wind",   StringComparison.OrdinalIgnoreCase)) { tile = new Tile(TileSuit.Wind, 4); return true; }
        if (name.Equals("White Dragon", StringComparison.OrdinalIgnoreCase)) { tile = new Tile(TileSuit.Dragon, 1); return true; }
        if (name.Equals("Green Dragon", StringComparison.OrdinalIgnoreCase)) { tile = new Tile(TileSuit.Dragon, 2); return true; }
        if (name.Equals("Red Dragon",   StringComparison.OrdinalIgnoreCase)) { tile = new Tile(TileSuit.Dragon, 3); return true; }

        var parenStart = name.IndexOf('(');
        var parenEnd   = name.IndexOf(')');
        if (parenStart < 0 || parenEnd < 0) return false;

        var prefix = name[..parenStart].Trim();
        var numStr = name[(parenStart + 1)..parenEnd].Trim();
        if (!int.TryParse(numStr, out var number)) return false;

        var isRed = name.Contains("(Red)", StringComparison.OrdinalIgnoreCase) || number == 0;
        if (number == 0) number = 5;
        if (number < 1 || number > 9) return false;
        if (isRed && number != 5) return false;

        TileSuit suit;
        if (prefix.StartsWith("Character",        StringComparison.OrdinalIgnoreCase) ||
            prefix.StartsWith("Five of Character", StringComparison.OrdinalIgnoreCase))
            suit = TileSuit.Man;
        else if (prefix.StartsWith("Dot",          StringComparison.OrdinalIgnoreCase) ||
                 prefix.StartsWith("Five of Dot",  StringComparison.OrdinalIgnoreCase))
            suit = TileSuit.Pin;
        else if (prefix.StartsWith("Bamboo",       StringComparison.OrdinalIgnoreCase) ||
                 prefix.StartsWith("Five of Bamboo", StringComparison.OrdinalIgnoreCase))
            suit = TileSuit.Sou;
        else
            return false;

        tile = new Tile(suit, number, isRed);
        return true;
    }

    // ──────────────────────────────────────── RECORDING CALLBACKS ───────────────────────────────────────
    //
    // Filtered recorder: only PostRefresh events for key event types (5/6/15/19/21/29/32) OR
    // whenever AtkValuesCount=109 (post-round state). All PostReceiveEvents are captured since
    // they're sparse and include call-window button clicks.
    // Each captured frame gets a human-readable SUMMARY line above the full AtkValues table so
    // the output is useful without manual decoding.

    // PostRefresh event types worth recording.
    private static readonly HashSet<int> RecordedRefreshTypes = [5, 6, 15, 19, 21, 29, 32];

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        try { this.CaptureAnnotatedFrame((AtkUnitBase*)args.Addon.Address, "OPEN", null); }
        catch (Exception ex) { this.pluginLog.Warning(ex, "[Recorder] OnAddonSetup error."); }
    }

    private void OnAddonRefresh(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 1) return;

            var eventType = addon->AtkValues[0].Int;

            // Only capture key event types or the post-round count=109 state.
            if (!RecordedRefreshTypes.Contains(eventType) && addon->AtkValuesCount < 109) return;

            var label = eventType switch
            {
                5  => "DISCARD",
                6  => "DRAW",
                15 => "TURN",
                19 => "CALL_WINDOW",
                21 => "NEW_DEAL",
                29 => "SCORE",
                32 => "WIN_SCREEN",
                _  => $"REFRESH_T{eventType}",
            };
            this.CaptureAnnotatedFrame(addon, $"{label} count={addon->AtkValuesCount}", eventType);
        }
        catch (Exception ex) { this.pluginLog.Warning(ex, "[Recorder] OnAddonRefresh error."); }
    }

    private void OnAddonReceiveEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null) return;
            if (args is not AddonReceiveEventArgs ev) return;
            var atkType = (ushort)ev.AtkEventType;
            // Skip pure mouse-move spam (atkType 4/5/7); keep clicks, hover-enter, custom events.
            if (atkType is 4 or 5 or 7) return;
            var label = $"EVENT atkType={atkType} param={ev.EventParam} count={addon->AtkValuesCount}";
            this.CaptureAnnotatedFrame(addon, label, null);
            // Extra annotation for meld-accepted events
            if (atkType == 74 && addon->AtkValues != null && addon->AtkValuesCount >= 11)
            {
                var meldTiles = new List<string>(4);
                for (var i = 8; i < Math.Min(12, (int)addon->AtkValuesCount); i++)
                {
                    if (addon->AtkValues[i].Type == AtkValueType.Int &&
                        TryTileFromIconId(addon->AtkValues[i].Int, out var mt))
                        meldTiles.Add(mt.ToString());
                    else break;
                }
                var n14 = addon->AtkValuesCount > 14 ? addon->AtkValues[14].Int : 0;
                this.recordLog.Add($"  # MELD_ACCEPTED  tiles=[{string.Join(" ", meldTiles)}]  layout[14]={n14}");
            }
        }
        catch (Exception ex) { this.pluginLog.Warning(ex, "[Recorder] OnAddonReceiveEvent error."); }
    }

    private void OnAddonFinalize(AddonEvent type, AddonArgs args)
    {
        try
        {
            var t = (DateTime.UtcNow - this.recordingStart).TotalSeconds;
            this.recordLog.Add($"T+{t,8:F3}  [CLOSE]");
            this.recordLog.Add(string.Empty);
        }
        catch (Exception ex) { this.pluginLog.Warning(ex, "[Recorder] OnAddonFinalize error."); }
    }

    // Captures a full AtkValues table plus a human-readable SUMMARY for the known event types.
    private void CaptureAnnotatedFrame(AtkUnitBase* addon, string label, int? eventType)
    {
        if (!this.isRecording || addon == null) return;
        var t = (DateTime.UtcNow - this.recordingStart).TotalSeconds;
        this.recordLog.Add($"T+{t,8:F3}  [{label}]");

        // ── Human-readable summary for known event types ──────────────────────────────
        if (eventType.HasValue && addon->AtkValues != null)
        {
            var ev = eventType.Value;
            var v  = addon->AtkValues;
            var n  = addon->AtkValuesCount;
            switch (ev)
            {
                case 5 when n >= 4:
                {
                    var seat = v[2].Int;
                    var who  = seat == 0 ? "local" : $"seat{seat}";
                    TryTileFromIconId(v[3].Int, out var tile);
                    this.recordLog.Add($"  # DISCARD  {who} discarded {tile}  wall={v[1].Int}  tileIcon={v[3].Int}");
                    break;
                }
                case 6 when n >= 3:
                {
                    var icon = v[2].Int;
                    var desc = icon is >= 76068 and <= 76071
                        ? $"seat-wind notification (icon={icon})"
                        : TryTileFromIconId(icon, out var dt) ? $"drew {dt} (icon={icon})" : $"icon={icon}";
                    this.recordLog.Add($"  # DRAW  {desc}");
                    break;
                }
                case 15 when n >= 3:
                    this.recordLog.Add($"  # TURN  seatWindIcon={v[2].Int}  phase=[6]=\"{SafeString(ref v[6])}\"");
                    break;
                case 19 when n >= 9:
                {
                    var chi = SafeString(ref v[6]);
                    var pon = SafeString(ref v[7]);
                    var ron = SafeString(ref v[8]);
                    this.recordLog.Add($"  # CALL_WINDOW  Chi=[6]\"{chi}\"  Pon=[7]\"{pon}\"  Ron=[8]\"{ron}\"");
                    this.recordLog.Add($"  #   lastOpponentDiscard={this.lastOpponentDiscard}");
                    break;
                }
                case 21 when n >= 3:
                {
                    var layout = n >= 15 ? v[14].Int : 0;
                    // Layout 2: tiles at [18]=count, [19..] = icons
                    var tiles2 = new List<string>();
                    if (layout > 0 && n >= 20)
                    {
                        var cnt = v[18].Int;
                        for (var i = 0; i < cnt && 19 + i < n; i++)
                        {
                            if (v[19 + i].Type != AtkValueType.Int) break;
                            if (TryTileFromIconId(v[19 + i].Int, out var ht)) tiles2.Add(ht.ToString());
                        }
                    }
                    // count=109: tiles at [24-37]
                    var tiles109 = new List<string>();
                    for (var i = 24; i <= 37 && i < n; i++)
                    {
                        if (v[i].Type != AtkValueType.Int) break;
                        if (TryTileFromIconId(v[i].Int, out var ht)) tiles109.Add(ht.ToString());
                    }
                    this.recordLog.Add($"  # NEW_DEAL  seat={v[2].Int}  tileCount={v[1].Int}  layout[14]={layout}");
                    if (tiles2.Count > 0)
                        this.recordLog.Add($"  #   Layout2 tiles=[{string.Join(" ", tiles2)}]");
                    if (tiles109.Count > 0)
                        this.recordLog.Add($"  #   count=109 tilesAt[24+]=[{string.Join(" ", tiles109)}]");
                    break;
                }
                case 29 when n >= 5:
                {
                    var s0 = v[1].Int * 100;
                    var s1 = v[2].Int * 100;
                    var s3 = n >= 5 ? v[3].Int * 100 : 0;
                    var s4 = n >= 6 ? v[4].Int * 100 : 0;
                    var method = n >= 6 ? SafeString(ref v[5]) : "?";
                    this.recordLog.Add($"  # SCORE  seat0(local)={s0:+#;-#;0}  seat1={s1:+#;-#;0}  seat2={s3:+#;-#;0}  seat3={s4:+#;-#;0}");
                    this.recordLog.Add($"  #   method=\"{method}\"  trackedScore_after={this.currentScore}");
                    break;
                }
                case 32 when n >= 8:
                {
                    var round  = SafeString(ref v[2]);
                    var method = SafeString(ref v[5]);
                    var hanFu  = SafeString(ref v[6]);
                    var score  = v[7].Int * 100;
                    var tiles  = new List<string>();
                    for (var i = 24; i <= 37 && i < n; i++)
                    {
                        if (v[i].Type != AtkValueType.Int) break;
                        if (TryTileFromIconId(v[i].Int, out var ht)) tiles.Add(ht.ToString());
                    }
                    this.recordLog.Add($"  # WIN_SCREEN  round=\"{round}\"  method=\"{method}\"  {hanFu}  total={score}");
                    this.recordLog.Add($"  #   winningHand=[{string.Join(" ", tiles)}]  [41]winTile={v[41].Int}");
                    if (n >= 46)
                    {
                        var yaku = string.Join(", ", new[] { SafeString(ref v[43]), SafeString(ref v[44]), SafeString(ref v[45]) }
                                  .Where(s => !string.IsNullOrWhiteSpace(s) && s != "-"));
                        this.recordLog.Add($"  #   yaku=[{yaku}]");
                    }
                    break;
                }
            }
        }

        // ── Hand tile node state (PartId from component tree) ──────────────────────
        if (addon->RootNode != null)
            this.CaptureHandTileNodeState(addon);

        // ── Deep node-scan sections (/mhater analyze sessions only) ────────────────
        if (this.isDeepScan && addon->RootNode != null)
            this.CaptureDeepScanSections(addon);

        // ── Full AtkValues table ────────────────────────────────────────────────────
        if (addon->AtkValuesCount > 0 && addon->AtkValues != null)
        {
            this.recordLog.Add($"  AtkValues (count={addon->AtkValuesCount}):");
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                ref var v = ref addon->AtkValues[i];
                var tileHint = TryTileFromIconId(v.Int, out var tile) ? $"  →{tile}" : string.Empty;
                this.recordLog.Add($"    [{i,2}] {v.Type,-14} int={v.Int,10}  str={SafeString(ref v)}{tileHint}");
            }
        }

        this.recordLog.Add(string.Empty);
    }

    // Captures the PartId of every visible type-1055 hand tile component in left-to-right order.
    // PartIds allow mapping node state to tile identity (mapping itself requires a calibration recording).
    private void CaptureHandTileNodeState(AtkUnitBase* addon)
    {
        var nodeList  = addon->UldManager.NodeList;
        var nodeCount = addon->UldManager.NodeListCount;
        var handTiles = new List<(uint NodeId, float AbsX, ushort? PartId)>(16);
        for (var ni = 0; ni < nodeCount; ni++)
        {
            var n = nodeList[ni];
            if (n == null || !n->IsVisible()) continue;
            if ((ushort)n->Type != 1055) continue;
            handTiles.Add((n->NodeId, NodeAbsX(n), GetTileComponentPartId(n)));
        }
        if (handTiles.Count == 0) return;
        handTiles.Sort((a, b) => a.AbsX.CompareTo(b.AbsX));
        if (handTiles.Count > 14) handTiles.RemoveRange(14, handTiles.Count - 14);
        var parts = handTiles.Select((ht, idx) =>
            $"s{idx}:nid={ht.NodeId}/p{(ht.PartId.HasValue ? ht.PartId.Value.ToString() : "?")}");
        this.recordLog.Add($"  # TILE_NODE_PARTS  [{string.Join(" | ", parts)}]");
    }

    // Per-frame node-scan capture for /mhater analyze: hand slot faces + resolutions,
    // pile snapshot stats, call button texts, learning progress. Together with the
    // AtkValues tables this is the dataset for reversing the Emj addon.
    private void CaptureDeepScanSections(AtkUnitBase* addon)
    {
        try
        {
            var slots = EmjScanner.ScanHandSlots(addon);
            var resolved = this.tileFaceMap.Resolved;
            var slotDesc = slots.Select((s, i) =>
            {
                var tile = s.FaceKey is not null && resolved.TryGetValue(s.FaceKey, out var t) ? t.ToString() : "?";
                return $"s{i}:{s.FaceKey ?? "?"}={tile}";
            });
            this.recordLog.Add($"  # SCAN_HAND  [{string.Join(" | ", slotDesc)}]");

            // Full reversing detail for slot 0 and the draw slot — enough to see which
            // face attribute actually varies per tile without flooding the log.
            if (slots.Count > 0)
            {
                this.recordLog.Add($"  # SCAN_FACEDETAIL  s0: {EmjScanner.DescribeFace((AtkResNode*)slots[0].NodePtr)}");
                if (slots.Count > 1)
                    this.recordLog.Add($"  # SCAN_FACEDETAIL  s{slots.Count - 1}: {EmjScanner.DescribeFace((AtkResNode*)slots[^1].NodePtr)}");
            }

            var piles = EmjScanner.ScanPileFaces(addon);
            this.recordLog.Add($"  # SCAN_PILES  slots={piles.Count}  distinctFaces={piles.Values.Distinct().Count()}");
            var pileKeys = piles.Values.GroupBy(k => k).OrderByDescending(g => g.Count()).Take(8)
                .Select(g => $"{g.Key}×{g.Count()}");
            this.recordLog.Add($"  # SCAN_PILEKEYS  [{string.Join(" | ", pileKeys)}]");

            var buttons = EmjScanner.ScanCallButtonTexts(addon);
            if (buttons.Count > 0)
                this.recordLog.Add($"  # SCAN_CALLBTNS  [{string.Join("][", buttons)}]");

            this.recordLog.Add($"  # SCAN_FACEMAP  resolved={this.tileFaceMap.ResolvedCount}/{this.tileFaceMap.KeyCount}  nodeRead={(this.nodeReadActive ? "ACTIVE" : "off")}");

            // Tracked state vs recommendation — the direct diagnostic for
            // "the plugin told me to discard a tile I don't have".
            this.recordLog.Add(
                $"  # SCAN_TRACKED  hand=[{string.Join(" ", this.directHandTiles)}]  " +
                $"melds={this.trackedCalledMelds.Count}  eventPile={this.eventDiscardPile.Count}  " +
                $"scanPile={this.scannedPileTiles.Count}  wall={this.trackedWallRemaining}  " +
                $"callWin={this.isCallWindowActive}[{string.Join(",", this.callWindowOptions)}]");
            if (this.AnalysisSummaryProvider?.Invoke() is { Length: > 0 } reco)
                this.recordLog.Add($"  # SCAN_RECO  {reco}");
        }
        catch (Exception ex)
        {
            this.recordLog.Add($"  # SCAN_ERROR  {ex.Message}");
        }
    }

    // Returns the PartId of the tile-face image inside a type-1055 component.
    // NodeID=9 (type=1010, 42×55) is the tile face sub-component; NodeID=8 (type=2, 23×23) is
    // a state indicator dot that always reads PartId=2. We descend into sub-components first
    // and require the image to be ≥30×40 to skip the small indicator images.
    private static ushort? GetTileComponentPartId(AtkResNode* node)
    {
        if ((ushort)node->Type < 1000) return null;
        var comp = ((AtkComponentNode*)node)->Component;
        if (comp == null) return null;
        var cList  = comp->UldManager.NodeList;
        var cCount = comp->UldManager.NodeListCount;
        for (var i = 0; i < cCount; i++)
        {
            var cn = cList[i];
            if (cn == null) continue;
            // Descend into sub-components (NodeID=9 type=1010 is the tile face renderer)
            if ((ushort)cn->Type >= 1000)
            {
                var subComp = ((AtkComponentNode*)cn)->Component;
                if (subComp == null) continue;
                var subList  = subComp->UldManager.NodeList;
                var subCount = subComp->UldManager.NodeListCount;
                for (var j = 0; j < subCount; j++)
                {
                    var sn = subList[j];
                    if (sn == null) continue;
                    var subImg = sn->GetAsAtkImageNode();
                    if (subImg != null && sn->Width >= 30 && sn->Height >= 40)
                        return subImg->PartId;
                }
                continue;
            }
            // Large image nodes only — skip the 23×23 indicator dot (NodeID=8)
            var img = cn->GetAsAtkImageNode();
            if (img != null && cn->Width >= 30 && cn->Height >= 40)
                return img->PartId;
        }
        return null;
    }


    // ──────────────────────────────────────────────── TICK ──────────────────────────────────────────────

    public void Tick()
    {
        try
        {
            var addonPtr = this.gameGui.GetAddonByName("Emj");
            if (addonPtr.IsNull)
            {
                this.CurrentState = null;
                return;
            }

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon->RootNode == null || addon->AtkValues == null || addon->AtkValuesCount < 16)
            {
                this.CurrentState = null;
                return;
            }

            // Self-correct a stale/false-positive tracked meld BEFORE anything below
            // relies on trackedCalledMelds.Count: 13-14 visible hand slots is the max
            // possible with ZERO melds — it can NEVER coexist with any tracked meld
            // (more melds only ever shrinks the legal closed-hand size), so seeing that
            // many slots while trackedCalledMelds is non-empty proves the tracking is
            // wrong, not that the hand is oversized. Checked via a cheap slot-count scan
            // (identity doesn't matter here) so it can run ahead of the bootstrap below
            // and correct its target size the SAME tick, rather than one tick late.
            // Live 2026-07-06: a single false-positive `HandleMeldAccepted` firing (exact
            // trigger not yet root-caused) left a stale `Chi[...]` entry that corrupted
            // EVERY expected-slot-count calculation used by the claim-window/ghost
            // detection for the rest of the round — the symptom was a real, visible
            // Chi/Pass window never getting recognized at all, since every size
            // comparison against the wrong (too-small) expected closed-hand size
            // silently failed to match.
            if (this.trackedCalledMelds.Count > 0)
            {
                var visibleSlotCount = EmjScanner.ScanHandSlots(addon).Count;
                if (visibleSlotCount >= HandTracking.MaxClosedTiles(0) - 1)
                {
                    this.LogNote($"[HandTrack] Tracked melds [{string.Join(", ", this.trackedCalledMelds)}] contradict {visibleSlotCount} visible hand slots (impossible with any meld) — clearing as stale.");
                    this.trackedCalledMelds.Clear();
                    this.lastAddedMeldSignature = null;
                }
            }

            // Cold-start bootstrap: a freshly constructed reader (plugin reload or game
            // crash mid-round) can take its very first Tick while a claim window is
            // ALREADY open — the hand-scan below never runs in that case (it's skipped
            // whenever isCallWindowActive), so directHandTiles would stay permanently
            // empty and OnClaimPromptEdge's ghost-strip would have nothing to strip from
            // (live 2026-07-05: reload during an open Chi window left the tracked hand
            // empty and reco stuck at "no hand" for the rest of the round). Populate
            // before prompt detection runs below. Skipped on post-round event types
            // (29/32) — otherwise it would immediately re-read the WINNING hand right
            // after HandleScoreDelta/HandleWinScreen deliberately cleared it, silently
            // undoing that clear.
            //
            // Retries every tick until the hand reaches a LEGITIMATE size, not just
            // once on Count==0: a real hover event can add a single confirmed tile via
            // OnHoverEvent's sync logic before the very first bootstrap attempt manages
            // a full decode (e.g. a transiently-undecodable face on that first tick) —
            // that bumps Count off zero, and a one-shot gate would then never retry even
            // though a full decode succeeds moments later (live 2026-07-06: stuck
            // growing one tile at a time via real mouse hovers for the whole window,
            // instead of the bootstrap ever completing).
            var bootstrapEventType = addon->AtkValues[0].Int;
            var bootstrapExpectedSize = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count);
            var bootstrapNeeded = this.directHandTiles.Count != bootstrapExpectedSize
                && this.directHandTiles.Count != bootstrapExpectedSize - 1;
            if (bootstrapNeeded && bootstrapEventType is not 29 and not 32)
            {
                var bootTiles = ReadHandTilesFromAtkValues(addon);
                if (bootTiles.Count >= 1)
                {
                    this.directHandTiles = bootTiles;
                }
                else
                {
                    // Stop at the first undecodable slot rather than discarding
                    // everything decoded so far — the far-right slot is commonly an
                    // active claim window's ghost (the OPPONENT's offered discard, not
                    // part of the closed hand) and has no learnable face of its own
                    // (2026-07-06: bootstrap during an open window discarded a
                    // perfectly good 13-tile prefix because slot 14 alone failed to
                    // decode, leaving the tracked hand empty for the whole window).
                    // Only trust the prefix when it lands on a legitimate closed-hand
                    // size — a truncation for any OTHER reason (an unresolved learned
                    // key, a mid-render glitch) is not safe to publish as the hand.
                    var bootSlots = EmjScanner.ScanHandSlots(addon);
                    var decoded = new List<Tile>(bootSlots.Count);
                    foreach (var slot in bootSlots)
                    {
                        if (!this.TryDecodeSlot(slot, out var bootTile))
                            break;
                        decoded.Add(bootTile);
                    }

                    var legitBootSize = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count);
                    if (decoded.Count == legitBootSize || decoded.Count == legitBootSize - 1)
                        this.directHandTiles = decoded;
                }
            }

            // Prompt detection first. Riichi/Ron/Chi prompts fire NO type-19 event
            // (recorded 2026-07-04 round 3), so button labels under NodeID=104 are the
            // detection source — but EDGE-triggered, not level-triggered: in count=109
            // mode the labels stay visible forever after a window (recorded round: a
            // stuck "Chi" label re-froze hand tracking every tick for the whole round).
            // A CHANGED label set marks a new prompt; label disappearance or a
            // resolution event (type-5/15/21/29/32, real meld) ends it.
            var promptLabels = ScanPromptOptions(addon);
            var promptSignature = string.Join(",", promptLabels);
            if (promptLabels.Count == 0)
            {
                this.isCallWindowActive = false;
                this.callWindowOptions = [];
                this.callOpportunityTile = null;
            }
            else if (promptSignature != this.lastPromptSignature)
            {
                this.OnPromptDetected();
                this.callWindowOptions = promptLabels;
                this.OnClaimPromptEdge(addon, promptLabels);
            }
            else if (!this.isCallWindowActive
                     && this.lastDiscardEventUtc != this.slotJumpEdgeConsumedUtc
                     && HandTracking.IsClaimWindowSlotJump(
                         (DateTime.UtcNow - this.lastDiscardEventUtc).TotalMilliseconds,
                         (DateTime.UtcNow - this.lastLocalDrawUtc).TotalMilliseconds))
            {
                // Same-signature claim window: identical stuck labels produce no edge,
                // so detect the window by its state fingerprint instead — a fresh
                // discard with no local draw after it, the ghost slot pushing the hand
                // one over its legal claim-time size, and a legal call on the claimable.
                // (An own-turn 14th tile fails the draw-recency check; a stale panel
                // with no game activity fails the freshness check.) The claimable's
                // identity: a fresh authoritative type-8 kind outranks the ghost decode
                // — the ghost face reuses the drawn-tile node and decodes stale (live
                // 2026-07-05: ghost read 1m while the claimable was a pon-able 7z).
                var ghostSlots = EmjScanner.ScanHandSlots(addon);
                var expectedClaim = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count) - 1;
                var ghostKind = ghostSlots.Count == expectedClaim + 1
                    && this.TryDecodeSlot(ghostSlots[^1], out var jumpGhost) ? jumpGhost : (Tile?)null;
                var claimKind = this.lastDiscardKindAuthoritative && this.lastDiscardEventKind is { } authoritative
                    ? authoritative
                    : ghostKind is { } ghost && (this.lastDiscardEventKind is not { } dk || TileHelpers.SameKind(ghost, dk))
                        ? ghostKind
                        : null;
                if (ghostSlots.Count == expectedClaim + 1
                    && claimKind is { } claimable
                    && this.HasAnyLegalLocalCall(claimable))
                {
                    this.slotJumpEdgeConsumedUtc = this.lastDiscardEventUtc;
                    this.LogNote($"[HandTrack] Claim window detected via ghost-slot jump (stuck labels: {promptSignature}; claimable {claimable}).");
                    this.OnPromptDetected();
                    this.callWindowOptions = promptLabels;
                    this.OnClaimPromptEdge(addon, promptLabels);
                }
            }

            this.lastPromptSignature = promptSignature;
            if (this.isCallWindowActive)
                this.lastPromptActiveUtc = DateTime.UtcNow;

            var liveEventType = bootstrapEventType;

            // Hand tracking FREEZES while a prompt is up: the hand cannot legally change,
            // and chi/pon prompts render the claimable opponent discard as an extra
            // hand-slot node (recorded: a ghost 14th tile matching the offered tile,
            // e.g. a second 7p during a 7p chi offer) which would poison every reader.
            if (!this.isCallWindowActive)
            {
                // Live hand scan: refresh directHandTiles from AtkValues every tick.
                // Skip event types 29/32 (post-round screens with stale data).
                List<Tile>? atkTiles = null;
                if (liveEventType is not 29 and not 32)
                {
                    atkTiles = ReadHandTilesFromAtkValues(addon);
                    if (atkTiles.Count >= 1)
                        this.directHandTiles = atkTiles;
                }

                // Node-scan engine: decode slot faces (icons directly, learned map as
                // fallback) — the primary, hover-free hand source.
                var handSlots = EmjScanner.ScanHandSlots(addon);
                this.LearnFromOrderedRead(atkTiles, handSlots);
                this.TryNodeHandRead(handSlots, liveEventType);

                // A LEGITIMATELY sized hand is proof the round is genuinely in progress —
                // clears the "genuine new deal" gate a fresh reader's default true value
                // would otherwise wrongly claim for the next type-21 (2026-07-06: a mid-
                // round type-21 refresh, arriving before this reader had ever confirmed a
                // real hand, wiped a correctly-tracked 13-tile hand to empty because
                // `roundEnded` still read true from construction — see HandleNewDeal).
                var legitSize = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count);
                var isLegitSize = this.directHandTiles.Count == legitSize || this.directHandTiles.Count == legitSize - 1;
                if (isLegitSize)
                    this.roundEnded = false;

                // Keep a short history for the prompt-edge rollback (see OnPromptDetected).
                // Only a legitimately sized hand qualifies — a freshly-constructed reader's
                // very first ticks can bootstrap a partial/garbage read (2026-07-06: a
                // 3-tile snapshot from mid-initialization got queued, then rolled back INTO
                // by the very next claim window, corrupting a hand that had otherwise
                // already resynced correctly). Filtering at the source is more robust than
                // filtering at the rollback site, since it also protects any other future
                // consumer of recentHands.
                if (isLegitSize)
                {
                    this.recentHands.Enqueue([.. this.directHandTiles]);
                    while (this.recentHands.Count > 6)
                        this.recentHands.Dequeue();
                }
            }

            // Fresh pile baseline for discard-diff learning (see HandleDiscard) — and,
            // in icon render modes, a direct read of the whole discard pile.
            this.pileFaceBaseline = EmjScanner.ScanPileFaces(addon);
            this.scannedPileTiles = this.DecodePileTiles();

            // Periodic deep-scan sample so /analyze captures state between events too.
            if (this.isDeepScan && this.isRecording && (DateTime.UtcNow - this.lastDeepSample).TotalSeconds >= 1)
            {
                this.lastDeepSample = DateTime.UtcNow;
                this.CaptureAnnotatedFrame(addon, $"TICK_SAMPLE count={addon->AtkValuesCount}", null);
            }

            // Hand tiles: primary = event-maintained directHandTiles (deal/discard/draw).
            // Fallback = hover-captured hoverHandSlots for mid-match plugin loads.
            List<Tile> closedTiles;
            if (this.directHandTiles.Count >= 1)
                closedTiles = this.directHandTiles.ToList();
            else if (this.hoverHandSlots.Count > 0)
                closedTiles = this.hoverHandSlots.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            else
                closedTiles = [];

            // Never publish an oversized hand — an exponential analyzer input is a UI freeze.
            // Clamped to 0: trackedCalledMelds.Count should never exceed 4 (a hand has at
            // most 4 melds), but a corrupted count going negative here must degrade to
            // "truncate everything" rather than crash Tick() every frame (live 2026-07-06:
            // an unguarded duplicate-meld bug drove this negative and threw inside
            // RemoveRange — every subsequent Tick failed before CurrentState could be
            // reassigned, silently freezing the recommendation on stale data).
            var maxClosed = Math.Max(0, HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count));
            if (closedTiles.Count > maxClosed)
            {
                if (!this.oversizeHandWarned)
                {
                    this.oversizeHandWarned = true;
                    this.pluginLog.Warning($"[HandTrack] Tracked hand has {closedTiles.Count} tiles (max {maxClosed}); truncating.");
                }

                closedTiles.RemoveRange(maxClosed, closedTiles.Count - maxClosed);
            }

            var seatWind       = ReadSeatWindFromAtkValues(addon) ?? Wind.East;
            var doraIndicators = ReadDoraIconsFromAtkValues(addon);
            var roundWind      = ReadRoundWindFromTextNodes(addon) ?? this.trackedRoundWind;

            this.CurrentState = new GameState
            {
                InGame               = true,
                ClosedTiles          = closedTiles,
                CalledMelds          = this.trackedCalledMelds.ToList(),
                SeatWind             = seatWind,
                RoundWind            = roundWind,
                CurrentScore         = this.currentScore,
                HonbaCount           = 0,
                RiichiSticksCount    = 0,
                MgpEarned            = 0,
                WinsThisSession      = this.winsThisSession,
                LossesThisSession    = this.lossesThisSession,
                IsRiichi             = this.isRiichiDeclared,
                TilesRemainingInWall = this.trackedWallRemaining,
                DiscardPile          = MergeDiscardPiles(this.scannedPileTiles, this.eventDiscardPile),
                DoraIndicators       = doraIndicators,
                UradoraIndicators    = [],
                CallOpportunityTile  = this.isCallWindowActive
                    ? this.callOpportunityTile ?? this.lastOpponentDiscard
                    : null,
                IsCallWindowActive   = this.isCallWindowActive,
                CallWindowOptions    = this.callWindowOptions.ToList(),
            };
        }
        catch (Exception ex)
        {
            this.pluginLog.Error(ex, "Mahjong Hater failed to read the Emj addon.");
        }
    }

    // ─────────────────────────────────────── NODE-SCAN LEARNING ─────────────────────────────────────────

    // Pairs a trusted ordered tile read (Layout 2 / deal-frame AtkValues, which list
    // tiles in visual hand order) with the scanned slot faces. Mismatched counts are
    // skipped; occasional wrong pairings scatter and never dominate (see TileFaceMap).
    // Deduplicated per pairing — Tick runs at frame rate and re-observing the same
    // state thousands of times would swamp legitimate evidence (recorded: 8700×).
    private void LearnFromOrderedRead(List<Tile>? orderedTiles, List<ScannedTileSlot> slots)
    {
        if (orderedTiles is null || orderedTiles.Count < 4 || orderedTiles.Count != slots.Count)
            return;

        var signature = string.Join(",", orderedTiles) + "#" + string.Join(",", slots.Select(s => s.FaceKey));
        if (signature == this.lastOrderedLearnSignature)
            return;
        this.lastOrderedLearnSignature = signature;

        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].FaceKey is { } faceKey && !FaceKeys.TryDecodeIcon(faceKey, out _))
                this.tileFaceMap.Observe(faceKey, orderedTiles[i]);
        }
    }

    // Reads the hand from scanned slot faces when every visible slot resolves.
    // Icon-bearing faces decode DIRECTLY (no learning); non-icon faces go through the
    // learned map. Overrides the AtkValues/hover sources — the node tree is visual
    // ground truth, works in Layout 1, and matches on-screen slot order (highlight-accurate).
    private void TryNodeHandRead(List<ScannedTileSlot> slots, int liveEventType)
    {
        if (liveEventType is 29 or 32)
            return; // post-round screens show the winning hand, not a playable one

        if (slots.Count is < 1 or > 14)
        {
            this.SetNodeReadActive(false);
            return;
        }

        var resolved = this.tileFaceMap.Resolved;
        var tiles = new List<Tile>(slots.Count);
        foreach (var slot in slots)
        {
            if (FaceKeys.TryDecodeIcon(slot.FaceKey, out var iconTile))
            {
                tiles.Add(iconTile);
            }
            else if (slot.FaceKey is not null && resolved.TryGetValue(slot.FaceKey, out var learned))
            {
                tiles.Add(learned);
            }
            else
            {
                this.SetNodeReadActive(false);
                return;
            }
        }

        // The game's hover tooltip is definitionally the truth for a slot; a pooled slot
        // node can keep a stale face texture (drawn-tile slot, 2026-07-05: read the tile
        // drawn two turns earlier). Same-turn hover pairings (hoverHandSlots is cleared
        // on every discard/meld/deal, before slot indexes can shift) override the decode.
        for (var i = 0; i < tiles.Count; i++)
        {
            if (!this.hoverHandSlots.TryGetValue(i, out var hoverTile) || TileHelpers.SameKind(hoverTile, tiles[i]))
                continue;

            var sig = $"{i}:{tiles[i]}→{hoverTile}";
            if (sig != this.staleFaceWarnSig)
            {
                this.staleFaceWarnSig = sig;
                this.pluginLog.Warning($"[FaceMap] Slot {i} face decodes to {tiles[i]} but hover says {hoverTile} — stale slot texture, trusting hover.");
            }

            tiles[i] = hoverTile;
        }

        // The drawn slot (far right of a full hand) is the most stale-prone face. When
        // no hover pairing covers it, prefer lastDrawnTile — but ONLY the authoritative
        // reading (type-5's seat==0 [3]); a non-authoritative type-6-only guess has
        // been caught wrong itself (2026-07-05: type-6 said 7z, the face said 8m, and a
        // live hover confirmed 8m — trusting type-6 there would have been the bug).
        var maxClosed = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count);
        if (this.lastDrawnTileAuthoritative && this.lastDrawnTile is { } drawnTile && tiles.Count == maxClosed
            && !this.hoverHandSlots.ContainsKey(tiles.Count - 1)
            && !TileHelpers.SameKind(tiles[^1], drawnTile))
        {
            var sig = $"draw{tiles.Count - 1}:{tiles[^1]}→{drawnTile}";
            if (sig != this.staleFaceWarnSig)
            {
                this.staleFaceWarnSig = sig;
                this.LogNote($"[FaceMap] Drawn slot face decodes to {tiles[^1]} but the authoritative draw reading said {drawnTile} — stale face, trusting the draw event.");
            }

            tiles[^1] = drawnTile;
        }

        this.SetNodeReadActive(true);
        this.directHandTiles = tiles;
    }

    private void SetNodeReadActive(bool active)
    {
        if (this.nodeReadActive == active)
            return;

        this.nodeReadActive = active;
        this.pluginLog.Info(active
            ? $"[FaceMap] Node-based hand reading ACTIVE ({this.tileFaceMap.ResolvedCount}/{this.tileFaceMap.KeyCount} faces resolved) — hover no longer needed."
            : "[FaceMap] Node-based hand reading inactive (unresolved tile faces); using AtkValues/hover sources.");
    }

    // Decodes every readable tile currently visible in the discard piles.
    // Icon faces decode directly; learned faces resolve via the map; everything else
    // (empty-slot faces, frames) is skipped.
    private List<Tile> DecodePileTiles()
    {
        var tiles = new List<Tile>(70);
        var resolved = this.tileFaceMap.Resolved;
        foreach (var key in this.pileFaceBaseline.Values)
        {
            if (FaceKeys.TryDecodeIcon(key, out var iconTile))
                tiles.Add(iconTile);
            else if (resolved.TryGetValue(key, out var learned))
                tiles.Add(learned);
        }

        return tiles;
    }

    // Per-kind maximum of the scanned pile (ground truth for what's on the table) and
    // the event pile (retains tiles that were called away and left the piles).
    private static List<Tile> MergeDiscardPiles(List<Tile> scanned, List<Tile> events)
    {
        if (scanned.Count == 0)
            return events.ToList();

        Span<int> scannedCounts = stackalloc int[34];
        Span<int> eventCounts = stackalloc int[34];
        foreach (var t in scanned)
            scannedCounts[TileHelpers.ToIndex(t)]++;
        foreach (var t in events)
            eventCounts[TileHelpers.ToIndex(t)]++;

        var result = new List<Tile>(Math.Max(scanned.Count, events.Count));
        for (var kind = 0; kind < 34; kind++)
        {
            // A claim-pending discard renders duplicate copies in the pond scan
            // (live 2026-07-05: six images of one 8s) — no kind ever exceeds 4.
            var count = Math.Min(Math.Max(scannedCounts[kind], eventCounts[kind]), 4);
            for (var i = 0; i < count; i++)
                result.Add(TileHelpers.FromIndex(kind));
        }

        return result;
    }

    // Known prompt button labels under the call panel; "Pass" and the timer text are
    // always present alongside them and carry no information.
    private static List<string> ScanPromptOptions(AtkUnitBase* addon)
    {
        var options = new List<string>(3);
        foreach (var text in EmjScanner.ScanCallButtonTexts(addon))
        {
            var label = text.Trim();
            if (label is "Chi" or "Pon" or "Kan" or "Ron" or "Riichi" or "Tsumo")
                options.Add(label);
        }

        return options;
    }

    // Diff the discard piles against the last Tick's baseline and refresh it.
    // A discard fills exactly one slot (empty face → tile face): that slot's new key
    // identifies the discarded tile — teachable for local discards ([3] is real) and
    // resolvable for opponents once the face map has learned it.
    private (int Changes, string? NewKey) DiffPilesAndUpdateBaseline(AtkUnitBase* addon)
    {
        var current = EmjScanner.ScanPileFaces(addon);
        string? newKey = null;
        var changes = 0;
        foreach (var (ptr, key) in current)
        {
            if (!this.pileFaceBaseline.TryGetValue(ptr, out var oldKey) || oldKey != key)
            {
                changes++;
                newKey = key;
            }
        }

        this.pileFaceBaseline = current;
        return (changes, newKey);
    }

    // ─────────────────────────────────────── ANALYSIS SESSION ───────────────────────────────────────────

    // /mhater analyze: recording session with per-frame node scans (hand slot faces,
    // pile snapshots, call button texts) — the reversing dataset for the Emj addon.
    public void StartAnalysis(string outputPath)
    {
        if (this.isRecording)
        {
            this.pluginLog.Info("[Analyze] Already recording — stop the current session first.");
            return;
        }

        this.isDeepScan = true;
        this.lastDeepSample = DateTime.MinValue;
        this.StartRecording(outputPath);
    }

    public void StopAnalysis()
    {
        this.StopRecording();
        this.tileFaceMap.Save(this.faceMapPath);
    }

    // ─────────────────────────────────────────────── RESET ──────────────────────────────────────────────

    // Debug-API manual override (`/riichi?declared=true|false`) — no reliable automatic
    // detection signal was found live 2026-07-06 (see the isRiichiDeclared field
    // comment). Resets naturally on the next genuine new deal like the real thing would.
    public void SetRiichiDeclared(bool declared) => this.isRiichiDeclared = declared;

    public bool IsRiichiDeclared => this.isRiichiDeclared;

    public void Reset()
    {
        this.CurrentState = null;
        this.lastOutcomeFingerprint = null;
        this.winsThisSession = 0;
        this.lossesThisSession = 0;
        this.currentScore = 25000;
        this.trackedWallRemaining = 70;
        this.trackedRoundWind = Wind.East;
        this.directHandTiles = [];
        this.trackedCalledMelds.Clear();
        this.lastAddedMeldSignature = null;
        this.hoverHandSlots.Clear();
        this.eventDiscardPile.Clear();
        this.callOpportunityTile = null;
        this.lastOpponentDiscard = null;
        this.isCallWindowActive = false;
        this.callWindowOptions = [];
        this.lastDiscardKey = default;
        this.oversizeHandWarned = false;
        this.roundEnded = true;
        this.isRiichiDeclared = false;
    }

    // ─────────────────────────────────────────────── DUMP ───────────────────────────────────────────────

    public void DumpToLog(string outputPath)
    {
        var addonPtr = this.gameGui.GetAddonByName("Emj");
        if (addonPtr.IsNull)
        {
            this.pluginLog.Info("[Dump] Emj addon not open.");
            return;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        var lines = new List<string>(8192);
        lines.Add($"# MahjongHater Emj Dump  {DateTime.Now:O}");
        var rootNode = addon->RootNode;
        lines.Add($"# AddonAddress=0x{addonPtr.Address:X}  X={addon->X}  Y={addon->Y}  Scale={addon->Scale:F3}" +
                  $"  RootNodeX={rootNode->X:F1}  RootNodeY={rootNode->Y:F1}" +
                  $"  AtkValuesCount={addon->AtkValuesCount}  NodeListCount={addon->UldManager.NodeListCount}");
        lines.Add(string.Empty);

        // ── AtkValues with known-slot annotations ──
        lines.Add($"=== AtkValues (count={addon->AtkValuesCount}) ===");
        var slotNotes = new Dictionary<int, string>
        {
            [0]  = "event_type",
            [1]  = "t5=wall_remain | t6=? | t21=tile_count | t29=seat0_delta×100",
            [2]  = "t5=seat(0=local) | t6=drawn_icon_or_seatwind | t15/30=seatwind_icon | t21=seat | t32=round_string",
            [3]  = "t5=discarded_tile_icon",
            [5]  = "t29=win_method_string",
            [6]  = "t15/30='Discard' | t19=Chi_option | t29/32=fu_han_string",
            [7]  = "t19=Pon_option | t29/32=score_div100",
            [8]  = "t19=Ron_option",
            [14] = "count50: 0=Layout1(no_melds) >0=Layout2(has_melds;2=Chi,3=Pon,7=Tsumo) | count109: tile_count(13)",
            [15] = "t15/21=0_or_neg1",
            [16] = "Layout1=dora1 | Layout2=dora1",
            [17] = "Layout1=dora2 | Layout2=-1_sentinel",
            [18] = "Layout1=dora3 | Layout2=closed_tile_count(1-13)",
            [19] = "Layout1=dora4 | Layout2=closed_tile[0]_icon",
            [20] = "Layout1=dora5 | Layout2=closed_tile[1]_icon",
            [21] = "Layout1=dora6 | Layout2=closed_tile[2]_icon",
            [22] = "Layout1=-1_sentinel | count109=4(win/score_screen)",
            [24] = "count109_hand_tile[0]_icon",
            [25] = "count109_hand_tile[1]_icon",
            [26] = "count109_hand_tile[2]_icon",
            [27] = "count109_hand_tile[3]_icon",
            [28] = "count109_hand_tile[4]_icon",
            [29] = "count109_hand_tile[5]_icon",
            [30] = "count109_hand_tile[6]_icon",
            [31] = "count109_hand_tile[7]_icon",
            [32] = "count109_hand_tile[8]_icon",
            [33] = "count109_hand_tile[9]_icon",
            [34] = "count109_hand_tile[10]_icon",
            [35] = "count109_hand_tile[11]_icon",
            [36] = "count109_hand_tile[12]_icon",
            [37] = "count109_hand_tile[13]_icon(draw_tile;0_during_discard_phase)",
            [39] = "-1(win_screen) | 5(active_game?)",
            [40] = "unknown",
            [41] = "t32=winning_tile_icon",
            [42] = "t32=fu_count?",
            [43] = "yaku_1_name",
            [44] = "yaku_2_name",
            [45] = "yaku_3_name",
        };
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            ref var v = ref addon->AtkValues[i];
            var tileHint = TryTileFromIconId(v.Int, out var t) ? $"  →{t}" : string.Empty;
            var note     = slotNotes.TryGetValue(i, out var n) ? $"  # {n}" : string.Empty;
            lines.Add($"  [{i,3}]  type={v.Type,-14}  int={v.Int,12}  uint={v.UInt,12}  float={v.Float,14:F4}  str={SafeString(ref v)}{tileHint}{note}");
        }

        // ── Game state snapshot ──
        lines.Add(string.Empty);
        lines.Add("=== Game State Snapshot ===");
        lines.Add($"  currentScore          = {this.currentScore}");
        lines.Add($"  trackedWallRemaining  = {this.trackedWallRemaining}");
        lines.Add($"  trackedRoundWind      = {this.trackedRoundWind}");
        lines.Add($"  isCallWindowActive    = {this.isCallWindowActive}");
        lines.Add($"  callWindowOptions     = [{string.Join(", ", this.callWindowOptions)}]");
        lines.Add($"  callOpportunityTile   = {this.callOpportunityTile?.ToString() ?? "null"}");
        lines.Add($"  lastOpponentDiscard   = {this.lastOpponentDiscard?.ToString() ?? "null"}");
        lines.Add($"  wins/losses           = {this.winsThisSession}/{this.lossesThisSession}");
        lines.Add($"  directHandTiles ({this.directHandTiles.Count}): [{string.Join(" ", this.directHandTiles)}]");
        lines.Add($"  eventDiscardPile ({this.eventDiscardPile.Count}): [{string.Join(" ", this.eventDiscardPile)}]");

        // ── Derived hand from live AtkValues[24-37] ──
        lines.Add(string.Empty);
        lines.Add("=== Live AtkValues Hand Read [24-37] ===");
        for (var i = 24; i <= 37 && i < addon->AtkValuesCount; i++)
        {
            ref var v = ref addon->AtkValues[i];
            var tileHint = TryTileFromIconId(v.Int, out var t2) ? $"  →{t2}" : "  (no tile)";
            lines.Add($"  [{i}]  int={v.Int,10}  type={v.Type}{tileHint}");
        }

        // ── Full recursive node tree ──
        lines.Add(string.Empty);
        lines.Add("=== Node Tree (recursive, with component flat-lists) ===");
        if (addon->RootNode != null)
        {
            var visited = new HashSet<nint>();
            DumpNodeRecursive(addon->RootNode, lines, 0, visited);
        }
        else
        {
            lines.Add("  (RootNode is null)");
        }

        // ── Flat addon NodeList (raw index order) ──
        lines.Add(string.Empty);
        lines.Add($"=== Flat Addon NodeList (count={addon->UldManager.NodeListCount}) ===");
        var flatList  = addon->UldManager.NodeList;
        var flatCount = addon->UldManager.NodeListCount;
        for (var ni = 0; ni < flatCount; ni++)
        {
            var node = flatList[ni];
            if (node == null) continue;
            DumpSingleNodeLine(node, lines, $"  [{ni,3}] ");
        }

        // ── Focused: hand tiles + all four discard piles ──
        lines.Add(string.Empty);
        lines.Add("=== Focused Node Exploration ===");
        lines.Add("# Nodes confirmed via Dalamud addon inspector.");
        lines.Add("# NodeID=133  hand tiles (children: #1340001-#1340016 + #135)");
        lines.Add("# NodeID=125  East discard pile");
        lines.Add("# NodeID=122  North discard pile");
        lines.Add("# NodeID=119  West discard pile");
        lines.Add("# NodeID=116  Player discard pile");

        DumpKeyNode(addon, 133, "Hand Tiles",      lines);
        DumpKeyNode(addon, 125, "Discard East",    lines);
        DumpKeyNode(addon, 122, "Discard North",   lines);
        DumpKeyNode(addon, 119, "Discard West",    lines);
        DumpKeyNode(addon, 116, "Discard Player",  lines);

        // ── Node-scan engine view: hand slot faces, call buttons, learned map ──
        lines.Add(string.Empty);
        lines.Add("=== Node Scan: Hand Slots (visual order) ===");
        var scanSlots = EmjScanner.ScanHandSlots(addon);
        var resolvedFaces = this.tileFaceMap.Resolved;
        for (var si = 0; si < scanSlots.Count; si++)
        {
            var slot = scanSlots[si];
            var tile = slot.FaceKey is not null && resolvedFaces.TryGetValue(slot.FaceKey, out var rt) ? rt.ToString() : "?";
            var face = EmjScanner.DescribeFace((AtkResNode*)slot.NodePtr);
            lines.Add($"  slot[{si,2}]  NodeID={slot.NodeId,10}  absX={slot.AbsX,7:F1}  key={slot.FaceKey ?? "-",-14} → {tile,-3}  {face}");
        }

        lines.Add(string.Empty);
        lines.Add("=== Node Scan: Call Window Texts (NodeID=104 subtree) ===");
        var callTexts = EmjScanner.ScanCallButtonTexts(addon);
        lines.Add(callTexts.Count == 0 ? "  (none visible)" : $"  [{string.Join("][", callTexts)}]");

        lines.Add(string.Empty);
        lines.Add($"=== Tile Face Map ({this.tileFaceMap.ResolvedCount}/{this.tileFaceMap.KeyCount} resolved, nodeRead={(this.nodeReadActive ? "ACTIVE" : "off")}) ===");
        foreach (var entry in this.tileFaceMap.DescribeEntries())
            lines.Add($"  {entry}");

        var text = string.Join("\n", lines);
        System.IO.File.WriteAllText(outputPath, text);
        this.pluginLog.Info($"[Dump] {lines.Count} lines → {outputPath}");
    }

    // Finds the node with nodeId in the addon's flat NodeList.
    private static AtkResNode* FindNodeById(AtkUnitBase* addon, uint nodeId)
    {
        var list  = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var n = list[i];
            if (n != null && n->NodeId == nodeId) return n;
        }
        return null;
    }

    // Focused dump of a key node (e.g. hand-tile container or discard-pile container).
    // Walks the direct ChildNode chain so every tile slot is listed in visual order.
    // For each slot that is a component (type ≥ 1000), dives into the component's ULD NodeList
    // and captures image-part data (asset IDs, UVs, active part) and text content — the data
    // needed to map PartId/UV → tile identity.
    private static void DumpKeyNode(AtkUnitBase* addon, uint nodeId, string label, List<string> lines)
    {
        lines.Add(string.Empty);
        lines.Add($"-- NodeID={nodeId} ({label}) --");

        var node = FindNodeById(addon, nodeId);
        if (node == null)
        {
            lines.Add("  NOT FOUND in flat NodeList.");
            return;
        }

        var vis = node->IsVisible() ? "V" : " ";
        lines.Add($"  [{vis}] type={(ushort)node->Type}({node->Type}) " +
                  $"addr=0x{(nint)node:X}  X={node->X:F1} Y={node->Y:F1}  W={node->Width} H={node->Height}");

        // Components keep their internals in their ULD manager, not in ChildNode links —
        // dump those directly so ?node=<tile slot> shows the face images and textures.
        if ((ushort)node->Type >= 1000)
        {
            DumpTileSlot(node, 0, lines, "  ");
            return;
        }

        // Collect children via PrevSiblingNode (FFXIV: ChildNode = last-added/topmost child;
        // PrevSiblingNode walks toward the first child). NextSiblingNode is the fallback.
        var seen     = new HashSet<nint>();
        var children = new List<(nint Addr, float AbsX)>();
        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            if (seen.Add((nint)c)) children.Add(((nint)c, NodeAbsX(c)));
        for (var c = node->ChildNode; c != null; c = c->NextSiblingNode)
            if (seen.Add((nint)c)) children.Add(((nint)c, NodeAbsX(c)));

        lines.Add($"  children found: {children.Count}");

        // Sort by absolute X so slots read left-to-right in the output.
        children.Sort((a, b) => a.AbsX.CompareTo(b.AbsX));

        var slotIdx = 0;
        foreach (var (addr, _) in children)
            DumpTileSlot((AtkResNode*)addr, slotIdx++, lines, "  ");
    }

    // Computes absolute X by summing up the ParentNode chain.
    private static float NodeAbsX(AtkResNode* node)
    {
        var x = 0f;
        for (var n = node; n != null; n = n->ParentNode) x += n->X;
        return x;
    }

    // Dumps a single tile-slot node with its component internals.
    // Compact tint description — a greyed-out (non-selectable) tile is expected to
    // render via a color/multiply effect rather than a chain/visibility change (both
    // were confirmed unchanged live 2026-07-06 across a riichi-locked hand's greyed
    // tiles vs its one selectable drawn tile).
    private static string DescribeTint(AtkResNode* node)
        => $"Col=({node->Color.R},{node->Color.G},{node->Color.B},{node->Color.A}) " +
           $"Mul=({node->MultiplyRed},{node->MultiplyGreen},{node->MultiplyBlue}) " +
           $"Add=({node->AddRed},{node->AddGreen},{node->AddBlue})";

    private static void DumpTileSlot(AtkResNode* node, int slotIdx, List<string> lines, string prefix)
    {
        if (node == null) return;
        var vis = node->IsVisible() ? "V" : " ";
        lines.Add($"{prefix}[{vis}] slot[{slotIdx,2}] NodeID={node->NodeId,10}  " +
                  $"type={(ushort)node->Type,5}  " +
                  $"X={node->X,6:F1} Y={node->Y,6:F1}  W={node->Width,4} H={node->Height,4}  A={node->Alpha_2,3} Rot={node->Rotation:F3} " +
                  DescribeTint(node));

        if ((ushort)node->Type < 1000)
        {
            // Simple/Res node: check for image/text on it directly, then recurse into
            // its children — Res nodes are often intermediate containers (e.g. NodeID=128
            // holds the 13 main tile-slot components as children).
            DumpImageAndText(node, lines, prefix + "  ");
            var subIdx = 0;
            var subSeen = new HashSet<nint>();
            for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
                if (subSeen.Add((nint)c)) DumpTileSlot(c, subIdx++, lines, prefix + "  ");
            for (var c = node->ChildNode; c != null; c = c->NextSiblingNode)
                if (subSeen.Add((nint)c)) DumpTileSlot(c, subIdx++, lines, prefix + "  ");
            return;
        }

        // Component node — explore its ULD NodeList.
        var comp = ((AtkComponentNode*)node)->Component;
        if (comp == null) { lines.Add($"{prefix}  [component null]"); return; }

        var cList  = comp->UldManager.NodeList;
        var cCount = comp->UldManager.NodeListCount;
        lines.Add($"{prefix}  [comp ULD count={cCount}]");

        for (var ci = 0; ci < cCount && ci < 200; ci++)
        {
            var cn = cList[ci];
            if (cn == null) continue;

            var cVis = cn->IsVisible() ? "V" : " ";
            lines.Add($"{prefix}    [{cVis}] NodeID={cn->NodeId,10}  type={(ushort)cn->Type,5}  " +
                      $"X={cn->X,6:F1} Y={cn->Y,6:F1}  W={cn->Width,4} H={cn->Height,4}  A={cn->Alpha_2,3} Rot={cn->Rotation:F3} " +
                      DescribeTint(cn));

            DumpImageAndText(cn, lines, prefix + "      ");

            // If this is a Res container (e.g. NodeID=2 inside type-1055), recurse into
            // its children — the actual tile-face image node lives one level deeper.
            if ((ushort)cn->Type == 1)
            {
                var childSeen = new HashSet<nint>();
                for (var c = cn->ChildNode; c != null; c = c->PrevSiblingNode)
                {
                    if (!childSeen.Add((nint)c)) continue;
                    var cvVis = c->IsVisible() ? "V" : " ";
                    lines.Add($"{prefix}      [{cvVis}] NodeID={c->NodeId,10}  type={(ushort)c->Type,5}  " +
                              $"X={c->X,6:F1} Y={c->Y,6:F1}  W={c->Width,4} H={c->Height,4}  A={c->Alpha_2,3} Rot={c->Rotation:F3} " +
                              DescribeTint(c));
                    DumpImageAndText(c, lines, prefix + "        ");
                }
            }
        }
    }

    // Emits image-parts data and/or text content for a single node.
    // Image: every part listed with assetId, UV, size, tile hint; active part flagged.
    // Text: raw string content.
    private static void DumpImageAndText(AtkResNode* node, List<string> lines, string prefix)
    {
        var imgNode = node->GetAsAtkImageNode();
        if (imgNode != null)
        {
            lines.Add($"{prefix}[IMG] wrapMode={imgNode->WrapMode}  activePart={imgNode->PartId}  faceKey={EmjScanner.BuildFaceKey(imgNode) ?? "-"}");
            if (imgNode->PartsList != null)
            {
                var pCount = imgNode->PartsList->PartCount;
                for (var pi = 0; pi < pCount && pi < 128; pi++)
                {
                    ref var part  = ref imgNode->PartsList->Parts[pi];
                    var assetId   = part.UldAsset != null ? part.UldAsset->Id : 0u;
                    var tileHint  = TryTileFromIconId((int)assetId, out var t) ? $"  →{t}" : string.Empty;
                    var activeTag = pi == imgNode->PartId ? "  <<<ACTIVE" : string.Empty;
                    lines.Add($"{prefix}  part[{pi,3}]  asset={assetId,10}  " +
                              $"UV=({part.U,4},{part.V,4})  {part.Width,3}×{part.Height,-3}{tileHint}{activeTag}");
                    if (pi == imgNode->PartId)
                        lines.Add($"{prefix}    tex: {EmjScanner.DescribeTexture(part.UldAsset)}");
                }
            }
            return;
        }

        var txtNode = node->GetAsAtkTextNode();
        if (txtNode != null)
        {
            string text;
            try { text = Marshal.PtrToStringUTF8((nint)(byte*)txtNode->NodeText.StringPtr) ?? "(null)"; }
            catch { text = "(err)"; }
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{prefix}[TXT]  \"{text}\"  fontSize={txtNode->FontSize}");
        }
    }

    private static void DumpNodeRecursive(AtkResNode* node, List<string> lines, int depth, HashSet<nint> visited)
    {
        if (node == null) return;
        var addr = (nint)node;
        if (!visited.Add(addr))
        {
            lines.Add($"{Indent(depth)}(already-visited 0x{addr:X})");
            return;
        }

        DumpSingleNodeLine(node, lines, Indent(depth));

        if ((ushort)node->Type >= 1000)
        {
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp != null)
            {
                // Recurse the component's own ULD tree
                if (comp->UldManager.RootNode != null)
                {
                    lines.Add($"{Indent(depth + 1)}[comp-tree type={(ushort)node->Type}]");
                    DumpNodeRecursive(comp->UldManager.RootNode, lines, depth + 2, visited);
                }

                // Also dump the component's flat NodeList (catches nodes not in the tree path)
                var cList  = comp->UldManager.NodeList;
                var cCount = comp->UldManager.NodeListCount;
                if (cCount > 0 && cList != null)
                {
                    lines.Add($"{Indent(depth + 1)}[comp-flat count={cCount}]");
                    for (var ci = 0; ci < cCount && ci < 2000; ci++)
                    {
                        var cn = cList[ci];
                        if (cn == null) continue;
                        // Only print nodes not yet visited to keep output manageable
                        if (visited.Contains((nint)cn)) continue;
                        DumpSingleNodeLine(cn, lines, Indent(depth + 2));
                    }
                }
            }
        }

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
            DumpNodeRecursive(child, lines, depth + 1, visited);
    }

    private static void DumpSingleNodeLine(AtkResNode* node, List<string> lines, string prefix)
    {
        if (node == null) return;
        var vis = node->IsVisible() ? "V" : " ";
        lines.Add(
            $"{prefix}[{vis}] NodeId={node->NodeId,5} Type={(ushort)node->Type,5}({node->Type,-15}) " +
            $"X={node->X,7:F1} Y={node->Y,7:F1} W={node->Width,5} H={node->Height,5} " +
            $"Sc={node->ScaleX:F2}/{node->ScaleY:F2} A={node->Alpha_2,3} Rot={node->Rotation:F3} " +
            $"Flags={node->NodeFlags}");

        var imgNode = node->GetAsAtkImageNode();
        if (imgNode != null)
        {
            lines.Add($"{prefix}  [IMG] partId={imgNode->PartId} wrapMode={imgNode->WrapMode}");
            if (imgNode->PartsList != null)
            {
                var pCount = imgNode->PartsList->PartCount;
                for (var pi = 0; pi < pCount && pi < 64; pi++)
                {
                    ref var part   = ref imgNode->PartsList->Parts[pi];
                    var assetId    = part.UldAsset != null ? part.UldAsset->Id : 0u;
                    var tileHint   = TryTileFromIconId((int)assetId, out var t) ? $"  →{t}" : string.Empty;
                    var active     = pi == imgNode->PartId ? " <<<" : string.Empty;
                    lines.Add($"{prefix}    part[{pi,3}] assetId={assetId,10} UV=({part.U,4},{part.V,4}) {part.Width,3}×{part.Height,-3}{tileHint}{active}");
                }
            }
            return; // image nodes have no text
        }

        var txtNode = node->GetAsAtkTextNode();
        if (txtNode != null)
        {
            string text;
            try { text = Marshal.PtrToStringUTF8((nint)(byte*)txtNode->NodeText.StringPtr) ?? "(null)"; }
            catch { text = "(err)"; }
            lines.Add($"{prefix}  [TXT] \"{text}\"  fontSize={txtNode->FontSize} align={txtNode->AlignmentFontType} spacing=L{txtNode->LineSpacing}/C{txtNode->CharSpacing}");
        }
    }

    private static string Indent(int depth) => new(' ', depth * 2);

    // ───────────────────────────────────────────── DEBUG API ────────────────────────────────────────────
    //
    // Snapshot builders for the /mhater debug HTTP endpoint. MUST run on the framework
    // thread (they touch game memory); the plugin marshals requests there. Everything
    // returned is a plain serializable shape (strings/numbers/lists/dictionaries).

    // ── Event timeline (/events) ──

    // Appends one line to the debug timeline. Framework-thread only (all callers are
    // AddonLifecycle handlers, Tick, or marshaled debug requests).
    internal void DebugNote(string message)
    {
        if (this.debugEventRing.Count >= DebugEventRingCap)
            this.debugEventRing.Dequeue();
        this.debugEventRing.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    // Tracker decision visible in both the Dalamud log and the /events timeline.
    private void LogNote(string message)
    {
        this.pluginLog.Information(message);
        this.DebugNote(message);
    }

    // Raw addon refresh event: type + the leading AtkValues with tile-icon hints.
    private void NoteRefreshEvent(AtkUnitBase* addon, int eventType)
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append("evt type=").Append(eventType).Append(" n=").Append(addon->AtkValuesCount);
        var max = Math.Min((int)addon->AtkValuesCount, 9);
        for (var i = 1; i < max; i++)
        {
            ref var v = ref addon->AtkValues[i];
            sb.Append(" [").Append(i).Append("]=");
            if (v.Type is AtkValueType.String or AtkValueType.ConstString)
                sb.Append('"').Append(SafeString(ref v)).Append('"');
            else
                sb.Append(v.Int);
            if (TryTileFromIconId(v.Int, out var hint))
                sb.Append('→').Append(hint);
        }

        this.DebugNote(sb.ToString());
    }

    public List<string> BuildDebugEvents(int tail)
        => this.debugEventRing.TakeLast(Math.Clamp(tail, 1, DebugEventRingCap)).ToList();

    // ── Operate endpoints (framework-thread wrappers around EmjOperator) ──

    public Dictionary<string, object?> DebugListAddons()
        => new() { ["addons"] = EmjOperator.ListAddons() };

    public Dictionary<string, object?> DebugListNodes(string addonName)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        result["interactables"] = EmjOperator.ListInteractables(addon);
        return result;
    }

    public Dictionary<string, object?> DebugFireEvent(string addonName, string nodeSpec, int eventType, int? param)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var node = EmjOperator.FindNodeBySpec(addon, nodeSpec);
        if (node == null)
        {
            result["error"] = $"node '{nodeSpec}' not found in {addonName}";
            return result;
        }

        var fired = EmjOperator.FireEvent(addon, node, (ushort)eventType, param);
        this.LogNote($"op fire {addonName} node={nodeSpec}: {fired}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    public Dictionary<string, object?> DebugClickNode(string addonName, string nodeSpec, int? param)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var node = EmjOperator.FindNodeBySpec(addon, nodeSpec);
        if (node == null)
        {
            result["error"] = $"node '{nodeSpec}' not found in {addonName}";
            return result;
        }

        var fired = EmjOperator.ClickNode(addon, node, param);
        this.LogNote($"op click {addonName} node={nodeSpec}: {string.Join("; ", fired)}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    public Dictionary<string, object?> DebugClickLabel(string addonName, string label)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var clicked = EmjOperator.ClickByLabel(addon, label);
        if (clicked is null)
        {
            result["error"] = $"no visible text matching '{label}' in {addonName}";
            return result;
        }

        this.LogNote($"op call \"{label}\" → {clicked["matchedText"]} {clicked["target"]}: {string.Join("; ", (List<string>)clicked["fired"]!)}");
        result["clicked"] = clicked;
        this.AppendOperateState(result);
        return result;
    }

    // Selects a list row by index (visual order) via the list's addon-bound ListItemClick.
    public Dictionary<string, object?> DebugListClick(string addonName, string nodeSpec, int index)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var listNode = EmjOperator.FindNodeBySpec(addon, nodeSpec);
        if (listNode == null)
        {
            result["error"] = $"node '{nodeSpec}' not found in {addonName}";
            return result;
        }

        var renderer = EmjOperator.ListRendererAt(listNode, index);
        var fired = EmjOperator.SelectListItem(addon, listNode, renderer, index);
        if (fired is null)
        {
            result["error"] = $"node '{nodeSpec}' has no registered ListItemClick";
            return result;
        }

        this.LogNote($"op listclick {addonName} node={nodeSpec} index={index}: {fired}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    public Dictionary<string, object?> DebugFireCallback(string addonName, int[] values)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;

        var fired = EmjOperator.FireCallback(addon, values);
        this.LogNote($"op {addonName} {fired}");
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    // Discards by visual slot index or tile name ("8s"): clicks the hand slot node.
    public Dictionary<string, object?> DebugDiscard(int? slot, string? tileName)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName("Emj", result);
        if (addon == null)
            return result;

        var slots = EmjScanner.ScanHandSlots(addon);
        var index = slot ?? -1;
        if (index < 0 && tileName is not null)
        {
            Tile wanted;
            try
            {
                wanted = Tile.Parse(tileName);
            }
            catch (Exception ex)
            {
                result["error"] = $"unparseable tile '{tileName}': {ex.Message}";
                return result;
            }

            for (var i = 0; i < slots.Count; i++)
            {
                if (this.TryDecodeSlot(slots[i], out var decoded) && TileHelpers.SameKind(decoded, wanted))
                {
                    index = i;
                    break;
                }
            }

            // Far-right face can be stale; the tracked hand (draw-event corrected)
            // knows what the drawn slot really holds.
            if (index < 0 && this.directHandTiles.Count == slots.Count && slots.Count > 0
                && TileHelpers.SameKind(this.directHandTiles[^1], wanted))
                index = slots.Count - 1;

            if (index < 0)
            {
                result["error"] = $"no scanned slot decodes to {wanted}";
                return result;
            }
        }

        if (index < 0 || index >= slots.Count)
        {
            result["error"] = $"slot {index} out of range (0-{slots.Count - 1})";
            return result;
        }

        var tile = this.TryDecodeSlot(slots[index], out var t) ? t.ToString() : "?";
        if (!EmjOperator.HasAddonBoundActivation(addon, (AtkResNode*)slots[index].NodePtr))
        {
            result["error"] = $"slot {index} ({tile}) has no addon-bound activation — "
                + "not discardable right now (claim window open / not your turn / ghost slot)";
            return result;
        }

        // Registered chain params are authoritative (a slot's ButtonClick param is
        // slot+15, NOT the slot index — live-confirmed) — never override them.
        var fired = EmjOperator.ClickNode(addon, (AtkResNode*)slots[index].NodePtr, null);
        this.LogNote($"op discard slot={index} ({tile}): {string.Join("; ", fired)}");
        result["slot"] = index;
        result["tile"] = tile;
        result["fired"] = fired;
        this.AppendOperateState(result);
        return result;
    }

    // Hovers a hand slot (MouseOver only) and returns the addon's tile-name response —
    // the highest-confidence read of what a slot really holds.
    public Dictionary<string, object?> DebugHoverSlot(int slot)
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName("Emj", result);
        if (addon == null)
            return result;

        var slots = EmjScanner.ScanHandSlots(addon);
        if (slot < 0 || slot >= slots.Count)
        {
            result["error"] = $"slot {slot} out of range (0-{slots.Count - 1})";
            return result;
        }

        var fired = EmjOperator.FireEvent(addon, (AtkResNode*)slots[slot].NodePtr, (ushort)AtkEventType.MouseOver, slot);
        var name = addon->AtkValuesCount >= 2 ? SafeString(ref addon->AtkValues[1]) : "-";
        this.DebugNote($"op hover slot={slot}: {fired} → \"{name}\"");
        result["slot"] = slot;
        result["fired"] = fired;
        result["atkValues1"] = name;
        result["hoverTracked"] = this.hoverHandSlots.TryGetValue(slot, out var hoverTile) ? hoverTile.ToString() : null;
        return result;
    }

    // Post-action snapshot so every operate response doubles as a state read.
    private void AppendOperateState(Dictionary<string, object?> result)
    {
        result["trackedHand"] = this.directHandTiles.Select(t => t.ToString()).ToList();
        result["callWindowActive"] = this.isCallWindowActive;
        result["offeredTile"] = this.callOpportunityTile?.ToString();
    }

    private AtkUnitBase* GetDebugAddonByName(string addonName, Dictionary<string, object?> result)
    {
        var addonPtr = this.gameGui.GetAddonByName(addonName);
        if (addonPtr.IsNull)
        {
            result["error"] = $"{addonName} addon not open";
            return null;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->RootNode == null)
        {
            result["error"] = $"{addonName} addon not ready";
            return null;
        }

        return addon;
    }

    public Dictionary<string, object?> BuildDebugStatus()
    {
        return new Dictionary<string, object?>
        {
            ["emjOpen"] = !this.gameGui.GetAddonByName("Emj").IsNull,
            ["inGame"] = this.CurrentState?.InGame ?? false,
            ["trackedHand"] = this.directHandTiles.Select(t => t.ToString()).ToList(),
            ["melds"] = this.trackedCalledMelds.Select(m => $"{m.Type}[{string.Join(" ", m.Tiles)}]").ToList(),
            ["wall"] = this.trackedWallRemaining,
            ["eventPileCount"] = this.eventDiscardPile.Count,
            ["scannedPileCount"] = this.scannedPileTiles.Count,
            ["roundEnded"] = this.roundEnded,
            ["nodeReadActive"] = this.nodeReadActive,
            ["callWindowActive"] = this.isCallWindowActive,
            ["callOptions"] = this.callWindowOptions.ToList(),
            ["offeredTile"] = this.callOpportunityTile?.ToString(),
            ["isRiichiDeclared"] = this.isRiichiDeclared,
            ["faceMap"] = $"{this.tileFaceMap.ResolvedCount}/{this.tileFaceMap.KeyCount}",
            ["recording"] = this.isRecording,
            ["deepScan"] = this.isDeepScan,
            ["analysis"] = this.AnalysisSummaryProvider?.Invoke(),
        };
    }

    public Dictionary<string, object?> BuildDebugHand()
    {
        var result = new Dictionary<string, object?>
        {
            ["trackedHand"] = this.directHandTiles.Select(t => t.ToString()).ToList(),
            ["frozenByPrompt"] = this.isCallWindowActive,
            ["nodeReadActive"] = this.nodeReadActive,
            ["hoverSlots"] = this.hoverHandSlots.OrderBy(kv => kv.Key)
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString()),
        };

        var addon = this.GetDebugAddon(result);
        if (addon == null)
            return result;

        var resolved = this.tileFaceMap.Resolved;
        var scanned = EmjScanner.ScanHandSlots(addon);
        var slotRows = new List<Dictionary<string, object?>>(scanned.Count);
        for (var i = 0; i < scanned.Count; i++)
        {
            var s = scanned[i];
            var icon = FaceKeys.TryDecodeIcon(s.FaceKey, out var iconTile) ? iconTile.ToString() : null;
            var learned = s.FaceKey is not null && resolved.TryGetValue(s.FaceKey, out var mapTile) ? mapTile.ToString() : null;
            slotRows.Add(new Dictionary<string, object?>
            {
                ["slot"] = i,
                ["nodeId"] = s.NodeId,
                ["absX"] = (int)s.AbsX,
                ["icon"] = icon,
                ["learned"] = learned,
                ["hover"] = this.hoverHandSlots.TryGetValue(i, out var hoverTile) ? hoverTile.ToString() : null,
                ["faceKey"] = s.FaceKey,
                ["faceDetail"] = EmjScanner.DescribeFace((AtkResNode*)s.NodePtr),
            });
        }

        result["scannedSlots"] = slotRows;
        result["atkRead"] = ReadHandTilesFromAtkValues(addon).Select(t => t.ToString()).ToList();
        result["eventType0"] = addon->AtkValues[0].Int;
        result["layout14"] = addon->AtkValuesCount >= 15 ? addon->AtkValues[14].Int : null;
        result["sentinel17"] = addon->AtkValuesCount >= 18 ? addon->AtkValues[17].Int : null;
        return result;
    }

    public Dictionary<string, object?> BuildDebugPiles()
    {
        var result = new Dictionary<string, object?>
        {
            ["eventPile"] = this.eventDiscardPile.Select(t => t.ToString()).ToList(),
        };

        var addon = this.GetDebugAddon(result);
        if (addon == null)
            return result;

        var faces = EmjScanner.ScanPileFaces(addon);
        var resolved = this.tileFaceMap.Resolved;
        result["scannedSlots"] = faces.Count;
        result["faces"] = faces.Values.GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g =>
        {
            var tile = FaceKeys.TryDecodeIcon(g.Key, out var iconTile) ? iconTile.ToString()
                : resolved.TryGetValue(g.Key, out var mapTile) ? mapTile.ToString() : null;
            return new Dictionary<string, object?> { ["key"] = g.Key, ["count"] = g.Count(), ["tile"] = tile };
        }).ToList();

        var decoded = new List<string>();
        foreach (var key in faces.Values)
        {
            if (FaceKeys.TryDecodeIcon(key, out var t))
                decoded.Add(t.ToString());
            else if (resolved.TryGetValue(key, out var learned))
                decoded.Add(learned.ToString());
        }

        decoded.Sort(StringComparer.Ordinal);
        result["decodedPile"] = decoded;
        result["mergedPile"] = this.CurrentState?.DiscardPile.Select(t => t.ToString()).ToList();
        return result;
    }

    public Dictionary<string, object?> BuildDebugFrame(string addonName = "Emj")
    {
        var result = new Dictionary<string, object?>();
        var addon = this.GetDebugAddonByName(addonName, result);
        if (addon == null)
            return result;
        if (addon->AtkValues == null)
        {
            result["error"] = $"{addonName} has no AtkValues";
            return result;
        }

        result["atkValuesCount"] = (int)addon->AtkValuesCount;
        result["position"] = $"X={addon->X} Y={addon->Y} scale={addon->Scale:F2}";
        var rows = new List<string>((int)addon->AtkValuesCount);
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            ref var v = ref addon->AtkValues[i];
            var tileHint = TryTileFromIconId(v.Int, out var t) ? $"  →{t}" : string.Empty;
            rows.Add($"[{i,3}] {v.Type,-12} int={v.Int,11}  str={SafeString(ref v)}{tileHint}");
        }

        result["values"] = rows;
        return result;
    }

    public Dictionary<string, object?> BuildDebugPrompt()
    {
        var result = new Dictionary<string, object?>
        {
            ["active"] = this.isCallWindowActive,
            ["options"] = this.callWindowOptions.ToList(),
            ["labelSignature"] = this.lastPromptSignature,
            ["offeredTile"] = this.callOpportunityTile?.ToString(),
            ["lastOpponentDiscard"] = this.lastOpponentDiscard?.ToString(),
            ["secondsSinceActive"] = Math.Round((DateTime.UtcNow - this.lastPromptActiveUtc).TotalSeconds, 1),
            ["secondsSinceDiscardEvent"] = Math.Round((DateTime.UtcNow - this.lastDiscardEventUtc).TotalSeconds, 1),
            ["secondsSinceLocalDraw"] = Math.Round((DateTime.UtcNow - this.lastLocalDrawUtc).TotalSeconds, 1),
            ["lastDiscardEventKind"] = this.lastDiscardEventKind?.ToString(),
        };

        var addon = this.GetDebugAddon(result);
        if (addon == null)
            return result;

        result["buttonTextsRaw"] = EmjScanner.ScanCallButtonTexts(addon);

        // Ghost-slot view: during a claim window the hand row shows one slot more than
        // the player holds; the far-right slot is normally the claimable tile, but its
        // face can be stale (2026-07-05: ghost read 8m while a fresh type-8 truth said
        // 2m — the real gate already applies this override; farRightCallable must use
        // the SAME corrected identity or it reports a false "not callable" for a window
        // the gate legitimately activated).
        var slots = EmjScanner.ScanHandSlots(addon);
        result["handSlotCount"] = slots.Count;
        result["expectedClosed"] = HandTracking.MaxClosedTiles(this.trackedCalledMelds.Count) - 1;
        if (slots.Count > 0 && this.TryDecodeSlot(slots[^1], out var farRight))
        {
            result["farRightSlot"] = farRight.ToString();
            // No elapsed-time gate — see the matching comment in OnClaimPromptEdge; the
            // flag alone (not a wall-clock re-check) is the correct validity signal.
            var claimable = this.lastDiscardKindAuthoritative && this.lastDiscardEventKind is { } authKind ? authKind : farRight;
            result["claimableTile"] = claimable.ToString();
            result["farRightCallable"] = this.HasAnyLegalLocalCall(claimable);
        }
        else
        {
            result["farRightSlot"] = null;
            result["claimableTile"] = null;
            result["farRightCallable"] = null;
        }

        return result;
    }

    // Full annotated node tree (or a focused subtree via ?node=<id>), as plain text lines.
    public List<string> BuildDebugTree(uint? nodeId, string addonName = "Emj")
    {
        var lines = new List<string>(4096);
        var addonPtr = this.gameGui.GetAddonByName(addonName);
        if (addonPtr.IsNull)
        {
            lines.Add($"{addonName} addon not open.");
            return lines;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->RootNode == null)
        {
            lines.Add("Addon has no root node.");
            return lines;
        }

        if (nodeId is { } id)
        {
            DumpKeyNode(addon, id, $"node {id}", lines);
        }
        else
        {
            var visited = new HashSet<nint>();
            DumpNodeRecursive(addon->RootNode, lines, 0, visited);
        }

        return lines;
    }

    private AtkUnitBase* GetDebugAddon(Dictionary<string, object?> result)
    {
        var addonPtr = this.gameGui.GetAddonByName("Emj");
        if (addonPtr.IsNull)
        {
            result["error"] = "Emj addon not open";
            return null;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->RootNode == null || addon->AtkValues == null || addon->AtkValuesCount < 1)
        {
            result["error"] = "Emj addon not ready";
            return null;
        }

        return addon;
    }

    // ────────────────────────────────────────────── HELPERS ─────────────────────────────────────────────

    // Maps tile icon IDs 76041–76074 (34 base tile types) to Tile structs.
    //   76041-76049: 1m-9m  |  76050-76058: 1p-9p  |  76059-76067: 1s-9s
    //   76068-76071: East-North winds  |  76072-76074: Haku/Hatsu/Chun
    private static bool TryTileFromIconId(int iconId, out Tile tile)
        => TileHelpers.TryTileFromIconId(iconId, out tile);

    // [2] holds seat-wind icon ID (76068-76071) for stable-state events.
    // Types 5 and 6 use [2] for different data so are excluded.
    private static Wind? ReadSeatWindFromAtkValues(AtkUnitBase* addon)
    {
        if (addon->AtkValuesCount < 3 || addon->AtkValues == null) return null;
        var eventType = addon->AtkValues[0].Int;
        if (eventType is 5 or 6) return null;
        return addon->AtkValues[2].Int switch
        {
            76068 => Wind.East,
            76069 => Wind.South,
            76070 => Wind.West,
            76071 => Wind.North,
            _ => null,
        };
    }

    // [16-21] hold dora indicator icon IDs.
    private static List<Tile> ReadDoraIconsFromAtkValues(AtkUnitBase* addon)
    {
        var doras = new List<Tile>(6);
        if (addon->AtkValuesCount < 22 || addon->AtkValues == null) return doras;
        for (var i = 16; i <= 21 && i < addon->AtkValuesCount; i++)
        {
            if (TryTileFromIconId(addon->AtkValues[i].Int, out var t))
                doras.Add(t);
        }
        return doras;
    }

    // Best-effort round wind from visible text nodes (wind kanji or English wind words).
    private static Wind? ReadRoundWindFromTextNodes(AtkUnitBase* addon)
    {
        var nodeList  = addon->UldManager.NodeList;
        var nodeCount = addon->UldManager.NodeListCount;
        for (var ni = 0; ni < nodeCount; ni++)
        {
            var node = nodeList[ni];
            if (node == null || !node->IsVisible()) continue;
            var textNode = node->GetAsAtkTextNode();
            if (textNode == null) continue;
            if (!TryReadText(textNode, out var snap)) continue;

            var tx = snap.Text;
            if (tx.Contains("East",  StringComparison.OrdinalIgnoreCase) || tx.Contains("東")) return Wind.East;
            if (tx.Contains("South", StringComparison.OrdinalIgnoreCase) || tx.Contains("南")) return Wind.South;
            if (tx.Contains("West",  StringComparison.OrdinalIgnoreCase) || tx.Contains("西")) return Wind.West;
            if (tx.Contains("North", StringComparison.OrdinalIgnoreCase) || tx.Contains("北")) return Wind.North;
        }
        return null;
    }

    private static bool TryReadText(AtkTextNode* textNode, out TextNodeSnapshot snapshot)
    {
        snapshot = default;
        var raw = Marshal.PtrToStringUTF8((nint)(byte*)textNode->NodeText.StringPtr);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        snapshot = new TextNodeSnapshot(textNode->NodeId, raw.Trim(), textNode->X, textNode->Y);
        return true;
    }

    private static string SafeString(ref AtkValue v)
    {
        if (v.Type is not (AtkValueType.String or AtkValueType.ConstString))
            return "-";
        try { return v.String.ToString() ?? "(null)"; }
        catch { return $"ptr=0x{v.UInt:X}"; }
    }

    private readonly record struct TextNodeSnapshot(uint NodeId, string Text, float X, float Y);
}

public sealed class GameState
{
    public bool InGame { get; set; }

    public List<Tile> ClosedTiles { get; set; } = [];

    public List<Meld> CalledMelds { get; set; } = [];

    public Wind SeatWind { get; set; } = Wind.East;

    public Wind RoundWind { get; set; } = Wind.East;

    public int CurrentScore { get; set; }

    public int HonbaCount { get; set; }

    public int RiichiSticksCount { get; set; }

    public long MgpEarned { get; set; }

    public int WinsThisSession { get; set; }

    public int LossesThisSession { get; set; }

    public bool IsRiichi { get; set; }

    public int TilesRemainingInWall { get; set; }

    public List<Tile> DiscardPile { get; set; } = [];

    public List<Tile> DoraIndicators { get; set; } = [];

    public List<Tile> UradoraIndicators { get; set; } = [];

    // Tile being offered in the current call window (last opponent discard).
    // Null outside of a call window.
    public Tile? CallOpportunityTile { get; set; }

    // True when the call opportunity window (type-19) is the current active event.
    // While true, normal tile hovering is blocked and the player must choose a call or pass.
    public bool IsCallWindowActive { get; set; }

    // Non-Pass option names from the active call window: "Pon", "Chi", "Kan", "Ron".
    // Empty when IsCallWindowActive is false.
    public List<string> CallWindowOptions { get; set; } = [];
}
