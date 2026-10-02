using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lonnii.Client.Common.Controls;

/// <summary>
/// Prev/Next plus a jump-to-page box. The caller owns the actual paging (slicing the list,
/// re-rendering it) and just calls <see cref="Configure"/> after every load or filter change;
/// this control only tracks the page number and tells the caller when it changes.
/// </summary>
public partial class PaginationBar : UserControl
{
    public event EventHandler? PageChanged;

    public int CurrentPage { get; private set; } = 1;
    public int PageCount { get; private set; } = 1;

    public PaginationBar()
    {
        InitializeComponent();
    }

    /// <summary>Applies the caller's current page and count, then redraws - called after every
    /// load or filter change, since a narrower filter can shrink the page count as easily as a
    /// refresh can grow it. Does not raise <see cref="PageChanged"/>: the caller already has
    /// the page it asked for.</summary>
    public void Configure(int currentPage, int pageCount)
    {
        PageCount = Math.Max(pageCount, 1);
        CurrentPage = Math.Clamp(currentPage, 1, PageCount);
        Render();
    }

    private void Render()
    {
        PageText.Text = $"Page {CurrentPage} / {PageCount}";
        PrevButton.IsEnabled = CurrentPage > 1;
        NextButton.IsEnabled = CurrentPage < PageCount;
        JumpBox.Text = string.Empty;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Jump(CurrentPage - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => Jump(CurrentPage + 1);

    private void JumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse(JumpBox.Text, out var page)) Jump(page);
        else JumpBox.Text = string.Empty;
    }

    private void Jump(int page)
    {
        page = Math.Clamp(page, 1, PageCount);
        if (page == CurrentPage)
        {
            Render();
            return;
        }

        CurrentPage = page;
        Render();
        PageChanged?.Invoke(this, EventArgs.Empty);
    }
}
