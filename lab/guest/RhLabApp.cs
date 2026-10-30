// RhLabApp - the lab's stand-in for "an app" in app tab tests (docs/app-tabs-design.md, lab/tests/D-APP*.Tests.ps1).
// Compiled in the guest by Install-LabTestApp (Add-Type, C# 5 - no newer syntax). A plain window filled with one colour,
// so a picture shows which app it is, and a small text box whose text is logged, so a test sees where typing went.
//   RhLabApp.exe [/colour:R,G,B] [/log:<file>] [/title:<text>] [/ask]
//   /ask - closing asks "Save changes?" (Yes / No / Cancel), as an app with unsaved work does.
// The log is JSON lines: started (pid, elevated), activated, text, closing, closed.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

public static class RhLabApp
{
    private static string _log;

    [STAThread]
    public static void Main(string[] args)
    {
        var colour = Color.FromArgb(255, 0, 0);
        var title = "RH Lab App";
        var ask = false;
        foreach (var a in args)
        {
            if (a.StartsWith("/colour:", StringComparison.OrdinalIgnoreCase))
            {
                var p = a.Substring(8).Split(',');
                colour = Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
            }
            else if (a.StartsWith("/log:", StringComparison.OrdinalIgnoreCase)) _log = a.Substring(5);
            else if (a.StartsWith("/title:", StringComparison.OrdinalIgnoreCase)) title = a.Substring(7);
            else if (a.Equals("/ask", StringComparison.OrdinalIgnoreCase)) ask = true;
        }
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Log("started", "\"pid\":" + Process.GetCurrentProcess().Id + ",\"elevated\":" + (elevated ? "true" : "false") + ",\"title\":" + Quote(title));

        Application.EnableVisualStyles();
        var form = new Form { Text = title, BackColor = colour, Width = 800, Height = 600, StartPosition = FormStartPosition.CenterScreen };
        var box = new TextBox { Left = 8, Top = 8, Width = 200 };
        form.Controls.Add(box);
        box.TextChanged += delegate { Log("text", "\"text\":" + Quote(box.Text)); };
        form.Activated += delegate { Log("activated", ""); };
        form.FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            Log("closing", "");
            if (!ask) return;
            var answer = MessageBox.Show(form, "Save changes?", title, MessageBoxButtons.YesNoCancel);
            Log("asked", "\"answer\":" + Quote(answer.ToString()));
            if (answer == DialogResult.Cancel) e.Cancel = true;
        };
        form.FormClosed += delegate { Log("closed", ""); };
        Application.Run(form);
    }

    private static void Log(string e, string more)
    {
        if (string.IsNullOrEmpty(_log)) return;
        var line = "{\"e\":" + Quote(e) + ",\"at\":" + Quote(DateTime.UtcNow.ToString("o")) + (more.Length > 0 ? "," + more : "") + "}" + Environment.NewLine;
        for (var i = 0; i < 20; i++)
        {
            try { File.AppendAllText(_log, line, Encoding.UTF8); return; }
            catch (IOException) { System.Threading.Thread.Sleep(25); } // the test reads it at the same moment
        }
    }

    private static string Quote(string s)
    {
        var b = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }
}
