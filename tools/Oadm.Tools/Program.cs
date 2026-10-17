using Oadm.Tools;

// The oadm command (docs/plugin-sdk.md). Exit codes: 0 done, 1 failed (the message says why), 2 usage error.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // the running command ends cleanly (the development server stops, the service starts again)
    cancel.Cancel();
};

var output = Console.Out;
var error = Console.Error;
StreamWriter? log = null;
var list = args.ToList();
if (list.IndexOf("--log") is var at and >= 0 && at + 1 < list.Count)
{
    // An elevated run (oadm started itself with administrator rights): its output also goes to the caller's file.
    log = new StreamWriter(list[at + 1]) { AutoFlush = true };
    output = new TeeWriter(Console.Out, log);
    error = new TeeWriter(Console.Error, log);
    list.RemoveRange(at, 2);
}

try
{
    return await Commands.RunAsync(list, output, error, cancel.Token).ConfigureAwait(false);
}
finally
{
    if (log is not null)
    {
        await log.DisposeAsync().ConfigureAwait(false);
    }
}
