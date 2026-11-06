using System.Windows.Threading;
using Lonnii.Client.Features.Ventes;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Print;

/// <summary>
/// Prints the sales the shop's phones send to this computer (the API's <c>Features/Print</c>).
///
/// <para>
/// While the app is open in an espace, it asks the host every few seconds for a job; the asking is
/// also what puts this computer on the phones' list of postes, so a closed app is never offered.
/// A job carries only a sale and a format: the sale is fetched here and printed exactly as the
/// Ventes screen would print it - the shop's receipt settings, logo and QR code - on this
/// computer's default printer, with no dialog, since the person is at the phone, not this screen.
/// </para>
/// <para>
/// Never from a remote session (the computer is not at the shop) and never on the UI's time when
/// the host is unreachable: a failed ask just waits for the next tick.
/// </para>
/// </summary>
public sealed class PhonePrintService(AppSession session)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private bool _busy;

    public void Start()
    {
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_busy || session.Groupe is null || session.IsRemote) return;
        _busy = true;
        try
        {
            var job = await session.Api.NextPrintJobAsync(DeviceIdentity.FriendlyName);
            if (job is null) return;

            string? error = null;
            try
            {
                await PrintAsync(job);
            }
            catch (Exception e)
            {
                error = e is ApiException api ? api.Message : $"Impression impossible : {e.Message}";
            }

            try { await session.Api.ReportPrintJobAsync(job.Id, error is null, error); }
            catch (Exception e) when (e is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
        }
        catch (Exception e) when (e is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Host unreachable or an older host without /api/print: try again at the next tick.
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PrintAsync(PhonePrintJob job)
    {
        var vente = await session.Api.GetVenteAsync(job.VenteId);
        var settings = await session.GetReceiptSettingsAsync();
        if (job.Format == "a4")
            settings = settings with { ReceiptTemplate = ReceiptTemplates.A4, FactureTemplate = ReceiptTemplates.A4 };

        var data = ReceiptData.FromVente(vente);
        var singlePage = settings.Template(data.IsFacture) == ReceiptTemplates.A4;
        var jobName = $"{(data.IsFacture ? "Facture" : "Reçu")} {data.Numero}";
        var logo = session.ReceiptLogo;
        var qr = session.ReceiptQrCode;
        var company = session.Groupe?.Nom ?? "Lonnii";

        ReceiptDocument.PrintSilently(
            () => ReceiptDocument.Build(data, settings, logo, qr, company, forPrint: true), singlePage, jobName);
    }
}
