using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Marges;

/// <summary>One calendar month of actual figures, the forecast's input.</summary>
public sealed record MonthActual(DateOnly Mois, decimal Revenue, decimal Cost, decimal ChargesFixes, decimal ChargesVariables);

/// <summary>
/// The Prévisions tab's statistics, kept free of the database so they can be tested on their own.
///
/// Revenue is forecast month by month with damped Holt-Winters exponential smoothing over the
/// completed months (at most <see cref="FitWindow"/>): a level and a trend updated month by
/// month, recent months weighing most, the trend damped so growth levels off rather than
/// running away, and a multiplicative seasonal index per calendar month once two full years
/// are available. The smoothing parameters are the grid point with the smallest one-step-ahead
/// error on the shop's own history. The uncertainty is the standard error of those one-step
/// errors, widened for each month ahead as the model's own error propagation dictates.
///
/// A backtest then hides the last months, refits on the rest and compares its forecast for
/// them with what was really sold: that real error, not a guess, is the reliability shown.
///
/// Costs are not forecast separately: cost of sales and variable charges are taken as their
/// recent share of revenue, fixed charges as their recent monthly average - the same split the
/// break-even analysis uses - so every projected figure follows from the revenue forecast.
/// </summary>
public static class MargesForecast
{
    /// <summary>Completed months the trend is fitted on: enough for three seasonal cycles,
    /// recent enough to describe the shop as it is now.</summary>
    private const int FitWindow = 36;

    /// <summary>Months needed before a seasonal index is estimated: two of each calendar month.</summary>
    private const int SeasonalMinMonths = 24;

    /// <summary>Recent completed months the cost and charge rates are averaged over.</summary>
    private const int RateWindow = 6;

    /// <summary>How far ahead "first profitable month" and "losses recovered" are searched.</summary>
    private const int SearchHorizon = 36;

    /// <summary>Months hidden by the backtest, at most; never more than a third of the history.</summary>
    private const int BacktestMonths = 6;

    /// <summary>Months of history needed before a backtest means anything.</summary>
    private const int BacktestMinMonths = 9;

    /// <summary>z of an 80 % two-sided interval: the "bas"/"haut" bands.</summary>
    private const double Z80 = 1.2816;

    private static int Index(DateOnly d) => d.Year * 12 + d.Month - 1;
    private static DateOnly FromIndex(int i) => new(i / 12, i % 12 + 1, 1);

