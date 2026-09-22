namespace MahjongHater.Core;

public sealed class ScoringEngine
{
    private static readonly Lazy<bool> ReferenceScoresValidated = new(ValidateReferenceScores);

    public ScoringEngine()
    {
        _ = ReferenceScoresValidated.Value;
    }

    public ScoreResult Calculate(Hand hand, List<YakuResult> yaku, int fu, bool isDealer)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(yaku);

        var hanFromYaku = yaku.Where(result => !result.IsYakuman).Sum(result => result.Han);
        var totalHan = hanFromYaku + hand.DoraCount + hand.AkadoraCount + hand.UraDoraCount;
        var yakumanCount = Math.Min(4, yaku.Count(result => result.IsYakuman));
        var result = new ScoreResult
        {
            Han = yakumanCount > 0 ? yakumanCount * 13 : totalHan,
            Fu = fu,
            HonbaBonus = 0,
        };

        if (yakumanCount > 0)
        {
            result.IsLimit = true;
            result.LimitName = yakumanCount switch
            {
                1 => "Yakuman",
                2 => "Double Yakuman",
                3 => "Triple Yakuman",
                _ => "Quadruple Yakuman",
            };

            result.BasePoints = 8000 * yakumanCount;
            ApplyPayments(result, hand.WinMethod, isDealer, result.BasePoints);
            return result;
        }

        var basePoints = BasePointsFor(fu, totalHan, out var limitName);
        if (limitName is null)
        {
            result.BasePoints = basePoints;
            ApplyPayments(result, hand.WinMethod, isDealer, basePoints);
        }
        else
        {
            ApplyLimit(result, hand.WinMethod, isDealer, basePoints, limitName);
        }

        return result;
    }

    // The base-point table, shared by the scorer and by the live check against the game's own
    // win screen. Doman follows the standard limits: mangan at 5 han, or 4 han 40 fu, or
    // 3 han 70 fu. Round-up ("kiriage") mangan at 4 han 30 fu / 3 han 60 fu is NOT applied -
    // no such hand has been observed yet, and the 23 win screens of 2026-09-22 all matched
    // this table exactly (docs/research/RULES_CROSSCHECK_2026_09_22.md).
    public static int BasePointsFor(int fu, int totalHan, out string? limitName)
    {
        limitName = totalHan switch
        {
            >= 13 => "Kazoe Yakuman",
            >= 11 => "Sanbaiman",
            >= 8 => "Baiman",
            >= 6 => "Haneman",
            5 => "Mangan",
            4 when fu >= 40 => "Mangan",
            3 when fu >= 70 => "Mangan",
            _ => null,
        };
        return limitName switch
        {
            "Kazoe Yakuman" => 8000,
            "Sanbaiman" => 6000,
            "Baiman" => 4000,
            "Haneman" => 3000,
            "Mangan" => 2000,
            _ => fu * (1 << (2 + totalHan)),
        };
    }

    // What the table says the winner collects in total, for a hand the GAME has already
    // scored. Used to check our rules against the win screen every hand, which is the only
    // way a rule difference (a limit boundary, a rounding rule) shows up as evidence rather
    // than as a slow bias in every hand value we estimate.
    public static int TotalPaymentFor(int fu, int han, bool isDealer, bool tsumo)
    {
        var basePoints = BasePointsFor(fu, han, out _);
        if (!tsumo)
            return RoundUpHundred(basePoints * (isDealer ? 6 : 4));
        return isDealer
            ? RoundUpHundred(basePoints * 2) * 3
            : RoundUpHundred(basePoints * 2) + (RoundUpHundred(basePoints) * 2);
    }

    private static void ApplyLimit(ScoreResult result, WinMethod winMethod, bool isDealer, int basePoints, string limitName)
    {
        result.IsLimit = true;
        result.LimitName = limitName;
        result.BasePoints = basePoints;
        ApplyPayments(result, winMethod, isDealer, basePoints);
    }

    private static void ApplyPayments(ScoreResult result, WinMethod winMethod, bool isDealer, int basePoints)
    {
        if (winMethod == WinMethod.Ron)
        {
            result.RonPayment = RoundUpHundred(basePoints * (isDealer ? 6 : 4));
            return;
        }

        if (isDealer)
        {
            result.TsumoPaymentDealer = RoundUpHundred(basePoints * 2);
            result.TotalTsumoPayment = result.TsumoPaymentDealer * 3;
        }
        else
        {
            result.TsumoPaymentDealer = RoundUpHundred(basePoints * 2);
            result.TsumoPaymentNonDealer = RoundUpHundred(basePoints);
            result.TotalTsumoPayment = result.TsumoPaymentDealer + (result.TsumoPaymentNonDealer * 2);
        }
    }

    private static int RoundUpHundred(int value)
    {
        return ((value + 99) / 100) * 100;
    }

    private static bool ValidateReferenceScores()
    {
        AssertReferenceRon(1, 30, 1000);
        AssertReferenceRon(2, 30, 2000);
        AssertReferenceRon(3, 30, 3900);
        AssertReferenceRon(3, 40, 5200);
        AssertReferenceRon(4, 30, 7700);
        return true;
    }

    private static void AssertReferenceRon(int han, int fu, int expectedRon)
    {
        var payment = TotalPaymentFor(fu, han, isDealer: false, tsumo: false);
        if (payment != expectedRon)
        {
            throw new InvalidOperationException($"Reference score validation failed for {han} han {fu} fu.");
        }
    }
}

public sealed class ScoreResult
{
    public int Han { get; set; }

    public int Fu { get; set; }

    public int BasePoints { get; set; }

    public bool IsLimit { get; set; }

    public string LimitName { get; set; } = string.Empty;

    public int RonPayment { get; set; }

    public int TsumoPaymentDealer { get; set; }

    public int TsumoPaymentNonDealer { get; set; }

    public int TotalTsumoPayment { get; set; }

    public int HonbaBonus { get; set; }
}
