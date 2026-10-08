using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RemoteHub.Services;

/// <summary>
/// Keeps one instance per user, Windows session and data folder. A second launch (e.g. double-clicking an
/// .rdp file) forwards its arguments to the running instance over a named pipe restricted to the current user.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    private static string Key()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var raw = $"{sid}|{Process.GetCurrentProcess().SessionId}|{SettingsStore.Directory.ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24];
    }

    /// <summary>Returns the primary-instance handle, or null after forwarding <paramref name="args"/> to the running instance.</summary>
    public static SingleInstance? ClaimOrForward(string[] args)
    {
        var key = Key();
        var mutex = new Mutex(true, $@"Local\RemoteHub-{key}", out var created);
        var pipe = $"RemoteHub-{key}";
        if (created) return new SingleInstance(mutex, pipe);

        mutex.Dispose();
        try
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: let the running instance come to the front
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var payload = Encoding.UTF8.GetBytes(string.Join("\n", args.Select(Path.GetFullPath)));
            client.Write(payload);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Could not reach running instance: " + ex.Message);
        }
        return null;
    }

    /// <summary>Starts listening for forwarded arguments; <paramref name="onArgs"/> runs on a background thread.</summary>
    public void Listen(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, _cts.Token);
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine("Single-instance pipe: " + ex.Message);
                    await Task.Delay(500);
                }
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }
}

