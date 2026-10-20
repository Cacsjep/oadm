using Microsoft.Extensions.DependencyInjection;

using Oadm.SiteNode;
using Oadm.SiteNode.Cli;

// OADM Site Node (docs/sites.md). Without a verb (or "run") the service runs; with a verb it is the command line of the
// installed service: status, pairing-code, server <address>, allow-binding, networks, ports, accept-server, reset.
// --data-dir <folder> (or env OADM_SITE_NODE_DATA_DIR) uses another data folder.
var (verb, rest, dataDirectory) = CommandLine.Split(args);
if (verb is not null && verb != "run")
    return await CommandLine.RunAsync(verb, rest, dataDirectory, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);

var options = new NodeOptions();
if (dataDirectory is not null)
    options.DataDirectory = Path.GetFullPath(dataDirectory);
options.AdminEndpoint = NodeOptions.EndpointFor(options.DataDirectory);
var app = NodeHost.Build(options, rest);
var failed = false;
await using (app.ConfigureAwait(false))
{
    await app.RunAsync().ConfigureAwait(false);
    // A part that failed stops the host: exit non-zero so that systemd or the SCM restarts the node.
    failed = app.Services.GetRequiredService<NodeFailure>().Failed;
}

await Serilog.Log.CloseAndFlushAsync().ConfigureAwait(false);
return failed ? 1 : 0;
