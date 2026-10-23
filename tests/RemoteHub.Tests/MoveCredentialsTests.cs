using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.ViewModels;

namespace RemoteHub.Tests;

/// <summary>Moving an item in or out of a folder changes the credentials it inherits – detected so the UI can ask first.</summary>
public class MoveCredentialsTests
{
    [Fact]
    public void Moving_out_of_a_folder_with_credentials_is_detected()
    {
        var data = new AppData();
        var lab = new Folder { Name = "LAB", InheritCredentials = false, Credentials = new CredentialSet { Username = "[REDACTED]", Password = Secret.From("x") } };
        var other = new Folder { Name = "Other" };
        var nuc = new Connection { Name = "NUC13", Host = "h", Parent = lab };
        lab.Children.Add(nuc);
        data.Items.Add(lab);
        data.Items.Add(other);
        var vm = new MainViewModel(data);

        Assert.Equal("LAB › NUC13", Inheritance.Location(nuc));
        // To the top level: from LAB's credentials to none.
        Assert.Equal(("folder \"LAB\"", "none – Windows will prompt"), vm.CredentialChange(nuc, null));
        // Reordering inside the same folder changes nothing.
        Assert.Null(vm.CredentialChange(nuc, lab));
        // A folder without own credentials inherits from the top level: still a change.
        Assert.NotNull(vm.CredentialChange(nuc, other));

        vm.Move(nuc, null);
        Assert.Equal("Top level › NUC13", Inheritance.Location(nuc));
        Assert.Equal(("none – Windows will prompt", "folder \"LAB\""), vm.CredentialChange(nuc, lab));

        // Own credentials travel with the item: no change.
        nuc.InheritCredentials = false;
        Assert.Null(vm.CredentialChange(nuc, lab));
    }
}
