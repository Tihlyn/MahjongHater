using MahjongHater.Core.Policy;
using Xunit;
using Xunit.Abstractions;

namespace MahjongHater.Tests.Policy.Golden;

// Scores DecisionPolicy against every golden position and prints the disagreements.
// Agreement is asserted only for positions listed in MustAgree; the rest is a report that
// phase 4 of docs/DEFENSE_PLAN.md turns into assertions as the policy catches up.
public class GoldenPositionTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> MustAgree =
    [
        "rb1-ch8-perfect-1shanten-push",
    ];

    [Fact]
    public void Every_position_loads_and_gets_a_decision()
    {
        var all = GoldenPositions.LoadAll();
        Assert.NotEmpty(all);
        var policy = new DecisionPolicy();
        var agreed = 0;
        var failures = new List<string>();
        foreach (var (file, position) in all)
        {
            var snapshot = GoldenPositions.ToSnapshot(position.Situation);
            Assert.Equal(14, snapshot.Hand.Count + 3 * snapshot.OurMelds.Count);
            var choice = policy.Choose(snapshot, CancellationToken.None);
            Assert.NotEqual(ActionKind.None, choice.Kind);
            var why = GoldenPositions.Disagreement(position, choice);
            if (why is null)
                agreed++;
            output.WriteLine($"{(why is null ? "OK  " : "DIFF")} {file}:{position.Id} → {choice.Kind} {choice.Tile}{(why is null ? string.Empty : "  (" + why + ")")}");
            if (why is not null && MustAgree.Contains(position.Id))
                failures.Add($"{position.Id}: {why}");
        }

        output.WriteLine($"agreement {agreed}/{all.Count}");
        Assert.Empty(failures);
    }
}
