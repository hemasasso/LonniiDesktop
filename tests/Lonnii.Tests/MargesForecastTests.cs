using Lonnii.Api.Features.Marges;
using Lonnii.Shared.Contracts;

namespace Lonnii.Tests;

/// <summary>
/// Covers <see cref="MargesForecast"/> on shops whose answer is known by hand: a perfectly
/// steady shop must project exactly its own run rate, a growing loss-making one must be told
/// when it turns profitable, and a shop with no history must not break the screen.
/// </summary>
public class MargesForecastTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);

    private static List<MonthActual> Months(DateOnly first, int count, Func<int, MonthActual> make) =>
        Enumerable.Range(0, count).Select(make).ToList();

    [Fact]
    public void Steady_shop_projects_its_run_rate_and_break_even()
    {
        // 1 000 000 of sales a month at 60 % cost, 300 000 of fixed charges: 100 000 net a month.
        var first = new DateOnly(2025, 1, 1);
        var history = Months(first, 22, i =>
        {
            var month = first.AddMonths(i);
            // Today's month is only 11 days in.
            var share = month == new DateOnly(2026, 10, 1) ? 11m / 31 : 1m;
            return new MonthActual(month, 1_000_000 * share, 600_000 * share, 300_000, 0);
        });

        var r = MargesForecast.Build(history, first, Today);

        Assert.Equal(40m, Math.Round(r.TauxMcv, 3));
        Assert.Equal(300_000m, Math.Round(r.ChargesFixesMensuelles));
        Assert.InRange(r.RevenueAnnee, 11_999_000m, 12_001_000m);
        Assert.InRange(r.BeneficeAnnee, 1_199_000m, 1_201_000m);
        Assert.InRange(r.SeuilAnnuel!.Value, 8_999_000m, 9_001_000m);
        Assert.Equal(new DateOnly(2026, 9, 30), r.PointMortDate);
        Assert.Equal(new DateOnly(2026, 10, 1), r.PremierMoisRentable);
        Assert.Equal(0m, r.CaJournalierNecessaire);
        Assert.True(r.ProbabiliteBenefice > 99);
        Assert.Equal(MargePrevisionFiabilite.Elevee, r.Fiabilite);
        Assert.Null(r.DateRecuperationPertes);
    }

    [Fact]
    public void Growing_loss_making_shop_is_told_when_it_turns_profitable()
    {
        // Sales up 100 000 a month from 200 000, at 50 % cost, against 800 000 of fixed charges:
        // the month pays for itself once sales pass 1 600 000.
        var first = new DateOnly(2026, 1, 1);
        var history = Months(first, 10, i =>
        {
            var rev = 200_000m + 100_000m * i;
            if (i == 9) rev *= 11m / 31;
            return new MonthActual(first.AddMonths(i), rev, rev / 2, 800_000, 0);
        });

        var r = MargesForecast.Build(history, first, Today);

        Assert.True(r.BeneficeAnnee < 0);
        Assert.True(r.ProbabiliteBenefice < 5);
        Assert.True(r.ResultatCumule < 0);
        Assert.True(r.CroissanceMensuelle > 0);
        Assert.NotNull(r.PremierMoisRentable);
        Assert.True(r.PremierMoisRentable > new DateOnly(2026, 12, 1));
        // Losses are earned back, if at all within the three years searched, only once the
        // months themselves are profitable.
        if (r.DateRecuperationPertes is { } recovered) Assert.True(recovered > r.PremierMoisRentable);
        Assert.Null(r.PointMortDate);
        Assert.True(r.CaJournalierNecessaire > 0);
    }

    [Fact]
    public void Backtest_measures_the_real_error_and_follows_seasons()
    {
        // Three years of a shop selling twice as much every December, with ±5 % noise.
        var first = new DateOnly(2023, 10, 1);
        var random = new Random(42);
        var history = Months(first, 37, i =>
        {
            var month = first.AddMonths(i);
            var rev = 1_000_000m * (month.Month == 12 ? 2 : 1) * (decimal)(0.95 + random.NextDouble() * 0.1);
            if (i == 36) rev *= 11m / 31;
            return new MonthActual(month, rev, rev * 0.6m, 300_000, 0);
        });

        var r = MargesForecast.Build(history, first, Today);

        Assert.True(r.Saisonnier);
        Assert.Equal(6, r.MoisTestes);
        Assert.True(r.ErreurMoyenne < 10, $"erreur {r.ErreurMoyenne}");
        Assert.Equal(MargePrevisionFiabilite.Elevee, r.Fiabilite);
        var december = r.Mois.Single(m => m.Mois == new DateOnly(2026, 12, 1));
        var november = r.Mois.Single(m => m.Mois == new DateOnly(2026, 11, 1));
        Assert.True(december.Revenue > november.Revenue * 1.6m, $"déc. {december.Revenue} / nov. {november.Revenue}");
    }

    [Fact]
    public void Shop_without_history_still_answers()
    {
        var r = MargesForecast.Build([], null, Today);

        Assert.Equal(0m, r.RevenueAnnee);
        Assert.Equal(MargePrevisionFiabilite.Faible, r.Fiabilite);
        Assert.NotEmpty(r.Mois);
    }

    [Fact]
    public void Normal_cdf_matches_known_values()
    {
        Assert.Equal(0.5, MargesForecast.NormalCdf(0), 6);
        Assert.Equal(0.9, MargesForecast.NormalCdf(1.2816), 3);
        Assert.Equal(0.1, MargesForecast.NormalCdf(-1.2816), 3);
    }
}
