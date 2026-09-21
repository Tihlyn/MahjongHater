using System.Text.Json;
using MahjongHater.Core.Simulation;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Learning;

public sealed record NeuralLayer(int Inputs, int Outputs, int Kernel, float[] Weight, float[] Bias);
public sealed record ProbabilityCalibration(float Slope, float Bias);
public sealed record LearnedArtifact(int Schema, string Features, string Corpus, SimulationRules Rules,
    NeuralLayer Conv1, NeuralLayer Conv2, NeuralLayer Dense, NeuralLayer Output,
    ProbabilityCalibration TenpaiCalibration, ProbabilityCalibration WaitCalibration, float ValueScale,
    bool HasSearchLabels, string Status);

// Small CPU CNN; no native inference runtime or training dependency in the plugin.
// Output: policy[74], tenpai[3], conditional ron[102], points[102], search Q[74].
public sealed class LearnedModel
{
    public const int Outputs = 355;
    private readonly LearnedArtifact artifact;
    public SimulationRules Rules => this.artifact.Rules;
    public string Status => this.artifact.Status;
    public bool HasSearchLabels => this.artifact.HasSearchLabels;
    public LearnedModel(LearnedArtifact artifact)
    {
        artifact.Rules.Validate();
        if (artifact.Schema != 1 || artifact.Features != LearningFeatures.Version || string.IsNullOrWhiteSpace(artifact.Corpus)
            || artifact.Conv1.Inputs != LearningFeatures.Channels || artifact.Conv1.Kernel != 3
            || artifact.Conv2.Inputs != artifact.Conv1.Outputs || artifact.Conv2.Kernel != 3
            || artifact.Dense.Inputs != artifact.Conv2.Outputs * 34 || artifact.Dense.Kernel != 1
            || artifact.Output.Inputs != artifact.Dense.Outputs || artifact.Output.Outputs != Outputs || artifact.Output.Kernel != 1
            || !float.IsFinite(artifact.ValueScale) || artifact.ValueScale is < .1f or > 10f)
            throw new InvalidDataException("Incompatible learned model schema or dimensions.");
        foreach (var layer in new[] { artifact.Conv1, artifact.Conv2, artifact.Dense, artifact.Output })
            if (layer.Inputs is < 1 or > 8704 || layer.Outputs is < 1 or > 512
                || layer.Weight.Length != checked(layer.Inputs * layer.Outputs * layer.Kernel)
                || layer.Bias.Length != layer.Outputs || layer.Weight.Any(v => !float.IsFinite(v)) || layer.Bias.Any(v => !float.IsFinite(v)))
                throw new InvalidDataException("Invalid learned model weights.");
        foreach (var cal in new[] { artifact.TenpaiCalibration, artifact.WaitCalibration })
            if (!float.IsFinite(cal.Slope) || !float.IsFinite(cal.Bias) || cal.Slope <= 0 || cal.Slope > 100 || Math.Abs(cal.Bias) > 100)
                throw new InvalidDataException("Invalid probability calibration.");
        // Isolate immutable inference from callers retaining the deserialized arrays.
        NeuralLayer Copy(NeuralLayer l) => l with { Weight = (float[])l.Weight.Clone(), Bias = (float[])l.Bias.Clone() };
        this.artifact = artifact with { Conv1 = Copy(artifact.Conv1), Conv2 = Copy(artifact.Conv2), Dense = Copy(artifact.Dense), Output = Copy(artifact.Output) };
    }

    public static LearnedModel Load(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Model exceeds size limit.");
        return new LearnedModel(JsonSerializer.Deserialize<LearnedArtifact>(File.ReadAllText(path), SimulationFiles.Json)
            ?? throw new InvalidDataException("Empty learned model."));
    }

    public bool Supports(StateSnapshot state) => state.Ruleset.Kuitan == this.Rules.Kuitan
        && state.Ruleset.HandsInMatch == this.Rules.HandsInMatch;

    public float[] Predict(StateSnapshot state, CancellationToken ct = default) => this.Predict(LearningFeatures.Encode(state), ct);
    public float[] Predict(float[] input, CancellationToken ct = default)
    {
        if (input.Length != LearningFeatures.Count || input.Any(x => !float.IsFinite(x))) throw new ArgumentException("Invalid features.");
        var h1 = Apply(input, this.artifact.Conv1, 34, true, ct);
        var h2 = Apply(h1, this.artifact.Conv2, 34, true, ct);
        var h3 = Apply(h2, this.artifact.Dense, 1, true, ct);
        var output = Apply(h3, this.artifact.Output, 1, false, ct);
        if (output.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Nonfinite neural prediction.");
        return output;
    }

    public double Tenpai(float logit) => Calibrate(logit, this.artifact.TenpaiCalibration);
    public double Wait(float logit) => Calibrate(logit, this.artifact.WaitCalibration);
    public double Points(float value) => Math.Clamp(Softplus(value) * 32000 * this.artifact.ValueScale, 0, 128000);
    private static double Softplus(double x) => x > 20 ? x : Math.Log(1 + Math.Exp(x));
    private static double Calibrate(float x, ProbabilityCalibration cal) => 1 / (1 + Math.Exp(-Math.Clamp(cal.Slope * x + cal.Bias, -40, 40)));
    private static float[] Apply(float[] x, NeuralLayer l, int width, bool relu, CancellationToken ct)
    {
        var y = new float[l.Outputs * width];
        for (var o = 0; o < l.Outputs; o++)
        {
            ct.ThrowIfCancellationRequested();
            for (var pos = 0; pos < width; pos++)
            {
                var value = l.Bias[o];
                for (var i = 0; i < l.Inputs; i++)
                    for (var k = 0; k < l.Kernel; k++)
                    {
                        var src = pos + k - l.Kernel / 2;
                        if (src >= 0 && src < width) value += x[i * width + src] * l.Weight[(o * l.Inputs + i) * l.Kernel + k];
                    }
                y[o * width + pos] = relu ? Math.Max(0, value) : value;
            }
        }
        return y;
    }
}
