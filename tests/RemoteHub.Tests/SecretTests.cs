using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.ViewModels;
using RemoteHub.Views;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;

namespace RemoteHub.Tests;

/// <summary>
/// Stored passwords held encrypted in memory (<see cref="Secret"/>): the value only inside a call, comparing without
/// decrypting, JSON and database records without the value, and editors that never show a stored password.
/// </summary>
public class SecretTests
{
    /// <summary>A made-up password for the tests, built at run time (no credential literals in the source).</summary>
    private static string TestPassword(string name) => string.Concat(name, "-ä \"", Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void A_secret_gives_its_value_only_to_the_call_that_uses_it()
    {
        var plain = TestPassword("Use");
        var secret = Secret.From(plain)!;
        Assert.Equal(plain, secret.Use(v => new string(v)));
        Assert.Equal(plain.Length, secret.Length);
        Assert.Equal(plain, new System.Net.NetworkCredential("", secret.ToSecureString()).Password);
        Assert.Equal("(password)", secret.ToString());
        Assert.DoesNotContain(plain, $"{secret}");
    }

    [Fact]
    public void An_empty_password_is_no_secret()
    {
        Assert.Null(Secret.From(""));
        Assert.Null(Secret.From((string?)null));
        Assert.Null(Secret.FromUtf8([]));
    }

    [Fact]
    public void Equal_passwords_compare_equal_and_different_ones_do_not()
    {
        var plain = TestPassword("Same");
        var (a, b, c) = (Secret.From(plain)!, Secret.From(plain)!, Secret.From(plain + "x")!);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(c));
        Assert.True(Secret.AreEqual(null, null));
        Assert.False(Secret.AreEqual(a, null));
        Assert.Single(new[] { a, b }.Distinct());
    }

    [Fact]
    public void Leading_or_trailing_white_space_is_noticed_without_showing_the_password()
    {
        Assert.True(Secret.From(" " + TestPassword("Space"))!.HasOuterWhiteSpace);
        Assert.False(Secret.From(TestPassword("Space") + "!")!.HasOuterWhiteSpace);
    }

    [Fact]
    public void A_password_in_a_settings_or_backup_file_is_a_plain_JSON_string_and_reads_back()
    {
        var plain = TestPassword("Json");
        var json = JsonSerializer.SerializeToUtf8Bytes(new CredentialSet { Username = "u", Password = Secret.From(plain) }, SettingsStore.Json);
        // Older versions read the file: the password is a JSON string, as before.
        Assert.Equal(plain, JsonDocument.Parse(json).RootElement.GetProperty("Password").GetString());
        var back = JsonSerializer.Deserialize<CredentialSet>(json, SettingsStore.Json)!;
        Assert.Equal(plain, back.Password?.Reveal());
        Assert.Null(JsonSerializer.Deserialize<CredentialSet>("""{"Username":"u"}""", SettingsStore.Json)!.Password);
    }

    [Fact]
    public void A_database_record_keeps_the_password_out_of_its_JSON()
    {
        var plain = TestPassword("Record");
        var conn = new Connection { Name = "srv", Host = "srv", InheritCredentials = false, Credentials = { Username = "adm", Password = Secret.From(plain) } };
        var data = new AppData { Items = [conn] };
        var record = AppDataRecords.From(data).Single(r => r.Id == conn.Id.ToString());
        Assert.DoesNotContain(plain, record.Data);
        Assert.DoesNotContain(SecretJsonConverter.PlaceholderPrefix, record.Data);
        var (path, value) = Assert.Single(record.Secrets);
        Assert.Equal("Credentials/Password", path);
        Assert.Same(conn.Credentials.Password, value);
    }

    [Fact]
    public void A_changed_password_changes_the_record_and_is_named_in_the_audit_but_never_shown()
    {
        var conn = new Connection { Name = "srv", Host = "srv", InheritCredentials = false, Credentials = { Username = "adm", Password = Secret.From(TestPassword("Old")) } };
        var data = new AppData { Items = [conn] };
        var before = AppDataRecords.From(data).Single(r => r.Id == conn.Id.ToString());
        conn.Credentials.Password = Secret.From(conn.Credentials.Password!.Reveal()); // the same value again
        Assert.Equal(before.Hash, AppDataRecords.From(data).Single(r => r.Id == conn.Id.ToString()).Hash);

        var plain = TestPassword("New");
        conn.Credentials.Password = Secret.From(plain);
        var after = AppDataRecords.From(data).Single(r => r.Id == conn.Id.ToString());
        Assert.NotEqual(before.Hash, after.Hash);
        var fields = AppDataRecords.ChangedFields(before, after);
        Assert.Equal(["Credentials.Password"], fields);
    }

