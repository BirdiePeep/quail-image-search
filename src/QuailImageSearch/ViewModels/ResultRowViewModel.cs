using System.Collections.Generic;

namespace QuailImageSearch.ViewModels;

/// <summary>
/// One row of the thumbnail grid. Avalonia has no virtualizing wrap panel, so the grid is a
/// virtualized list of rows whose width (items per row) follows the view's width.
/// </summary>
public sealed class ResultRowViewModel(IReadOnlyList<ImageResultViewModel> items)
{
    public IReadOnlyList<ImageResultViewModel> Items { get; } = items;
}
