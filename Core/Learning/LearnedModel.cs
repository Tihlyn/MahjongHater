using System.Numerics;
using System.IO.Compression;
using System.Text.Json;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

public sealed record NeuralLayer(int Inputs, int Outputs, int Kernel, float[] Weight, float[] Bias);
public sealed record ResidualBlock(NeuralLayer Conv1, NeuralLayer Conv2);
public sealed record ProbabilityCalibration(float Slope, float Bias);

// Schema 1: Conv1 → Conv2 → Dense → Output (public-tiles-v1, 74 actions).
// Schema 2: Stem → Blocks (residual: conv-relu-conv + skip, relu) → Dense → Output, any
// known feature version; Actions follows the feature version (82 for v2).
public sealed record LearnedArtifact(int Schema, string Features, string Corpus, SimulationRules Rules,
    NeuralLayer? Conv1, NeuralLayer? Conv2, NeuralLayer Dense, NeuralLayer Output,
    ProbabilityCalibration TenpaiCalibration, ProbabilityCalibration WaitCalibration, float ValueScale,
    bool HasSearchLabels, string Status)
{
    public NeuralLayer? Stem { get; init; }

    public ResidualBlock[]? Blocks { get; init; }
}

// Small CPU CNN; no native inference runtime or training dependency in the plugin.
// Output: policy[A], tenpai[3], conditional ron[102], points[102], search Q[A],
// optionally final placement[4] (logits over 1st..4th for the acting seat).
public sealed class LearnedModel
{
    private readonly LearnedArtifact artifact;
    private readonly NeuralLayer stem;
    private readonly ResidualBlock[] blocks;
    private readonly NeuralLayer? conv2;   // schema 1 only

    public const int OpponentOutputs = 3 + 102 + 102;

    public string FeatureVersion => this.artifact.Features;

    public int Actions => LearningFeatures.ActionsFor(this.FeatureVersion);

    public int Outputs => 2 * this.Actions + OpponentOutputs;

    public int TenpaiOffset => this.Actions;

    public int RonOffset => this.Actions + 3;

    public int PointsOffset => this.Actions + 105;

    public int SearchOffset => this.Actions + 207;

    public int PlacementOffset => 2 * this.Actions + OpponentOutputs;

    public bool HasPlacementHead { get; }

    public SimulationRules Rules => this.artifact.Rules;

    public string Status => this.artifact.Status;

    public bool HasSearchLabels => this.artifact.HasSearchLabels;

    public int Schema => this.artifact.Schema;

    public LearnedModel(LearnedArtifact artifact)
    {
        artifact.Rules.Validate();
        if (string.IsNullOrWhiteSpace(artifact.Corpus) || !LearningFeatures.IsKnownVersion(artifact.Features))
            throw new InvalidDataException("Incompatible learned model schema or feature version.");
        var channels = LearningFeatures.ChannelsFor(artifact.Features);
        var outputs = 2 * LearningFeatures.ActionsFor(artifact.Features) + OpponentOutputs;
        NeuralLayer first;
        switch (artifact.Schema)
        {
            case 1:
                if (artifact.Conv1 is null || artifact.Conv2 is null || artifact.Features != LearningFeatures.LegacyVersion
                    || artifact.Conv1.Kernel != 3 || artifact.Conv2.Kernel != 3 || artifact.Conv2.Inputs != artifact.Conv1.Outputs)
                    throw new InvalidDataException("Incompatible schema-1 learned model.");
                first = artifact.Conv1;
                this.conv2 = artifact.Conv2;
                this.blocks = [];
                if (artifact.Dense.Inputs != artifact.Conv2.Outputs * 34) throw new InvalidDataException("Incompatible learned model dimensions.");
                break;
            case 2:
                if (artifact.Stem is null || artifact.Stem.Kernel != 3)
                    throw new InvalidDataException("Incompatible schema-2 learned model.");
                first = artifact.Stem;
                this.blocks = artifact.Blocks ?? [];
                if (this.blocks.Length > 64) throw new InvalidDataException("Too many residual blocks.");
                foreach (var block in this.blocks)
                    if (block.Conv1.Inputs != first.Outputs || block.Conv1.Outputs != first.Outputs || block.Conv2.Inputs != first.Outputs
                        || block.Conv2.Outputs != first.Outputs || block.Conv1.Kernel != 3 || block.Conv2.Kernel != 3)
                        throw new InvalidDataException("Residual block dimensions do not match the stem.");
                if (artifact.Dense.Inputs != first.Outputs * 34) throw new InvalidDataException("Incompatible learned model dimensions.");
                break;
            default:
                throw new InvalidDataException("Unsupported learned model schema.");
        }

        this.HasPlacementHead = artifact.Output.Outputs == outputs + 4;
        if (first.Inputs != channels || artifact.Dense.Kernel != 1 || artifact.Output.Inputs != artifact.Dense.Outputs
            || artifact.Output.Outputs != outputs && !this.HasPlacementHead || artifact.Output.Kernel != 1
            || !float.IsFinite(artifact.ValueScale) || artifact.ValueScale is < .1f or > 10f)
            throw new InvalidDataException("Incompatible learned model schema or dimensions.");
        var layers = new List<NeuralLayer> { first, artifact.Dense, artifact.Output };
        if (this.conv2 is not null) layers.Add(this.conv2);
        layers.AddRange(this.blocks.SelectMany(b => new[] { b.Conv1, b.Conv2 }));
        foreach (var layer in layers)
            if (layer.Inputs is < 1 or > 8704 || layer.Outputs is < 1 or > 512
                || layer.Weight.Length != checked(layer.Inputs * layer.Outputs * layer.Kernel)
                || layer.Bias.Length != layer.Outputs || layer.Weight.Any(v => !float.IsFinite(v)) || layer.Bias.Any(v => !float.IsFinite(v)))
                throw new InvalidDataException("Invalid learned model weights.");
        foreach (var cal in new[] { artifact.TenpaiCalibration, artifact.WaitCalibration })
            if (!float.IsFinite(cal.Slope) || !float.IsFinite(cal.Bias) || cal.Slope <= 0 || cal.Slope > 100 || Math.Abs(cal.Bias) > 100)
                throw new InvalidDataException("Invalid probability calibration.");
        // Isolate immutable inference from callers retaining the deserialized arrays.
        static NeuralLayer Copy(NeuralLayer l) => l with { Weight = (float[])l.Weight.Clone(), Bias = (float[])l.Bias.Clone() };
        this.stem = Copy(first);
        this.conv2 = this.conv2 is null ? null : Copy(this.conv2);
        this.blocks = this.blocks.Select(b => new ResidualBlock(Copy(b.Conv1), Copy(b.Conv2))).ToArray();
        this.artifact = artifact with { Dense = Copy(artifact.Dense), Output = Copy(artifact.Output), Conv1 = null, Conv2 = null, Stem = null, Blocks = null };
    }

