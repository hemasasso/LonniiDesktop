using System.Globalization;
using Microsoft.Win32;

namespace Lonnii.Api.Features.Licensing;

/// <summary>
/// What a shop's computer remembers about its licence outside lonnii.db: the newest signed licence
/// it has held per espace, and the latest moment it is known to have reached.
///
/// <para>
/// <b>Newest licence.</b> A copy of lonnii.db taken earlier carries an earlier licence: one from
/// before the shop was blocked, or from when it was still in local mode, which never expires. With
/// the newest issue time recorded where the database does not reach, the host can tell the licence it
/// is shown is not the latest it has had, and asks to connect once instead.
/// </para>
/// <para>
/// <b>Latest moment.</b> A licence runs out by date, so the obvious attack is to stop the date: set
/// Windows' clock back at every start. The latest moment seen used to live in one editable database
/// column. Now it is also kept here, and carried forward by how long the API has been running - which
/// no clock setting changes - so a till kept running on a frozen date still reaches its deadline.
/// </para>
/// <para>
/// Only on a shipped host (see Program.cs): development machines and the tests have none, so they
/// keep the earlier behaviour and never write to the registry of the computer they run on.
/// </para>
/// </summary>
public interface ILicenceWatermark
{
    /// <summary>The newest licence issue time recorded for <paramref name="groupId"/>, or null.</summary>
    DateTime? NewestIssuedAt(string groupId);

    /// <summary>Records <paramref name="issuedAt"/> if it is newer than what is recorded.</summary>
    void Raise(string groupId, DateTime issuedAt);

    /// <summary>The latest moment this computer is known to have reached: the highest one recorded,
    /// plus the time the API has run since. Null when nothing was ever recorded.</summary>
    DateTime? ClockFloor();

    /// <summary>Records that <paramref name="now"/> has been reached (written now and then, not on
    /// every call).</summary>
    void MarkClock(DateTime now);

    /// <summary>
    /// A newer signed licence states what time it really is: the floor restarts from
    /// <paramref name="serverTime"/>, lower than before if need be - which is how a computer whose
    /// clock once ran years ahead recovers. Never below a licence already held.
    /// </summary>
    void ResetClock(DateTime serverTime);
}

/// <summary>
/// <see cref="ILicenceWatermark"/> kept twice: in <c>HKLM\SOFTWARE\Lonnii\Host</c> and in a file in
/// the profile of the account the API runs as (SYSTEM on a shop's computer), and read as the higher of
/// the two, so deleting one of them changes nothing. Both are writable by the API and not by a cashier's
/// Windows account. Neither is in the data folder, so putting back a copy of that folder does not
/// bring them back with it, nor under <c>Software\Lonnii\Business</c>, which the uninstaller deletes.
/// An administrator of the computer can still delete both - this raises the cost of the attack, it does
/// not make it impossible.
/// </summary>
public sealed class HostLicenceWatermark : ILicenceWatermark
{
    private const string ClockName = "clk";
    private static string LicenceName(string groupId) => "lic-" + groupId;

    /// <summary>How often the latest moment is written, so a busy till does not write on every request.</summary>
    private static readonly TimeSpan WriteInterval = TimeSpan.FromMinutes(5);

    private readonly RegistryHive _hive;
    private readonly string _keyPath;
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    /// <summary>The moment the floor counts from, and the uptime tick it was taken at.</summary>
    private DateTime? _base;
    private long _baseTicks;
    private DateTime _lastWrite = DateTime.MinValue;

    public HostLicenceWatermark(ILogger<HostLicenceWatermark> logger)
        : this(RegistryHive.LocalMachine, @"SOFTWARE\Lonnii\Host", DefaultFilePath(), logger)
    {
    }

    /// <summary>Tests point it at a key under HKCU and a temporary file.</summary>
    internal HostLicenceWatermark(RegistryHive hive, string keyPath, string filePath, ILogger logger)
    {
        _hive = hive;
        _keyPath = keyPath;
        _filePath = filePath;
        _logger = logger;

        // A copy deleted while the API was stopped is written back from the other one at start.
        lock (_gate) Resync();

        _base = Read(ClockName);
        _baseTicks = Environment.TickCount64;
    }

    /// <summary>Brings both copies up to the higher of the two, value by value.</summary>
    private void Resync()
    {
        var names = new HashSet<string>(ReadFile().Keys, StringComparer.Ordinal);
        foreach (var name in RegistryNames()) names.Add(name);
        foreach (var name in names)
            if (Read(name) is { } value) Write(name, value);
    }

