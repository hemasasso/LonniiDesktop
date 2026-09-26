namespace Lonnii.Data.Entities;

/// <summary>Values stored in <see cref="Charge.TypeCharge"/>.</summary>
public static class ChargeTypes
{
    /// <summary>A recurring cost that stays the same every period - rent, a salary, an
    /// insurance premium. Only a <c>fixe</c> charge may be marked recurring.</summary>
    public const string Fixe = "fixe";

    /// <summary>A one-off or fluctuating cost - a repair, a supply run.</summary>
    public const string Variable = "variable";
}

/// <summary>Values stored in <see cref="Charge.RecurringDay"/>: which day of the month a
/// recurring charge re-creates itself on. Ported from backend/utils/recurringDay.js.</summary>
public static class RecurringDay
{
    /// <summary>The 1st of the month.</summary>
    public const string Debut = "debut";

    /// <summary>The month's last day, whatever it is that month. The default, matching the
    /// source app's own default.</summary>
    public const string Fin = "fin";

    /// <summary>Resolves <paramref name="day"/> to an actual day-of-month for
    /// <paramref name="year"/>/<paramref name="month"/> (1-12), clamped to that month's
    /// length - so a charge set for "the 31st" still lands somewhere sensible in February.</summary>
    public static int Resolve(string? day, int year, int month)
    {
        var lastDay = DateTime.DaysInMonth(year, month);
        if (day == Debut) return 1;
        if (day == Fin || string.IsNullOrEmpty(day)) return lastDay;
        return int.TryParse(day, out var n) && n >= 1 ? Math.Min(n, lastDay) : lastDay;
    }

    /// <summary>Validates a day value coming from a request: <see cref="Debut"/>,
    /// <see cref="Fin"/>, or a 1-31 day-of-month written as a plain number string. Anything
    /// else falls back to <paramref name="fallback"/> rather than being stored as-is - ported
    /// from backend/utils/recurringDay.js's own <c>normalizeRecurringDay</c>.</summary>
    public static string Normalise(string? value, string fallback = Fin)
    {
        if (value == Debut || value == Fin) return value;
        return int.TryParse(value, out var n) && n is >= 1 and <= 31 ? n.ToString() : fallback;
    }

    /// <summary>A short French description, e.g. for a details screen - "le 1er de chaque
    /// mois", "le 15 de chaque mois".</summary>
    public static string Describe(string? day) => day switch
    {
        Debut => "le 1er de chaque mois",
        Fin or null or "" => "le dernier jour de chaque mois",
        _ when int.TryParse(day, out var n) && n >= 1 => $"le {n} de chaque mois",
        _ => "le dernier jour de chaque mois",
    };
}

/// <summary>
/// A business expense. Ported from Lonnii Business's <c>charges</c> table
/// (backend/migrations/create_charges_system.sql, add_recurring_charges.sql) - including its
/// <c>groupe_id</c> column name, which (unlike most tables) was never renamed to
/// <c>group_id</c> there. <c>recurring_day</c> has no CREATE/ALTER TABLE in the repo (see
/// lonnii-live-schema-drift memory) - its shape is inferred from
/// backend/routes/gestionCharges.js and backend/utils/recurringDay.js, both of which read
/// and write it as a plain string defaulting to <see cref="Entities.RecurringDay.Fin"/>.
/// </summary>
public class Charge
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public decimal Montant { get; set; }

    /// <summary>One of <see cref="ChargeTypes"/>.</summary>
    public string TypeCharge { get; set; } = ChargeTypes.Variable;

    /// <summary>Free text, matching a <see cref="ChargeCategory.Nom"/> by convention rather
    /// than a foreign key - same as the source table, which stores <c>categorie</c> as a
    /// plain column rather than referencing <c>charges_categories</c>. A category can
    /// therefore be renamed or deleted without touching every charge that named it.</summary>
    public string Categorie { get; set; } = string.Empty;

    public DateTime Date { get; set; } = DateTime.UtcNow.Date;

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True only for a <see cref="ChargeTypes.Fixe"/> "source" charge (one with no
    /// <see cref="RecurringSourceId"/>) that auto-creates itself again each month.</summary>
    public bool IsRecurring { get; set; }

    /// <summary>Last month this should still auto-create itself for. Required whenever
    /// <see cref="IsRecurring"/> is set, mirroring the source app's own validation.</summary>
    public DateTime? RecurringEndDate { get; set; }

    /// <summary>False once <see cref="RecurringEndDate"/> has passed (deactivated
    /// automatically) or the user stopped it by hand.</summary>
    public bool RecurringActive { get; set; } = true;

    /// <summary>Set only on a charge auto-created FROM a recurring source - points back at
    /// that source charge. Null on the source itself and on any ordinary charge.</summary>
    public int? RecurringSourceId { get; set; }

    /// <summary>One of <see cref="Entities.RecurringDay"/> - "debut", "fin", or a specific
    /// day-of-month as a string ("15"). Only meaningful when <see cref="IsRecurring"/>.</summary>
    public string? RecurringDay { get; set; } = Entities.RecurringDay.Fin;

    public Charge? RecurringSource { get; set; }
}

/// <summary>
/// A charge category, customisable per group. Ported from <c>charges_categories</c>. Seeded
/// with the same twelve defaults the source app inserts for every group (Loyer, Électricité,
/// Eau, Internet, Salaires, Fournitures, Marketing, Transport, Assurances, Maintenance, Taxes,
/// Autres) - done lazily on first read here rather than at migration time, since the source
/// seeds by iterating every existing <c>groupes</c> row, which a fresh desktop install has
/// none of yet.
/// </summary>
public class ChargeCategory
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string Nom { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Hex colour, e.g. <c>#6366f1</c> - the source app's own default.</summary>
    public string Color { get; set; } = "#6366f1";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
