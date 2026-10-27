using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Client.Updates;

/// <summary>
/// "OADM V4.6.0 is available": the version this client has, the release notes behind What's new, "Don't tell me about new
/// versions", Later and Open download page (the download page of the website).
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    public const string DownloadPage = "https://oadm.app/start/install/";

    public UpdateViewModel(ReleaseInfo release, string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(release);
        Title = $"OADM {release.VersionText} is available";
        Current = "You have " + (ClientVersion.Parse(currentVersion) is { } v ? ClientVersion.Format(v) : currentVersion);
        Sections = ReleaseNotes.Parse(release.Notes);
        int changes = Sections.Sum(s => s.Items.Count);
        ChangeCount = changes == 1 ? "1 change" : string.Create(CultureInfo.InvariantCulture, $"{changes} changes");
    }

    public string Title { get; }

    /// <summary>"You have V4.5.0".</summary>
    public string Current { get; }

    public IReadOnlyList<ReleaseNoteSection> Sections { get; }

    public bool HasNotes => Sections.Count > 0;

    /// <summary>"4 changes", right of What's new.</summary>
    public string ChangeCount { get; }

    [ObservableProperty]
    public partial bool IsNotesOpen { get; set; }

    /// <summary>"Don't tell me about new versions": saved when the window closes.</summary>
    [ObservableProperty]
    public partial bool DontTellAgain { get; set; }

    /// <summary>Open download page was clicked.</summary>
    public bool OpenDownloadPage { get; private set; }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenDownload()
    {
        OpenDownloadPage = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
