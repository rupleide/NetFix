using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NetFix.Services;

using Application = System.Windows.Application;

namespace NetFix;

public partial class App : Application
{
    private const string MutexName = "NetFix_SingleInstance_Mutex";
    private const string PipeName = "NetFix_SingleInstance_Pipe";
    private static Mutex? _mutex;
    private static CancellationTokenSource? _pipeCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        StartPipeServer();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client);
            writer.WriteLine("ACTIVATE");
            writer.Flush();
        }
        catch
        {
        }
    }

    private static void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            while (!_pipeCts.Token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_pipeCts.Token);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(_pipeCts.Token);
                    if (line == "ACTIVATE")
                    {
                        Current?.Dispatcher.BeginInvoke(() =>
                        {
                            if (Current.MainWindow is MainWindow main)
                            {
                                main.ShowFromTray();
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(100);
                }
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
