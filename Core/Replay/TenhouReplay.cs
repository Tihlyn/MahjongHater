using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MahjongHater.Core.Simulation;

namespace MahjongHater.Core.Replay;

public sealed record ReplayDecision(int Hand, int Event, int Seat, int Rank, SimAction ObservedAction,
    SimulationObservation Observation, int ObservedHandDelta)
{
    public SimAction[] LegalActions { get; init; } = [];
    public OpponentTrainingTarget[] OpponentTargets { get; init; } = [];
}
// Hidden information is a supervised TARGET only. Never feed this into search,
// runtime features or the student policy; value labels use target Doman rules,
// ordinary (non-red) candidate tiles, and exclude unknown ura and honba.
public sealed record OpponentTrainingTarget(int Seat, bool Tenpai, ulong ShapeWaitMask, bool Furiten, int[] RonPointsExcludingUra);
public sealed record ReplayGame(int Schema, string Source, string Sha256, int GameType, int[] Ranks,
    MatchResult Match, ReplayDecision[] Decisions, Dictionary<string, int> Skipped);
public sealed record TenhouMeld(MeldType Type, int From, int Called, int[] Tiles);

// Replay state follows the recorded events, not a simulated wall or source scoring
// guesses. Only observation projections leave this parser; SHUFFLE, ura, names,
// opponents' concealed tiles and future draws never become decision features.
public static class TenhouReplay
{
    public static Tile Tile136(int id)
    {
        if (id is < 0 or >= 136) throw new InvalidDataException("Tile ID outside 0..135.");
        var tile = TileHelpers.FromIndex(id / 4);
        return id is 16 or 52 or 88 ? new Tile(tile.Suit, 5, true) : tile;
    }

    // Tenhou's packed N-tag bit layout (also documented by MahjongRepository's
    // tenhou decoder). Physical copy IDs preserve the called tile and red fives.
    public static TenhouMeld DecodeMeld(int who, int code)
    {
        if (who is < 0 or > 3 || code is < 0 or > 65535) throw new InvalidDataException("Invalid meld encoding.");
        var from = (who + (code & 3)) % 4;
        if ((code & 4) != 0)
        {
            var packed = code >> 10;
            var start = packed / 3;
            start = start / 7 * 9 + start % 7;
            var tiles = Enumerable.Range(0, 3).Select(i => 4 * (start + i) + ((code >> (3 + 2 * i)) & 3)).ToArray();
            return new TenhouMeld(MeldType.Chi, from, tiles[packed % 3], tiles);
        }
        if ((code & 24) != 0)
        {
            var packed = code >> 9;
            var fourth = (code >> 5) & 3;
            var tiles = Enumerable.Range(0, 4).Where(i => i != fourth).Select(i => 4 * (packed / 3) + i).ToArray();
            return (code & 8) != 0 ? new TenhouMeld(MeldType.Pon, from, tiles[packed % 3], tiles)
                : new TenhouMeld(MeldType.Shouminkan, from, 4 * (packed / 3) + fourth, [.. tiles, 4 * (packed / 3) + fourth]);
        }
        if ((code & 32) != 0) throw new NotSupportedException("Three-player north extraction is not supported.");
        var called = code >> 8;
        return new TenhouMeld(from == who ? MeldType.Ankan : MeldType.Daiminkan, from, called,
            Enumerable.Range(called / 4 * 4, 4).ToArray());
    }

