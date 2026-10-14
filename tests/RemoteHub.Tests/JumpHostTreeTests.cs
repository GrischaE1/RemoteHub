using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Connections and folders under a connection (reached through it as a jump host): what may go where, what they inherit,
/// and how they are saved – next to their jump host, so a RemoteHub version without jump hosts keeps them.
/// </summary>
[Collection("SettingsStore")]
public class JumpHostTreeTests : IDisposable
{
    public JumpHostTreeTests()
    {
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
    }

    public void Dispose() => StoreReset.Clean();

    /// <summary>Customer › JUMP01 › (SQL01, Branch › PC01), and a second jump host JUMP02 › WEB01 at the top level.</summary>
    private static (AppData Data, Folder Customer, Connection Jump, Connection Sql, Folder Branch, Connection Pc) Sample()
    {
        var data = new AppData();
        var customer = new Folder { Name = "Customer", InheritCredentials = false, Credentials = { Username = "customer-admin" } };
        var jump = new Connection { Name = "JUMP01", Host = "jump01", InheritCredentials = false, Credentials = { Username = "jump-admin" } };
        var sql = new Connection { Name = "SQL01", Host = "sql01" };
        var branch = new Folder { Name = "Branch" };
        var pc = new Connection { Name = "PC01", Host = "pc01" };
        branch.Children.Add(pc);
        jump.Children.Add(sql);
        jump.Children.Add(branch);
        customer.Children.Add(jump);
        customer.Children.Add(new Connection { Name = "Direct", Host = "direct" });
        data.Items.Add(customer);
        var jump2 = new Connection { Name = "JUMP02", Host = "jump02" };
        jump2.Children.Add(new Connection { Name = "WEB01", Host = "web01" });
        data.Items.Add(jump2);
        SettingsStore.RelinkParents(data.Items, null);
        return (data, customer, jump, sql, branch, pc);
    }

    [Fact]
    public void Items_below_a_jump_host_know_it_and_inherit_past_it()
    {
        var (data, customer, jump, sql, branch, pc) = Sample();
        Assert.Same(jump, sql.Parent);
        Assert.Same(jump, sql.JumpHost);
        Assert.Same(jump, pc.JumpHost);   // through a folder below the jump host
        Assert.Null(jump.JumpHost);
        Assert.Equal([branch, customer], pc.Ancestors());
        Assert.Equal("customer-admin", Inheritance.ResolveCredentials(sql, data).Credentials.Username); // never the jump host's own
        Assert.Equal("Customer › JUMP01 › Branch › PC01", Inheritance.Location(pc));
        Assert.Equal(["SQL01", "Branch", "PC01"], jump.Descendants().Select(n => n.Name));
        Assert.Equal("via JUMP01", pc.RouteHint);
        Assert.Equal("pc01 via JUMP01", pc.HostWithRoute);
        Assert.False(pc.RouteUsable); // JUMP01 has no agent
        Assert.Null(jump.RouteHint);
    }

    [Fact]
    public void Saved_next_to_the_jump_host_and_loaded_back_under_it()
    {
        var (data, _, _, _, _, _) = Sample();
        var json = SettingsStore.ToJson(data);

        // What an older RemoteHub reads: the items are on the jump host's level, each with the id of its jump host.
        var doc = JsonNode.Parse(json)!;
        var top = doc["Items"]!.AsArray();
        Assert.Equal(["Customer", "JUMP02", "WEB01"], top.Select(n => (string)n!["Name"]!));
        var inCustomer = top[0]!["Children"]!.AsArray();
        Assert.Equal(["JUMP01", "SQL01", "Branch", "Direct"], inCustomer.Select(n => (string)n!["Name"]!));
        Assert.NotNull(inCustomer[1]!["JumpHostId"]);
        Assert.Null(inCustomer[3]!["JumpHostId"]);
        Assert.Null(inCustomer[2]!["Children"]![0]!["JumpHostId"]); // below a jump host's folder: the folder carries the route
        Assert.Null(inCustomer[0]!["Children"]);                       // a connection has no "Children" in the file

        var back = SettingsStore.FromJson(json);
        var customer = Assert.IsType<Folder>(back.Items[0]);
        Assert.Equal(["JUMP01", "Direct"], customer.Children.Select(n => n.Name));
        var jump = Assert.IsType<Connection>(customer.Children[0]);
        Assert.Equal(["SQL01", "Branch"], jump.Children.Select(n => n.Name));
        Assert.Same(jump, ((Folder)jump.Children[1]).Children[0].JumpHost);
        Assert.Equal(["JUMP02"], back.Items.Skip(1).Select(n => n.Name));
        Assert.Equal(json, SettingsStore.ToJson(back));
    }

