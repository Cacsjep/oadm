using Oadm.SiteNode;
using Oadm.SiteNode.Cli;

// OADM Site Node (docs/sites.md). Without a verb (or "run") the service runs; with a verb it is the command line of the
// installed service: status, pairing-code, server <address>, allow-binding, networks, ports, accept-server, reset.
// --data-dir <folder> (or env OADM_SITE_NODE_DATA_DIR) uses another data folder; --port <n> the port of mode Wait.
var (verb, rest, dataDirectory, port) = CommandLine.Split(args);
if (verb is not null && verb != "run")
    return await CommandLine.RunAsync(verb, rest, dataDirectory, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);

var options = new NodeOptions();
if (dataDirectory is not null)
    options.DataDirectory = Path.GetFullPath(dataDirectory);
if (port is { } listenPort)
    options.ListenPort = listenPort;
options.AdminEndpoint = NodeOptions.EndpointFor(options.DataDirectory);
var app = NodeHost.Build(options, rest);
await using (app.ConfigureAwait(false))
{
    await app.RunAsync().ConfigureAwait(false);
}

await Serilog.Log.CloseAndFlushAsync().ConfigureAwait(false);
return 0;
