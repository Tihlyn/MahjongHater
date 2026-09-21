using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MahjongHater.Core;
using MahjongHater.Core.Replay;
using MahjongHater.Core.Simulation;
using Xunit;

namespace MahjongHater.Tests.Replay;

public sealed class TenhouReplayTests
{
    private static readonly SimulationRules Rules = new();

    // Small event trace for parser contracts; not presented as competitive data.
    private static byte[] Trace(string events = "<T60/><D0/><U61/><E13/>", string ranks = "16,17,18,19")
    {
        var hands = string.Join(" ", Enumerable.Range(0, 4).Select(s => $"hai{s}=\"{string.Join(',', Enumerable.Range(13 * s, 13))}\""));
        return Encoding.UTF8.GetBytes($"<mjloggm><GO type=\"169\"/><UN dan=\"{ranks}\" n0=\"SHOULD-NOT-EXPORT\"/>"
            + $"<INIT seed=\"0,0,0,1,1,135\" ten=\"250,250,250,250\" oya=\"0\" {hands}/>"
            + events + "<RYUUKYOKU sc=\"250,0,250,0,250,0,250,0\" owari=\"250,0,250,0,250,0,250,0\"/></mjloggm>");
    }

    [Theory]
    [InlineData(16, "0m")]
    [InlineData(17, "5m")]
    [InlineData(52, "0p")]
    [InlineData(88, "0s")]
    [InlineData(108, "1z")]
    [InlineData(135, "7z")]
    public void Physical_tiles_preserve_reds_and_honors(int id, string code) => Assert.Equal(code, TenhouReplay.Tile136(id).ToString());

    [Theory]
    [InlineData(3, 34314, MeldType.Pon, "89,90,91", 1)]
    [InlineData(0, 15872, MeldType.Ankan, "60,61,62,63", 0)]
    [InlineData(3, 13825, MeldType.Daiminkan, "52,53,54,55", 0)]
    [InlineData(3, 18547, MeldType.Shouminkan, "48,49,50,51", 2)]
    [InlineData(3, 27031, MeldType.Chi, "42,44,51", 2)]
    public void Meld_bit_layout_preserves_called_physical_copies(int seat, int packed, MeldType type, string tiles, int from)
    {
        var meld = TenhouReplay.DecodeMeld(seat, packed);
        Assert.Equal(type, meld.Type);
        Assert.Equal(tiles.Split(',').Select(int.Parse), meld.Tiles.Order());
        Assert.Equal(from, meld.From);
        Assert.Contains(meld.Called, meld.Tiles);
    }

    [Fact]
    public void Observations_precede_actions_and_exclude_private_opponent_hands_and_future_draws()
    {
        var a = TenhouReplay.Parse(Trace(), Rules);
        var b = TenhouReplay.Parse(Trace("<T60/><D0/><U62/><E13/>"), Rules);
        Assert.Equal(2, a.Decisions.Length);
        var first = a.Decisions[0];
        Assert.Equal(first.Observation.InformationKey(), b.Decisions[0].Observation.InformationKey());
        Assert.Equal(14, first.Observation.Snapshot.Hand.Count);
        Assert.Equal(69, first.Observation.Snapshot.WallRemaining);
        Assert.Empty(first.Observation.Snapshot.Us.Discards);
        Assert.Equal("1m", first.ObservedAction.Tile);
        Assert.Contains(first.ObservedAction, first.LegalActions);
        Assert.Equal(68, a.Decisions[1].Observation.Snapshot.WallRemaining);
        Assert.Single(a.Decisions[1].Observation.Snapshot.Seats[3].Discards);
        Assert.Equal(3, first.OpponentTargets.Length);
        var json = JsonSerializer.Serialize(a, SimulationFiles.Json);
        Assert.DoesNotContain("SHOULD-NOT-EXPORT", json);
        Assert.DoesNotContain("SHUFFLE", json);
        var observation = JsonSerializer.Serialize(first.Observation, SimulationFiles.Json);
        Assert.DoesNotContain("OpponentTargets", observation);
        Assert.DoesNotContain("ObservedAction", observation);
        var formatted = XDocument.Parse(Encoding.UTF8.GetString(Trace()));
        formatted.Root!.Element("UN")!.SetAttributeValue("n0", "OTHER-DISPLAY-NAME");
        Assert.Equal(a.Sha256, TenhouReplay.Parse(Encoding.UTF8.GetBytes(formatted.ToString()), Rules).Sha256);
        var particle = new BeliefSampler().Sample(first.Observation, new Random(7));
        RiichiSimulator.ValidateConservation(particle, 100000);
    }

    [Fact]
    public void Rank_filters_apply_to_actor_and_do_not_leak_to_observation()
    {
        var game = TenhouReplay.Parse(Trace(), Rules, minimumRank: 17);
        Assert.Single(game.Decisions);
        Assert.Equal(1, game.Decisions[0].Seat);
        Assert.Equal(17, game.Decisions[0].Rank);
        Assert.Equal(0, game.Decisions[0].Observation.TurnSeat);
        Assert.Equal(3, game.Decisions[0].Observation.Snapshot.DealerSeat);
    }

