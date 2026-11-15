using System.Runtime.InteropServices;
using System.Text;
using Glebosaurus.Agent;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
// systemd zatrzymuje usługę sygnałem SIGTERM – kończymy czysto, jak przy Ctrl+C.
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });

try
{
    return await Cli.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Przerwano.");
    return 130;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Upłynął limit czasu operacji Bluetooth.");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Błąd: {exception.Message}");
    return 1;
}
