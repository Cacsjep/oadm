using System.Collections.Concurrent;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The server folder checks in memory (<see cref="IServerFolderCheck"/>, SDK 0.17.0): every folder passes unless a test
/// refuses it with <see cref="Refuse"/>; <see cref="Checked"/> lists the paths asked, in order.
/// </summary>
public sealed class FakeServerFolderCheck : IServerFolderCheck
{
    private readonly ConcurrentDictionary<string, string> _refused = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _checked = new();

    /// <summary>The paths checked so far.</summary>
    public IReadOnlyList<string> Checked => [.. _checked];

    /// <summary>The check of <paramref name="path"/> answers <paramref name="problem"/> (e.g. <see cref="ServerFolderTexts.CannotWrite"/>).</summary>
    public FakeServerFolderCheck Refuse(string path, string problem)
    {
        _refused[path] = problem;
        return this;
    }

    /// <inheritdoc />
    public Task<string?> CheckAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _checked.Enqueue(path);
        return Task.FromResult(string.IsNullOrWhiteSpace(path) ? ServerFolderTexts.ChooseFolder : _refused.GetValueOrDefault(path));
    }
}
