using System.Text.Json;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Auth;

/// <summary>An espace registered with Lonnii from this machine and not approved yet.</summary>
public sealed record PendingEspace(string GroupId, string Name, string Email, string ServerUrl, DateTime RequestedAt)
{
    public PendingEspaceDto ToDto() => new(GroupId, Name, RequestedAt);
}

/// <summary>
/// The espaces this host asked Lonnii for that are still waiting for approval, in
/// <c>pending-espaces.json</c> beside the database. Nothing is built locally until Lonnii approves:
/// the owner comes back to the picker, presses "Vérifier", and the espace is then activated for this
/// machine and built under the id Lonnii issued. Never holds a password.
/// </summary>
public sealed class PendingEspaceStore(string dataDirectory)
{
    private readonly object _gate = new();
    private string PathOf => Path.Combine(dataDirectory, "pending-espaces.json");

    public IReadOnlyList<PendingEspace> ForEmail(string email)
    {
        lock (_gate) return Read().Where(p => p.Email == email).OrderBy(p => p.RequestedAt).ToList();
    }

    public PendingEspace? Find(string groupId, string email)
    {
        lock (_gate) return Read().FirstOrDefault(p => p.GroupId == groupId && p.Email == email);
    }

    public PendingEspace Add(PendingEspace pending)
    {
        lock (_gate)
        {
            var all = Read().Where(p => p.GroupId != pending.GroupId).ToList();
            all.Add(pending);
            Write(all);
            return pending;
        }
    }

    public bool Remove(string groupId, string email)
    {
        lock (_gate)
        {
            var all = Read();
            var kept = all.Where(p => !(p.GroupId == groupId && p.Email == email)).ToList();
            if (kept.Count == all.Count) return false;
            Write(kept);
            return true;
        }
    }

    private List<PendingEspace> Read()
    {
        try
        {
            return File.Exists(PathOf)
                ? JsonSerializer.Deserialize<List<PendingEspace>>(File.ReadAllText(PathOf)) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    private void Write(List<PendingEspace> all)
    {
        var temp = PathOf + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all));
        File.Move(temp, PathOf, overwrite: true);
    }
}
