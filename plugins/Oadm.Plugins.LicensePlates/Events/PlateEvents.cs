using System.Globalization;

using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates.Events;

/// <summary>
/// The events of the plugin for the rules (docs/spec/license-plate-center.md "Events for the rules"): Plate allowed, Plate
/// not allowed, Unknown plate and Plate read (stateless, on every camera that takes part, with the read's fields), Camera
/// problem (stateful per camera) and the server event Any camera problem. Opening a gate is an ordinary rule on Plate allowed.
/// </summary>
internal static class PlateEvents
{
    public static readonly IReadOnlyList<string> Path = ["Devices", LicensePlatesInfo.EventGroup];

    private static readonly string[] Results = [LicensePlateTexts.Allowed, LicensePlateTexts.NotAllowed, LicensePlateTexts.Unknown];
    private static readonly string[] Directions = [LicensePlateTexts.In, LicensePlateTexts.Out, LicensePlateTexts.UnknownDirection];

    private static List<EventField> ReadFields(bool withResult)
    {
        var fields = new List<EventField>
        {
            new(LicensePlatesInfo.PlateField, "Plate"),
            new(LicensePlatesInfo.ReadAsField, "Read as"),
            new(LicensePlatesInfo.ListEntryField, "List entry"),
            new(LicensePlatesInfo.NameField, "Name"),
            new(LicensePlatesInfo.TagsField, "Tags"),
            new(LicensePlatesInfo.DirectionField, "Direction", EventFieldType.Choice, Directions),
            new(LicensePlatesInfo.AreaField, "Area", EventFieldType.Choice, ["1", "2"]),
            new(LicensePlatesInfo.CountryField, "Country"),
            new(LicensePlatesInfo.ConfidenceField, "Confidence", EventFieldType.Number),
            new(LicensePlatesInfo.VehicleTypeField, "Vehicle type"),
            new(LicensePlatesInfo.VehicleColourField, "Vehicle colour"),
        };
        if (withResult)
        {
            fields.Insert(0, new EventField(LicensePlatesInfo.ResultField, "Result", EventFieldType.Choice, Results));
        }

        return fields;
    }

    private static string Values(IEnumerable<EventField> fields) => "\n\nValues:\n" + string.Join("\n", fields.Select(f => "• {" + f.Name + "}"));

    /// <summary>Every type for the cameras that take part now (<paramref name="cameras"/>).</summary>
    public static IReadOnlyList<EventTypeInfo> Types(IReadOnlyList<Guid> cameras)
    {
        EventTypeInfo Read(string id, string name, string description, bool withResult)
        {
            var fields = ReadFields(withResult);
            return new EventTypeInfo(id, name, LicensePlatesInfo.EventGroup, EventKind.Stateless, EventScope.Device, fields)
            {
                Description = description + Values(fields),
                DeviceIds = cameras,
                Path = Path,
            };
        }

        var problem = new EventField[] { new(LicensePlatesInfo.ProblemField, "Problem") };
        var any = new EventField[]
        {
            new(LicensePlatesInfo.CountField, "Cameras") { Kind = EventValueKind.Number, Filter = false },
            new(LicensePlatesInfo.CameraField, "Camera") { Filter = false },
            new(LicensePlatesInfo.ProblemField, "Problem") { Filter = false },
        };
        return
        [
            Read(LicensePlatesInfo.PlateAllowedType, "Plate allowed", "A camera read a plate that is allowed there.", withResult: false),
            Read(LicensePlatesInfo.PlateNotAllowedType, "Plate not allowed", "A camera read a plate of the list that is not allowed there now.", withResult: false),
            Read(LicensePlatesInfo.UnknownPlateType, "Unknown plate", "A camera read a plate that is not in the list.", withResult: false),
            Read(LicensePlatesInfo.PlateReadType, "Plate read", "A camera read a plate.", withResult: true),
            new EventTypeInfo(LicensePlatesInfo.CameraProblemType, "Camera problem", LicensePlatesInfo.EventGroup, EventKind.Stateful, EventScope.Device, problem)
            {
                Description = "The camera lost its connection for 30 s, ALPV stopped, or its events are off." + Values(problem),
                DeviceIds = cameras,
                Path = Path,
            },
            new EventTypeInfo(LicensePlatesInfo.AnyCameraProblemType, "Any camera problem", LicensePlatesInfo.EventGroup, EventKind.Stateful, EventScope.Server, any)
            {
                Description = "At least one camera has a problem, or the log cannot store reads." + Values(any),
                OwnerScopeKey = ICallContext.AllDevicesScopeKey,
                Path = Path,
            },
        ];
    }

