using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Lonnii.Client.Common;

/// <summary>
/// Cuts one tall, already laid-out visual into printable pages: scaled down to fit the page width
/// if it is wider than that, then sliced top to bottom, one page per slice. It lets a report built
/// as a plain WPF panel go through the same preview and print path as a <see cref="FlowDocument"/>.
/// </summary>
public sealed class VisualPaginator : DocumentPaginator
{
    private readonly FrameworkElement _paper;
    private readonly double _margin;
    private readonly double _scale;
    private readonly double _sliceHeight;   // how much of the paper fits on a page, in paper units

    public VisualPaginator(FrameworkElement paper, Size pageSize, double margin)
    {
        _paper = paper;
        _margin = margin;
        PageSize = pageSize;

        var paperWidth = Math.Max(1, paper.ActualWidth > 0 ? paper.ActualWidth : paper.DesiredSize.Width);
        _scale = Math.Min(1, (pageSize.Width - 2 * margin) / paperWidth);
        _sliceHeight = (pageSize.Height - 2 * margin) / _scale;
    }

    private double PaperHeight =>
        Math.Max(1, _paper.ActualHeight > 0 ? _paper.ActualHeight : _paper.DesiredSize.Height);

    private double PaperWidth =>
        Math.Max(1, _paper.ActualWidth > 0 ? _paper.ActualWidth : _paper.DesiredSize.Width);

    public override bool IsPageCountValid => true;
    public override int PageCount => Math.Max(1, (int)Math.Ceiling(PaperHeight / _sliceHeight));
    public override Size PageSize { get; set; }
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        var top = pageNumber * _sliceHeight;
        var height = Math.Max(1, Math.Min(_sliceHeight, PaperHeight - top));
        var slice = new Rect(0, top, PaperWidth, height);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(_margin, _margin));
            dc.PushTransform(new ScaleTransform(_scale, _scale));

            var brush = new VisualBrush(_paper)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = slice,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, slice.Width, slice.Height),
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, slice.Width, slice.Height));

            dc.Pop();
            dc.Pop();
        }

        var page = new Rect(PageSize);
        return new DocumentPage(visual, PageSize, page, page);
    }
}
