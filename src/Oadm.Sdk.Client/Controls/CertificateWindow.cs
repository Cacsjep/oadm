using System.Globalization;
using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Certificates;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// What <see cref="CertificateWindow"/> shows of one certificate (texts only, no Avalonia; SDK 0.4.0): validity with a
/// status chip (or the revocation), issued to and issued by, key and signature, version, serial number, SHA-256
/// fingerprint, uses and the names in it. The certificate comes from the server as a <see cref="CertificateDescription"/>.
/// </summary>
public sealed class CertificateDetails
{
    /// <param name="certificate">The certificate (<see cref="CertificateDescriber.Describe"/> on the server).</param>
    /// <param name="title">The window title ("Certificate · 10.0.0.48 · https").</param>
    /// <param name="nowUtc">Now, for the validity.</param>
    /// <param name="revocation">The revocation when the certificate is revoked: the chip says so instead of the validity.</param>
    /// <param name="warningDays">Days before the end that count as ending soon (the device grid's 30).</param>
    public CertificateDetails(CertificateDescription certificate, string title, DateTime nowUtc, CertificateRevocation? revocation = null, int warningDays = 30)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        Certificate = certificate;
        Title = title ?? string.Empty;
        var days = (int)Math.Floor((certificate.NotAfterUtc - nowUtc).TotalDays);
        IsExpired = certificate.NotAfterUtc <= nowUtc;
        IsNotYetValid = certificate.NotBeforeUtc > nowUtc;
        IsRevoked = revocation is not null;
        ValidityText = revocation is not null ? revocation.Describe()
            : IsExpired
            ? string.Create(CultureInfo.InvariantCulture, $"Expired {(int)Math.Floor((nowUtc - certificate.NotAfterUtc).TotalDays) switch { 0 => "today", 1 => "1 day ago", var d => $"{d:N0} days ago" }}")
            : IsNotYetValid ? "Not valid yet"
            : days switch
            {
                0 => "Valid, ends today",
                1 => "Valid, 1 day left",
                _ => string.Create(CultureInfo.InvariantCulture, $"Valid, {days:N0} days left"),
            };
        IsEndingSoon = !IsRevoked && !IsExpired && !IsNotYetValid && days <= warningDays;
        IsValid = !IsRevoked && !IsExpired && !IsNotYetValid && !IsEndingSoon;
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>The window closes (Close, Escape).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Close.</summary>
    public ICommand CloseCommand { get; }

    /// <summary>The certificate as the server described it.</summary>
    public CertificateDescription Certificate { get; }

    /// <summary>"Certificate · 10.0.0.48 · https".</summary>
    public string Title { get; }

    /// <summary>"Valid, 365 days left", "Expired 3 days ago", "Certificate revoked on 2026-11-02 (Key compromised)".</summary>
    public string ValidityText { get; }

    /// <summary>Valid with more than the warning days left.</summary>
    public bool IsValid { get; }

    /// <summary>Valid, ends within the warning days.</summary>
    public bool IsEndingSoon { get; }

    /// <summary>Its validity ended.</summary>
    public bool IsExpired { get; }

    /// <summary>Its validity has not started.</summary>
    public bool IsNotYetValid { get; }

    /// <summary>On the revocation list.</summary>
    public bool IsRevoked { get; }

    /// <summary>Shown as an error: revoked, expired or not valid yet.</summary>
    public bool IsProblem => IsRevoked || IsExpired || IsNotYetValid;

    /// <summary>Start of validity, local time.</summary>
    public string ValidFrom => Local(Certificate.NotBeforeUtc);

    /// <summary>End of validity, local time.</summary>
    public string ValidUntil => Local(Certificate.NotAfterUtc);

    /// <summary>The subject's parts.</summary>
    public IReadOnlyList<CertificateNamePart> IssuedTo => Certificate.Subject;

    /// <summary>The issuer's parts.</summary>
    public IReadOnlyList<CertificateNamePart> IssuedBy => Certificate.Issuer;

    /// <summary>"RSA 2048".</summary>
    public string Key => Certificate.Key;

    /// <summary>"SHA-256 with RSA".</summary>
    public string Signature => Certificate.Signature;

    /// <summary>"3".</summary>
    public string Version => Certificate.Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>"7D FD 05 ...".</summary>
    public string SerialNumber => Certificate.SerialNumber;

