using System.Globalization;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Events;

/// <summary>
/// The events "Reader problem" of each reader and "Any reader problem" (docs/spec/ocr.md "Events and the rule action"): on
/// while a reader's chip on the page is red, off when it reads again. <see cref="ReaderRules.Starting"/> is no problem: the
/// graphics card's test of a start only delays the readers.
/// </summary>
public static class ReaderProblemEvents
{
    /// <summary>The event type of <paramref name="reader"/>: on its camera, owned by the reader's owner.</summary>
    public static EventTypeInfo TypeOf(Reader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new EventTypeInfo(
            OcrInfo.ReaderProblemTypeId(reader.Id),
            OcrInfo.ReaderProblemName,
            OcrInfo.EventGroup,
            EventKind.Stateful,
            EventScope.Device,
            [
                new EventField(OcrInfo.ReaderField, "Reader") { Filter = false },
                new EventField(OcrInfo.ProblemField, "Problem"),
            ])
        {
            Description = $"The reader {reader.Name} cannot read.\n\nValues:\n• {{{OcrInfo.ReaderField}}}\n• {{{OcrInfo.ProblemField}}}",
            DeviceIds = [reader.DeviceId],
            FixedDeviceId = reader.DeviceId,
            OwnerScopeKey = OwnerScope.Of(reader.OwnerScopeKey),
            Path = OcrInfo.TextReadPath(reader.Name),
        };
    }

    /// <summary>"Any reader problem": one state for the server. It names readers of every owner, so it is for users with all devices.</summary>
    public static EventTypeInfo AnyType { get; } = new(
        OcrInfo.AnyReaderProblemTypeId,
        OcrInfo.AnyReaderProblemName,
        OcrInfo.EventGroup,
        EventKind.Stateful,
        EventScope.Server,
        [
            new EventField(OcrInfo.CountField, "Readers") { Kind = EventValueKind.Number, Filter = false },
            new EventField(OcrInfo.ReaderField, "Reader") { Filter = false },
        ])
    {
        Description = $"At least one text reader cannot read.\n\nValues:\n• {{{OcrInfo.CountField}}}\n• {{{OcrInfo.ReaderField}}}",
        OwnerScopeKey = ICallContext.AllDevicesScopeKey,
    };

    /// <summary>The values of a reader's event.</summary>
    public static IReadOnlyDictionary<string, string> Data(Reader reader, string problem)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OcrInfo.ReaderField] = reader.Name,
            [OcrInfo.ProblemField] = problem,
        };
    }

    /// <summary>The values of "Any reader problem".</summary>
    public static IReadOnlyDictionary<string, string> AnyData(IReadOnlyCollection<string> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OcrInfo.CountField] = readers.Count.ToString(CultureInfo.InvariantCulture),
            [OcrInfo.ReaderField] = string.Join(", ", readers),
        };
    }
}

/// <summary>
/// Tells the event rules what changed in the readers' problems. Each type gets one full report first (its "ready"), then only
/// changes; a new problem text while the event is on updates its values without a new edge. Every member may run beside the
/// others.
/// </summary>
internal sealed class ReaderProblemReporter(IEventPublisher publisher)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (bool On, string Text, Guid? Device)> _reported = new(StringComparer.Ordinal);

    /// <summary>
    /// Reports <paramref name="readers"/> with their problems. A reader that is <see cref="ReaderRules.Starting"/> is not known yet:
    /// its type and "Any reader problem" wait, like a source before its first report.
    /// </summary>
    public void Report(IReadOnlyList<Reader> readers, Func<string, string?> problemOf)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(problemOf);
        lock (_sync)
        {
            var failing = new List<string>();
            var waiting = false;
            foreach (var reader in readers)
            {
                var problem = problemOf(reader.Id);
                if (problem == ReaderRules.Starting)
                {
                    waiting = true;
                    continue;
                }

                if (problem is not null)
                {
                    failing.Add(reader.Name);
                }

                var type = OcrInfo.ReaderProblemTypeId(reader.Id);
                Set(type, reader.DeviceId, reader.Name + "\u001f" + problem, problem is null ? null : ReaderProblemEvents.Data(reader, problem), problem is not null);
            }

            if (!waiting)
            {
                var data = failing.Count == 0 ? null : ReaderProblemEvents.AnyData(failing);
                Set(OcrInfo.AnyReaderProblemTypeId, null, string.Join('\u001f', failing), data, failing.Count > 0);
            }

            // The types of deleted readers are gone from the catalog.
            var current = readers.Select(r => OcrInfo.ReaderProblemTypeId(r.Id)).Append(OcrInfo.AnyReaderProblemTypeId).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _reported.Keys.Where(k => !current.Contains(k)).ToList())
            {
                _reported.Remove(gone);
            }
        }
    }

    private void Set(string typeId, Guid? deviceId, string text, IReadOnlyDictionary<string, string>? data, bool on)
    {
        var first = !_reported.TryGetValue(typeId, out var last);
        var moved = !first && last.Device != deviceId;
        if (!first && !moved && last.On == on && (!on || last.Text == text))
        {
            return;
        }

        _reported[typeId] = (on, text, deviceId);
        if (on)
        {
            publisher.SetState(typeId, deviceId, true, data);
        }

        if (first || moved || !on)
        {
            // The full report: makes the type ready, turns it off, or takes it from the reader's old camera.
            publisher.ReplaceStates(typeId, on ? [deviceId ?? Guid.Empty] : []);
        }
    }
}
