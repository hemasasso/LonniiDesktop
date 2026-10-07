using System.IO;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;

namespace Lonnii.Client.Common;

/// <summary>
/// Shows what is about to print, page by page, before the printer is chosen.
///
/// <para>
/// Windows' own print dialog has no preview for a desktop application like this one - it says
/// "No preview available" - so the preview has to be ours. The document is written once into an
/// in-memory XPS package and shown in a <see cref="DocumentViewer"/>; the same fixed pages are
/// what the Imprimer button sends, so what you see is what comes out.
/// </para>
/// </summary>
public partial class PrintPreviewWindow : Window
{
    /// <summary>A4 at WPF's 96 units per inch.</summary>
    public static readonly Size A4 = new(793.7, 1122.5);

    private readonly string _jobName;
    private readonly Package _package;
    private readonly Uri _packageUri;
    private readonly XpsDocument _xps;

    private PrintPreviewWindow(DocumentPaginator paginator, string jobName)
    {
        InitializeComponent();
        _jobName = jobName;

        var stream = new MemoryStream();
        _package = Package.Open(stream, FileMode.Create, FileAccess.ReadWrite);
        _packageUri = new Uri($"memorypackage://{Guid.NewGuid():N}.xps");
        PackageStore.AddPackage(_packageUri, _package);
        _xps = new XpsDocument(_package, CompressionOption.NotCompressed, _packageUri.AbsoluteUri);

        XpsDocument.CreateXpsDocumentWriter(_xps).Write(paginator);
        var sequence = _xps.GetFixedDocumentSequence();

        Viewer.Document = sequence;
        Title = $"Aperçu avant impression — {jobName}";
        TitleText.Text = jobName;
        var pages = sequence.DocumentPaginator.PageCount;
        PagesText.Text = pages == 1 ? "1 page" : $"{pages} pages";

        Closed += (_, _) =>
        {
            Viewer.Document = null;
            _xps.Close();
            PackageStore.RemovePackage(_packageUri);
            _package.Close();
        };
    }

    /// <summary>
    /// Previews <paramref name="paginator"/> and lets the user print it. If the preview itself
    /// cannot be built the document is still printed the old way - a missing preview must never
    /// mean a missing printout.
    /// </summary>
    public static void Show(Window? owner, DocumentPaginator paginator, string jobName)
    {
        PrintPreviewWindow window;
        try
        {
            window = new PrintPreviewWindow(paginator, jobName) { Owner = owner };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                       or System.Xml.XmlException or NotSupportedException)
        {
            MessageBox.Show(owner,
                $"L'aperçu n'a pas pu être affiché ({ex.Message}).\n\nLe document va être envoyé directement à l'impression.",
                "Imprimer", MessageBoxButton.OK, MessageBoxImage.Information);

            var dialog = new System.Windows.Controls.PrintDialog();
            if (dialog.ShowDialog() == true) dialog.PrintDocument(paginator, jobName);
            return;
        }

        window.ShowDialog();
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        // Asks for the printer, then prints the very pages shown here.
        var dialog = new System.Windows.Controls.PrintDialog();
        if (Viewer.Document is not IDocumentPaginatorSource source) return;
        if (dialog.ShowDialog() != true) return;

        dialog.PrintDocument(source.DocumentPaginator, _jobName);
        Close();
    }
}
