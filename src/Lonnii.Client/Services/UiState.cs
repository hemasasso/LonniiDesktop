using System.IO;
using System.Text.Json;

namespace Lonnii.Client.Services;

/// <summary>A cart line as saved to disk: enough to rebuild it against the live catalogue,
/// never the product itself, so a restored cart picks up today's name, stock and fixed price
/// rather than whatever they were when it was saved.</summary>
public sealed record SavedCartLine(
    string ProductId, int Quantity, decimal UnitPrice, decimal Discount, string DiscountType);

/// <summary>What one user was looking at, and had in the cart, in one group.</summary>
public sealed class ScreenState
{
    public string? LastModule { get; set; }

    /// <summary>Active inner tab per module key, e.g. <c>"ventes" → "liste"</c>.</summary>
    public Dictionary<string, string> Tabs { get; set; } = [];

    public List<SavedCartLine> Cart { get; set; } = [];

    public string? RemiseGlobale { get; set; }
}

/// <summary>
/// Remembers where the user was - module, inner tab, cart - across an F5 "Actualiser"
/// (which rebuilds every module from scratch) and across a restart. Keyed by group and user,
/// since a till laptop is often shared and one cashier's cart must not appear for another.
/// Stored beside the client settings; losing it only costs the convenience, so every read and
/// write failure is swallowed.
/// </summary>
public static class UiState
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "ui-state.json");

    private static Dictionary<string, ScreenState>? _all;

    /// <summary>The state for the signed-in user in the current group, or a throwaway
    /// instance when there is no group yet (nothing to key it on).</summary>
    public static ScreenState For(AppSession session)
    {
        if (session.Groupe is null || session.User is null) return new ScreenState();

        _all ??= Load();
        var key = $"{session.Groupe.Id}:{session.User.IdUser}";
        if (!_all.TryGetValue(key, out var state))
        {
            state = new ScreenState();
            _all[key] = state;
        }
        return state;
    }

    public static void Save()
    {
        if (_all is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_all));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, ScreenState> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, ScreenState>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }
}
