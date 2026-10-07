using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oadm.Plugins.Pki.Client;

/// <summary>The kinds of files the PKI page opens and saves (file type filters of the pickers).</summary>
public enum PkiFileKind
{
    /// <summary>Public CA certificate, PEM (.crt).</summary>
    CertificatePem = 0,

    /// <summary>Public CA certificate, DER (.cer).</summary>
    CertificateDer = 1,

    /// <summary>CA backup with the key (.pfx).</summary>
    Backup = 2,

    /// <summary>CA to import: .pfx, .p12, .pem, .crt, .cer, .key.</summary>
    ImportCa = 3,

    /// <summary>Private key of a PEM certificate: .key, .pem.</summary>
    PrivateKey = 4,

    /// <summary>RADIUS server CA: .crt, .cer, .pem.</summary>
    RadiusCa = 5,
}

/// <summary>A file the user picked (at most 1 MB + 1 byte is read, so "too large" can be told).</summary>
public sealed record PickedFile(string Name, byte[] Data);

/// <summary>File pickers of the page and its dialogs (tests use a fake).</summary>
public interface IPkiFiles
{
    /// <summary>The chosen file, or null when cancelled.</summary>
    Task<PickedFile?> OpenAsync(string title, PkiFileKind kind);

    /// <summary>Saves <paramref name="data"/> where the user says; returns the folder ("" when unknown), or null when cancelled.</summary>
    Task<string?> SaveAsync(string title, string fileName, PkiFileKind kind, byte[] data, string? startFolder);
}

/// <summary>The platform pickers of a top level (page view or dialog window).</summary>
public sealed class PkiFilePicker(Func<TopLevel?> topLevel) : IPkiFiles
{
    private static readonly FilePickerFileType CaFiles = new("CA files (*.pfx, *.p12, *.pem, *.crt, *.cer, *.key)") { Patterns = ["*.pfx", "*.p12", "*.pem", "*.crt", "*.cer", "*.key"] };
    private static readonly FilePickerFileType KeyFiles = new("Private keys (*.key, *.pem)") { Patterns = ["*.key", "*.pem"] };
    private static readonly FilePickerFileType CertificateFiles = new("Certificates (*.crt, *.cer, *.pem)") { Patterns = ["*.crt", "*.cer", "*.pem"] };
    private static readonly FilePickerFileType PemFiles = new("PEM certificate (*.crt)") { Patterns = ["*.crt"], MimeTypes = ["application/x-pem-file"] };
    private static readonly FilePickerFileType DerFiles = new("DER certificate (*.cer)") { Patterns = ["*.cer"], MimeTypes = ["application/pkix-cert"] };
    private static readonly FilePickerFileType PfxFiles = new("CA backup (*.pfx)") { Patterns = ["*.pfx"], MimeTypes = ["application/x-pkcs12"] };

    public async Task<PickedFile?> OpenAsync(string title, PkiFileKind kind)
    {
        if (topLevel()?.StorageProvider is not { } storage)
        {
            return null;
        }

        var filter = kind switch
        {
            PkiFileKind.PrivateKey => KeyFiles,
            PkiFileKind.RadiusCa => CertificateFiles,
            _ => CaFiles,
        };
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [filter, FilePickerFileTypes.All] }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        var buffer = new byte[PkiPluginInfo.MaxImportBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(true)) > 0)
        {
            length += read;
        }

        return new PickedFile(files[0].Name, buffer[..length]);
    }

    public async Task<string?> SaveAsync(string title, string fileName, PkiFileKind kind, byte[] data, string? startFolder)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (topLevel()?.StorageProvider is not { } storage)
        {
            return null;
        }

        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(startFolder))
        {
            start = await storage.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true);
        }

        var (type, extension) = kind switch
        {
            PkiFileKind.CertificateDer => (DerFiles, "cer"),
            PkiFileKind.Backup => (PfxFiles, "pfx"),
            _ => (PemFiles, "crt"),
        };
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = fileName,
            DefaultExtension = extension,
            FileTypeChoices = [type],
            SuggestedStartLocation = start,
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        if (file is null)
        {
            return null;
        }

        await using (var stream = await file.OpenWriteAsync().ConfigureAwait(true))
        {
            await stream.WriteAsync(data).ConfigureAwait(true);
        }

        var path = file.TryGetLocalPath();
        return path is null ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
    }
}

/// <summary>What the page remembers per client: the folder of the last export or backup.</summary>
public sealed class PkiClientSettings
{
    public string? LastFolder { get; set; }
}

/// <summary>
/// <c>LocalApplicationData/Oadm/plugins/oadm.pki/client.json</c>. Failures are ignored (defaults): remembering is a
/// convenience only.
/// </summary>
public sealed class PkiClientSettingsStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Oadm",
        "plugins",
        PkiPluginInfo.PluginId,
        "client.json");

    public PkiClientSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<PkiClientSettings>(File.ReadAllText(Path), PkiJson.Options) ?? new PkiClientSettings()
                : new PkiClientSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PkiClientSettings();
        }
    }

    public void Save(PkiClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, PkiJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // convenience only
        }
    }
}
