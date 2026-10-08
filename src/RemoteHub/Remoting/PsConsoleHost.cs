using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace RemoteHub.Remoting;

/// <summary>What the PowerShell host needs from the console UI. All members are called on background threads.</summary>
public interface IConsoleSink
{
    void Write(string text, ConsoleColor? color);
    /// <summary>Waits for the next line typed in the console; null when the command was stopped.</summary>
    string? ReadLine();
    /// <summary>Asks for a secret (password dialog); null when cancelled.</summary>
    string? ReadSecret(string caption, string message);
    void Progress(ProgressRecord record);
    int Width { get; }
}

/// <summary>
/// Minimal PowerShell host for the remote session: output, colours, warnings and progress go to the console view;
/// Read-Host, confirmations and parameter prompts are answered in the console (passwords in a dialog).
/// </summary>
public sealed class PsConsoleHost : PSHost
{
    private readonly Ui _ui;
    private readonly Guid _id = Guid.NewGuid();

    public PsConsoleHost(IConsoleSink sink) => _ui = new Ui(sink);

    public override string Name => "RemoteHub";
    public override Version Version => typeof(PsConsoleHost).Assembly.GetName().Version ?? new Version(1, 0);
    public override Guid InstanceId => _id;
    public override PSHostUserInterface UI => _ui;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override void EnterNestedPrompt() => throw new NotSupportedException("Nested prompts are not supported.");
    public override void ExitNestedPrompt() => throw new NotSupportedException("Nested prompts are not supported.");
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
    public override void SetShouldExit(int exitCode) { }

    private sealed class Ui(IConsoleSink sink) : PSHostUserInterface
    {
        private readonly RawUi _raw = new(sink);
        public override PSHostRawUserInterface RawUI => _raw;

        public override void Write(string value) => sink.Write(value, null);
        public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => sink.Write(value, foregroundColor);
        public override void WriteLine(string value) => sink.Write(value + "\n", null);
        public override void WriteErrorLine(string value) => sink.Write(value + "\n", ConsoleColor.Red);
        public override void WriteWarningLine(string message) => sink.Write("WARNING: " + message + "\n", ConsoleColor.Yellow);
        public override void WriteVerboseLine(string message) => sink.Write("VERBOSE: " + message + "\n", ConsoleColor.Cyan);
        public override void WriteDebugLine(string message) => sink.Write("DEBUG: " + message + "\n", ConsoleColor.Magenta);
        public override void WriteProgress(long sourceId, ProgressRecord record) => sink.Progress(record);
        // Write-Host already arrives via Write(); other information records are not shown (like the console host).
        public override void WriteInformation(InformationRecord record) { }

        public override string ReadLine() => sink.ReadLine() ?? throw new PipelineStoppedException();

        public override SecureString ReadLineAsSecureString() =>
            ToSecure(sink.ReadSecret("Input", "Enter the value") ?? throw new PipelineStoppedException());

        public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
        {
            WriteHeader(caption, message);
            var result = new Dictionary<string, PSObject>();
            foreach (var d in descriptions)
            {
                var type = d.ParameterTypeFullName ?? "";
                if (type.Contains("PSCredential", StringComparison.OrdinalIgnoreCase))
                    result[d.Name] = new PSObject(PromptForCredential(caption, message, "", ""));
                else if (type.Contains("SecureString", StringComparison.OrdinalIgnoreCase))
                    result[d.Name] = new PSObject(ToSecure(sink.ReadSecret(caption, d.Name) ?? throw new PipelineStoppedException()));
                else
                {
                    sink.Write(string.IsNullOrEmpty(d.Label) ? d.Name + ": " : d.Label.Replace("&", "") + ": ", null);
                    result[d.Name] = new PSObject(ReadLine());
                }
            }
            return result;
        }

        public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
        {
            WriteHeader(caption, message);
            var keys = choices.Select(Hotkey).ToList();
            var line = string.Join("  ", choices.Select((c, i) => $"[{keys[i]}] {c.Label.Replace("&", "")}")) + "  [?] Help";
            if (defaultChoice >= 0 && defaultChoice < choices.Count) line += $" (default is \"{keys[defaultChoice]}\")";
            while (true)
            {
                sink.Write(line + ": ", ConsoleColor.Yellow);
                var answer = ReadLine().Trim();
                if (answer.Length == 0 && defaultChoice >= 0) return defaultChoice;
                if (answer == "?")
                {
                    for (int i = 0; i < choices.Count; i++)
                        sink.Write($"{keys[i]} - {choices[i].HelpMessage}\n", null);
                    continue;
                }
                var idx = keys.FindIndex(k => k.Equals(answer, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = choices.ToList().FindIndex(c => c.Label.Replace("&", "").Equals(answer, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) return idx;
            }
        }

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
            PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName,
            PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        {
            WriteHeader(caption, message);
            if (string.IsNullOrEmpty(userName))
            {
                sink.Write("User: ", null);
                userName = ReadLine().Trim();
            }
            var pw = sink.ReadSecret(string.IsNullOrEmpty(caption) ? "Credential" : caption, $"Password for {userName}")
                     ?? throw new PipelineStoppedException();
            return new PSCredential(userName, ToSecure(pw));
        }

        private void WriteHeader(string caption, string message)
        {
            if (!string.IsNullOrEmpty(caption)) sink.Write(caption + "\n", null);
            if (!string.IsNullOrEmpty(message)) sink.Write(message + "\n", null);
        }

        private static string Hotkey(ChoiceDescription c)
        {
            var i = c.Label.IndexOf('&');
            return i >= 0 && i + 1 < c.Label.Length ? c.Label[i + 1].ToString().ToUpperInvariant() : c.Label.Length > 0 ? c.Label[..1] : "?";
        }

        private static SecureString ToSecure(string s)
        {
            var ss = new SecureString();
            foreach (var ch in s) ss.AppendChar(ch);
            ss.MakeReadOnly();
            return ss;
        }
    }

    /// <summary>Just enough of a console window for formatting (width) – the buffer itself lives in the WPF view.</summary>
    private sealed class RawUi(IConsoleSink sink) : PSHostRawUserInterface
    {
        public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
        public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
        public override Coordinates CursorPosition { get; set; }
        public override int CursorSize { get; set; } = 25;
        public override Size BufferSize { get => new(sink.Width, 9999); set { } }
        public override Size WindowSize { get => new(sink.Width, 50); set { } }
        public override Size MaxWindowSize => new(sink.Width, 50);
        public override Size MaxPhysicalWindowSize => new(sink.Width, 50);
        public override Coordinates WindowPosition { get; set; }
        public override string WindowTitle { get; set; } = "";
        public override bool KeyAvailable => false;
        public override void FlushInputBuffer() { }
        public override BufferCell[,] GetBufferContents(Rectangle rectangle) => throw new NotSupportedException();
        public override KeyInfo ReadKey(ReadKeyOptions options) => throw new NotSupportedException("Reading single keys is not supported in this console.");
        public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
        public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
        public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }
    }
}