    private const long SizeLimit = 256 * 1024 * 1024;

    // Plain .json, or .json.gz as the models ship inside the plugin (a third of the size).
    public static LearnedModel Load(string path)
    {
        if (new FileInfo(path).Length > SizeLimit) throw new InvalidDataException("Model exceeds size limit.");
        using var file = File.OpenRead(path);
        Stream content = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        try
        {
            // Decompression must not be able to blow past the limit either.
            using var bounded = new BoundedStream(content, SizeLimit);
            return new LearnedModel(JsonSerializer.Deserialize<LearnedArtifact>(bounded, SimulationFiles.Json)
                ?? throw new InvalidDataException("Empty learned model."));
        }
        finally
        {
            if (!ReferenceEquals(content, file)) content.Dispose();
        }
    }

    private sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        private long read;

        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            this.read += n;
            if (this.read > limit) throw new InvalidDataException("Model exceeds size limit.");
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => this.read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public bool Supports(StateSnapshot state) => state.Ruleset.Kuitan == this.Rules.Kuitan
        && state.Ruleset.HandsInMatch == this.Rules.HandsInMatch;

    public int ActionIndex(SimAction action) => LearningFeatures.ActionIndex(action, this.FeatureVersion);

    // One decision is scored by several consumers (opponent model, discard ordering, call
    // policy, harness variants) on the same snapshot: remember the last encoding and
    // prediction per thread so the network runs once. Callers get copies.
    private sealed record Memo(StateSnapshot? State, float[] Input, float[]? Output);
    private readonly ThreadLocal<Memo?> memo = new();

    public float[] Encode(StateSnapshot state)
    {
        var m = this.memo.Value;
        if (m?.State is not null && ReferenceEquals(m.State, state)) return (float[])m.Input.Clone();
        var input = LearningFeatures.Encode(state, this.FeatureVersion);
        this.memo.Value = new Memo(state, input, null);
        return (float[])input.Clone();
    }

    public float[] Predict(StateSnapshot state, CancellationToken ct = default) => this.Predict(this.Encode(state), ct);

    public float[] Predict(float[] input, CancellationToken ct = default)
    {
        if (input.Length != LearningFeatures.CountFor(this.FeatureVersion) || input.Any(x => !float.IsFinite(x))) throw new ArgumentException("Invalid features.");
        var m = this.memo.Value;
        if (m?.Output is not null && input.AsSpan().SequenceEqual(m.Input)) return (float[])m.Output.Clone();
        var output = this.Run(input, ct);
        if (m is { Output: null } && input.AsSpan().SequenceEqual(m.Input)) this.memo.Value = m with { Output = output };
        else if (m?.State is null) this.memo.Value = new Memo(null, (float[])input.Clone(), output);
        // else: a counterfactual input (e.g. Placement with score deltas) must not evict the
        // snapshot's own prediction, which the next consumer of this decision will ask for.
        return (float[])output.Clone();
    }

