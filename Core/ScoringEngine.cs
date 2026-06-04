namespace MahjongHater.Core;

public sealed class ScoringEngine
{
    private static bool referenceScoresValidated;
    private readonly Configuration configuration;

    public ScoringEngine(Configuration configuration)
    {
        this.configuration = configuration;
        if (!referenceScoresValidated)
        {
            ValidateReferenceScores();
            referenceScoresValidated = true;
        }
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

        var basePoints = fu * (1 << (2 + totalHan));
        if (totalHan >= 13)
        {
            ApplyLimit(result, hand.WinMethod, isDealer, 8000, "Kazoe Yakuman");
        }
        else if (totalHan is >= 11 and <= 12)
        {
            ApplyLimit(result, hand.WinMethod, isDealer, 6000, "Sanbaiman");
        }
        else if (totalHan is >= 8 and <= 10)
        {
            ApplyLimit(result, hand.WinMethod, isDealer, 4000, "Baiman");
        }
        else if (totalHan is >= 6 and <= 7)
        {
            ApplyLimit(result, hand.WinMethod, isDealer, 3000, "Haneman");
        }
        else if (totalHan == 5 || (totalHan == 4 && fu >= 40) || (totalHan == 3 && fu >= 70))
        {
            ApplyLimit(result, hand.WinMethod, isDealer, 2000, "Mangan");
        }
        else
        {
            result.BasePoints = basePoints;
            ApplyPayments(result, hand.WinMethod, isDealer, basePoints);
        }

        return result;
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

    private static void ValidateReferenceScores()
    {
        AssertReferenceRon(1, 30, 1000);
        AssertReferenceRon(2, 30, 2000);
        AssertReferenceRon(3, 30, 3900);
        AssertReferenceRon(3, 40, 5200);
        AssertReferenceRon(4, 30, 7700);
    }

    private static void AssertReferenceRon(int han, int fu, int expectedRon)
    {
        var totalHan = han;
        var basePoints = fu * (1 << (2 + totalHan));
        if (totalHan == 5 || (totalHan == 4 && fu >= 40) || (totalHan == 3 && fu >= 70))
        {
            basePoints = 2000;
        }

        var payment = RoundUpHundred(basePoints * 4);
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
