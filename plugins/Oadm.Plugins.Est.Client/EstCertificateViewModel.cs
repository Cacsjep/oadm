using System.Globalization;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

namespace Oadm.Plugins.Est.Client;

/// <summary>The certificate an enrollment issued, as the details window shows it (texts only, no logic in the view).</summary>
public sealed class EstCertificateViewModel
{
    public EstCertificateViewModel(CertificateDescription certificate, EnrollmentRow enrollment, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(enrollment);
        Certificate = certificate;
        Title = $"Certificate · {enrollment.Device} · {enrollment.Profile}";
        var days = (int)Math.Floor((certificate.NotAfterUtc - nowUtc).TotalDays);
        IsExpired = certificate.NotAfterUtc <= nowUtc;
        IsNotYetValid = certificate.NotBeforeUtc > nowUtc;
        ValidityText = IsExpired
            ? string.Create(CultureInfo.InvariantCulture, $"Expired {(int)Math.Floor((nowUtc - certificate.NotAfterUtc).TotalDays) switch { 0 => "today", 1 => "1 day ago", var d => $"{d:N0} days ago" }}")
            : IsNotYetValid ? "Not valid yet"
            : days switch
            {
                0 => "Valid, ends today",
                1 => "Valid, 1 day left",
                _ => string.Create(CultureInfo.InvariantCulture, $"Valid, {days:N0} days left"),
            };
        IsEndingSoon = !IsExpired && !IsNotYetValid && days <= 30;
        IsValid = !IsExpired && !IsNotYetValid && !IsEndingSoon;
    }

    /// <summary>The window closes (Close, Escape).</summary>
    public event EventHandler? CloseRequested;

    public ICommand CloseCommand => new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

    public CertificateDescription Certificate { get; }

    /// <summary>"Certificate · 10.0.0.48 · https".</summary>
    public string Title { get; }

    /// <summary>"Valid, 365 days left", "Expired 3 days ago".</summary>
    public string ValidityText { get; }

    public bool IsValid { get; }

    /// <summary>30 days or less left (the device grid's warning for certificates).</summary>
    public bool IsEndingSoon { get; }

    public bool IsExpired { get; }

    public bool IsNotYetValid { get; }

    public bool IsProblem => IsExpired || IsNotYetValid;

    public string ValidFrom => Local(Certificate.NotBeforeUtc);

    public string ValidUntil => Local(Certificate.NotAfterUtc);

    public IReadOnlyList<NameValue> IssuedTo => Certificate.Subject;

    public IReadOnlyList<NameValue> IssuedBy => Certificate.Issuer;

    public string Key => Certificate.Key;

    public string Signature => Certificate.Signature;

    public string Version => Certificate.Version.ToString(CultureInfo.InvariantCulture);

    public string SerialNumber => Certificate.SerialNumber;

    public string Fingerprint => Certificate.Fingerprint;

    /// <summary>"Digital signature, Server authentication"; "None listed" without key usages.</summary>
    public string Uses => Certificate.Uses.Count == 0 ? "None listed" : string.Join(", ", Certificate.Uses);

    /// <summary>One name per line ("IP 10.0.0.48").</summary>
    public string Names => Certificate.Names.Count == 0 ? "None" : string.Join(Environment.NewLine, Certificate.Names);

    private static string Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}
