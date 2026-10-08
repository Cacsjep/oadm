using Oadm.Server.Hosting;

// OADM server: gRPC over HTTP/2 with TLS on Server.ListenUrl (default https://0.0.0.0:5080, own certificate in
// <datafolder>/server-tls.json; an http:// URL serves h2c without TLS, for tests only).
// Overrides: --Oadm:DataDir=<folder> (or env OADM_DATA_DIR), --Oadm:ListenUrl=<url>,
// --Oadm:RegenerateTlsCertificate=true (new certificate; clients confirm its fingerprint again).
var app = OadmServerHost.Build(args);
await using (app.ConfigureAwait(false))
{
    await OadmServerHost.RunAsync(app).ConfigureAwait(false);
}