    [Fact]
    public void Moved_out_of_a_jump_host_it_is_saved_without_one()
    {
        var (data, customer, jump, sql, _, _) = Sample();
        var back = SettingsStore.FromJson(SettingsStore.ToJson(data));
        var loadedJump = (Connection)((Folder)back.Items[0]).Children[0];
        var loadedSql = loadedJump.Children[0];
        loadedJump.Children.Remove(loadedSql);
        ((Folder)back.Items[0]).Children.Add(loadedSql);
        loadedSql.Parent = back.Items[0];
        Assert.Null(loadedSql.JumpHostId); // the id read from the file doesn't stick
        var again = SettingsStore.FromJson(SettingsStore.ToJson(back));
        Assert.Equal(["JUMP01", "Direct", "SQL01"], ((Folder)again.Items[0]).Children.Select(n => n.Name));
    }

    [Fact]
    public void A_missing_jump_host_or_a_second_hop_in_the_file_leaves_items_where_they_are()
    {
        var a = new Connection { Name = "A", JumpHostId = Guid.NewGuid() };              // its jump host isn't there
        var host = new Connection { Name = "HOST" };
        var b = new Connection { Name = "B", JumpHostId = host.Id };
        var c = new Connection { Name = "C", JumpHostId = b.Id };                          // a second hop: not supported
        var data = new AppData();
        foreach (var n in new NodeBase[] { a, host, b, c }) data.Items.Add(n);
        var back = SettingsStore.FromJson(SettingsStore.ToJson(SettingsStore.Normalize(data)));
        Assert.Equal(["A", "HOST", "C"], back.Items.Select(n => n.Name));
        Assert.Equal(["B"], ((Connection)back.Items[1]).Children.Select(n => n.Name));
        Assert.Null(back.Items[2].JumpHost);
    }

    [Fact]
    public void The_database_keeps_items_under_their_jump_host()
    {
        var (data, _, _, _, _, _) = Sample();
        SettingsStore.Save(data);
        LocalDb.Close();
        var back = SettingsStore.Load();
        Assert.Equal(SettingsStore.ToJson(data), SettingsStore.ToJson(back));
        var jump = (Connection)((Folder)back.Items[0]).Children[0];
        Assert.Equal(["SQL01", "Branch"], jump.Children.Select(n => n.Name));
        Assert.Equal(["WEB01"], ((Connection)back.Items[1]).Children.Select(n => n.Name));
    }