    public static ReplayGame Parse(byte[] xml, SimulationRules targetRules, int minimumRank = 0)
    {
        targetRules.Validate();
        if (minimumRank is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(minimumRank));
        using var input = new MemoryStream(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "mjloggm") throw new InvalidDataException("Expected a Tenhou mjloggm XML document.");
        var events = document.Root.Elements().ToArray();
        var type = Number(events.FirstOrDefault(e => e.Name == "GO") ?? throw new InvalidDataException("Missing GO rules."), "type");
        if ((type & 16) != 0 || (type & 2) != 0 || type > 255)
            throw new NotSupportedException("Only ordinary four-player logs with red fives are supported.");
        if (((type & 4) == 0) != targetRules.Kuitan || ((type & 8) != 0 ? 8 : 4) != targetRules.HandsInMatch)
            throw new NotSupportedException("Source kuitan/game length does not match the target configuration.");
        var ranksTag = events.FirstOrDefault(e => e.Name == "UN" && e.Attribute("dan") is not null);
        var ranks = ranksTag is null ? [-1, -1, -1, -1] : Numbers(ranksTag, "dan", 4);
        var decisions = new List<ReplayDecision>();
        var skipped = new Dictionary<string, int>();
        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;
        HandReplay? hand = null;
        int[]? finalScores = null;
        var initialDealer = -1;
        var handNumber = 0;
        var endings = new Dictionary<HandEnd, int>();
        for (var index = 0; index < events.Length; index++)
        {
            var e = events[index];
            var name = e.Name.LocalName;
            if (name == "INIT")
            {
                if (hand is not null) decisions.AddRange(hand.Finish());
                var dealer = Number(e, "oya");
                if (initialDealer < 0) initialDealer = dealer;
                hand = new HandReplay(e, targetRules, initialDealer, handNumber++, ranks, minimumRank, Skip);
                continue;
            }
            if (name is "GO" or "UN" or "SHUFFLE" or "TAIKYOKU" or "BYE") continue;
            if (hand is null) throw new InvalidDataException($"Event {name} before INIT.");
            if (name is "AGARI" or "RYUUKYOKU")
            {
                var end = name == "AGARI" ? (Number(e, "who") == Number(e, "fromWho") ? HandEnd.Tsumo : HandEnd.Ron)
                    : HandEnd.ExhaustiveDraw;
                if (!hand.Ended) endings[end] = endings.GetValueOrDefault(end) + 1;
                hand.End(e);
                if (e.Attribute("owari") is { } owari)
                {
                    var values = owari.Value.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    if (values.Length != 8) throw new InvalidDataException("Invalid final scores.");
                    finalScores = Enumerable.Range(0, 4).Select(s => checked((int)(values[2 * s] * 100))).ToArray();
                }
                continue;
            }
            if (hand.Ended) throw new InvalidDataException($"Unexpected event {name} after settlement.");
            if (name.Length > 1 && "TUVWDEFGtuvwdefg".Contains(name[0]) && int.TryParse(name.AsSpan(1), out var tile))
            {
                var letter = char.ToUpperInvariant(name[0]);
                var seat = "TUVW".IndexOf(letter);
                if (seat >= 0) hand.Draw(seat, tile);
                else hand.Discard("DEFG".IndexOf(letter), tile, index);
            }
            else if (name == "N") hand.Call(Number(e, "who"), DecodeMeld(Number(e, "who"), Number(e, "m")));
            else if (name == "REACH") hand.Reach(Number(e, "who"), Number(e, "step"));
            else if (name == "DORA") hand.Dora(Number(e, "hai"));
            else throw new NotSupportedException($"Unsupported replay event {name}.");
        }
        if (hand is null || finalScores is null) throw new InvalidDataException("Incomplete match: INIT and final owari settlement are required.");
        decisions.AddRange(hand.Finish());
        var ranking = MatchRunner.Rank(finalScores, initialDealer);
        var match = new MatchResult(handNumber, decisions.Count, finalScores,
            Enumerable.Range(0, 4).Select(s => Array.IndexOf(ranking, s) + 1).ToArray(), initialDealer, endings);
        // Formatting, player display names and shuffle seeds must not put copies
        // of the same game into different training/evaluation partitions.
        var identity = JsonSerializer.SerializeToUtf8Bytes(events.Where(e => e.Name.LocalName is not ("UN" or "SHUFFLE" or "TAIKYOKU" or "BYE"))
            .Select(e => new { Tag = e.Name.LocalName, Attributes = e.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
                .Select(a => new { Name = a.Name.LocalName, a.Value }).ToArray() }).ToArray());
        return new ReplayGame(1, "tenhou-mjlog", Convert.ToHexString(SHA256.HashData(identity)), type, ranks, match, decisions.ToArray(), skipped);
    }

