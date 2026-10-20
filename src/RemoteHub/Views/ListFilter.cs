using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GridViewColumn = System.Windows.Controls.GridViewColumn;
using TextBox = System.Windows.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// The filter box and sortable column headers of a list view: typing narrows the rows and selects the first match
/// (Enter then activates it, Down moves into the list, Esc clears), and a header click sorts by that column – again to
/// reverse, with an arrow in the header. Used by the file panes and the registry's value list.
/// </summary>
internal sealed class ListFilter<TRow, TSort> where TRow : class where TSort : struct, Enum
{
    private readonly ListView _list;
    private readonly TextBox _filter;
    private readonly Func<TRow, string, bool> _matches;
    private readonly Func<TRow, TRow, TSort, bool, int> _compare;
    private readonly Dictionary<GridViewColumn, (string Header, TSort Sort)> _columns = [];
    private ListCollectionView? _view;

    /// <param name="activate">Enter in the filter box with one row selected (open a folder, edit a value).</param>
    public ListFilter(ListView list, TextBox filter, Func<TRow, string, bool> matches, Func<TRow, TRow, TSort, bool, int> compare,
                      TSort initial, Action<TRow> activate)
    {
        _list = list;
        _filter = filter;
        _matches = matches;
        _compare = compare;
        SortBy = initial;
        _filter.TextChanged += (_, _) => Apply();
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _filter.Text = ""; _list.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down) { _list.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && _list.SelectedItems.Count == 1 && _list.SelectedItem is TRow one) { activate(one); e.Handled = true; }
        };
        _list.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is GridViewColumnHeader { Column: { } col }) ClickColumn(col);
        }));
    }

    public TSort SortBy { get; private set; }
    public bool Descending { get; private set; }
    /// <summary>Text is in the filter box.</summary>
    public bool Active => _filter.Text.Trim().Length > 0;
    /// <summary>How many rows the filter lets through.</summary>
    public int Shown => _view?.Count ?? 0;
    /// <summary>The filter or the rows changed (for a status line).</summary>
    public event Action? Changed;

    /// <summary>A sortable column: its header text gets the sort arrow.</summary>
    public void Column(GridViewColumn column, string header, TSort sort)
    {
        _columns[column] = (header, sort);
        ShowSort();
    }

    /// <summary>Shows <paramref name="rows"/>. A new place (another folder or key) starts unfiltered; a refresh keeps the filter.</summary>
    public void SetRows(IList<TRow> rows, bool samePlace)
    {
        if (!samePlace && _filter.Text.Length > 0) _filter.Text = "";
        _view = new ListCollectionView((System.Collections.IList)rows) { CustomSort = new Order(this), Filter = o => o is TRow r && Passes(r) };
        _list.ItemsSource = _view;
        // A refresh that keeps the filter: the first match is selected when nothing is (a row arrived after the text was typed).
        if (Active && _list.SelectedItem is null) { _list.SelectedItem = _view.Cast<TRow>().FirstOrDefault(); if (_list.SelectedItem is { } first) _list.ScrollIntoView(first); }
        Changed?.Invoke();
    }

    /// <summary>Whether the filter lets <paramref name="row"/> through (always, without filter text).</summary>
    public bool Passes(TRow row) => !Active || _matches(row, _filter.Text);

    /// <summary>Shows <paramref name="row"/>, clearing the filter if it hides it (a value just created or renamed).</summary>
    public void Reveal(TRow row)
    {
        if (!Passes(row)) _filter.Text = "";
        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
    }

    public void Focus() { _filter.Focus(); _filter.SelectAll(); }

    private void Apply()
    {
        if (_view is null) return;
        _view.Refresh();
        if (Active)
        {
            // Setting SelectedItem selects just that row (or none), in single and in multiple selection mode alike.
            _list.SelectedItem = _view.Cast<TRow>().FirstOrDefault();
            if (_list.SelectedItem is { } first) _list.ScrollIntoView(first);
        }
        Changed?.Invoke();
    }

    private void ClickColumn(GridViewColumn col)
    {
        if (!_columns.TryGetValue(col, out var c)) return;
        Descending = EqualityComparer<TSort>.Default.Equals(c.Sort, SortBy) && !Descending;
        SortBy = c.Sort;
        ShowSort();
        if (_view is not null) _view.CustomSort = new Order(this);
    }

    private void ShowSort()
    {
        foreach (var (col, (header, sort)) in _columns)
            col.Header = EqualityComparer<TSort>.Default.Equals(sort, SortBy) ? header + (Descending ? "  ▼" : "  ▲") : header;
    }

    private sealed class Order(ListFilter<TRow, TSort> owner) : System.Collections.IComparer
    {
        private readonly TSort _by = owner.SortBy;
        private readonly bool _descending = owner.Descending;
        public int Compare(object? x, object? y) => x is TRow a && y is TRow b ? owner._compare(a, b, _by, _descending) : 0;
    }
}