    /// <param name="history">Consecutive months, oldest first, ending with today's month
    /// (partial, up to <paramref name="today"/>). Empty when the shop has no activity yet.</param>
    /// <param name="firstSale">Day of the first sale, null when nothing has been sold.</param>
    public static MargesPrevisionResponse Build(IReadOnlyList<MonthActual> history, DateOnly? firstSale, DateOnly today)
    {
        var current = Index(today);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var byIndex = history.ToDictionary(m => Index(m.Mois));
        MonthActual Actual(int i) => byIndex.GetValueOrDefault(i) ?? new MonthActual(FromIndex(i), 0, 0, 0, 0);

        // --- Months the trend is fitted on: completed months since the first sale. The
        // opening month is usually partial, so it is dropped once there are others to use.
        var completed = firstSale is { } fs
            ? Enumerable.Range(Index(fs), Math.Max(0, current - Index(fs))).Select(Actual).ToList()
            : [];
        if (completed.Count >= 4) completed.RemoveAt(0);
        if (completed.Count > FitWindow) completed = completed.Skip(completed.Count - FitWindow).ToList();

        var thisMonth = Actual(current);
        var points = completed.Select(m => (Index(m.Mois), (double)m.Revenue)).ToList();
        var model = completed.Count > 0
            ? RevenueModel.Fit(points)
            // Nothing completed yet: the current month's run rate, with a wide band.
            : RevenueModel.Flat(current, (double)thisMonth.Revenue / today.Day * daysInMonth, 0.35);

        var (erreur, moisTestes) = Backtest(points);

        // --- Rates over the recent completed months (or this month alone, for a new shop)
        var rateMonths = completed.Count > 0 ? completed.TakeLast(RateWindow).ToList() : [thisMonth];
        var rateRevenue = rateMonths.Sum(m => m.Revenue);
        var costRate = rateRevenue > 0 ? rateMonths.Sum(m => m.Cost) / rateRevenue : 0m;
        var varRate = rateRevenue > 0 ? rateMonths.Sum(m => m.ChargesVariables) / rateRevenue : 0m;
        var tauxMcv = 1m - costRate - varRate;

        // Fixed charges: the average of the recent months, never below what has already been
        // booked this month. Charges recorded before the first sale (set-up costs) are left
        // out of the average - they would make every future month look like an opening month.
        var fixedMonths = history.Where(m => Index(m.Mois) < current && (firstSale is null || m.Mois >= FirstOfMonth(firstSale.Value)))
            .TakeLast(RateWindow).ToList();
        var fixedMonthly = fixedMonths.Count > 0 ? fixedMonths.Average(m => m.ChargesFixes) : thisMonth.ChargesFixes;

        // A whole future month at its forecast: revenue, then everything else from the rates.
        (decimal Rev, decimal Sd, decimal Gross, decimal Net) Forecast(int i)
        {
            var (mean, sd) = model.At(i);
            var rev = (decimal)mean;
            return (rev, (decimal)sd, rev * (1 - costRate), rev * tauxMcv - fixedMonthly);
        }

        // Today's month: what has been sold so far plus the forecast for the days left.
        var remainingShare = (decimal)(daysInMonth - today.Day) / daysInMonth;
        var currentFc = Forecast(current);
        var currentRestRev = currentFc.Rev * remainingShare;
        var currentFixed = Math.Max(thisMonth.ChargesFixes, fixedMonthly);

        // --- The calendar year: actual months, then today's, then forecast months
        var yearStart = Index(new DateOnly(today.Year, 1, 1));
        var yearEnd = yearStart + 11;

        var ytd = Enumerable.Range(yearStart, current - yearStart + 1).Select(Actual).ToList();
        var revenueRealise = ytd.Sum(m => m.Revenue);
        var grossRealise = ytd.Sum(m => m.Revenue - m.Cost);
        var beneficeRealise = ytd.Sum(NetOf);

        var revenueRestant = currentRestRev;
        var fixedRestant = currentFixed - thisMonth.ChargesFixes;
        var variance = Math.Pow((double)(currentFc.Sd * remainingShare), 2);
        for (var i = current + 1; i <= yearEnd; i++)
        {
            var f = Forecast(i);
            revenueRestant += f.Rev;
            fixedRestant += fixedMonthly;
            variance += Math.Pow((double)f.Sd, 2);
        }

        var revenueSd = (decimal)Math.Sqrt(variance);
        var revenueAnnee = revenueRealise + revenueRestant;
        var beneficeAnnee = beneficeRealise + revenueRestant * tauxMcv - fixedRestant;
        var beneficeSd = revenueSd * Math.Max(tauxMcv, 0);
        var probabilite = beneficeSd > 0
            ? (decimal)NormalCdf((double)(beneficeAnnee / beneficeSd)) * 100
            : beneficeAnnee >= 0 ? 100 : 0;

        // --- Break-even
        var fixedRealise = ytd.Sum(m => m.ChargesFixes);
        var fixedAnnee = fixedRealise + fixedRestant;
        decimal? seuilAnnuel = tauxMcv > 0 ? fixedAnnee / tauxMcv : null;
        decimal? seuilMensuel = tauxMcv > 0 ? fixedMonthly / tauxMcv : null;

        // The day cumulative revenue reaches the annual seuil, revenue spread evenly within
        // each month - the textbook point mort, over actual months then forecast ones.
        DateOnly? pointMort = null;
        if (seuilAnnuel is { } seuil)
        {
            var cumul = 0m;
            for (var i = yearStart; i <= yearEnd && pointMort is null; i++)
            {
                var rev = i < current ? Actual(i).Revenue
                    : i == current ? thisMonth.Revenue + currentRestRev
                    : Forecast(i).Rev;
                if (rev > 0 && cumul + rev >= seuil)
                {
                    var month = FromIndex(i);
                    var days = DateTime.DaysInMonth(month.Year, month.Month);
                    var day = (int)Math.Ceiling((seuil - cumul) / rev * days);
                    pointMort = month.AddDays(Math.Clamp(day, 1, days) - 1);
                }
                cumul += rev;
            }
        }

        // Revenue still needed per remaining day for the year to end at zero or better.
        var joursRestants = new DateOnly(today.Year, 12, 31).DayNumber - today.DayNumber;
        decimal? caJournalier = null;
        if (tauxMcv > 0)
        {
            var needed = (fixedRestant - beneficeRealise) / tauxMcv;
            caJournalier = needed <= 0 ? 0 : joursRestants > 0 ? needed / joursRestants : null;
        }

        // First month (from today's) whose own forecast pays its fixed charges.
        DateOnly? premierMoisRentable = null;
        for (var i = current; i <= current + SearchHorizon; i++)
        {
            if (Forecast(i).Net > 0) { premierMoisRentable = FromIndex(i); break; }
        }

        // Every result since the shop opened, and when the forecast earns any loss back.
        var resultatCumule = history.Where(m => Index(m.Mois) <= current).Sum(NetOf);
        DateOnly? recuperation = null;
        if (resultatCumule < 0)
        {
            var cumul = resultatCumule + currentRestRev * tauxMcv - (currentFixed - thisMonth.ChargesFixes);
            if (cumul >= 0) recuperation = FromIndex(current);
            for (var i = current + 1; i <= current + SearchHorizon && recuperation is null; i++)
            {
                cumul += Forecast(i).Net;
                if (cumul >= 0) recuperation = FromIndex(i);
            }
        }

        // --- Chart: the last twelve months, then forecasts to December (six months at least)
        var chartStart = Math.Max(current - 11, history.Count > 0 ? Index(history[0].Mois) : current);
        var chartEnd = Math.Max(yearEnd, current + 5);
        var mois = new List<MargePrevisionMoisDto>();
        for (var i = chartStart; i <= chartEnd; i++)
        {
            if (i < current)
            {
                var a = Actual(i);
                mois.Add(new(a.Mois, false, a.Revenue, a.Revenue, a.Revenue, a.Revenue - a.Cost, NetOf(a)));
            }
            else if (i == current)
            {
                var rev = thisMonth.Revenue + currentRestRev;
                var band = currentFc.Sd * remainingShare * (decimal)Z80;
                mois.Add(new(thisMonth.Mois, true, rev, Math.Max(0, rev - band), rev + band,
                    thisMonth.Revenue - thisMonth.Cost + currentRestRev * (1 - costRate),
                    NetOf(thisMonth) + currentRestRev * tauxMcv - (currentFixed - thisMonth.ChargesFixes)));
            }
            else
            {
                var f = Forecast(i);
                var band = f.Sd * (decimal)Z80;
                mois.Add(new(FromIndex(i), true, f.Rev, Math.Max(0, f.Rev - band), f.Rev + band, f.Gross, f.Net));
            }
        }

        return new MargesPrevisionResponse(
            today.Year, completed.Count, model.Seasonal, Fiabilite(completed.Count, erreur), erreur, moisTestes,
            model.GrowthPercent,
            costRate * 100, varRate * 100, tauxMcv * 100, fixedMonthly,
            revenueRealise, beneficeRealise, fixedRealise, revenueRestant, fixedRestant,
            revenueAnnee, Math.Max(revenueRealise, revenueAnnee - revenueSd * (decimal)Z80), revenueAnnee + revenueSd * (decimal)Z80,
            grossRealise + revenueRestant * (1 - costRate),
            beneficeAnnee, beneficeAnnee - beneficeSd * (decimal)Z80, beneficeAnnee + beneficeSd * (decimal)Z80,
            probabilite,
            seuilAnnuel, pointMort, seuilMensuel, premierMoisRentable,
            caJournalier, joursRestants,
            resultatCumule, recuperation, firstSale,
            mois);
    }