    [Fact]
    public void Corrupt_tiles_unknown_events_and_incomplete_matches_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => TenhouReplay.Parse(Trace("<T0/><D0/>"), Rules));
        Assert.Throws<InvalidDataException>(() => TenhouReplay.Parse(Trace("<T60/><D75/>"), Rules));
        Assert.Throws<InvalidDataException>(() => TenhouReplay.Parse(Trace("<U60/><E13/>"), Rules));
        Assert.Throws<NotSupportedException>(() => TenhouReplay.Parse(Trace("<MYSTERY/>"), Rules));
        var document = XDocument.Parse(Encoding.UTF8.GetString(Trace()));
        document.Root!.Element("RYUUKYOKU")!.Attribute("owari")!.Remove();
        Assert.Throws<InvalidDataException>(() => TenhouReplay.Parse(Encoding.UTF8.GetBytes(document.ToString()), Rules));
        Assert.Throws<NotSupportedException>(() => TenhouReplay.Parse(Trace(), Rules with { HandsInMatch = 4 }));
    }

    [Fact]
    public async Task Archive_deduplication_export_and_resumable_search_keep_targets_separate()
    {
        using var temporary = new TemporaryDirectory();
        var zipPath = Path.Combine(temporary.Path, "input.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var plain = zip.CreateEntry("../../one.xml").Open()) plain.Write(Trace());
            using (var packed = zip.CreateEntry("duplicate.mjlog").Open())
            using (var gzip = new GZipStream(packed, CompressionLevel.Fastest)) gzip.Write(Trace());
            using (var invalid = new StreamWriter(zip.CreateEntry("bad.xml").Open())) invalid.Write("<not-mahjong/>");
        }
        var corpusPath = Path.Combine(temporary.Path, "corpus");
        var manifest = ReplayCorpus.Import(zipPath, corpusPath, Rules, 16);
        Assert.Single(manifest.Games);
        Assert.Equal(1, manifest.Duplicates);
        Assert.Single(manifest.Rejected);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "one.xml")));
        var corpus = new ReplayCorpus(corpusPath);
        var exported = Path.Combine(temporary.Path, "rows.jsonl");
        Assert.Equal(2, corpus.Export(exported));
        var rows = File.ReadLines(exported).Select(l => JsonSerializer.Deserialize<ReplayLearningRow>(l, SimulationFiles.Json)!).ToArray();
        Assert.Single(rows.Select(r => r.Split).Distinct());
        Assert.All(rows, r => { Assert.Equal(3, r.Targets.Opponents.Length); Assert.Equal(14, r.Inputs.Snapshot.Hand.Count); });
        var config = new GenerationConfig { Matches = 2, Workers = 1, StatesPerMatch = 1,
            Search = new SearchOptions { Iterations = 8, Particles = 1, MinimumVisits = 1, MaximumTreeNodes = 8 } };
        var run = Path.Combine(temporary.Path, "run");
        await GenerationRunner.Run(config, run, corpus: corpus);
        GenerationProgress? progress = null;
        await GenerationRunner.Run(config with { Workers = 2 }, run, p => progress = p, corpus: corpus);
        Assert.Equal(1, progress!.Resumed);
        var runManifest = SimulationFiles.Read<RunManifest>(Path.Combine(run, "manifest.json.gz"));
        Assert.Equal(corpus.Fingerprint, runManifest.CorpusSha256);
        Assert.NotEqual(config.Fingerprint(), runManifest.Fingerprint);
        await Assert.ThrowsAsync<InvalidDataException>(() => GenerationRunner.Run(config, run));
        var database = Path.Combine(temporary.Path, "db");
        var packedManifest = SimulationDatabase.Pack([run], database);
        Assert.Equal(1, packedManifest.InputRecords);
        using (var loaded = new SimulationDatabase(database, verifyChecksums: true))
        {
            var job = SimulationFiles.Read<GenerationJob>(Directory.GetFiles(Path.Combine(run, "completed")).Single());
            Assert.True(loaded.TryExact(job.Records[0].Exact, out _));
        }
        var gameFile = Path.Combine(corpusPath, "games", manifest.Games[0].Sha256 + ".json.gz");
        File.AppendAllText(gameFile, "corrupt");
        Assert.Throws<InvalidDataException>(() => corpus.Read(0));
    }

    [Fact]
    public void Existing_self_play_run_identities_remain_compatible()
    {
        var config = new GenerationConfig();
        var old = JsonSerializer.Serialize(new { Schema = 1, Fingerprint = config.Fingerprint(), Profile = config.Rules.Profile(config.Weights), Config = config });
        var manifest = JsonSerializer.Deserialize<RunManifest>(old, SimulationFiles.Json)!;
        Assert.Null(manifest.CorpusSha256);
        Assert.Equal(config.Fingerprint(), RunManifest.Identity(manifest.Config, manifest.CorpusSha256));
    }

    [Fact]
    public void Observed_returns_use_settlement_and_multi_ron_is_accumulated_once()
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(Trace()));
        document.Root!.Element("RYUUKYOKU")!.ReplaceWith(
            XElement.Parse("<AGARI who=\"0\" fromWho=\"1\" sc=\"250,10,250,-10,250,0,250,0\"/>"),
            XElement.Parse("<AGARI who=\"2\" fromWho=\"1\" sc=\"250,0,250,-20,250,20,250,0\" owari=\"260,0,220,0,270,0,250,0\"/>"));
        var game = TenhouReplay.Parse(Encoding.UTF8.GetBytes(document.ToString()), Rules);
        Assert.Equal(1000, game.Decisions[0].ObservedHandDelta);
        Assert.Equal(-3000, game.Decisions[1].ObservedHandDelta);
        Assert.Equal(new[] { 26000, 22000, 27000, 25000 }, game.Match.Scores);
        Assert.Equal(new[] { 2, 4, 1, 3 }, game.Match.Placement);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MahjongHater-ReplayTests"));
        public string Path { get; }
        public TemporaryDirectory() { this.Path = System.IO.Path.Combine(this.parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(this.Path); }
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(this.Path);
            if (!target.StartsWith(this.parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }
}
