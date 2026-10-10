using System.Globalization;

namespace Oadm.Plugins.ImageHealth.Client;

/// <summary>
/// The counts of the five dashboard cards above the table. Every camera with the app counts in one card, the worst state
/// wins: Errors (app not running or the camera could not be checked), Detected, Pending, Detection off, Good. Built in one
/// pass over all rows, independent of the search.
/// </summary>
public sealed record ImageHealthCounts(
    int Good, int Detected, int Pending, int Off, int NotRunning, int Failed,
    string DetectedDetail, string PendingDetail, string OffDetail)
{
    public static readonly ImageHealthCounts Empty = new(0, 0, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty);

    private static readonly string[] Names = ["Blur", "Block", "Redirect", "Under-exposure", "Unsuitability"];

    public int Errors => NotRunning + Failed;

    public int Total => Good + Detected + Pending + Off + Errors;

    public string GoodText => Number(Good);

    public string DetectedText => Number(Detected);

    public string PendingText => Number(Pending);

    public string OffText => Number(Off);

    public string ErrorsText => Number(Errors);

    public string GoodDetail => string.Create(CultureInfo.CurrentCulture, $"of {Total:N0} {(Total == 1 ? "camera" : "cameras")}");

    /// <summary>"1 not running · 2 could not be checked".</summary>
    public string ErrorsDetail => string.Join(" · ", new[]
    {
        NotRunning > 0 ? string.Create(CultureInfo.CurrentCulture, $"{NotRunning:N0} not running") : null,
        Failed > 0 ? string.Create(CultureInfo.CurrentCulture, $"{Failed:N0} could not be checked") : null,
    }.OfType<string>());

    public bool IsGoodOk => Good > 0;

    public bool IsDetectedError => Detected > 0;

    public bool IsPendingWarning => Pending > 0;

    public bool IsErrorsError => Errors > 0;

    public static ImageHealthCounts From(IEnumerable<ImageHealthRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int good = 0, detected = 0, pending = 0, off = 0, notRunning = 0, failed = 0;
        var detectedBy = new int[Names.Length];
        var pendingBy = new int[Names.Length];
        var offBy = new int[Names.Length];
        foreach (var row in rows)
        {
            switch (row.Bucket)
            {
                case HealthBucket.Good:
                    good++;
                    break;
                case HealthBucket.Detected:
                    detected++;
                    Tally(row, detectedBy, DetectionStates.Detected);
                    break;
                case HealthBucket.Pending:
                    pending++;
                    Tally(row, pendingBy, DetectionStates.Pending);
                    break;
                case HealthBucket.Off:
                    off++;
                    Tally(row, offBy, DetectionStates.Off);
                    break;
                case HealthBucket.Error when row.IsNotRunning:
                    notRunning++;
                    break;
                case HealthBucket.Error:
                    failed++;
                    break;
            }
        }

        return new ImageHealthCounts(good, detected, pending, off, notRunning, failed, Detail(detectedBy), Detail(pendingBy), Detail(offBy));
    }

    private static void Tally(ImageHealthRowViewModel row, int[] counts, string state)
    {
        var i = 0;
        foreach (var cell in row.Cells)
        {
            if (cell.State == state)
            {
                counts[i]++;
            }

            i++;
        }
    }

    /// <summary>"Blur 2 · Block 1": how many cameras each detection concerns, in column order.</summary>
    private static string Detail(int[] counts) =>
        string.Join(" · ", counts.Select((n, i) => n > 0 ? string.Create(CultureInfo.CurrentCulture, $"{Names[i]} {n:N0}") : null).OfType<string>());

    private static string Number(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
