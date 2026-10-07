using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>Builds small synthetic .eap packages (gzip-compressed tar) in memory.</summary>
internal static class EapBuilder
{
    public static string Manifest(string appName = "hello", string version = "1.2.0", string? architecture = "aarch64",
        string schema = "1.7.1", string? osMin = null, string? osMax = null, string? user = null, string friendlyName = "Hello World", string vendor = "Acme")
    {
        var arch = architecture is null ? string.Empty : $"\"architecture\": \"{architecture}\",";
        var os = osMin is null && osMax is null ? string.Empty : $", \"compatibleOsVersions\": [{{\"min\": \"{osMin}\", \"max\": \"{osMax}\"}}]";
        var usr = user is null ? string.Empty : $", \"user\": {{\"username\": \"{user}\", \"group\": \"sdk\"}}";
        return $$"""
            {
              "schemaVersion": "{{schema}}",
              "acapPackageConf": {
                "setup": {
                  "appName": "{{appName}}",
                  "friendlyName": "{{friendlyName}}",
                  "vendor": "{{vendor}}",
                  {{arch}}
                  "embeddedSdkVersion": "3.0",
                  "runMode": "never",
                  "version": "{{version}}"{{os}}{{usr}}
                }
              }
            }
            """;
    }

    public static string PackageConf(string appName = "legacy", string major = "2", string minor = "1", string micro = "0", string appType = "armv7hf", string reqEmbDev = "2.0") => $"""
        PACKAGENAME="Legacy App"
        APPTYPE="{appType}"
        APPNAME="{appName}"
        APPID=""
        LICENSENAME="Available"
        LICENSEPAGE="none"
        VENDOR="Old Vendor"
        REQEMBDEVVERSION="{reqEmbDev}"
        APPMAJORVERSION="{major}"
        APPMINORVERSION="{minor}"
        APPMICROVERSION="{micro}"
        APPGRP="sdk"
        APPUSR="sdk"
        APPOPTS=""
        OTHERFILES=""
        SETTINGSPAGEFILE=""
        SETTINGSPAGETEXT=""
        VENDORHOMEPAGELINK=''
        PREUPGRADESCRIPT=""
        POSTINSTALLSCRIPT=""
        STARTMODE="never"
        HTTPCGIPATHS=""
        """;

    /// <summary>A .eap with the given files (name -> content) plus a binary payload, gzip-compressed.</summary>
    public static byte[] Build(params (string Name, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            Add(tar, "./hello", new byte[4096]);
            foreach (var (name, content) in files)
            {
                Add(tar, name, Encoding.UTF8.GetBytes(content));
            }
        }

        return output.ToArray();
    }

    public static byte[] FromManifest(string manifest) => Build(("./manifest.json", manifest));

    private static void Add(TarWriter tar, string name, byte[] data)
    {
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) };
        tar.WriteEntry(entry);
    }
}
