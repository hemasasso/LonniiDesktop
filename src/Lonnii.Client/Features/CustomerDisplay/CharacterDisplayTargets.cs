using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>
/// A pole display or character LCD, written to from a background loop so a slow or unplugged
/// device can never stall the till. Only the newest frame matters: while the device is busy or
/// being reconnected, older frames are dropped rather than queued up.
/// </summary>
public abstract class CharacterDisplayTarget : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private string[]? _lastRows;

    protected CharacterDisplayTarget(CharacterDisplaySettings settings)
    {
        Settings = settings;
        _loop = Task.Run(RunAsync);
    }

    protected CharacterDisplaySettings Settings { get; }

    /// <summary>Human-readable state for the settings screen: "Connecté", or why not.</summary>
    public string Status { get; private set; } = "Connexion…";

    public bool IsConnected { get; private set; }

    /// <summary>Raised on a background thread when <see cref="Status"/> changes.</summary>
    public event EventHandler? StatusChanged;

    public void Show(CustomerDisplaySnapshot snapshot)
    {
        var rows = CharacterDisplayFormatter.Format(snapshot, Settings.Columns, Settings.Rows);

        // Repainting an identical frame makes some displays flicker.
        if (_lastRows is not null && _lastRows.SequenceEqual(rows)) return;
        _lastRows = rows;

        _frames.Writer.TryWrite(CharacterDisplayFormatter.Encode(Settings.Protocol, rows));
    }

    /// <summary>Open the link to the device. Throws if it cannot be reached.</summary>
    protected abstract Task<Stream> OpenAsync(CancellationToken ct);

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            try
            {
                stream = await OpenAsync(ct);
                SetStatus(true, "Connecté");

                var init = CharacterDisplayFormatter.Initialise(Settings.Protocol);
                if (init.Length > 0) await stream.WriteAsync(init, ct);

                // Whatever was last on screen goes back up after a reconnect.
                _lastRows = null;

                await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
                {
                    await stream.WriteAsync(frame, ct);
                    await stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException
                or InvalidOperationException or ArgumentException or TimeoutException)
            {
                SetStatus(false, Describe(e));
            }
            finally
            {
                if (stream is not null) await stream.DisposeAsync();
            }

            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string Describe(Exception e) => e switch
    {
        UnauthorizedAccessException => "Port occupé par un autre programme",
        SocketException => "Appareil injoignable",
        _ => e.Message,
    };

    private void SetStatus(bool connected, string status)
    {
        IsConnected = connected;
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _stop.Dispose();
    }
}

/// <summary>A pole display or microcontroller on a USB or serial port.</summary>
public sealed class SerialDisplayTarget(SerialDisplaySettings settings) : CharacterDisplayTarget(settings)
{
    protected override Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Port))
            throw new InvalidOperationException("Aucun port choisi");

        var port = new SerialPort(settings.Port, settings.BaudRate, Parity.None, 8, StopBits.One)
        {
            WriteTimeout = 2000,
        };
        port.Open();
        return Task.FromResult<Stream>(new SerialStream(port));
    }

    /// <summary>Closing the stream closes the port; <see cref="SerialPort.BaseStream"/> alone would leave it open.</summary>
    private sealed class SerialStream(SerialPort port) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => port.BaseStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => port.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            port.Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && port.IsOpen) port.Close();
            if (disposing) port.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>An ESP32 (or anything else) listening on a TCP port and driving the LCD.</summary>
public sealed class NetworkDisplayTarget(NetworkDisplaySettings settings) : CharacterDisplayTarget(settings)
{
    protected override async Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("Aucune adresse saisie");

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(settings.Host.Trim(), settings.Port, timeout.Token);
            return new OwningNetworkStream(client);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("Appareil injoignable");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class OwningNetworkStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            _inner.WriteAsync(buffer, ct);

        protected override void Dispose(bool disposing)
        {
            if (disposing) client.Dispose();
            base.Dispose(disposing);
        }
    }
}
