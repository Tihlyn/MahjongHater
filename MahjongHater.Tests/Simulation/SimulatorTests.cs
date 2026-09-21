using System.Text.Json;
using MahjongHater.Core;
using MahjongHater.Core.Precomputed;
using MahjongHater.Core.Simulation;
using Xunit;

namespace MahjongHater.Tests.Simulation;

public sealed class SimulatorTests
{
    [Fact]
    public void Deal_contains_136_physical_tiles_and_a_fourteen_tile_dead_wall()
    {
        var game = RiichiSimulator.Deal(new SimulationRules(), new Random(42));
        Assert.Equal(69, game.LiveWall.Count);
        Assert.Equal(14, game.DeadWall.Length);
        Assert.Equal(14, game.Players[0].Hand.Count);
        Assert.All(game.Players.Skip(1), p => Assert.Equal(13, p.Hand.Count));
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Illegal_action_does_not_mutate_the_game()
    {
        var game = RiichiSimulator.Deal(new SimulationRules(), new Random(2));
        var before = JsonSerializer.Serialize(game, SimulationFiles.Json);
        Assert.Throws<ArgumentException>(() => RiichiSimulator.Apply(game, new SimAction(SimActionKind.Pass)));
        Assert.Equal(before, JsonSerializer.Serialize(game, SimulationFiles.Json));
    }

    [Fact]
    public void Closed_kan_moves_four_tiles_draws_rinshan_and_reveals_dora()
    {
        var game = Game(["1111m234p567s1122z", null, null, null]);
        var draw = game.DeadWall[0];
        var wall = game.LiveWall.Count;
        game.Players[1].Ippatsu = true;
        var action = Assert.Single(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.ClosedKan);
        RiichiSimulator.Apply(game, action);
        Assert.Equal(draw, game.DrawnTile);
        Assert.True(game.Rinshan);
        Assert.Equal(wall - 1, game.LiveWall.Count);
        Assert.Equal(2, game.Dora.Count);
        Assert.Single(game.Players[0].Melds);
        Assert.All(game.Players, p => Assert.False(p.Ippatsu));
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Added_kan_can_be_robbed_and_its_pending_tile_is_conserved()
    {
        var pon = Meld.MakePon(Tile.Parse("1m"), true);
        var game = Game(["1m234p567s1122z", "23m456789p234s11z", null, null], ownMelds: [pon]);
        var action = Assert.Single(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.AddedKan);
        RiichiSimulator.Apply(game, action);
        Assert.Equal(SimPhase.KanResponses, game.Phase);
        Assert.Contains(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Ron);
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Ron));
        PassResponses(game);
        Assert.Equal(HandEnd.Ron, game.Result!.End);
        Assert.Equal(new[] { 1 }, game.Result.Winners);
        Assert.Equal(MeldType.Pon, game.Players[0].Melds[0].Shape.Type);
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Doman_does_not_allow_robbing_a_concealed_kan()
    {
        var game = Game(["1111m234p567s1122z", null, null, null]);
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.ClosedKan));
        Assert.Equal(SimPhase.Turn, game.Phase);
        Assert.Equal(0, game.Actor);
    }

    [Fact]
    public void Chi_is_only_from_previous_player_and_enforces_both_kuikae_restrictions()
    {
        var game = Game(["1m234p567s1123456z", "1234m456p789s123z", null, null]);
        Discard(game, "1m");
        var chi = RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Chi && a.Consumed == "2m,3m");
        RiichiSimulator.Apply(game, chi);
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Chi);
        PassResponses(game);
        Assert.Equal(1, game.Actor);
        Assert.Null(game.DrawnTile);
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Tile is "1m" or "4m");
        var view = SimulationObservation.Observe(game);
        Assert.Contains(0, view.Snapshot.Seats[3].ClaimedDiscardIndices);
        var seen = view.Snapshot.SeenForAnalyzer().Concat(view.Snapshot.OurMelds.SelectMany(m => m.Tiles));
        Assert.Equal(1, seen.Count(t => t == Tile.Parse("1m")));
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Pon_has_priority_over_chi_and_open_kan_does_not_require_an_already_open_hand()
    {
        var game = Game(["1m234p567s1234567z", "1234m456p789s123z", "11m234p567s11223z", null]);
        Discard(game, "1m");
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Chi));
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Pon));
        PassResponses(game);
        Assert.Equal(2, game.Actor);
        Assert.Equal(MeldType.Pon, game.Players[2].Melds[0].Shape.Type);
        RiichiSimulator.ValidateConservation(game, 100000);

        game = Game(["5z234p567s1123467z", "555z123m456p789s1z", null, null]);
        Discard(game, "5z");
        Assert.Contains(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.OpenKan);
    }

    [Fact]
    public void Riichi_requires_points_and_another_draw_and_deposits_after_ron_resolution()
    {
        var game = Game(["123m456m789p23s11z4s", null, null, null]);
        game.Players[0].Score = 999;
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Riichi);
        game.Players[0].Score = 1000;
        var riichi = RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Riichi && a.Tile == "4s");
        RiichiSimulator.Apply(game, riichi);
        Assert.Equal(1000, game.Players[0].Score);
        Assert.Equal(0, game.RiichiSticks);
        PassResponses(game);
        Assert.Equal(0, game.Players[0].Score);
        Assert.Equal(1, game.RiichiSticks);
        Assert.True(game.Players[0].DoubleRiichi);
        Assert.True(game.Players[0].Ippatsu);

        game = Game(["123m456m789p23s11z4s", null, null, null]);
        ExposeWall(game, 3);
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Riichi);
    }

    [Fact]
    public void Riichi_forces_the_exact_draw_and_only_allows_wait_preserving_kan()
    {
        var game = Game(["123m456m045p678s11z", null, null, null]);
        game.DrawnTile = Tile.Parse("0p");
        game.Players[0].Riichi = true;
        Assert.Equal("0p", Assert.Single(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Discard).Tile);

        game = Game(["1111m234p567s234s5z", null, null, null]);
        game.DrawnTile = Tile.Parse("1m");
        game.Players[0].Riichi = true;
        Assert.Contains(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.ClosedKan);

        game = Game(["111123m456p789s11z", null, null, null]);
        game.DrawnTile = Tile.Parse("1m");
        game.Players[0].Riichi = true;
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.ClosedKan);
    }

    [Fact]
    public void Passing_a_win_causes_temporary_furiten_and_riichi_furiten_does_not_expire()
    {
        foreach (var riichi in new[] { false, true })
        {
            var game = TripleRonGame();
            game.Players[1].Riichi = riichi;
            Discard(game, "5z");
            Assert.Contains(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Ron);
            RiichiSimulator.Apply(game, new SimAction(SimActionKind.Pass));
            Assert.True(game.Players[1].TemporaryFuriten);
            Assert.Equal(riichi, game.Players[1].RiichiFuriten);
            PassResponses(game);
            Assert.False(game.Players[1].TemporaryFuriten);
            Assert.Equal(riichi, game.Players[1].RiichiFuriten);
        }
    }

    [Fact]
    public void Passing_a_shape_without_yaku_still_causes_temporary_furiten()
    {
        var game = Game(["5z234p567s1123467z", "123m456m789p234s5z", null, null]);
        Discard(game, "5z");
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Ron);
        RiichiSimulator.Apply(game, new SimAction(SimActionKind.Pass));
        Assert.True(game.Players[1].TemporaryFuriten);
    }

    [Fact]
    public void Discard_furiten_blocks_all_ron_waits_but_not_tsumo()
    {
        var game = Game(["123m456m789p23s11z4s", null, null, null]);
        game.Players[0].TemporaryFuriten = true;
        game.Players[0].RiichiFuriten = true;
        Assert.Contains(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Tsumo);
        game.Players[0].Hand.Remove(Tile.Parse("4s"));
        game.Players[0].TemporaryFuriten = false;
        game.Players[0].RiichiFuriten = false;
        game.Players[0].River.Add(new SimDiscard(Tile.Parse("1s"), 0, Claimed: true));
        Assert.True(SimScoring.Furiten(game.Players[0]));
    }

    [Fact]
    public void Double_ron_pays_both_winners_and_triple_ron_aborts()
    {
        foreach (var winners in new[] { 2, 3 })
        {
            var game = TripleRonGame();
            Discard(game, "5z");
            for (var seat = 1; seat <= 3; seat++)
                RiichiSimulator.Apply(game, seat <= winners ? RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Ron) : new SimAction(SimActionKind.Pass));
            Assert.Equal(winners == 3 ? HandEnd.TripleRon : HandEnd.Ron, game.Result!.End);
            if (winners == 2)
            {
                Assert.Equal(new[] { 1, 2 }, game.Result.Winners);
                Assert.True(game.Players[1].Score > 25000);
                Assert.True(game.Players[2].Score > 25000);
                Assert.True(game.Players[0].Score < 25000);
            }
            else
                Assert.All(game.Players, p => Assert.Equal(25000, p.Score));
            RiichiSimulator.ValidateConservation(game, 100000);
        }
    }

    [Fact]
    public void Last_discard_allows_ron_but_no_calls()
    {
        var game = TripleRonGame();
        ExposeWall(game, 0);
        Discard(game, "5z");
        Assert.Contains(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.Ron);
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Kind is SimActionKind.Chi or SimActionKind.Pon or SimActionKind.OpenKan);
    }

    [Fact]
    public void Nine_terminals_is_available_only_on_an_uninterrupted_first_draw()
    {
        var game = Game(["19m19p19s1234567z1z", null, null, null]);
        Assert.Contains(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.NineTerminals);
        game.Interrupted = true;
        Assert.DoesNotContain(RiichiSimulator.Legal(game), a => a.Kind == SimActionKind.NineTerminals);
        game.Interrupted = false;
        RiichiSimulator.Apply(game, new SimAction(SimActionKind.NineTerminals));
        Assert.Equal(HandEnd.NineTerminals, game.Result!.End);
        Assert.True(game.Result.DealerRepeats);
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Four_same_first_winds_abort_after_all_response_windows()
    {
        var game = Game(["123m456p789s1234z", null, null, null]);
        // Controlled river state isolates the abort condition from the random deal.
        for (var s = 0; s < 3; s++)
            game.Players[s].River = [new SimDiscard(Tile.Parse("1z"), s)];
        game.Players[3].River.Clear();
        game.TurnSeat = 3;
        game.Players[3].Hand.Add(Tile.Parse("1z"));
        game.DrawnTile = Tile.Parse("1z");
        Discard(game, "1z");
        PassResponses(game);
        Assert.Equal(HandEnd.FourWinds, game.Result!.End);
    }

    [Fact]
    public void Four_kans_by_different_players_abort_after_the_discard_unless_ron_wins()
    {
        var game = TripleRonGame();
        game.AbortAfterDiscard = true;
        Discard(game, "5z");
        PassResponses(game);
        Assert.Equal(HandEnd.FourKans, game.Result!.End);
        game = TripleRonGame();
        game.AbortAfterDiscard = true;
        Discard(game, "5z");
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Ron));
        PassResponses(game);
        Assert.Equal(HandEnd.Ron, game.Result!.End);
    }

    [Fact]
    public void Exhaustive_draw_settles_noten_and_dealer_repeat()
    {
        var game = TripleRonGame();
        game.Players[0].Hand = TestTiles.Parse("147m258p369s1234z");
        foreach (var p in game.Players) p.River.Clear(); // No nagashi candidates.
        SimScoring.Exhaustive(game);
        Assert.Equal(HandEnd.ExhaustiveDraw, game.Result!.End);
        Assert.Equal(new[] { 22000, 26000, 26000, 26000 }, game.Players.Select(p => p.Score).ToArray());
        Assert.False(game.Result.DealerRepeats);
    }

    [Fact]
    public void Nagashi_is_a_draw_and_claimed_river_disqualifies_it()
    {
        var game = TripleRonGame();
        game.Players[0].Hand.Remove(Tile.Parse("5z"));
        game.Players[0].River = [new SimDiscard(Tile.Parse("1z"), 0)];
        SimScoring.Exhaustive(game);
        Assert.Equal(HandEnd.NagashiMangan, game.Result!.End);
        Assert.Equal(37000, game.Players[0].Score);
        Assert.Equal(100000, game.Players.Sum(p => p.Score));
        game = TripleRonGame();
        game.Players[0].Hand.Remove(Tile.Parse("5z"));
        game.Players[0].River = [new SimDiscard(Tile.Parse("1z"), 0, Claimed: true)];
        SimScoring.Exhaustive(game);
        Assert.Equal(HandEnd.ExhaustiveDraw, game.Result!.End);
    }

    [Fact]
    public void Pao_tsumo_charges_responsible_player_and_ron_splits_the_yakuman()
    {
        var melds = new[] { Meld.MakePon(Tile.Parse("5z"), true), Meld.MakePon(Tile.Parse("6z"), true), Meld.MakePon(Tile.Parse("7z"), true) };
        var game = Game(["123m11z", null, null, null], ownMelds: melds);
        game.Players[0].DragonLiability = 2;
        game.DrawnTile = Tile.Parse("1z");
        SimScoring.SettleWins(game, [0], -1, true);
        Assert.Equal(new[] { 73000, 25000, -23000, 25000 }, game.Players.Select(p => p.Score).ToArray());
        game = Game(["123m11z", null, null, null], ownMelds: melds);
        game.Players[0].DragonLiability = 2;
        game.Players[0].Hand.Remove(Tile.Parse("1z"));
        game.PendingTile = Tile.Parse("1z");
        SimScoring.SettleWins(game, [0], 1, false);
        Assert.Equal(new[] { 73000, 1000, 1000, 25000 }, game.Players.Select(p => p.Score).ToArray());
    }

    [Fact]
    public void Kan_hands_are_scored_and_ambiguous_waits_are_all_considered()
    {
        var game = Game(["123m456p789s11z", null, null, null], ownMelds: [Meld.MakeKan(Tile.Parse("5z"), MeldType.Ankan)]);
        Assert.NotNull(SimScoring.Win(game, 0, Tile.Parse("1z"), true));
        var hand = new Hand { WinningTile = Tile.Parse("3m"), WinMethod = WinMethod.Ron };
        hand.ClosedTiles.AddRange(TestTiles.Parse("123345m456p678s22z"));
        var waits = HandDecomposer.GetWinningDecompositions(hand).Select(d => d.Wait).ToArray();
        Assert.Contains(WaitType.Penchan, waits);
        Assert.Contains(WaitType.Ryanmen, waits);
    }

    [Fact]
    public void Honroutou_is_not_also_chanta_and_seven_pairs_is_not_also_ryanpeikou()
    {
        var hand = new Hand { WinningTile = Tile.Parse("1m"), WinMethod = WinMethod.Ron };
        hand.ClosedTiles.AddRange(TestTiles.Parse("111999m111p111z22z"));
        var detector = new YakuDetector(RulesetOptions.Default);
        foreach (var d in HandDecomposer.GetWinningDecompositions(hand))
        {
            var yaku = detector.Detect(hand, d.Melds, d.Pair, d.Wait);
            Assert.DoesNotContain(yaku, y => y.Name is "Chanta" or "Junchan");
        }
        hand.ClosedTiles.Clear();
        hand.ClosedTiles.AddRange(TestTiles.Parse("112233m445566p77s"));
        foreach (var d in HandDecomposer.GetWinningDecompositions(hand))
        {
            var names = detector.Detect(hand, d.Melds, d.Pair, d.Wait).Select(y => y.Name).ToArray();
            Assert.False(names.Contains("Chiitoitsu") && names.Contains("Ryanpeikou"));
        }
    }

    [Fact]
    public void Ron_completed_triplet_has_open_fu_and_tanki_suuankou_stays_concealed()
    {
        var hand = new Hand { WinningTile = Tile.Parse("2m"), WinMethod = WinMethod.Ron, SeatWind = Wind.South, RoundWind = Wind.East };
        var sets = new List<Meld> { Meld.MakePon(Tile.Parse("2m"), false), Meld.MakeChi(Tile.Parse("3p"), Tile.Parse("4p"), Tile.Parse("5p")),
            Meld.MakeChi(Tile.Parse("4s"), Tile.Parse("5s"), Tile.Parse("6s")), Meld.MakeChi(Tile.Parse("6m"), Tile.Parse("7m"), Tile.Parse("8m")) };
        Assert.Equal(40, FuCalculator.Calculate(hand, sets, Tile.Parse("3z"), WaitType.Shanpon, 4));
        // Two simple triplets distinguish open completion (2+4 fu) from 4+4.
        sets[1] = Meld.MakePon(Tile.Parse("4p"), false);
        sets[2] = Meld.MakePon(Tile.Parse("6s"), false);
        Assert.Equal(40, FuCalculator.Calculate(hand, sets, Tile.Parse("3z"), WaitType.Shanpon, 4));
        hand.ClosedTiles.AddRange(TestTiles.Parse("111222m333p444s55z"));
        hand.WinningTile = Tile.Parse("5z");
        var d = HandDecomposer.GetWinningDecompositions(hand).First();
        Assert.Contains(new YakuDetector(RulesetOptions.Default).Detect(hand, d.Melds, d.Pair, d.Wait), y => y.Name == "Suuankou");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1049)]
    public void Complete_matches_conserve_tiles_points_and_produce_reproducible_results(int seed)
    {
        var rules = new SimulationRules { HandsInMatch = 4 };
        var first = MatchRunner.Play(rules, seed, new SimulationPolicy());
        var second = MatchRunner.Play(rules, seed, new SimulationPolicy());
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(100000, first.Scores.Sum());
        Assert.Equal(new[] { 1, 2, 3, 4 }, first.Placement.Order().ToArray());
        Assert.True(first.Decisions > 0);
    }

    [Fact]
    public void Observation_hides_opponent_hands_wall_and_private_responses()
    {
        var game = TripleRonGame();
        var before = SimulationObservation.Observe(game).InformationKey();
        (game.Players[1].Hand[0], game.LiveWall[0]) = (game.LiveWall[0], game.Players[1].Hand[0]);
        SimTiles.Shuffle(game.LiveWall, new Random(4));
        Assert.Equal(before, SimulationObservation.Observe(game).InformationKey());
        Discard(game, "5z");
        before = SimulationObservation.Observe(game).InformationKey();
        game.Responses[2] = new SimAction(SimActionKind.Ron, "5z");
        Assert.Equal(before, SimulationObservation.Observe(game).InformationKey());
    }

    [Fact]
    public void Conditioned_particles_keep_own_hand_public_state_and_riichi_tenpai()
    {
        var game = TripleRonGame();
        game.Players[1].Riichi = true;
        var view = SimulationObservation.Observe(game);
        var sampler = new BeliefSampler();
        for (var i = 0; i < 4; i++)
        {
            var sample = sampler.Sample(view, new Random(i));
            Assert.Equal(view.Snapshot.Hand.Order(), sample.Players[0].Hand.Order());
            Assert.Equal(0, Shanten.Calculate(sample.Players[1].Hand));
            Assert.Equal(view.Snapshot.DoraIndicators, sample.Dora);
            Assert.Equal(view.InformationKey(), SimulationObservation.Observe(sample).InformationKey());
            RiichiSimulator.ValidateConservation(sample, 100000);
        }
    }

    [Fact]
    public void Search_finishes_real_hands_and_backs_up_every_root_action()
    {
        var game = RiichiSimulator.Deal(new SimulationRules(), new Random(17));
        var view = SimulationObservation.Observe(game);
        var options = new SearchOptions { Iterations = 16, MinimumVisits = 1, Particles = 2, MaximumTreeNodes = 8 };
        var first = new InformationSetSearch().Train(view, options, 51);
        var second = new InformationSetSearch().Train(view, options, 51);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(first.Iterations, first.Actions.Sum(a => a.Visits));
        Assert.All(first.Actions, a => { Assert.True(a.Visits > 0); Assert.True(double.IsFinite(a.MeanScore)); });
        Assert.Equal(RiichiSimulator.Legal(game).Select(a => a.Key).Order(), first.Actions.Select(a => a.Action.Key).Order());
        Assert.Throws<OperationCanceledException>(() => new InformationSetSearch().Train(view, options, 51, new CancellationToken(true)));
    }

    [Fact]
    public void The_actual_fourth_kan_draws_rinshan_then_aborts_after_an_unclaimed_discard()
    {
        Meld[][] melds = [[], [Meld.MakeKan(Tile.Parse("5z"), MeldType.Ankan)],
            [Meld.MakeKan(Tile.Parse("6z"), MeldType.Ankan)], [Meld.MakeKan(Tile.Parse("7z"), MeldType.Ankan)]];
        var game = Game(["1111m234p567s1122z", null, null, null], allMelds: melds);
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.ClosedKan));
        Assert.Equal(4, game.KanCount);
        Assert.True(game.AbortAfterDiscard);
        Assert.True(game.Rinshan);
        Assert.Equal(5, game.Dora.Count);
        RiichiSimulator.ValidateConservation(game, 100000);
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Discard));
        PassResponses(game);
        Assert.Equal(HandEnd.FourKans, game.Result!.End);
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void One_players_four_kans_can_win_and_a_fifth_kan_aborts_without_drawing()
    {
        var melds = new[] { "1m", "2p", "3s", "5z" }.Select(t => Meld.MakeKan(Tile.Parse(t), MeldType.Ankan)).ToArray();
        var game = Game(["11z", "4444m456p567s123z", null, null], ownMelds: melds);
        Assert.Contains(SimScoring.Win(game, 0, Tile.Parse("1z"), true)!.Yaku, y => y.Name == "Suukantsu");
        Discard(game, "1z");
        PassResponses(game);
        var remaining = game.LiveWall.Count;
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.ClosedKan));
        Assert.Equal(HandEnd.FourKans, game.Result!.End);
        Assert.Equal(remaining, game.LiveWall.Count);
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Ron_on_riichi_declaration_does_not_charge_the_riichi_stick()
    {
        var game = TripleRonGame();
        RiichiSimulator.Apply(game, new SimAction(SimActionKind.Riichi, "5z"));
        var expectedLoss = SimScoring.Win(game, 1, Tile.Parse("5z"), false)!.Score.RonPayment;
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Ron));
        PassResponses(game);
        Assert.Equal(25000 - expectedLoss, game.Players[0].Score);
        Assert.False(game.Players[0].RiichiPaid);
        Assert.Equal(0, game.RiichiSticks);
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Claimed_third_dragon_meld_records_liability()
    {
        Meld[][] melds = [[], [Meld.MakePon(Tile.Parse("5z"), true), Meld.MakePon(Tile.Parse("6z"), true)], [], []];
        var game = Game(["7z123m456p789s1122z", "77z123m11p", null, null], allMelds: melds);
        Discard(game, "7z");
        RiichiSimulator.Apply(game, RiichiSimulator.Legal(game).First(a => a.Kind == SimActionKind.Pon));
        PassResponses(game);
        Assert.Equal(0, game.Players[1].DragonLiability);
        RiichiSimulator.ValidateConservation(game, 100000);
    }

    [Fact]
    public void Heavenly_and_earthly_hands_require_uninterrupted_first_draws()
    {
        foreach (var seat in new[] { 0, 1 })
        {
            var hands = new string?[4];
            hands[seat] = "123m456m789p234s11z";
            var game = Game(hands, seat);
            var name = seat == 0 ? "Tenhou" : "Chiihou";
            Assert.Contains(SimScoring.Win(game, seat, Tile.Parse("1z"), true)!.Yaku, y => y.Name == name);
            game.Interrupted = true;
            Assert.DoesNotContain(SimScoring.Win(game, seat, Tile.Parse("1z"), true)!.Yaku, y => y.Name == name);
        }
    }

    internal static SimGame Game(string?[] hands, int turn = 0, Meld[]? ownMelds = null, Meld[][]? allMelds = null)
    {
        var game = new SimGame { Rules = new SimulationRules(), Phase = SimPhase.Turn, TurnSeat = turn,
            StartingScores = [25000, 25000, 25000, 25000] };
        var remaining = SimTiles.Set();
        game.Players[0].Melds = (ownMelds ?? []).Select(m => new SimMeld(m, m.IsOpen ? 3 : -1)).ToList();
        if (allMelds is not null)
            for (var seat = 0; seat < 4; seat++)
                game.Players[seat].Melds = allMelds[seat].Select(m => new SimMeld(m, m.IsOpen ? (seat + 3) % 4 : -1)).ToList();
        foreach (var tile in game.Players.SelectMany(p => p.Melds).SelectMany(m => m.Shape.Tiles))
            Assert.True(remaining.Remove(tile), $"Too many {tile}");
        for (var seat = 0; seat < 4; seat++)
        {
            game.Players[seat].Score = 25000;
            if (hands[seat] is null) continue;
            game.Players[seat].Hand = TestTiles.Parse(hands[seat]!);
            foreach (var tile in game.Players[seat].Hand)
                Assert.True(remaining.Remove(tile), $"Too many {tile}");
        }
        SimTiles.Shuffle(remaining, new Random(124));
        for (var seat = 0; seat < 4; seat++)
            if (hands[seat] is null)
            {
                var count = (seat == turn ? 14 : 13) - 3 * game.Players[seat].Melds.Count;
                game.Players[seat].Hand = remaining.Take(count).ToList();
                remaining.RemoveRange(0, count);
            }
        game.LiveWall = remaining.Take(remaining.Count - 14).ToList();
        game.DeadWall = remaining.TakeLast(14).ToArray();
        game.DrawnTile = game.Players[turn].Hand[^1];
        game.Players[turn].DrawCount = 1;
        game.KanCount = game.Players.Sum(p => p.Melds.Count(m => m.Shape.IsKan));
        return game;
    }
    private static SimGame TripleRonGame() => Game(["2223334446667z5z", "123456789m111p5z", "123456789p111s5z", "123456789s111m5z"]);
    private static void Discard(SimGame game, string tile) => RiichiSimulator.Apply(game, new SimAction(SimActionKind.Discard, tile));
    private static void PassResponses(SimGame game)
    {
        while (game.Phase is SimPhase.DiscardResponses or SimPhase.KanResponses)
            RiichiSimulator.Apply(game, new SimAction(SimActionKind.Pass));
    }
    private static void ExposeWall(SimGame game, int left)
    {
        while (game.LiveWall.Count > left)
        {
            var tile = game.LiveWall[^1];
            game.LiveWall.RemoveAt(game.LiveWall.Count - 1);
            game.Players[3].River.Add(new SimDiscard(tile, game.DiscardCounter++));
        }
    }
}