    [Fact]
    public void A_sealed_password_opens_only_in_its_own_place()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = Secret.From(TestPassword("Seal"))!;
        var sealedValue = SecretBox.Seal(key, secret, "nodes|1|Credentials/Password");
        Assert.Equal(secret, SecretBox.OpenSecret(key, sealedValue, "nodes|1|Credentials/Password"));
        // The string form reads the same bytes (the move from settings.dat and the deleted items use it).
        Assert.Equal(secret.Reveal(), SecretBox.Open(key, sealedValue, "nodes|1|Credentials/Password"));
        Assert.Throws<InvalidDataException>(() => SecretBox.OpenSecret(key, sealedValue, "nodes|2|Credentials/Password"));
    }

    [Fact]
    public void The_editor_keeps_a_stored_password_unless_a_new_one_is_typed_or_it_is_removed()
    {
        var stored = Secret.From(TestPassword("Stored"))!;
        var conn = new Connection { Name = "srv", Host = "srv", InheritCredentials = false, Credentials = { Username = "adm", Password = stored } };
        var data = new AppData { Items = [conn] };

        var vm = new EditorViewModel(conn, data, isNew: false);
        Assert.True(vm.HasStoredPassword);
        Assert.Contains("A password is stored", vm.PasswordPlaceholder);
        vm.ReplacePassword(""); // the box was left empty
        Assert.True(vm.Apply(), vm.Error);
        Assert.Same(stored, conn.Credentials.Password);

        var typed = TestPassword("Typed");
        vm = new EditorViewModel(conn, data, isNew: false);
        vm.ReplacePassword(typed);
        Assert.True(vm.Apply(), vm.Error);
        Assert.Equal(typed, conn.Credentials.Password?.Reveal());

        vm = new EditorViewModel(conn, data, isNew: false);
        vm.RemovePassword();
        Assert.False(vm.HasStoredPassword);
        Assert.Contains("Leave empty", vm.PasswordPlaceholder);
        Assert.True(vm.Apply(), vm.Error);
        Assert.Null(conn.Credentials.Password);
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));

    [Fact]
    public void The_saved_credentials_window_never_shows_a_stored_password() => Sta.Run(owner =>
    {
        var stored = Secret.From(TestPassword("Saved"))!;
        var cred = new SavedCredential { Name = "Admin", Username = "adm", Password = stored };
        var data = new AppData { SavedCredentials = [cred] };
        var window = Sta.Theme(new CredentialsWindow(data, () => []) { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        window.Show();
        Sta.Pump();

        var box = Sta.Find<PasswordBox>(window);
        var remove = Sta.Find<Button>(window, b => Equals(b.Content, "Remove stored password"));
        Assert.Equal("", box.Password);
        Assert.Contains("A password is stored", box.PlaceholderText);
        Assert.True(remove.IsVisible);

        var typed = TestPassword("Typed");
        box.Password = typed;
        Assert.Equal(typed, cred.Password?.Reveal());
        box.Password = ""; // emptied again: the stored one stays
        Assert.Same(stored, cred.Password);

        Click(remove);
        Sta.Pump();
        Assert.Null(cred.Password);
        Assert.False(remove.IsVisible);
        Assert.Contains("Leave empty", box.PlaceholderText);
        window.Close();
    });

    [Fact]
    public void The_share_editor_never_shows_a_stored_password() => Sta.Run(owner =>
    {
        var share = new NetworkShare { Name = "media", Host = "nas", Path = "media", InheritCredentials = false,
                                       Credentials = { Username = "lab", Password = Secret.From(TestPassword("Share")) } };
        var window = Sta.Theme(new ShareEditorWindow(share, null, new AppData(), isNew: false) { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        window.Show();
        Sta.Pump();

        var box = Sta.Find<PasswordBox>(window);
        var remove = Sta.Find<Button>(window, b => Equals(b.Content, "Remove stored password"));
        Assert.Equal("", box.Password);
        Assert.Contains("A password is stored", box.PlaceholderText);
        Assert.True(remove.IsVisible);
        Click(remove);
        Sta.Pump();
        Assert.False(remove.IsVisible);
        Assert.Contains("Leave empty", box.PlaceholderText);
        window.Close();
    });
}
