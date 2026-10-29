namespace Lonnii.Client.Services;

/// <summary>
/// The French label for a Programme day type. Plain strings, as in ProgrammeView: the client does
/// not reference Lonnii.Data, where ProgrammeEntryTypes lives.
/// </summary>
public static class DayTypes
{
    public static string Label(string? type) => type switch
    {
        "reunion" => "Réunion",
        "repos" => "Repos",
        "autre" => "Autre",
        _ => "Travail",
    };
}