    private static decimal NetOf(MonthActual m) => m.Revenue - m.Cost - m.ChargesFixes - m.ChargesVariables;

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>
    /// Hides the last months (up to <see cref="BacktestMonths"/>, a third of the history at
    /// most), fits on what came before and forecasts them: the weighted absolute percentage
    /// error, Σ|réel − prévu| ÷ Σ réel, in %. Null when the history is too short to spare them.
    /// </summary>
    public static (decimal? Erreur, int Mois) Backtest(IReadOnlyList<(int Index, double Y)> points)
    {
        if (points.Count < BacktestMinMonths) return (null, 0);

        var k = Math.Min(BacktestMonths, points.Count / 3);
        var train = points.Take(points.Count - k).ToList();
        var test = points.Skip(points.Count - k).ToList();
        var model = RevenueModel.Fit(train);

        var actual = test.Sum(p => p.Y);
        if (actual <= 0) return (null, 0);
        var error = test.Sum(p => Math.Abs(p.Y - model.At(p.Index).Mean));
        return ((decimal)(error / actual * 100), k);
    }

    /// <summary>How far the forecast can be trusted: the backtest's real error when there is
    /// one; otherwise at best "moyenne", and "faible" under six months.</summary>
    private static string Fiabilite(int months, decimal? erreur) => erreur switch
    {
        < 10 => MargePrevisionFiabilite.Elevee,
        < 25 => MargePrevisionFiabilite.Moyenne,
        null when months >= 6 => MargePrevisionFiabilite.Moyenne,
        _ => MargePrevisionFiabilite.Faible,
    };