    /// <summary>The API's own profile (C:\Windows\System32\config\systemprofile\AppData\Local when it
    /// runs as SYSTEM), not ProgramData\Lonnii: a copy of the data folder put back must not carry it.</summary>
    private static string DefaultFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "Lonnii", "host.dat");
    }

    public DateTime? NewestIssuedAt(string groupId)
    {
        lock (_gate) return Read(LicenceName(groupId));
    }

    public void Raise(string groupId, DateTime issuedAt)
    {
        // Each copy is compared on its own, so a missing one is written back even when the other
        // already holds this time.
        lock (_gate) Write(LicenceName(groupId), issuedAt);
    }

    public DateTime? ClockFloor()
    {
        lock (_gate) return Floor();
    }

    public void MarkClock(DateTime now)
    {
        lock (_gate)
        {
            if (Floor() is { } floor && floor >= now) now = floor;
            else
            {
                _base = now;
                _baseTicks = Environment.TickCount64;
            }

            if (DateTime.UtcNow - _lastWrite < WriteInterval) return;
            _lastWrite = DateTime.UtcNow;
            Write(ClockName, now);
        }
    }

    public void ResetClock(DateTime serverTime)
    {
        lock (_gate)
        {
            var to = serverTime;
            foreach (var issuedAt in AllLicences())
                if (issuedAt > to) to = issuedAt;

            _base = to;
            _baseTicks = Environment.TickCount64;
            _lastWrite = DateTime.UtcNow;
            Write(ClockName, to, allowLower: true);
        }
    }

    // --- Storage ---

    /// <summary>The recorded floor carried forward by uptime. Uptime only ever adds: Windows' tick
    /// count is not touched by setting the clock.</summary>
    private DateTime? Floor() =>
        _base is { } at ? at + TimeSpan.FromMilliseconds(Environment.TickCount64 - _baseTicks) : null;

    /// <summary>The higher of the registry's and the file's value.</summary>
    private DateTime? Read(string name) => Max(ReadRegistry(name), FileValue(ReadFile(), name));

    /// <summary>Null when absent - never DateTime.MinValue, which would read as "something recorded".</summary>
    private static DateTime? FileValue(Dictionary<string, DateTime> file, string name) =>
        file.TryGetValue(name, out var at) ? at : null;

    private IEnumerable<DateTime> AllLicences()
    {
        var names = new HashSet<string>(ReadFile().Keys, StringComparer.Ordinal);
        foreach (var name in RegistryNames()) names.Add(name);
        foreach (var name in names)
            if (name.StartsWith("lic-", StringComparison.Ordinal) && Read(name) is { } at)
                yield return at;
    }

    /// <summary>Writes both copies; never lowers a value unless <paramref name="allowLower"/>.</summary>
    private void Write(string name, DateTime value, bool allowLower = false)
    {
        value = value.ToUniversalTime();

        if (allowLower || !(ReadRegistry(name) >= value)) WriteRegistry(name, value);

        var file = ReadFile();
        if (allowLower || !(FileValue(file, name) >= value))
        {
            file[name] = value;
            WriteFile(file);
        }
    }

    private RegistryKey? OpenKey(bool writable)
    {
        var root = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
        return writable ? root.CreateSubKey(_keyPath, writable: true) : root.OpenSubKey(_keyPath);
    }

    private DateTime? ReadRegistry(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = OpenKey(writable: false);
            return key?.GetValue(name) is string stamp ? Parse(stamp) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private IEnumerable<string> RegistryNames()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var key = OpenKey(writable: false);
            return key?.GetValueNames() ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private void WriteRegistry(string name, DateTime value)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = OpenKey(writable: true);
            key?.SetValue(name, Format(value), RegistryValueKind.String);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not fatal: the file still has it, and the next write tries again.
            _logger.LogWarning("Repère de licence non enregistré dans le registre ({Name}) : {Message}", name, e.Message);
        }
    }

    /// <summary>One "name=value" per line.</summary>
    private Dictionary<string, DateTime> ReadFile()
    {
        var values = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_filePath)) return values;
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var eq = line.IndexOf('=');
                if (eq > 0 && Parse(line[(eq + 1)..]) is { } at) values[line[..eq]] = at;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return values;
    }

    private void WriteFile(Dictionary<string, DateTime> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp";
            File.WriteAllLines(temp, values.Select(v => $"{v.Key}={Format(v.Value)}"));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Repère de licence non enregistré dans le fichier : {Message}", e.Message);
        }
    }

    private static string Format(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? Parse(string text) =>
        DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToUniversalTime() : null;

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
