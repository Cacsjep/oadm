using System.Globalization;

using Avalonia;
using Avalonia.Controls;

using Oadm.Plugins.DeviceClocks;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>
/// The clock check of Enable HTTPS and Enable IEEE 802.1X (docs/spec/pki.md "Clock check before installing a certificate"):
/// reads the camera clocks through the task's query, asks when a camera is behind the certificate's start, and gives the
/// task payload with the answer.
/// </summary>
public static class ClockCheck
{
    /// <summary>
    /// The payload after the question: "{}" when no camera is behind, <c>{"clockConfirmed":true}</c> after Yes, null after
    /// No. <paramref name="ask"/> shows the question (Yes / No).
    /// </summary>
    public static async Task<string?> PayloadAsync(IReadOnlyCollection<TimeSpan> behind, int selected, Func<string, Task<bool>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (CameraClock.Question(behind, selected) is not { } question)
        {
            return CameraClock.Payload(confirmed: false);
        }

        return await ask(question).ConfigureAwait(true) ? CameraClock.Payload(confirmed: true) : null;
    }

    /// <summary>
    /// Reads the clocks with a progress window over <paramref name="owner"/> (Cancel stops it), then asks; the payload, or
    /// null when the user cancelled or said No.
    /// </summary>
    public static async Task<string?> RunAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner, string title)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(devices);
        using var cancel = new CancellationTokenSource();
        var row = new ProgressRow { Text = ReadingText(0, devices.Count) };
        var footer = new DialogFooter();
        footer.CancelButton.Click += (_, _) => cancel.Cancel();
        var body = new Border { Child = row };
        body.Classes.Add("dialogBody");
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.BorderOnly,
            Content = new StackPanel { Children = { new DialogTitleBar { Text = title }, body, footer } },
        };
        var progress = new Progress<int>(done => row.Text = ReadingText(done, devices.Count));
        var read = CameraClock.ReadBehindAsync([.. devices.Select(d => d.Id)], (id, ct) => ctx.QueryAsync(id, CameraClock.QueryMethod, null, ct), progress, null, cancel.Token);
        var shown = window.ShowDialog(owner);
        IReadOnlyList<TimeSpan> behind;
        try
        {
            behind = await read.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            window.Close();
            await shown.ConfigureAwait(true);
        }

        return await PayloadAsync(behind, devices.Count, question => MessageWindow.ConfirmAsync(owner, title, question, "Yes", "No")).ConfigureAwait(true);
    }

    /// <summary>"Reading the camera clocks · 3 of 12".</summary>
    public static string ReadingText(int done, int total) =>
        total <= 1 ? "Reading the camera clock" : string.Create(CultureInfo.InvariantCulture, $"Reading the camera clocks · {done:N0} of {total:N0}");
}