    /// <summary>Standard normal CDF (Abramowitz &amp; Stegun 7.1.26, error below 1.5e-7).</summary>
    public static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * x);
        var erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }

    /// <summary>
    /// Damped Holt-Winters: level ℓ, trend b and (with two years of history) a multiplicative
    /// seasonal index s per calendar month. For each month y:
    ///   ŷ = (ℓ + φb)·s,  ℓ' = α·y/s + (1−α)(ℓ + φb),  b' = β(ℓ' − ℓ) + (1−β)φb,  s' = γ·y/ℓ' + (1−γ)s.
    /// h months ahead: (ℓ + b(φ + φ² + … + φʰ))·s.
    /// </summary>
    internal sealed class RevenueModel
    {
        private static readonly double[] Alphas = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9];
        private static readonly double[] Betas = [0.01, 0.05, 0.1, 0.2, 0.3];
        private static readonly double[] Phis = [0.8, 0.9, 0.98];
        private static readonly double[] Gammas = [0.05, 0.1, 0.2, 0.3];

        /// <summary>One-step errors before this many months are not counted: the start-up
        /// guesses for level and trend should not decide the parameters.</summary>
        private const int WarmUp = 3;

        private int _last;
        private double _level, _trend, _alpha, _beta, _phi, _sigma;
        private double[] _season = Enumerable.Repeat(1.0, 12).ToArray();

        public bool Seasonal { get; private set; }

        /// <summary>Trend growth per month as a % of the current level; null without a trend.</summary>
        public decimal? GrowthPercent { get; private set; }

        public static RevenueModel Flat(int origin, double level, double relativeSigma) => new()
        {
            _last = origin - 1, _level = Math.Max(level, 0), _sigma = Math.Max(level, 0) * relativeSigma,
        };

        public static RevenueModel Fit(IReadOnlyList<(int Index, double Y)> points)
        {
            var n = points.Count;
            var mean = points.Average(p => p.Y);
            var last = points[^1].Index;

            if (n < 3)
            {
                // Too few months for a trend: their average, at least ±20 %.
                var sd = n > 1 ? Math.Sqrt(points.Sum(p => Math.Pow(p.Y - mean, 2)) / (n - 1)) : 0;
                return new() { _last = last, _level = mean, _sigma = Math.Max(sd, mean * 0.2) };
            }

            var seasonal = n >= SeasonalMinMonths;
            double[] gammas = seasonal ? Gammas : [0.0];

            RevenueModel? best = null;
            var bestSse = double.MaxValue;
            foreach (var a in Alphas)
            foreach (var b in Betas)
            foreach (var phi in Phis)
            foreach (var g in gammas)
            {
                var m = Run(points, seasonal, a, b, phi, g, out var sse);
                if (sse < bestSse) (best, bestSse) = (m, sse);
            }

            // Never a zero-width band: at least 5 % of the average month.
            var counted = Math.Max(1, n - Math.Min(WarmUp, n - 1));
            best!._sigma = Math.Max(Math.Sqrt(bestSse / counted), mean * 0.05);
            best.Seasonal = seasonal;
            best.GrowthPercent = best._level > 0 ? (decimal)(best._trend * best._phi / best._level * 100) : null;
            return best;
        }

        private static RevenueModel Run(IReadOnlyList<(int Index, double Y)> points, bool seasonal,
            double alpha, double beta, double phi, double gamma, out double sse)
        {
            var n = points.Count;
            var season = Enumerable.Repeat(1.0, 12).ToArray();
            double level, trend;

            if (seasonal)
            {
                // Start-up: each calendar month's ratio to its own year's mean over the first
                // two years; level the first year's mean, trend the yearly change ÷ 12.
                var y1 = points.Take(12).Average(p => p.Y);
                var y2 = points.Skip(12).Take(12).Average(p => p.Y);
                var ratios = new double[12];
                var counts = new int[12];
                for (var j = 0; j < 24; j++)
                {
                    var yearMean = j < 12 ? y1 : y2;
                    if (yearMean <= 0) continue;
                    ratios[points[j].Index % 12] += points[j].Y / yearMean;
                    counts[points[j].Index % 12]++;
                }
                for (var k = 0; k < 12; k++)
                    season[k] = counts[k] > 0 && ratios[k] > 0 ? Math.Clamp(ratios[k] / counts[k], 0.3, 3) : 1;
                var avg = season.Average();
                for (var k = 0; k < 12; k++) season[k] /= avg;
                level = y1;
                trend = (y2 - y1) / 12;
            }
            else
            {
                // Start-up: the least-squares line through the first months (up to six), placed
                // so the first one-step forecast, ℓ + φb, falls on that line.
                var first = points.Take(Math.Min(n, 6)).Select((p, j) => (X: (double)j, p.Y)).ToList();
                var xm = first.Average(p => p.X);
                var ym = first.Average(p => p.Y);
                var sxx = first.Sum(p => Math.Pow(p.X - xm, 2));
                trend = sxx > 0 ? first.Sum(p => (p.X - xm) * (p.Y - ym)) / sxx : 0;
                level = ym - trend * xm - phi * trend;
            }

            sse = 0;
            var warmUp = Math.Min(WarmUp, n - 1);
            for (var j = 0; j < n; j++)
            {
                var (index, y) = points[j];
                var s = season[index % 12];
                var forecast = (level + phi * trend) * s;
                if (j >= warmUp) sse += Math.Pow(y - forecast, 2);

                var newLevel = alpha * (s > 0 ? y / s : y) + (1 - alpha) * (level + phi * trend);
                trend = beta * (newLevel - level) + (1 - beta) * phi * trend;
                level = newLevel;
                if (seasonal && level > 0) season[index % 12] = gamma * (y / level) + (1 - gamma) * s;
            }

            return new()
            {
                _last = points[^1].Index, _level = level, _trend = trend,
                _alpha = alpha, _beta = beta, _phi = phi, _season = season,
            };
        }

        /// <summary>Forecast mean and standard error for a month (by its year*12+month-1 index).
        /// A month not after the last one fitted gets the one-month-ahead figure.</summary>
        public (double Mean, double Sd) At(int monthIndex)
        {
            var h = Math.Max(1, monthIndex - _last);
            double damped = 0, phiPow = 1;
            for (var i = 1; i <= h; i++) { phiPow *= _phi; damped += phiPow; }
            var season = _season[monthIndex % 12];

            // Error propagation of damped Holt: Var(h) = σ²(1 + Σ_{j<h} (α(1 + β(φ + … + φʲ)))²).
            double variance = 1, partial = 0, p = 1;
            for (var j = 1; j < h; j++)
            {
                p *= _phi;
                partial += p;
                variance += Math.Pow(_alpha * (1 + _beta * partial), 2);
            }

            return (Math.Max(0, (_level + _trend * damped) * season), _sigma * Math.Sqrt(variance) * season);
        }
    }
}
