using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>Fake mode: tag definitions in memory, with the server's rules (names, uniqueness, rename / delete rewrite devices).</summary>
public sealed partial class FakeOadmApi
{
    private const int FakeMaxTagLength = 32;
    private readonly List<DeviceTag> _tagDefinitions = [];
    private readonly Broadcast<TagList> _tagEvents = new();

    /// <summary>Building A (blue), Building C (green), PTZ (amber), Outdoor (teal) on the sample devices.</summary>
    private void SeedTags()
    {
        AddTagLocked("Building A", TagColor.Blue);
        AddTagLocked("Building C", TagColor.Green);
        AddTagLocked("PTZ", TagColor.Amber);
        AddTagLocked("Outdoor", TagColor.Teal);
        string[][] tags =
        [
            ["Building A"], ["Building A"], ["Building A", "PTZ"], ["Building C"], ["Building C"], ["Building A", "Outdoor"],
            ["Building C", "Outdoor", "PTZ"], ["Building C"], [], ["Building A"], [], ["Outdoor"],
        ];
        for (int i = 0; i < _devices.Count && i < tags.Length; i++)
        {
            _devices[i].Tags.AddRange(tags[i]);
        }
    }

    public Task<IReadOnlyList<DeviceTag>> ListTagsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult<IReadOnlyList<DeviceTag>>(TagListLocked().Tags);
        }
    }

    public async IAsyncEnumerable<TagList> WatchTagsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Channel<TagList> channel;
        TagList first;
        lock (_gate)
        {
            ThrowIfOffline();
            channel = _tagEvents.Subscribe();
            first = TagListLocked();
        }

        try
        {
            yield return first;
            await foreach (TagList list in channel.Reader.ReadAllAsync(ct))
            {
                yield return list;
            }
        }
        finally
        {
            _tagEvents.Unsubscribe(channel);
        }
    }

    public Task<DeviceTag> CreateTagAsync(string name, TagColor color, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            string clean = CleanTagName(name);
            if (FindTagLocked(clean) is not null)
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, $"A tag named \"{clean}\" already exists."));
            }

            DeviceTag created = AddTagLocked(clean, color);
            PublishTagsLocked();
            return Task.FromResult(created.Clone());
        }
    }

    public Task<DeviceTag> UpdateTagAsync(string name, string? newName, TagColor color, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            DeviceTag tag = FindTagLocked(name.Trim())
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"There is no tag named \"{name.Trim()}\"."));
            if (!string.IsNullOrWhiteSpace(newName))
            {
                string clean = CleanTagName(newName);
                if (FindTagLocked(clean) is { } other && other != tag)
                {
                    throw new RpcException(new Status(StatusCode.AlreadyExists, $"A tag named \"{clean}\" already exists."));
                }

                string old = tag.Name;
                tag.Name = clean;
                RewriteTagsLocked(tags => tags.Select(t => string.Equals(t, old, StringComparison.OrdinalIgnoreCase) ? clean : t));
            }

            if (color != TagColor.Unspecified)
            {
                tag.Color = color;
            }

            PublishTagsLocked();
            return Task.FromResult(tag.Clone());
        }
    }

    public Task<int> DeleteTagAsync(string name, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            DeviceTag tag = FindTagLocked(name.Trim())
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"There is no tag named \"{name.Trim()}\"."));
            _tagDefinitions.Remove(tag);
            int changed = RewriteTagsLocked(tags => tags.Where(t => !string.Equals(t, tag.Name, StringComparison.OrdinalIgnoreCase)));
            PublishTagsLocked();
            return Task.FromResult(changed);
        }
    }

    public Task<SetDeviceTagsReply> SetDeviceTagsAsync(IReadOnlyCollection<string> deviceIds, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        lock (_gate)
        {
            ThrowIfOffline();
            var reply = new SetDeviceTagsReply();
            var names = new List<string>();
            foreach (string name in add)
            {
                string clean = CleanTagName(name);
                DeviceTag? tag = FindTagLocked(clean);
                if (tag is null)
                {
                    tag = AddTagLocked(clean, TagColor.Unspecified);
                    reply.Created.Add(tag.Clone());
                }

                names.Add(tag.Name);
            }

            var wanted = deviceIds.ToHashSet(StringComparer.Ordinal);
            foreach (Device device in _devices.Where(d => wanted.Contains(d.Id)))
            {
                List<string> next = device.Tags
                    .Where(t => !remove.Contains(t, StringComparer.OrdinalIgnoreCase) && !names.Contains(t, StringComparer.OrdinalIgnoreCase))
                    .Concat(names)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!next.SequenceEqual(device.Tags, StringComparer.Ordinal))
                {
                    device.Tags.Clear();
                    device.Tags.AddRange(next);
                    PublishUpdated(device);
                    reply.DevicesChanged++;
                }
            }

            if (reply.Created.Count > 0)
            {
                PublishTagsLocked();
            }

            return Task.FromResult(reply);
        }
    }

    private DeviceTag AddTagLocked(string name, TagColor color)
    {
        var tag = new DeviceTag
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Color = color == TagColor.Unspecified ? (TagColor)((_tagDefinitions.Count % 8) + 1) : color,
            Defined = true,
        };
        _tagDefinitions.Add(tag);
        return tag;
    }

    private DeviceTag? FindTagLocked(string name) =>
        _tagDefinitions.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private TagList TagListLocked()
    {
        var list = new TagList();
        foreach (DeviceTag tag in _tagDefinitions.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            DeviceTag copy = tag.Clone();
            copy.DeviceCount = _devices.Count(d => d.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase));
            list.Tags.Add(copy);
        }

        return list;
    }

    private void PublishTagsLocked() => _tagEvents.Publish(TagListLocked());

    private int RewriteTagsLocked(Func<IEnumerable<string>, IEnumerable<string>> rewrite)
    {
        int changed = 0;
        foreach (Device device in _devices)
        {
            List<string> next = rewrite(device.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (!next.SequenceEqual(device.Tags, StringComparer.Ordinal))
            {
                device.Tags.Clear();
                device.Tags.AddRange(next);
                PublishUpdated(device);
                changed++;
            }
        }

        return changed;
    }

    private static string CleanTagName(string? name)
    {
        string text = name?.Trim() ?? "";
        string? problem = text.Length == 0 ? "Enter a tag name."
            : text.Length > FakeMaxTagLength ? $"A tag name has at most {FakeMaxTagLength} characters."
            : text.Contains(';', StringComparison.Ordinal) ? "A tag name cannot contain \";\"."
            : null;
        return problem is null ? text : throw new RpcException(new Status(StatusCode.InvalidArgument, problem));
    }
}
