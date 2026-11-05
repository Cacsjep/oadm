using Oadm.Server.Hosting;

// OADM server: gRPC over HTTP/2 with TLS on Server.ListenUrl (default https://0.0.0.0:5080, own certificate in
// <datafolder>/server-tls.json; an http:// URL serves h2c without TLS, for tests only).
// Overrides: --Oadm:DataDir=<folder> (or env OADM_DATA_DIR), --Oadm:ListenUrl=<url>,
// --Oadm:RegenerateTlsCertificate=true (new certificate; clients confirm its fingerprint again).
// --update runs the update helper (Updates/ServerUpdateHelper).
// --print-folders prints the data and plugin folders as JSON and exits (FolderReport).
// --self-check checks the libraries and plugins this program needs and exits, 1 on a problem (SelfCheck).
// A restore stops the host; on a console a new one is built in this process (the installed service is restarted by its
// service manager instead, see ServerRestart).
// --update: the update helper (docs/spec/self-update.md), a copy of this program started outside the service.
if (args.Contains(Oadm.Server.Updates.HelperArguments.Flag, StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = await Oadm.Server.Updates.ServerUpdateHelper.RunFromCommandLineAsync(args).ConfigureAwait(false);
    return;
}

if (args.Contains(SelfCheck.Argument, StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = SelfCheck.Run(Console.Out, addOns: SelfCheck.AddOnFolders(args));
    return;
}

if (args.Contains(FolderReport.Argument, StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(FolderReport.Build(args));
    return;
}

do
{
    Oadm.Server.Backup.ServerRestart.Clear();
    var app = OadmServerHost.Build(args);
    await using (app.ConfigureAwait(false))
    {
        await OadmServerHost.RunAsync(app).ConfigureAwait(false);
    }

    // The connection pool belongs to the process: the next host (and the restore swap) must not reuse the old file.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
while (Oadm.Server.Backup.ServerRestart.Pending);
