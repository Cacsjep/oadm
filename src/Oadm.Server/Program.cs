using Oadm.Server.Hosting;

// OADM server: gRPC over h2c on Server.ListenUrl (default http://0.0.0.0:5080).
// Overrides: --Oadm:DataDir=<folder> (or env OADM_DATA_DIR), --Oadm:ListenUrl=<url>.
var app = OadmServerHost.Build(args);
await using (app.ConfigureAwait(false))
{
    await OadmServerHost.RunAsync(app).ConfigureAwait(false);
}
