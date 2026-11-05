using System.Security.Cryptography;
using System.Text.Json;

namespace Lonnii.Api.Features.Updates;

/// <summary>The phone app's newest APK, as named by <c>{data}/updates/mobile/latest.json</c>.</summary>
public sealed record MobileUpdateNotice(int VersionCode, string VersionName, string FileName, string? Notes);

/// <summary>What the phone gets: the notice plus the file's real size and MD5, worked out here.</summary>
public sealed record MobileUpdateInfo(int VersionCode, string VersionName, string FileName, string? Notes, long Size, string Md5);

/// <summary>
/// Hands the phone app its newest APK from our server, until it is on the Play Store.
///
/// <para>
/// No signature of ours here, unlike the desktop installer: Android itself refuses to install an
/// update not signed with the same key as the app already on the phone, so a tampered APK can
/// never replace Lonnii. The phone still checks the size and MD5 given here (a broken download) before offering it.
/// </para>
/// <para>
/// Publishing: copy <c>LonniiBusiness-x.y.z.apk</c> into <c>/var/lib/lonnii-api/updates/mobile/</c>
/// and write <c>latest.json</c> there: <c>{"versionCode":2,"versionName":"1.0.1","fileName":"...","notes":"..."}</c>.
/// </para>
/// </summary>
public static class MobileUpdateEndpoints
{
    private static (string Key, MobileUpdateInfo Info)? _cache;
    private static readonly Lock CacheLock = new();

    public static IEndpointRouteBuilder MapMobileUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mobile/update").AllowAnonymous();

        group.MapGet("/latest", (UpdatePaths paths) =>
            Read(paths) is { } info ? Results.Ok(info) : Results.NoContent());

        group.MapGet("/files/{name}", (string name, UpdatePaths paths) =>
        {
            // Only the file the current notice names: never a path built from what was asked.
            if (Read(paths) is not { } info || !string.Equals(name, info.FileName, StringComparison.Ordinal))
                return Results.NotFound();

            return Results.File(Path.Combine(Folder(paths), info.FileName),
                "application/vnd.android.package-archive", info.FileName, enableRangeProcessing: true);
        });

        return app;
    }

    private static string Folder(UpdatePaths paths) => Path.Combine(paths.Directory, "mobile");

    internal static MobileUpdateInfo? Read(UpdatePaths paths)
    {
        try
        {
            var manifestPath = Path.Combine(Folder(paths), "latest.json");
            if (!File.Exists(manifestPath)) return null;

            var notice = JsonSerializer.Deserialize<MobileUpdateNotice>(File.ReadAllText(manifestPath), JsonSerializerOptions.Web);
            if (notice is null || notice.VersionCode <= 0 || string.IsNullOrWhiteSpace(notice.FileName)
                || Path.GetFileName(notice.FileName) != notice.FileName
                || !notice.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                return null;

            var apk = new FileInfo(Path.Combine(Folder(paths), notice.FileName));
            if (!apk.Exists) return null;

            var key = $"{notice}|{apk.Length}|{apk.LastWriteTimeUtc.Ticks}";
            lock (CacheLock)
            {
                if (_cache is { } c && c.Key == key) return c.Info;
            }

            string md5;
            using (var stream = apk.OpenRead())
                md5 = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();

            var info = new MobileUpdateInfo(notice.VersionCode, notice.VersionName, notice.FileName, notice.Notes, apk.Length, md5);
            lock (CacheLock) _cache = (key, info);
            return info;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
