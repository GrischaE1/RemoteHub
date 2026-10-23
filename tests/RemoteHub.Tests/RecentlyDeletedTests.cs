using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.ViewModels;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// "Recently deleted" for folders and connections (docs/scripts-view-design.md §4.9): a deleted item is kept on this PC
/// for 30 days with what was inside it, its saved passwords and its favourite pins, and comes back where it was.
/// </summary>
[Collection("SettingsStore")]
public class RecentlyDeletedTests : IDisposable
{
    public RecentlyDeletedTests()
    {
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
        LocalDb.Open();
    }

    public void Dispose() => StoreReset.Clean();

    private static (MainViewModel Vm, Folder Lab, Connection Dc) Tree()
    {
        var data = new AppData();
        var lab = new Folder { Name = "LAB" };
        var dc = new Connection { Name = "DC01", Host = "dc01.lab", Parent = lab, InheritCredentials = false, Credentials = new CredentialSet { Username = "admin", Password = Secret.From("S3cret!") } };
        lab.Children.Add(dc);
        data.Items.Add(lab);
        data.Items.Add(new Connection { Name = "WEB01", Host = "web01" });
        data.Ui.FavoriteConnections.Add(dc.Id);
        return (new MainViewModel(data), lab, dc);
    }

    [Fact]
    public void A_deleted_folder_comes_back_with_its_connections_passwords_and_pins()
    {
        var (vm, lab, dc) = Tree();
        vm.Delete(lab);
        Assert.DoesNotContain(vm.AllNodes(), n => n.Id == dc.Id);
        Assert.Empty(vm.Data.Ui.FavoriteConnections);
        var entry = Assert.Single(DeletedNodeStore.List());
        Assert.Equal(("LAB", "folder", 1), (entry.Name, entry.Kind, entry.Items));

        var back = Assert.IsType<Folder>(vm.RestoreDeleted(entry.Id));
        var restored = Assert.IsType<Connection>(Assert.Single(back.Children));
        Assert.Same(back, restored.Parent);
        Assert.Equal(("DC01", "S3cret!"), (restored.Name, restored.Credentials.Password?.Reveal()));
        Assert.Equal([dc.Id], vm.Data.Ui.FavoriteConnections);
        Assert.Empty(DeletedNodeStore.List());
    }

    [Fact]
    public void A_connection_goes_back_into_its_folder_or_to_the_top_when_the_folder_is_gone()
    {
        var (vm, lab, dc) = Tree();
        vm.Delete(dc);
        Assert.Same(lab, vm.RestoreDeleted(dc.Id.ToString())!.Parent);

        var dc2 = (Connection)lab.Children.Single();
        vm.Delete(dc2);
        vm.Delete(lab);
        var back = vm.RestoreDeleted(dc2.Id.ToString())!;
        Assert.Null(back.Parent);
        Assert.Contains(back, vm.Items);
    }

    [Fact]
    public void An_item_isnt_restored_twice()
    {
        var (vm, _, dc) = Tree();
        vm.Delete(dc);
        Assert.NotNull(vm.RestoreDeleted(dc.Id.ToString()));
        Assert.Null(vm.RestoreDeleted(dc.Id.ToString()));
    }

    [Fact]
    public void The_password_is_sealed_in_the_database_not_stored_as_text()
    {
        var (vm, _, dc) = Tree();
        vm.Delete(dc);
        var blob = LocalDb.Use((c, _) => (byte[])LocalDb.Scalar(c, "SELECT data FROM deleted_nodes")!);
        Assert.DoesNotContain("S3cret!", System.Text.Encoding.UTF8.GetString(blob));
        Assert.DoesNotContain("dc01.lab", System.Text.Encoding.UTF8.GetString(blob));
    }

    [Fact]
    public void Deleting_for_good_and_after_thirty_days_removes_the_item()
    {
        var (vm, lab, dc) = Tree();
        vm.Delete(dc);
        vm.Delete(lab);
        DeletedNodeStore.DeleteNow(dc.Id.ToString());
        Assert.Equal(["LAB"], DeletedNodeStore.List().Select(d => d.Name));
        Assert.Equal(0, DeletedNodeStore.Purge(DateTime.UtcNow.AddDays(29)));
        Assert.Equal(1, DeletedNodeStore.Purge(DateTime.UtcNow.AddDays(31)));
        Assert.Empty(DeletedNodeStore.List());
    }
}
