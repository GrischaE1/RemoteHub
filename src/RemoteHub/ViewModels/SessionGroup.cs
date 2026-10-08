using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Models;
using RemoteHub.Rdp;

namespace RemoteHub.ViewModels;

/// <summary>All open tabs of one connection (remote desktop, PowerShell, file transfer, registry): one tab in the top row.</summary>
public sealed partial class SessionGroup : ObservableObject
{
    public SessionGroup(Connection connection)
    {
        Connection = connection;
        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Tooltip));
            OnPropertyChanged(nameof(HasMultiple));
            OnPropertyChanged(nameof(SingleSymbol));
            OnPropertyChanged(nameof(ShowSingleSymbol));
        };
    }

    public Connection Connection { get; }
    public ObservableCollection<SessionBase> Sessions { get; } = [];
    /// <summary>The tab shown when the group is selected again.</summary>
    public SessionBase? LastSelected { get; set; }

    [ObservableProperty] private bool _isSelected;

    public System.Windows.Media.Brush? AccentBrush => Sessions.FirstOrDefault()?.AccentBrush;
    /// <summary>Tooltip: which tabs are open.</summary>
    public string Summary => string.Join(" · ", Sessions.Select(s => s.KindLabel));

    /// <summary>The second tab row is only needed when the connection has more than one tab.</summary>
    public bool HasMultiple => Sessions.Count > 1;
    /// <summary>With a single tab, the connection tab shows what it is (no icon for remote desktop, like before grouping).</summary>
    public string SingleSymbol => Sessions.Count == 1 ? Sessions[0].KindSymbol : "Desktop24";
    public bool ShowSingleSymbol => Sessions.Count == 1 && Sessions[0] is not RdpSession;
    public string Tooltip => Summary;

    public void RefreshAccent() => OnPropertyChanged(nameof(AccentBrush));
}