    [Fact]
    public void Only_one_hop_below_a_jump_host()
    {
        var (data, customer, jump, sql, branch, pc) = Sample();
        var jump2 = (Connection)data.Items[1];
        var lone = new Connection { Name = "Lone" };
        Assert.Null(TreeLayout.WhyNot(lone, jump));
        Assert.Null(TreeLayout.WhyNot(lone, branch));
        Assert.Null(TreeLayout.WhyNot(new Folder(), jump));
        Assert.Contains("Only one jump host", TreeLayout.WhyNot(lone, sql));                // under a child
        Assert.Contains("Only one jump host", TreeLayout.WhyNot(lone, pc));
        Assert.Contains("is a jump host itself", TreeLayout.WhyNot(jump2, jump));           // a jump host below another
        Assert.Contains("is a jump host itself", TreeLayout.WhyNot(jump2, branch));
        var holder = new Folder { Name = "Holder" };
        holder.Children.Add(jump2);
        SettingsStore.RelinkParents(holder.Children, holder);
        Assert.Contains("in this folder is a jump host", TreeLayout.WhyNot(holder, jump));   // a folder containing one
        Assert.Contains("itself", TreeLayout.WhyNot(jump, branch));                          // into its own subtree
        Assert.Contains("itself", TreeLayout.WhyNot(customer, branch));
        Assert.Null(TreeLayout.WhyNot(new NetworkShare(), jump));                            // a share on its own, through the jump host
        Assert.Null(TreeLayout.WhyNot(new NetworkShare(), branch));
        Assert.Contains("Only one jump host", TreeLayout.WhyNot(new NetworkShare(), sql));
        Assert.Contains("Only connections", TreeLayout.WhyNot(new NetworkShare { Server = sql }, jump)); // a server's share stays with it
        Assert.Same(jump, TreeLayout.Allowed(new NetworkShare(), sql)); // the nearest place a share may go
        Assert.Null(TreeLayout.WhyNot(sql, null));
    }

    [Fact]
    public void A_share_on_its_own_below_a_jump_host_is_reached_through_it_and_saved_next_to_it()
    {
        var (data, customer, jump, _, branch, _) = Sample();
        var nas = new NetworkShare { Name = "NAS", Protocol = ShareProtocol.Sftp, Host = "nas01" };
        var docs = new NetworkShare { Name = "Docs", Protocol = ShareProtocol.Smb, Host = "files01", Path = "docs" };
        jump.Children.Add(nas);
        branch.Children.Add(docs);
        SettingsStore.RelinkParents(data.Items, null);
        Assert.Same(jump, nas.JumpHost);
        Assert.Same(jump, docs.JumpHost);
        Assert.Equal("via JUMP01", docs.RouteHint);
        Assert.Equal("customer-admin", Inheritance.ResolveCredentials(nas, data).Credentials.Username); // never the jump host's own

        var json = SettingsStore.ToJson(data);
        var inCustomer = JsonNode.Parse(json)!["Items"]![0]!["Children"]!.AsArray();
        Assert.Equal(["JUMP01", "SQL01", "Branch", "NAS", "Direct"], inCustomer.Select(n => (string)n!["Name"]!)); // an older RemoteHub shows it next to JUMP01
        Assert.Equal(jump.Id.ToString(), (string)inCustomer[3]!["JumpHostId"]!);

        var back = SettingsStore.FromJson(json);
        var loaded = (Connection)((Folder)back.Items[0]).Children[0];
        Assert.Equal(["SQL01", "Branch", "NAS"], loaded.Children.Select(n => n.Name));
        Assert.Same(loaded, ((Folder)loaded.Children[1]).Children.OfType<NetworkShare>().Single().JumpHost);
        Assert.Equal(json, SettingsStore.ToJson(back));
    }

    [Fact]
    public void Copying_a_folder_keeps_its_jump_hosts_items_and_copying_a_jump_host_doesnt()
    {
        var (data, customer, jump, _, _, _) = Sample();
        var json = JsonSerializer.SerializeToUtf8Bytes<NodeBase>(customer, SettingsStore.Json);
        var copy = (Folder)JsonSerializer.Deserialize<NodeBase>(json, SettingsStore.Json)!;
        TreeLayout.Nest(copy.Children);
        Assert.Equal(["SQL01", "Branch"], ((Connection)copy.Children[0]).Children.Select(n => n.Name));

        var jumpCopy = (Connection)JsonSerializer.Deserialize<NodeBase>(JsonSerializer.SerializeToUtf8Bytes<NodeBase>(jump, SettingsStore.Json), SettingsStore.Json)!;
        Assert.Empty(jumpCopy.Children);
    }
}
