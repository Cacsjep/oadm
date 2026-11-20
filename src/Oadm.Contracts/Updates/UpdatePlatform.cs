using System.Runtime.InteropServices;

namespace Oadm.Contracts.Updates;

/// <summary>The operating systems OADM ships installers for.</summary>
public enum UpdateOs
{
    Windows,
    Linux,
    MacOs,
}

/// <summary>The package a Linux installation came from: the .deb (apt-get) or the .rpm (dnf).</summary>
public enum LinuxPackage
{
    Deb,
    Rpm,
}

/// <summary>Which program an installer updates.</summary>
public enum UpdateApp
{
    Server,
    Client,
}

/// <summary>
/// The platform of this program and the release assets it updates from (docs/spec/packaging.md "Installers"): the MSI
/// <c>OADM-&lt;version&gt;-win-x64.msi</c> (server and client), the packages
/// <c>oadm-server_&lt;version&gt;_&lt;arch&gt;.deb</c> and <c>oadm-client_&lt;version&gt;_&lt;arch&gt;.deb</c> (amd64, arm64),
/// on an RPM installation <c>oadm-server-&lt;version&gt;-1.&lt;arch&gt;.rpm</c> and <c>oadm-client-&lt;version&gt;-1.&lt;arch&gt;.rpm</c>
/// (x86_64, aarch64), and <c>OADM-&lt;version&gt;-osx-x64.pkg</c> (server and client).
/// </summary>
public sealed record UpdatePlatform(UpdateOs Os, Architecture Architecture)
{
    /// <summary>This process (on Linux the .rpm when its folder holds <see cref="Installation.RpmMarker"/>).</summary>
    public static UpdatePlatform Current { get; } = new(
        OperatingSystem.IsWindows() ? UpdateOs.Windows : OperatingSystem.IsMacOS() ? UpdateOs.MacOs : UpdateOs.Linux,
        RuntimeInformation.OSArchitecture)
    {
        Package = OperatingSystem.IsLinux() && File.Exists(Path.Combine(AppContext.BaseDirectory, Installation.RpmMarker)) ? LinuxPackage.Rpm : LinuxPackage.Deb,
    };

    /// <summary>Linux: the package this program was installed from, so the update takes the same kind.</summary>
    public LinuxPackage Package { get; init; } = LinuxPackage.Deb;

    /// <summary>"windows", "linux", "macos" (UpdateService.Get).</summary>
    public string Name => Os switch
    {
        UpdateOs.Windows => "windows",
        UpdateOs.MacOs => "macos",
        _ => "linux",
    };

    /// <summary>
    /// The installer of <paramref name="app"/> for <paramref name="version"/> on this platform by its exact release asset
    /// name; null where OADM ships none (Windows on ARM, macOS on Apple silicon builds are x64 only and run in Rosetta, so
    /// macOS always takes the x64 package).
    /// </summary>
    public string? InstallerName(UpdateApp app, Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var v = UpdateVersions.Plain(version);
        return Os switch
        {
            UpdateOs.Windows => Architecture == Architecture.X64 ? $"OADM-{v}-win-x64.msi" : null,
            UpdateOs.MacOs => $"OADM-{v}-osx-x64.pkg",
            _ when Package == LinuxPackage.Rpm => RpmArchitecture is { } arch ? $"{PackageName(app)}-{v}-1.{arch}.rpm" : null,
            _ => DebArchitecture is { } arch ? $"{PackageName(app)}_{v}_{arch}.deb" : null,
        };
    }

    private static string PackageName(UpdateApp app) => app == UpdateApp.Server ? "oadm-server" : "oadm-client";

    /// <summary>"x86_64" or "aarch64"; null for other processors.</summary>
    public string? RpmArchitecture => Architecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        _ => null,
    };

    /// <summary>"amd64" or "arm64"; null for other processors.</summary>
    public string? DebArchitecture => Architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };
}
