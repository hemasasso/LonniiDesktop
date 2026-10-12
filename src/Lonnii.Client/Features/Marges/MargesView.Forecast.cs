using System.Windows;
using System.Windows.Controls;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Lonnii.Client.Features.Marges;

/// <summary>
/// The Prévisions tab: the calendar year projected from the monthly history by
/// GET /api/marges/previsions - year-end revenue and profit with an 80 % range, the chance
/// of a profitable year, the annual break-even and when it is reached, and a what-if
/// simulator re-running the year's result client-side with other assumptions.
/// </summary>
public partial class MargesView
{
    private MargesPrevisionResponse? _forecast;

    /// <summary>Amount with its sign - <see cref="Amount"/> already writes the "-" (via
    /// Money.WithLabel); named apart to mark the places a range can cross zero.</summary>
    private static string Signed(decimal value) => Amount(value);

    private static string MonthName(DateOnly month) =>
        month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", French);

    private static string ReliabilityLabel(string fiabilite) => fiabilite switch
    {
        MargePrevisionFiabilite.Elevee => "Fiabilité élevée",
        MargePrevisionFiabilite.Moyenne => "Fiabilité moyenne",
        _ => "Fiabilité faible",
    };

    private async Task LoadForecastAsync()
    {
        try
        {
            var categoryId = SelectedCategoryId;
            var forecast = await _session.Api.GetMargesPrevisionsAsync(categoryId);
            if (categoryId != SelectedCategoryId) return; // the filter changed meanwhile; its own load follows
            _forecast = forecast;
            HideMessage();
            RenderForecast();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void RenderForecast()
    {
        if (_forecast is not { } f) return;

        // --- Method and reliability
        var reliabilityKey = f.Fiabilite switch
        {
            MargePrevisionFiabilite.Elevee => "Success",
            MargePrevisionFiabilite.Moyenne => "Warning",
            _ => "Danger",
        };
        ForecastReliabilityText.Text = ReliabilityLabel(f.Fiabilite)
            + (f.ErreurMoyenne is { } err ? $" · erreur ±{Money.FormatPlain(err, 0)} %" : string.Empty);
        ForecastReliabilityText.SetResourceReference(TextBlock.ForegroundProperty, reliabilityKey);
        ForecastReliabilityBadge.SetResourceReference(Border.BorderBrushProperty, reliabilityKey);
        ForecastReliabilityBadge.ToolTip = f.ErreurMoyenne is { } e
            ? $"Test sur vos propres ventes : les {f.MoisTestes} derniers mois ont été cachés au modèle, puis prévus à partir "
              + $"des mois précédents. Écart moyen entre prévu et réel : {Money.FormatPlain(e, 1)} % du CA.\n"
              + "Moins de 10 % : élevée · moins de 25 % : moyenne · au-delà : faible."
            : f.MoisHistorique < 6
                ? "Moins de 6 mois complets d'historique : la projection reste indicative."
                : "Il faut au moins 9 mois complets pour tester la prévision sur vos propres ventes.";
        ToolTipService.SetShowDuration(ForecastReliabilityBadge, 30000);

        ForecastMethodText.Text =
            (f.MoisHistorique > 0
                ? $"Lissage exponentiel de Holt-Winters sur {f.MoisHistorique} mois complets (mois récents plus pondérés, tendance amortie"
                  + (f.Saisonnier ? ", saisonnalité mensuelle)" : ")")
                : "Aucun mois complet encore : projection du rythme du mois en cours")
            + $", fourchette à 80 %. Coût des ventes ({Percent(f.TauxCoutVentes)} du CA) et charges variables "
            + $"({Percent(f.TauxChargesVariables)}) suivent leur part récente du CA ; charges fixes : "
            + $"{Amount(f.ChargesFixesMensuelles)} par mois."
            + (SelectedCategoryId is null ? string.Empty : " Les charges restent celles de toutes les catégories.");

        // --- Year-end tiles
        ForecastTilesPanel.Children.Clear();
        ForecastBreakEvenPanel.Children.Clear();

        ForecastTilesPanel.Children.Add(Tile($"CA Prévu {f.Annee}", Amount(f.RevenueAnnee), "Accent",
            $"Entre {Amount(f.RevenueAnneeBas)} et {Amount(f.RevenueAnneeHaut)}\nDéjà réalisé : {Amount(f.RevenueRealise)}",
            formula: "CA réalisé depuis le 1er janvier + CA prévu jusqu'au 31 décembre.\nFourchette : intervalle de prévision à 80 %."));
        ForecastTilesPanel.Children.Add(Tile($"Marge Brute Prévue {f.Annee}", Signed(f.MargeBruteAnnee),
            f.MargeBruteAnnee >= 0 ? "Success" : "Danger",
            $"Taux de marque : {Percent(100 - f.TauxCoutVentes)}"));
        ForecastTilesPanel.Children.Add(Tile($"Bénéfice Net Prévu {f.Annee}", Signed(f.BeneficeAnnee),
            f.BeneficeAnnee >= 0 ? "Success" : "Danger",
            $"Entre {Signed(f.BeneficeAnneeBas)} et {Signed(f.BeneficeAnneeHaut)}\nÀ ce jour : {Signed(f.BeneficeRealise)}",
            formula: "Bénéfice réalisé + CA restant prévu × taux de MCV − charges fixes restantes"));
        ForecastTilesPanel.Children.Add(Tile("Probabilité d'Être Rentable", Percent(f.ProbabiliteBenefice),
            f.ProbabiliteBenefice >= 70 ? "Success" : f.ProbabiliteBenefice >= 40 ? "Warning" : "Danger",
            $"Chance de finir {f.Annee} en bénéfice",
            formula: "Probabilité que le bénéfice net de l'année soit positif, d'après l'incertitude de la prévision du CA (loi normale)."));
        ForecastTilesPanel.Children.Add(Tile("Tendance du CA",
            f.CroissanceMensuelle is { } g ? $"{SignedPercent(g)} / mois" : "—",
            f.CroissanceMensuelle is { } g2 ? g2 >= 0 ? "Success" : "Danger" : "TextMuted",
            f.CroissanceMensuelle is null ? "Pas assez de mois pour une tendance" : "Variation mensuelle moyenne",
            formula: "Pente de la droite de tendance, en % du mois moyen. Amortie au-delà du dernier mois observé."));

        // --- Break-even tiles
        const string SeuilFormula = "Seuil annuel = charges fixes de l'année (réelles + prévues) ÷ taux de MCV\n" +
            "Point mort = jour où le CA cumulé depuis le 1er janvier atteint ce seuil.";
        if (f.SeuilAnnuel is { } seuil)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var sub = f.PointMortDate is { } pm
                ? pm <= today ? $"Point mort atteint le {pm:dd/MM/yyyy}" : $"Point mort prévu le {pm:dd/MM/yyyy}"
                : "Non atteint d'ici le 31/12";
            ForecastBreakEvenPanel.Children.Add(Tile($"Seuil de Rentabilité {f.Annee}", Amount(seuil),
                f.PointMortDate is null ? "Danger" : f.PointMortDate <= today ? "Success" : "Warning", sub, formula: SeuilFormula));
        }
        else
        {
            ForecastBreakEvenPanel.Children.Add(Tile($"Seuil de Rentabilité {f.Annee}", "—", "TextMuted",
                "Taux de MCV nul ou négatif : aucun CA ne couvre les charges fixes", formula: SeuilFormula));
        }

        if (f.SeuilMensuel is { } seuilMois)
        {
            ForecastBreakEvenPanel.Children.Add(Tile("CA Mensuel d'Équilibre", Amount(seuilMois), "TextPrimary",
                $"Soit {Amount(seuilMois / 30)} par jour",
                formula: "Charges fixes mensuelles moyennes ÷ taux de MCV"));
        }

        if (f.CaJournalierNecessaire is { } perDay)
        {
            ForecastBreakEvenPanel.Children.Add(perDay == 0
                ? Tile("CA Journalier Nécessaire", "Atteint", "Success", $"L'année {f.Annee} est déjà à l'équilibre")
                : Tile("CA Journalier Nécessaire", Amount(perDay), "Warning",
                    $"Pendant les {f.JoursRestants} jours restants, pour finir {f.Annee} à l'équilibre",
                    formula: "(charges fixes restantes − bénéfice réalisé) ÷ taux de MCV ÷ jours restants"));
        }

        ForecastBreakEvenPanel.Children.Add(Tile("Premier Mois Rentable",
            f.PremierMoisRentable is { } first ? MonthName(first) : "Au-delà de 3 ans",
            f.PremierMoisRentable is null ? "Danger" : "TextPrimary",
            "Premier mois dont le CA prévu couvre ses charges fixes",
            formula: "Recherché sur 36 mois à partir du mois en cours."));

        ForecastBreakEvenPanel.Children.Add(Tile("Résultat Cumulé", Signed(f.ResultatCumule),
            f.ResultatCumule >= 0 ? "Success" : "Danger",
            f.ResultatCumule >= 0
                ? f.PremiereVente is { } pv ? $"Depuis la première vente ({pv:dd/MM/yyyy})" : "Depuis l'ouverture"
                : f.DateRecuperationPertes is { } r ? $"Pertes récupérées vers {MonthName(r)}" : "Pas de récupération prévue sous 3 ans",
            formula: "Somme des bénéfices et pertes nets de tous les mois depuis la première activité (ventes ou charges)."));

        RenderForecastChart(f);
        UpdateSimulation();
    }

    private void RenderForecastChart(MargesPrevisionResponse f)
    {
        var axisPaint = new SolidColorPaint(ThemeColor("TextSecondary"));
        var separatorPaint = new SolidColorPaint(ThemeColor("Border")) { StrokeThickness = 1 };
        var revenueColor = ThemeColor("Accent");
        var forecastColor = revenueColor.WithAlpha(0x66);
        var bandColor = ThemeColor("TextMuted");

        ForecastChartTitle.Text = $"Réalisé et Prévu ({Money.Label})";
        var months = f.Mois;
        ForecastEmptyText.Visibility = months.All(m => m.Revenue == 0 && m.BeneficeNet == 0) ? Visibility.Visible : Visibility.Collapsed;

        ColumnSeries<double> Bars(string name, bool prevu, SKColor color) => new()
        {
            Name = name,
            Values = months.Select(m => m.Prevu == prevu ? (double)m.Revenue : double.NaN).ToArray(),
            Fill = new SolidColorPaint(color), MaxBarWidth = 26, IgnoresBarPosition = true,
            YToolTipLabelFormatter = p => double.IsFinite(p.Coordinate.PrimaryValue) ? Amount((decimal)p.Coordinate.PrimaryValue) : string.Empty,
        };

        LineSeries<double?> Line(string name, IEnumerable<double?> values, SKColor color, float thickness, float[]? dash) => new()
        {
            Name = name, Values = values.ToArray(), Fill = null, GeometrySize = 0, LineSmoothness = 0,
            Stroke = new SolidColorPaint(color) { StrokeThickness = thickness, PathEffect = dash is null ? null : new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect(dash) },
            YToolTipLabelFormatter = p => Signed((decimal)p.Coordinate.PrimaryValue),
        };

        ForecastChart.Series =
        [
            Bars("CA réalisé", false, revenueColor),
            Bars("CA prévu", true, forecastColor),
            Line("Fourchette haute", months.Select(m => m.Prevu ? (double?)m.RevenueHaut : null), bandColor, 1.5f, [6, 4]),
            Line("Fourchette basse", months.Select(m => m.Prevu ? (double?)m.RevenueBas : null), bandColor, 1.5f, [6, 4]),
            Line("Bénéfice net", months.Select(m => (double?)m.BeneficeNet), NetColor, 3, null),
        ];

        var max = months.Count == 0 ? 0 : (double)months.Max(m => m.RevenueHaut);
        var min = months.Count == 0 ? 0 : (double)Math.Min(0, months.Min(m => m.BeneficeNet));
        ForecastChart.XAxes =
        [
            new Axis
            {
                Labels = months.Select(m => m.Mois.ToDateTime(TimeOnly.MinValue).ToString("MMM yy", French)).ToArray(),
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, TextSize = 10,
            },
        ];
        ForecastChart.YAxes =
        [
            new Axis
            {
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, TextSize = 11,
                Labeler = v => double.IsFinite(v) ? Money.FormatPlain((decimal)v) : string.Empty,
                MinStep = ChartAxis.NiceStep(Math.Max(Math.Abs(max), Math.Abs(min))),
            },
        ];
        RenderSeriesLegend(ForecastLegendPanel,
        [
            ("CA réalisé", revenueColor), ("CA prévu", forecastColor), ("Fourchette 80 %", bandColor), ("Bénéfice net", NetColor),
        ]);
    }

    // --- Simulation ---

    private void Simulation_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSimulation();

    private void ResetSimulation_Click(object sender, RoutedEventArgs e)
    {
        SimRevenueSlider.Value = 0;
        SimMarginSlider.Value = 0;
        SimFixedSlider.Value = 0;
    }

    /// <summary>The year's result under the slider assumptions, applied to the rest of the
    /// year only - what is already realised stays as it is. Same formula as the server's
    /// bénéfice prévu, so with every slider at zero both agree.</summary>
    private void UpdateSimulation()
    {
        if (SimResultPanel is null) return;

        var revPct = (decimal)SimRevenueSlider.Value;
        var marginPts = (decimal)SimMarginSlider.Value;
        var fixedPct = (decimal)SimFixedSlider.Value;
        SimRevenueValue.Text = SignedPercent(revPct);
        SimMarginValue.Text = (marginPts > 0 ? "+" : string.Empty) + Money.FormatPlain(marginPts) + " pt";
        SimFixedValue.Text = SignedPercent(fixedPct);

        SimResultPanel.Children.Clear();
        if (_forecast is not { } f) return;

        var taux = (f.TauxMcv + marginPts) / 100;
        var revRest = f.RevenueRestant * (1 + revPct / 100);
        var fixedRest = f.ChargesFixesRestantes * (1 + fixedPct / 100);
        var revenue = f.RevenueRealise + revRest;
        var net = f.BeneficeRealise + revRest * taux - fixedRest;
        var gap = net - f.BeneficeAnnee;
        decimal? seuil = taux > 0 ? (f.ChargesFixesRealisees + fixedRest) / taux : null;

        void Result(string label, string value, string colorKey, string? note = null)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var valueText = new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.Bold };
            valueText.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            DockPanel.SetDock(valueText, Dock.Right);
            row.Children.Add(valueText);
            var labelStack = new StackPanel();
            labelStack.Children.Add(new TextBlock { Text = label });
            if (note is not null)
            {
                var noteText = new TextBlock { Text = note, FontSize = 11 };
                noteText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                labelStack.Children.Add(noteText);
            }
            row.Children.Add(labelStack);
            SimResultPanel.Children.Add(row);
        }

        Result($"CA {f.Annee} simulé", Amount(revenue), "Accent");
        Result($"Bénéfice net {f.Annee} simulé", Signed(net), net >= 0 ? "Success" : "Danger",
            gap == 0 ? "Identique à la prévision" : $"{(gap > 0 ? "+" : "-")}{Amount(Math.Abs(gap))} par rapport à la prévision");
        Result("Marge nette simulée", revenue > 0 ? Percent(net / revenue * 100) : "—", "TextPrimary");
        Result("Seuil de rentabilité simulé", seuil is { } s ? Amount(s) : "—",
            seuil is { } s2 && revenue >= s2 ? "Success" : "Warning",
            seuil is { } s3 ? revenue >= s3 ? "Atteint d'ici la fin de l'année" : $"Manque {Amount(s3 - revenue)} de CA" : "Taux de MCV nul ou négatif");
    }
}
