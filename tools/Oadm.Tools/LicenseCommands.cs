using System.Globalization;
using System.Text;

using Oadm.Sdk.Licensing;

namespace Oadm.Tools;

/// <summary>
/// The commands of <c>manage license</c>: <c>keys</c> writes a vendor key pair (never overwrites one), <c>sign</c> writes a
/// license file for one plugin on one server. Exit codes: 0 done, 2 a usage error, 1 anything else.
/// </summary>
internal static class LicenseCommands
{
    public const string PrivateKeyFile = "private-key.pem";
    public const string PublicKeyFile = "public-key.txt";

    public static int Run(string[] args, TextWriter output, TextWriter error, string currentDirectory, DateOnly today)
    {
        if (args.Length == 0)
        {
            return Usage(error, "missing command (keys or sign)");
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                return Usage(error, $"unexpected argument '{name}'");
            }

            var value = args[++i];
            if (name == "--limit")
            {
                var eq = value.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0 || !long.TryParse(value[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
                {
                    return Usage(error, $"--limit needs name=number, e.g. devices=500 (got '{value}')");
                }

                limits[value[..eq].Trim()] = number;
            }
            else
            {
                options[name] = value;
            }
        }

        try
        {
            return args[0] switch
            {
                "keys" => Keys(options, output, error, currentDirectory),
                "sign" => Sign(options, limits, output, error, currentDirectory, today),
                _ => Usage(error, $"unknown command '{args[0]}' (keys or sign)"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Keys(Dictionary<string, string> options, TextWriter output, TextWriter error, string currentDirectory)
    {
        if (options.Keys.Any(k => k != "--out"))
        {
            return Usage(error, "keys takes only --out");
        }

        var folder = Path.GetFullPath(options.GetValueOrDefault("--out") ?? "license-keys", currentDirectory);
        var privatePath = Path.Combine(folder, PrivateKeyFile);
        if (File.Exists(privatePath))
        {
            error.WriteLine($"error: {privatePath} exists already. A new key would make every license you sold invalid; choose another folder.");
            return 1;
        }

        Directory.CreateDirectory(folder);
        var (privateKey, publicKey) = LicenseFile.CreateKeys();
        File.WriteAllText(privatePath, privateKey, Encoding.ASCII);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(Path.Combine(folder, PublicKeyFile), publicKey + "\n", Encoding.ASCII);
        output.WriteLine($"Private key: {privatePath}");
        output.WriteLine("Keep it secret and back it up: it signs every license. Never put it into the plugin or the repository.");
        output.WriteLine();
        output.WriteLine("Add this to the server part's project (Oadm.Plugin.Sdk writes it into plugin.json):");
        output.WriteLine($"  <OadmLicensePublicKey>{publicKey}</OadmLicensePublicKey>");
        output.WriteLine("  <OadmLicenseFeatures></OadmLicenseFeatures>   <!-- optional: features separated by ; -->");
        output.WriteLine("  <OadmLicenseTrialDays>30</OadmLicenseTrialDays>");
        output.WriteLine();
        output.WriteLine("or, with a plugin.json of your own:");
        output.WriteLine("  \"license\": {");
        output.WriteLine($"    \"publicKey\": \"{publicKey}\",");
        output.WriteLine("    \"features\": [],");
        output.WriteLine("    \"trialDays\": 30");
        output.WriteLine("  }");
        return 0;
    }

    private static int Sign(Dictionary<string, string> options, Dictionary<string, long> limits, TextWriter output, TextWriter error, string currentDirectory, DateOnly today)
    {
        string[] known = ["--key", "--plugin", "--server", "--customer", "--expires", "--features", "--out"];
        if (options.Keys.FirstOrDefault(k => !known.Contains(k)) is { } unknown)
        {
            return Usage(error, $"unknown option {unknown}");
        }

        foreach (var required in new[] { "--key", "--plugin", "--server", "--customer" })
        {
            if (string.IsNullOrWhiteSpace(options.GetValueOrDefault(required)))
            {
                return Usage(error, $"{required} is missing");
            }
        }

        var server = options["--server"].Trim().ToLowerInvariant();
        if (!Guid.TryParseExact(server, "D", out _))
        {
            return Usage(error, $"--server must be the server ID of the Plugins page, e.g. 7f3c2a1e-5b8d-4c6f-9a2e-1d0b4e8f6c37 (got '{server}')");
        }

        DateOnly? expires = null;
        if (options.GetValueOrDefault("--expires") is { } expiresText)
        {
            if (!DateOnly.TryParseExact(expiresText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return Usage(error, $"--expires needs a date like 2027-12-31 (got '{expiresText}')");
            }

            if (day < today)
            {
                return Usage(error, $"--expires {expiresText} is in the past");
            }

            expires = day;
        }

        var features = (options.GetValueOrDefault("--features") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var customer = options["--customer"].Trim();
        var data = new LicenseData(options["--plugin"].Trim(), server, customer, today, expires, features, limits);
        var keyPem = File.ReadAllText(Path.GetFullPath(options["--key"], currentDirectory));
        var text = LicenseFile.Sign(data, keyPem);
        var outPath = Path.GetFullPath(options.GetValueOrDefault("--out") ?? FileName(data), currentDirectory);
        if (File.Exists(outPath))
        {
            error.WriteLine($"error: {outPath} exists already; choose another --out.");
            return 1;
        }

        File.WriteAllText(outPath, text + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        output.WriteLine($"License for {customer}: {outPath}");
        output.WriteLine(text);
        output.WriteLine($"Plugin {data.Plugin}, server {server}, {(expires is { } e ? "until " + LicenseFile.Day(e) : "no expiry")}"
            + (features.Count > 0 ? ", features " + string.Join(", ", features) : ", every feature")
            + (limits.Count > 0 ? ", " + string.Join(", ", limits.Select(l => $"{l.Key} {l.Value}")) : string.Empty) + ".");
        output.WriteLine("Send the customer the key or the file; they enter it on the Plugins page (Enter license).");
        return 0;
    }

    /// <summary>"acme-gmbh-example.site-reports.oadmlicense".</summary>
    internal static string FileName(LicenseData data)
    {
        var slug = new string([.. data.Customer.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return (slug.Length == 0 ? "customer" : slug) + "-" + data.Plugin + LicenseFile.Extension;
    }

    private static int Usage(TextWriter error, string message)
    {
        error.WriteLine("error: " + message);
        error.WriteLine("Run \"oadm help license\" for the options.");
        return 2;
    }
}