    /// <summary>SHA-256 in pairs.</summary>
    public string Fingerprint => Certificate.Fingerprint;

    /// <summary>"Digital signature, Server authentication"; "None listed" without key usages.</summary>
    public string Uses => Certificate.Uses.Count == 0 ? "None listed" : string.Join(", ", Certificate.Uses);

    /// <summary>One name per line ("IP 10.0.0.48").</summary>
    public string Names => Certificate.Names.Count == 0 ? "None" : string.Join(Environment.NewLine, Certificate.Names);

    private static string Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>
/// OADM's own certificate window (SDK 0.4.0; EST enrollments, PKI's list of issued certificates, the 802.1X log): title
/// bar, one card with its sections (validity, issued to and by, key and signature, uses, names), footer with Close. Values
/// can be selected and copied. Open it with <see cref="ShowAsync"/>; the content is a <see cref="CertificateDetails"/>.
/// </summary>
public sealed class CertificateWindow : Window
{
    public CertificateWindow()
    {
        Width = 820;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 860;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;
        DataContextChanged += (_, _) => Build();
    }

    /// <summary>Shows <paramref name="certificate"/> over <paramref name="owner"/>; closes on Close or Escape.</summary>
    public static Task ShowAsync(CertificateDetails certificate, Window owner)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var window = new CertificateWindow { DataContext = certificate };
        certificate.CloseRequested += (_, _) => window.Close();
        return window.ShowDialog(owner);
    }

    private void Build()
    {
        if (DataContext is not CertificateDetails details)
        {
            Content = null;
            return;
        }

        Title = details.Title;
        var chip = new StatusChip
        {
            Text = details.ValidityText,
            IsOk = details.IsValid,
            IsWarning = details.IsEndingSoon,
            IsError = details.IsProblem,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var validity = Section("Icon.clock", "Validity", chip, Form(Field("Valid from", Value(details.ValidFrom)), Field("Valid until", Value(details.ValidUntil))));
        var names = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
        var issuedTo = Section("Icon.device.camera", "Issued to", Parts(details.IssuedTo));
        var issuedBy = Section("Icon.shield", "Issued by", Parts(details.IssuedBy));
        Grid.SetColumn(issuedBy, 2);
        names.Children.Add(issuedTo);
        names.Children.Add(issuedBy);
        var key = Section("Icon.key", "Key and signature", Form(
            Field("Key", Value(details.Key)),
            Field("Signature", Value(details.Signature)),
            Field("Version", Value(details.Version)),
            Field("Serial number", Value(details.SerialNumber, mono: true)),
            Field("SHA-256 fingerprint", Value(details.Fingerprint, mono: true))));
        var uses = Section("Icon.check", "Uses", Value(details.Uses));
        var alternative = Section("Icon.globe", "Names in the certificate", Value(details.Names));

        var card = new Border { Child = new StackPanel { Spacing = 20, Children = { validity, names, key, uses, alternative } } };
        card.Classes.Add("card");
        var footer = new DialogFooter { CancelText = "Close", CancelCommand = details.CloseCommand };
        var title = new DialogTitleBar { Text = details.Title };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var scroll = new ScrollViewer { Margin = new Thickness(16, 0), Content = card };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(title);
        root.Children.Add(scroll);
        root.Children.Add(footer);
        Content = root;
    }

    private static StackPanel Section(string icon, string heading, params Control[] content)
    {
        var label = new IconLabel { Text = heading };
        label.Classes.Add("sectionTitle");
        label.Bind(IconLabel.IconProperty, label.GetResourceObservable(icon));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(label);
        panel.Children.AddRange(content);
        return panel;
    }

    private static StackPanel Form(params Control[] fields)
    {
        var form = new StackPanel();
        form.Classes.Add("form");
        form.Children.AddRange(fields);
        return form;
    }

    private static FormField Field(string label, Control input) => new() { Label = label, Input = input };

    private static SelectableTextBlock Value(string text, bool mono = false)
    {
        var value = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (mono)
        {
            value.Classes.Add("mono");
        }

        return value;
    }

    /// <summary>The parts of a name: a few rows, so a plain panel (no list control needed).</summary>
    private static StackPanel Parts(IReadOnlyList<CertificateNamePart> parts) =>
        Form([.. parts.Select(p => (Control)Field(p.Name, Value(p.Value)))]);
}