    private static int Number(XElement e, string key) => int.Parse(e.Attribute(key)?.Value
        ?? throw new InvalidDataException($"Missing {e.Name}.{key}."), CultureInfo.InvariantCulture);
    private static int[] Numbers(XElement e, string key, int count)
    {
        var values = (e.Attribute(key)?.Value ?? throw new InvalidDataException($"Missing {e.Name}.{key}."))
            .Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != count) throw new InvalidDataException($"Expected {count} values for {e.Name}.{key}.");
        return values;
    }

    private sealed class HandReplay
    {
        private readonly SimGame game;
        private readonly List<int>[] hands;
        private readonly HashSet<int> revealed = [];
        private readonly List<int> indicators = [];
        private readonly List<ReplayDecision> samples = [];
        private readonly int number, minimumRank, pointTotal;
        private readonly int[] ranks;
        private readonly Action<string> skip;
        private readonly bool[] declaring = new bool[4];
        private int wall = 70, expectedDraw, drawn = -1, pendingDiscard = -1, pendingFrom = -1;
        private bool replacement;
        private int[]? terminalBase, terminalScores;
        public bool Ended => this.terminalScores is not null;

        public HandReplay(XElement init, SimulationRules rules, int initialDealer, int number, int[] ranks, int minimumRank, Action<string> skip)
        {
            var seed = Numbers(init, "seed", 6);
            var scores = Numbers(init, "ten", 4).Select(s => checked(s * 100)).ToArray();
            this.number = number; this.ranks = ranks; this.minimumRank = minimumRank; this.skip = skip;
            this.pointTotal = scores.Sum() + seed[2] * 1000;
            this.game = new SimGame { Rules = rules, Dealer = Number(init, "oya"), InitialDealer = initialDealer,
                RoundIndex = seed[0], Honba = seed[1], RiichiSticks = seed[2], StartingScores = scores, Phase = SimPhase.Turn };
            if (this.game.Dealer is < 0 or > 3 || seed[0] is < 0 or > 15 || seed[1] < 0 || seed[2] < 0)
                throw new InvalidDataException("Invalid INIT context.");
            this.expectedDraw = this.game.Dealer;
            this.hands = Enumerable.Range(0, 4).Select(s => Numbers(init, "hai" + s, 13).ToList()).ToArray();
            var physical = this.hands.SelectMany(h => h).Append(seed[5]).ToArray();
            foreach (var id in physical) Tile136(id);
            if (physical.Distinct().Count() != physical.Length) throw new InvalidDataException("Duplicate physical tile in INIT.");
            this.indicators.Add(seed[5]);
            this.revealed.Add(seed[5]);
            for (var s = 0; s < 4; s++) this.game.Players[s].Score = scores[s];
        }

        public void Draw(int seat, int tile)
        {
            Tile136(tile);
            if (seat != this.expectedDraw || this.wall <= 0 || this.hands.Any(h => h.Contains(tile)) || this.revealed.Contains(tile))
                throw new InvalidDataException("Impossible draw order or physical tile.");
            this.PassedDiscard();
            this.wall--;
            this.hands[seat].Add(tile);
            this.drawn = tile;
            this.game.TurnSeat = seat;
            this.game.DrawnTile = Tile136(tile);
            this.game.Rinshan = this.replacement;
            this.replacement = false;
            this.game.ForbiddenDiscards.Clear();
            this.game.Players[seat].DrawCount++;
            this.game.Players[seat].TemporaryFuriten = false;
            this.expectedDraw = -1;
        }

        public void Discard(int seat, int tile, int eventIndex)
        {
            Tile136(tile);
            var p = this.game.Players[seat];
            if (this.expectedDraw != -1 || seat != this.game.TurnSeat || !this.hands[seat].Contains(tile))
                throw new InvalidDataException("Discard is not in the acting player's hand.");
            this.SynchronizeHands();
            var chosen = SimAction.Make(this.declaring[seat] ? SimActionKind.Riichi : SimActionKind.Discard, Tile136(tile));
            if (this.game.RoundIndex >= this.game.Rules.HandsInMatch) this.skip("source extension round");
            else if (this.ranks[seat] < this.minimumRank && this.minimumRank > 0) this.skip("actor below minimum rank or rank unknown");
            else if (this.indicators.Count != this.game.KanCount + 1) this.skip("source kan-dora reveal still pending");
            else
            {
                this.FillUnknownWall();
                RiichiSimulator.ValidateConservation(this.game, this.pointTotal);
                var legal = RiichiSimulator.Legal(this.game);
                if (!legal.Contains(chosen)) this.skip("recorded action not legal under target rules");
                else if (legal.Count < 2 || legal.Any(a => a.Kind is SimActionKind.Tsumo or SimActionKind.Ron)) this.skip("forced move or available win");
                else this.samples.Add(new ReplayDecision(this.number, eventIndex, seat, this.ranks[seat], chosen,
                    SimulationObservation.Observe(this.game, legal), 0)
                    { LegalActions = legal.ToArray(), OpponentTargets = this.OpponentTargets(seat) });
            }
            if (p.RiichiPaid) p.Ippatsu = false;
            if (this.declaring[seat])
            {
                p.Riichi = true; p.DoubleRiichi = p.River.Count == 0 && !this.game.Interrupted; p.Ippatsu = true;
            }
            this.hands[seat].Remove(tile);
            this.revealed.Add(tile);
            p.River.Add(new SimDiscard(Tile136(tile), this.game.DiscardCounter++, this.declaring[seat], false, tile == this.drawn));
            this.declaring[seat] = false;
            this.pendingDiscard = tile; this.pendingFrom = seat;
            this.drawn = -1; this.game.DrawnTile = null;
            this.game.ForbiddenDiscards.Clear();
            this.expectedDraw = (seat + 1) % 4;
        }

        public void Reach(int seat, int step)
        {
            if (seat is < 0 or > 3) throw new InvalidDataException("Invalid riichi seat.");
            var p = this.game.Players[seat];
            if (step == 1 && seat == this.game.TurnSeat && !p.Riichi) this.declaring[seat] = true;
            else if (step == 2 && p.Riichi && !p.RiichiPaid)
            {
                p.Score -= 1000; p.RiichiPaid = true; this.game.RiichiSticks++;
            }
            else throw new InvalidDataException("Invalid riichi declaration sequence.");
        }

        public void Dora(int tile)
        {
            Tile136(tile);
            if (this.revealed.Contains(tile) || this.hands.Any(h => h.Contains(tile)) || this.indicators.Count >= 5)
                throw new InvalidDataException("Duplicate or impossible dora indicator.");
            this.revealed.Add(tile); this.indicators.Add(tile);
        }

        public void Call(int seat, TenhouMeld meld)
        {
            foreach (var tile in meld.Tiles) Tile136(tile);
            var p = this.game.Players[seat];
            var added = meld.Type == MeldType.Shouminkan;
            var closed = meld.Type == MeldType.Ankan;
            if (closed || added)
            {
                if (this.expectedDraw != -1 || this.game.TurnSeat != seat || this.drawn < 0)
                    throw new InvalidDataException("Self kan outside own draw.");
            }
            else if (this.pendingFrom != meld.From || this.pendingDiscard != meld.Called || seat == meld.From)
                throw new InvalidDataException("Meld does not claim the pending physical discard.");
            if (!closed && !added) this.PassedDiscard();
            var consumed = added ? new[] { meld.Called } : meld.Tiles.Where(t => closed || t != meld.Called).ToArray();
            foreach (var tile in consumed)
            {
                if (!this.hands[seat].Remove(tile)) throw new InvalidDataException("Called tile missing from concealed hand.");
                this.revealed.Add(tile);
            }
            if (!closed && !added)
            {
                var river = this.game.Players[meld.From].River;
                river[^1] = river[^1] with { Claimed = true };
            }
            if (added)
            {
                var index = p.Melds.FindIndex(m => m.Shape.Type == MeldType.Pon && TileHelpers.SameKind(m.Shape.Tiles[0], Tile136(meld.Called)));
                if (index < 0) throw new InvalidDataException("Added kan has no preceding pon.");
                var original = p.Melds[index];
                p.Melds[index] = new SimMeld(SimTiles.Meld(meld.Type, meld.Tiles.Select(Tile136), true), original.FromSeat);
            }
            else p.Melds.Add(new SimMeld(SimTiles.Meld(meld.Type, meld.Tiles.Select(Tile136), !closed), closed ? -1 : meld.From));
            if (!closed && !added && meld.Type != MeldType.Chi)
            {
                if (p.Melds.Count(m => m.Shape.IsOpen && m.Shape.IsTriplet && m.Shape.Tiles[0].Suit == TileSuit.Dragon) == 3) p.DragonLiability = meld.From;
                if (p.Melds.Count(m => m.Shape.IsOpen && m.Shape.IsTriplet && m.Shape.Tiles[0].Suit == TileSuit.Wind) == 4) p.WindLiability = meld.From;
            }
            this.game.Interrupted = true;
            foreach (var other in this.game.Players) other.Ippatsu = false;
            this.game.TurnSeat = seat;
            this.drawn = -1; this.game.DrawnTile = null; this.game.Rinshan = false;
            this.pendingDiscard = -1; this.pendingFrom = -1;
            // A following replacement draw confirms nobody robbed the added kan.
            // Shape-completing passes must still update temporary/riichi furiten.
            if (added) { this.pendingDiscard = meld.Called; this.pendingFrom = seat; }
            this.game.ForbiddenDiscards = meld.Type is MeldType.Chi or MeldType.Pon
                ? RiichiSimulator.Forbidden(SimAction.Make(meld.Type == MeldType.Chi ? SimActionKind.Chi : SimActionKind.Pon,
                    Tile136(meld.Called), consumed.Select(Tile136))) : [];
            this.replacement = meld.Type is MeldType.Ankan or MeldType.Daiminkan or MeldType.Shouminkan;
            this.expectedDraw = this.replacement ? seat : -1;
            if (this.replacement)
            {
                this.game.KanCount++;
                this.game.AbortAfterDiscard = this.game.KanCount == 4 && this.game.Players.Count(o => o.Melds.Any(m => m.Shape.IsKan)) > 1;
            }
        }

        private void PassedDiscard()
        {
            if (this.pendingDiscard < 0) return;
            for (var seat = 0; seat < 4; seat++)
                if (seat != this.pendingFrom && (SimTiles.Waits(this.hands[seat].Select(Tile136), this.game.Players[seat].Melds.Select(m => m.Shape))
                    & (1UL << (this.pendingDiscard / 4))) != 0)
                {
                    this.game.Players[seat].TemporaryFuriten = true;
                    if (this.game.Players[seat].Riichi) this.game.Players[seat].RiichiFuriten = true;
                }
            this.pendingDiscard = -1; this.pendingFrom = -1;
        }

        private void SynchronizeHands()
        {
            for (var seat = 0; seat < 4; seat++) this.game.Players[seat].Hand = this.hands[seat].Select(Tile136).ToList();
        }

        private OpponentTrainingTarget[] OpponentTargets(int viewer)
        {
            var lastDiscardWasRinshan = this.game.LastDiscardWasRinshan;
            this.game.LastDiscardWasRinshan = this.game.Rinshan;
            var targets = Enumerable.Range(1, 3).Select(relative =>
            {
                var seat = (viewer + relative) % 4;
                var player = this.game.Players[seat];
                var waits = SimTiles.Waits(player.Hand, player.Melds.Select(m => m.Shape));
                var furiten = SimScoring.Furiten(player);
                var values = new int[34];
                // This is the loss on a hypothetical discard at the current turn,
                // not a prediction of an actual future win or a rollout EV.
                if (!furiten)
                    for (var kind = 0; kind < 34; kind++)
                        if ((waits & (1UL << kind)) != 0)
                            values[kind] = SimScoring.Win(this.game, seat, TileHelpers.FromIndex(kind), false, includeUra: false)?.Score.RonPayment ?? 0;
                return new OpponentTrainingTarget(relative, waits != 0, waits, furiten, values);
            }).ToArray();
            this.game.LastDiscardWasRinshan = lastDiscardWasRinshan;
            return targets;
        }

        private void FillUnknownWall()
        {
            // Arbitrary placeholders are used only for physical validation and legal
            // actions. No future replay events/ura enter this wall or exported views.
            var used = this.revealed.Concat(this.hands.SelectMany(h => h)).ToHashSet();
            var unknown = Enumerable.Range(0, 136).Where(t => !used.Contains(t)).Select(Tile136).ToArray();
            if (unknown.Length != this.wall + 14 - this.indicators.Count) throw new InvalidDataException("Replay tile accounting failed.");
            this.game.LiveWall = unknown.Take(this.wall).ToList();
            var cursor = this.wall;
            this.game.DeadWall = new Tile[14];
            for (var i = 0; i < 14; i++) this.game.DeadWall[i] = i >= 4 && i % 2 == 0 && (i - 4) / 2 < this.indicators.Count
                ? Tile136(this.indicators[(i - 4) / 2]) : unknown[cursor++];
        }

        public void End(XElement e)
        {
            var sc = Numbers(e, "sc", 8);
            var before = Enumerable.Range(0, 4).Select(s => sc[2 * s] * 100).ToArray();
            var delta = Enumerable.Range(0, 4).Select(s => sc[2 * s + 1] * 100).ToArray();
            if (this.terminalScores is null)
            {
                if (!before.SequenceEqual(this.game.Players.Select(p => p.Score))) throw new InvalidDataException("Settlement base scores disagree with replay state.");
                this.terminalBase = before; this.terminalScores = before.ToArray();
            }
            else if (!before.SequenceEqual(this.terminalBase) && !before.SequenceEqual(this.terminalScores))
                throw new InvalidDataException("Multi-win settlement base is inconsistent.");
            for (var s = 0; s < 4; s++) this.terminalScores[s] += delta[s];
        }

        public IEnumerable<ReplayDecision> Finish()
        {
            if (this.terminalScores is null) throw new InvalidDataException("Hand is missing its terminal settlement.");
            return this.samples.Select(s => s with { ObservedHandDelta = this.terminalScores[s.Seat] - s.Observation.Snapshot.Us.Score });
        }
    }
}
