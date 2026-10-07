using Oadm.Sdk.Client;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>The server side of the Commander page (<c>ICorePlugin.InvokeAsync</c>); an interface so view models test without a server.</summary>
public interface ICommanderBackend
{
    Task<CommandListReply> ListLibraryAsync(CancellationToken ct);

    Task<CommandListReply> ListSavedAsync(CancellationToken ct);

    Task<SaveCommandReply> SaveAsync(SaveCommandRequest request, CancellationToken ct);

    Task<OkReply> DeleteAsync(string id, CancellationToken ct);

    Task<ExportReply> ExportAsync(IReadOnlyList<string> ids, CancellationToken ct);

    Task<ImportReply> ImportAsync(string json, string? owner, CancellationToken ct);

    Task<TryCommandReply> TryAsync(TryCommandRequest request, CancellationToken ct);

    Task<CompatibilityReply> CheckCompatibilityAsync(CompatibilityRequest request, CancellationToken ct);

    Task<RolloutReply> RolloutAsync(RolloutRequest request, CancellationToken ct);
}

/// <summary><see cref="ICommanderBackend"/> over <see cref="ICorePluginClientContext.InvokeAsync"/> (gRPC PluginService.Invoke).</summary>
public sealed class CommanderBackend(ICorePluginClientContext ctx) : ICommanderBackend
{
    public Task<CommandListReply> ListLibraryAsync(CancellationToken ct) => CallAsync<CommandListReply>(CommanderMethods.ListLibrary, null, ct);

    public Task<CommandListReply> ListSavedAsync(CancellationToken ct) => CallAsync<CommandListReply>(CommanderMethods.ListSaved, null, ct);

    public Task<SaveCommandReply> SaveAsync(SaveCommandRequest request, CancellationToken ct) => CallAsync<SaveCommandReply>(CommanderMethods.Save, request, ct);

    public Task<OkReply> DeleteAsync(string id, CancellationToken ct) => CallAsync<OkReply>(CommanderMethods.Delete, new DeleteCommandRequest { Id = id }, ct);

    public Task<ExportReply> ExportAsync(IReadOnlyList<string> ids, CancellationToken ct) => CallAsync<ExportReply>(CommanderMethods.Export, new ExportRequest { Ids = [.. ids] }, ct);

    public Task<ImportReply> ImportAsync(string json, string? owner, CancellationToken ct) =>
        CallAsync<ImportReply>(CommanderMethods.Import, new ImportRequest { Json = json, Owner = owner }, ct);

    public Task<TryCommandReply> TryAsync(TryCommandRequest request, CancellationToken ct) => CallAsync<TryCommandReply>(CommanderMethods.TryRequest, request, ct);

    public Task<CompatibilityReply> CheckCompatibilityAsync(CompatibilityRequest request, CancellationToken ct) =>
        CallAsync<CompatibilityReply>(CommanderMethods.CheckCompatibility, request, ct);

    public Task<RolloutReply> RolloutAsync(RolloutRequest request, CancellationToken ct) => CallAsync<RolloutReply>(CommanderMethods.Rollout, request, ct);

    private async Task<T> CallAsync<T>(string method, object? request, CancellationToken ct)
        where T : class, new()
    {
        var json = await ctx.InvokeAsync(method, request is null ? null : CommandJson.Write(request), ct).ConfigureAwait(false);
        return CommandJson.Read<T>(json);
    }
}

/// <summary>File pickers of the page (export/import), implemented by the view with the window's storage provider.</summary>
public interface ICommanderFiles
{
    /// <summary>Content of a JSON file the user picked, or null when cancelled.</summary>
    Task<string?> OpenJsonAsync();

    /// <summary>Saves <paramref name="content"/> under a name the user picks (suggested <paramref name="fileName"/>); false when cancelled.</summary>
    Task<bool> SaveJsonAsync(string fileName, string content);
}
