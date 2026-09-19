using System.Globalization;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Policy;

// One ground-truth row: an opponent's features at the end of a hand and whether they
// were tenpai. Draw screens label every seat ("Tenpai!" / "Noten..."); a win only
// proves the winner. Rows accumulate in tenpai_calibration.csv for tools/fit_tenpai.py.
public sealed record TenpaiSample(
    DateTime Utc,
    string Round,
    int Seat,
    TenpaiFeatures Features,
    double Predicted,
    bool Tenpai,
    string Source);

public static class TenpaiCalibration
{
    public const string CsvHeader = "utc,round,seat,discards,openMelds,riichi,earlyOutside,lateMiddle,predicted,tenpai,source";

    public static string ToCsv(TenpaiSample s) => string.Join(",",
        s.Utc.ToString("O", CultureInfo.InvariantCulture),
        s.Round,
        s.Seat.ToString(CultureInfo.InvariantCulture),
        s.Features.Discards.ToString(CultureInfo.InvariantCulture),
        s.Features.OpenMelds.ToString(CultureInfo.InvariantCulture),
        s.Features.Riichi ? "1" : "0",
        s.Features.EarlyOutside.ToString("F3", CultureInfo.InvariantCulture),
        s.Features.LateMiddle.ToString("F3", CultureInfo.InvariantCulture),
        s.Predicted.ToString("F3", CultureInfo.InvariantCulture),
        s.Tenpai ? "1" : "0",
        s.Source);

    // Seat banner text → tenpai fact. Null = says nothing about this hand (residue such
    // as "Pon!" or an empty banner).
    public static bool? BannerMeansTenpai(string? banner)
    {
        var t = banner?.Trim();
        if (string.IsNullOrEmpty(t))
            return null;
        if (t.StartsWith("Tenpai", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Tsumo", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Ron", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.StartsWith("Noten", StringComparison.OrdinalIgnoreCase))
            return false;
        return null;
    }

    // Samples for one finished hand. `last` is the final in-play snapshot (opponent
    // discards/melds/riichi as they stood when the hand ended); `banners` are the four
    // seat banners in relative-seat order; `winnerSeat` is the type-32 winner or -1 for
    // a draw. Opponents only — our own hands are not what the model predicts.
    public static IReadOnlyList<TenpaiSample> FromRoundEnd(
        StateSnapshot last, IReadOnlyList<string?> banners, int winnerSeat, PolicyWeights weights, DateTime utc)
    {
        var round = last.RoundWind.ToString();
        var samples = new List<TenpaiSample>(3);
        foreach (var seat in last.Seats)
        {
            if (seat.Seat == 0)
                continue;

            bool? tenpai;
            string source;
            if (winnerSeat >= 0)
            {
                // A win proves only the winner; everyone else's banner may be residue.
                if (seat.Seat != winnerSeat)
                    continue;
                tenpai = true;
                source = "win";
            }
            else
            {
                tenpai = seat.Seat < banners.Count ? BannerMeansTenpai(banners[seat.Seat]) : null;
                source = "draw";
            }

            if (tenpai is null)
                continue;

            var features = TenpaiFeatures.From(seat);
            samples.Add(new TenpaiSample(utc, round, seat.Seat, features, TenpaiEstimator.Estimate(features, weights), tenpai.Value, source));
        }

        return samples;
    }

    // True once every opponent banner reads Tenpai/Noten — the draw announcement has
    // landed for all seats and the banners can be trusted for this hand.
    public static bool DrawBannersComplete(IReadOnlyList<string?> banners)
    {
        if (banners.Count < 4)
            return false;
        for (var seat = 1; seat < 4; seat++)
        {
            var t = banners[seat]?.Trim() ?? string.Empty;
            if (!(t.StartsWith("Tenpai", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Noten", StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }
}
