using System.Threading;

namespace RemoteHub.Tests;

/// <summary>Guards against the WPF-UI PasswordBox returning something other than what the user entered.</summary>
public class PasswordBoxTests
{
    private static void Sta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        TestThread.Join(t, "the STA part");
        if (err is not null) throw new Exception(err.Message, err);
    }

    [Fact]
    public void PasswordBox_returns_entered_value_for_set_type_paste_and_edit()
    {
        Sta(() =>
        {
            var pb = new Wpf.Ui.Controls.PasswordBox { Password = "Abc!123" };
            Assert.Equal("Abc!123", pb.Password);
            Assert.DoesNotContain("Abc", pb.Text); // masked

            pb.CaretIndex = pb.Text.Length;
            pb.SelectedText = "x";
            Assert.Equal("Abc!123x", pb.Password);

            pb.SelectAll();
            pb.SelectedText = "N";
            Assert.Equal("N", pb.Password);

            var typed = new Wpf.Ui.Controls.PasswordBox();
            foreach (var ch in "P@ss w0rd!äß") { typed.CaretIndex = typed.Text.Length; typed.SelectedText = ch.ToString(); }
            Assert.Equal("P@ss w0rd!äß", typed.Password);

            var pasted = new Wpf.Ui.Controls.PasswordBox { SelectedText = "Pasted#Pw1" };
            Assert.Equal("Pasted#Pw1", pasted.Password);
        });
    }
}
