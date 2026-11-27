using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Alpv;

/// <summary>
/// ALPV's configuration (<c>GET /local/fflprapp/config.cgi</c>) as OADM keeps it: the values of its leaf elements by path
/// ("plate.threshold", "camera.recommended.maxgain"), read on the camera (ALPV 3.0.13 and 3.1.7 differ, so OADM uses only the
/// fields a camera returns). The logins and passwords of the answer (<see cref="Secrets"/>) are dropped while it is parsed:
/// never kept, logged, sent to the client or written back (rule 9).
/// </summary>
public sealed class AlpvConfig
{
    /// <summary>The elements that hold logins and passwords; removed from the answer before anything else reads it.</summary>
    public static readonly IReadOnlySet<string> Secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "login", "password", "ipc_login", "ipc_password" };

    // The recognition fields of the Set up dialog and where ALPV keeps them (3.1.7 has the number of characters twice).
    // Region is lprUseCase (the region code of ALPV's page); regional_settings is the unit system ("metric") and never used.
    internal const string Region = "lprUseCase";
    internal const string CameraLocation = "CountryPriority";
    internal const string State = "StatePriority";
    internal const string PlateWidthMin = "plate.plate_min_size";
    internal const string PlateWidthMax = "plate.plate_max_size";
    internal const string Threshold = "plate.threshold";
    internal static readonly string[] CharactersMin = ["plate_min_size_symb", "plate.plate_min_size_symb"];
    internal static readonly string[] CharactersMax = ["plate_max_size_symb", "plate.plate_max_size_symb"];

    internal const string VideoChannel = "camera.videoChannelId";
    internal const string FrameRotate = "frame_rotate";
    internal const string ParamsOptimal = "camera.isparamsoptimal";

    /// <summary>The image settings ALPV compares, its key and the name the column shows.</summary>
    internal static readonly (string Key, string Name)[] ImageKeys =
    [
        ("wdr", "WDR"), ("contrast", "Contrast"), ("localcontrast", "Local contrast"), ("tonemapping", "Tone mapping"), ("maxgain", "Max gain"),
        ("maxexposuretime", "Shutter speed"),
    ];

    private readonly Dictionary<string, string> _values;

    private AlpvConfig(Dictionary<string, string> values) => _values = values;

    /// <summary>Every leaf value by path, without the secrets.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Parses the answer of <c>config.cgi</c>; the secrets are gone before the values are read.</summary>
    public static AlpvConfig Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XElement root;
        try
        {
            root = DeviceXml.ParseElement(xml.Trim());
        }
        catch (XmlException ex)
        {
            throw new AlpvException("The configuration of ALPV could not be read.", ex);
        }

        DropSecrets(root);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in root.Elements())
        {
            Collect(child, string.Empty, values);
        }

        return new AlpvConfig(values);
    }

    /// <summary>Removes every login and password element from <paramref name="root"/>.</summary>
    internal static void DropSecrets(XElement root)
    {
        foreach (var secret in root.DescendantsAndSelf().Where(e => Secrets.Contains(e.Name.LocalName)).ToList())
        {
            secret.Remove();
        }
    }

    private static void Collect(XElement element, string prefix, Dictionary<string, string> values)
    {
        var path = prefix.Length == 0 ? element.Name.LocalName : prefix + "." + element.Name.LocalName;
        if (!element.HasElements)
        {
            values.TryAdd(path, element.Value.Trim());
            return;
        }

        foreach (var child in element.Elements())
        {
            Collect(child, path, values);
        }
    }

    public string? Get(string path) => _values.GetValueOrDefault(path);

    public bool Has(string path) => _values.ContainsKey(path);

    /// <summary>The paths of <paramref name="candidates"/> the camera has.</summary>
    public IReadOnlyList<string> Present(IEnumerable<string> candidates) => [.. candidates.Where(_values.ContainsKey)];

    /// <summary>The video channel ALPV reads (1 = the first sensor); null when the camera does not say.</summary>
    public int? Channel => Whole(VideoChannel);

    /// <summary>ALPV reads the first sensor (or does not say which, as before 3.1).</summary>
    public bool ReadsFirstSensor => Channel is null or 1;

    /// <summary>The picture is rotated in ALPV.</summary>
    public bool Rotated => Whole(FrameRotate) is { } r && r != 0;

    /// <summary>The recognition fields the camera has, with their values.</summary>
    public RecognitionSettings Recognition => new()
    {
        Region = Get(Region),
        CameraLocation = Get(CameraLocation),
        State = Get(State),
        PlateWidthMin = Whole(PlateWidthMin),
        PlateWidthMax = Whole(PlateWidthMax),
        CharactersMin = CharactersMin.Select(Whole).FirstOrDefault(v => v is not null),
        CharactersMax = CharactersMax.Select(Whole).FirstOrDefault(v => v is not null),
        ConfidenceThreshold = Number(Threshold),
    };

    /// <summary>The image settings: ALPV's own <c>camera</c> and <c>camera.recommended</c> values and <c>isparamsoptimal</c>; null when the camera has none.</summary>
    public ImageSettingsInfo? Image
    {
        get
        {
            var values = ImageKeys
                .Where(k => Has("camera." + k.Key) || Has("camera.recommended." + k.Key))
                .Select(k => new ImageValue(k.Name, Get("camera." + k.Key), Get("camera.recommended." + k.Key)))
                .ToList();
            if (values.Count == 0 && !Has(ParamsOptimal))
            {
                return null;
            }

            return new ImageSettingsInfo(Whole(ParamsOptimal) == 1 || (values.Count > 0 && values.TrueForAll(v => v.Matches)), values);
        }
    }

    public int? Whole(string path) => int.TryParse(Get(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public double? Number(string path) => double.TryParse(Get(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>An ALPV call failed or answered something OADM cannot read.</summary>
public sealed class AlpvException : Exception
{
    public AlpvException()
    {
    }

    public AlpvException(string message)
        : base(message)
    {
    }

    public AlpvException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