    private float[] Run(float[] input, CancellationToken ct)
    {
        var h = Apply(input, this.stem, 34, true, ct);
        if (this.conv2 is not null)
            h = Apply(h, this.conv2, 34, true, ct);
        foreach (var block in this.blocks)
        {
            var inner = Apply(h, block.Conv1, 34, true, ct);
            var outer = Apply(inner, block.Conv2, 34, false, ct);
            for (var i = 0; i < outer.Length; i++)
                h[i] = Math.Max(0, outer[i] + h[i]);
        }
        var dense = Apply(h, this.artifact.Dense, 1, true, ct);
        var output = Apply(dense, this.artifact.Output, 1, false, ct);
        if (output.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Nonfinite neural prediction.");
        return output;
    }

    // P(final placement 1st..4th) for seat 0 if every seat's score moved by `scoreDeltas`
    // (relative seats, points); null without a placement head. Only the score planes are
    // re-encoded, so the counterfactual keeps the rest of the position.
    public double[]? Placement(StateSnapshot state, int[]? scoreDeltas = null, CancellationToken ct = default)
    {
        if (!this.HasPlacementHead || !this.Supports(state)) return null;
        var input = this.Encode(state);
        if (scoreDeltas is not null)
        {
            if (scoreDeltas.Length != 4) throw new ArgumentException("Four relative-seat deltas expected.");
            for (var seat = 0; seat < 4; seat++)
                Array.Fill(input, (state.Seats[seat].Score + scoreDeltas[seat]) / 50000f, (32 + seat) * 34, 34);
        }
        var output = this.Predict(input, ct);
        var logits = output.AsSpan(this.PlacementOffset, 4);
        var max = float.NegativeInfinity;
        foreach (var l in logits) max = Math.Max(max, l);
        var p = new double[4];
        var sum = 0d;
        for (var i = 0; i < 4; i++) { p[i] = Math.Exp(logits[i] - max); sum += p[i]; }
        for (var i = 0; i < 4; i++) p[i] /= sum;
        return p;
    }

    public double Tenpai(float logit) => Calibrate(logit, this.artifact.TenpaiCalibration);
    public double Wait(float logit) => Calibrate(logit, this.artifact.WaitCalibration);
    public double Points(float value) => Math.Clamp(Softplus(value) * 32000 * this.artifact.ValueScale, 0, 128000);
    private static double Softplus(double x) => x > 20 ? x : Math.Log(1 + Math.Exp(x));
    private static double Calibrate(float x, ProbabilityCalibration cal) => 1 / (1 + Math.Exp(-Math.Clamp(cal.Slope * x + cal.Bias, -40, 40)));

    // SIMD over the 34 tile positions (conv) or the inputs (dense); scalar tails. Weight
    // layout is [output][input][kernel] as train.py exports it.
    private static float[] Apply(float[] x, NeuralLayer l, int width, bool relu, CancellationToken ct)
    {
        var y = new float[l.Outputs * width];
        var lanes = Vector<float>.Count;
        if (width == 1 && l.Kernel == 1)
        {
            for (var o = 0; o < l.Outputs; o++)
            {
                if ((o & 63) == 0) ct.ThrowIfCancellationRequested();
                var wBase = o * l.Inputs;
                var sum = Vector<float>.Zero;
                var i = 0;
                for (; i + lanes <= l.Inputs; i += lanes)
                    sum += new Vector<float>(x, i) * new Vector<float>(l.Weight, wBase + i);
                var value = l.Bias[o] + Vector.Sum(sum);
                for (; i < l.Inputs; i++) value += x[i] * l.Weight[wBase + i];
                y[o] = relu ? Math.Max(0, value) : value;
            }
            return y;
        }
        var half = l.Kernel / 2;
        var padded = width + 2 * half;
        var xp = new float[l.Inputs * padded];
        for (var i = 0; i < l.Inputs; i++) Array.Copy(x, i * width, xp, i * padded + half, width);
        for (var o = 0; o < l.Outputs; o++)
        {
            if ((o & 15) == 0) ct.ThrowIfCancellationRequested();
            var bias = l.Bias[o];
            var weightBase = o * l.Inputs * l.Kernel;
            var pos = 0;
            for (; pos + lanes <= width; pos += lanes)
            {
                var acc = new Vector<float>(bias);
                for (var i = 0; i < l.Inputs; i++)
                {
                    var src = i * padded + pos;
                    var wBase = weightBase + i * l.Kernel;
                    for (var k = 0; k < l.Kernel; k++)
                        acc += new Vector<float>(xp, src + k) * l.Weight[wBase + k];
                }
                if (relu) acc = Vector.Max(acc, Vector<float>.Zero);
                acc.CopyTo(y, o * width + pos);
            }
            for (; pos < width; pos++)
            {
                var value = bias;
                for (var i = 0; i < l.Inputs; i++)
                {
                    var src = i * padded + pos;
                    var wBase = weightBase + i * l.Kernel;
                    for (var k = 0; k < l.Kernel; k++) value += xp[src + k] * l.Weight[wBase + k];
                }
                y[o * width + pos] = relu ? Math.Max(0, value) : value;
            }
        }
        return y;
    }
}