    /// <summary>The type of a result's own event.</summary>
    public static string TypeOf(ReadResult result) => result switch
    {
        ReadResult.Allowed => LicensePlatesInfo.PlateAllowedType,
        ReadResult.NotAllowed => LicensePlatesInfo.PlateNotAllowedType,
        _ => LicensePlatesInfo.UnknownPlateType,
    };

    /// <summary>The values of a read's events (Result only in Plate read).</summary>
    public static Dictionary<string, string> Data(PlateRead read, bool withResult)
    {
        ArgumentNullException.ThrowIfNull(read);
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LicensePlatesInfo.PlateField] = read.Plate,
            [LicensePlatesInfo.ReadAsField] = read.ReadAs ?? string.Empty,
            [LicensePlatesInfo.ListEntryField] = read.ListEntry ?? string.Empty,
            [LicensePlatesInfo.NameField] = read.Name,
            [LicensePlatesInfo.TagsField] = string.Join(", ", read.Tags),
            [LicensePlatesInfo.DirectionField] = LicensePlateTexts.Direction(read.Direction),
            [LicensePlatesInfo.AreaField] = read.Area?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            [LicensePlatesInfo.CountryField] = read.Country ?? string.Empty,
            [LicensePlatesInfo.ConfidenceField] = read.Confidence.ToString(CultureInfo.InvariantCulture),
            [LicensePlatesInfo.VehicleTypeField] = read.VehicleType ?? string.Empty,
            [LicensePlatesInfo.VehicleColourField] = read.VehicleColor ?? string.Empty,
        };
        if (withResult)
        {
            data[LicensePlatesInfo.ResultField] = LicensePlateTexts.Result(read.Result);
        }

        return data;
    }

    /// <summary>Fires the read's own event and Plate read.</summary>
    public static void Fire(IEventPublisher publisher, PlateRead read)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(read);
        publisher.Fire(TypeOf(read.Result), read.CameraId, Data(read, withResult: false));
        publisher.Fire(LicensePlatesInfo.PlateReadType, read.CameraId, Data(read, withResult: true));
    }
}

/// <summary>
/// Tells the rules of the cameras' problems: one full report per type first (its "ready"), then only changes; a new problem
/// text while the event is on updates its values without a new edge.
/// </summary>
internal sealed class ProblemReporter(IEventPublisher publisher)
{
    private readonly Lock _sync = new();
    private Dictionary<Guid, string>? _cameras;
    private string? _any;

    /// <summary>Reports the cameras with a problem (id and text, name for the server event) and a problem of the log.</summary>
    public void Report(IReadOnlyDictionary<Guid, (string Name, string Problem)> problems, string? logProblem)
    {
        ArgumentNullException.ThrowIfNull(problems);
        lock (_sync)
        {
            var first = _cameras is null;
            var before = _cameras ?? [];
            var now = problems.ToDictionary(p => p.Key, p => p.Value.Problem);
            foreach (var (id, text) in now)
            {
                if (!before.TryGetValue(id, out var old) || old != text)
                {
                    publisher.SetState(LicensePlatesInfo.CameraProblemType, id, true, new Dictionary<string, string>(StringComparer.Ordinal) { [LicensePlatesInfo.ProblemField] = text });
                }
            }

            foreach (var id in before.Keys.Where(id => !now.ContainsKey(id)))
            {
                publisher.SetState(LicensePlatesInfo.CameraProblemType, id, false);
            }

            if (first)
            {
                publisher.ReplaceStates(LicensePlatesInfo.CameraProblemType, [.. now.Keys]);
            }

            _cameras = now;

            var names = problems.Values.Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            var count = names.Count;
            var anyText = names.Count == 0 && logProblem is null ? null : string.Join(", ", names) + "\u001f" + logProblem;
            if (anyText != _any || first)
            {
                _any = anyText;
                if (anyText is null)
                {
                    publisher.ReplaceStates(LicensePlatesInfo.AnyCameraProblemType, []);
                }
                else
                {
                    var problem = logProblem ?? (problems.Count == 1 ? problems.Values.First().Problem : "Several cameras have a problem.");
                    publisher.SetState(LicensePlatesInfo.AnyCameraProblemType, null, true, new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [LicensePlatesInfo.CountField] = count.ToString(CultureInfo.InvariantCulture),
                        [LicensePlatesInfo.CameraField] = string.Join(", ", names),
                        [LicensePlatesInfo.ProblemField] = problem,
                    });
                    publisher.ReplaceStates(LicensePlatesInfo.AnyCameraProblemType, [Guid.Empty]);
                }
            }
        }
    }
}
