using RemoteHub.Models;
using RemoteHub.ViewModels;

namespace RemoteHub.Tests;

/// <summary>Tabs grouped by connection: top row = connections, second row = that connection's tabs.</summary>
public class SessionGroupTests
{
    [Fact]
    public void Tabs_are_grouped_by_connection_and_selection_follows()
    {
        var vm = new MainViewModel(new AppData());
        var a = new Connection { Name = "A", Host = "a" };
        var b = new Connection { Name = "B", Host = "b" };
        var aReg = vm.OpenRegistry(a, null);
        var aFiles = vm.OpenFileBrowser(a, null);
        var bReg = vm.OpenRegistry(b, null);

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal([aReg, aFiles], vm.Groups[0].Sessions);
        Assert.Equal("Registry · File transfer", vm.Groups[0].Summary);
        Assert.Same(bReg, vm.SelectedSession);
        Assert.Same(vm.Groups[1], vm.SelectedGroup);
        Assert.True(vm.Groups[1].IsSelected);

        // Choosing connection A shows its last used tab.
        vm.GroupSelection = vm.Groups[0];
        Assert.Same(aFiles, vm.SelectedSession);
        vm.TabSelection = aReg;
        vm.GroupSelection = vm.Groups[1];
        vm.GroupSelection = vm.Groups[0];
        Assert.Same(aReg, vm.SelectedSession);

        // A list box clearing its selection while it switches items must not lose the tab.
        vm.TabSelection = null;
        vm.GroupSelection = null;
        Assert.Same(aReg, vm.SelectedSession);

        // Ctrl+Tab goes connection by connection, tab by tab.
        vm.SelectNextSession(1);
        Assert.Same(aFiles, vm.SelectedSession);
        vm.SelectNextSession(1);
        Assert.Same(bReg, vm.SelectedSession);

        // Closing: next tab of the same connection first, then the neighbouring connection.
        vm.TabSelection = aFiles;
        aFiles.Close();
        Assert.Same(aReg, vm.SelectedSession);
        aReg.Close();
        Assert.Single(vm.Groups);
        Assert.Same(bReg, vm.SelectedSession);
        bReg.Close();
        Assert.Empty(vm.Groups);
        Assert.Null(vm.SelectedSession);
        Assert.Null(vm.SelectedGroup);
    }
}
