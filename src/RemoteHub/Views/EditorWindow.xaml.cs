using System.Windows;
using RemoteHub.Models;
using RemoteHub.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteHub.Views;

public partial class EditorWindow : FluentWindow
{
    private readonly EditorViewModel _vm;

    public EditorWindow(NodeBase? node, AppData data, bool isNew)
    {
        InitializeComponent();
        DataContext = _vm = new EditorViewModel(node, data, isNew);
        PasswordInput.Password = _vm.Password ?? "";
        Loaded += (_, _) =>
        {
            if (_vm.IsConnection && isNew) HostBox.Focus();
            else NameBox.Focus();
        };
    }

    private void PickIconImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images (*.png;*.ico;*.jpg;*.jpeg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.IconImage = IconImages.LoadAsBase64Png(dlg.FileName); }
        catch (Exception ex) { _vm.Error = "The image could not be loaded: " + ex.Message; }
    }

    private void RemoveIconImage_Click(object sender, RoutedEventArgs e) => _vm.IconImage = null;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordInput.Password;
        if (_vm.Apply()) DialogResult = true;
    }
}
