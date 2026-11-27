using Grpc.Core;

using Oadm.Contracts.V1;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Api;

/// <summary>
/// Fake ServerFolderService so <c>--fake</c> browses a server's folders: two drives of a Windows server in memory, a few
/// folders on them, New folder adds to them; the server's checks for the drives, the Windows folder and OADM's data folder.
/// </summary>
public sealed partial class FakeOadmApi
{
    private const string FakeDataFolder = @"C:\ProgramData\OADM";

    private readonly Dictionary<string, SortedSet<string>> _serverFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\"] = new(StringComparer.OrdinalIgnoreCase) { "ProgramData", "Users", "Windows" },
        [@"C:\ProgramData"] = new(StringComparer.OrdinalIgnoreCase) { "OADM" },
        [FakeDataFolder] = new(StringComparer.OrdinalIgnoreCase) { "logs", "plugin-data", "plugins" },
        [@"D:\"] = new(StringComparer.OrdinalIgnoreCase) { "Exports", "Plates", "Recordings" },
        [@"D:\Plates"] = new(StringComparer.OrdinalIgnoreCase) { "Gate North", "Gate South" },
    };

    public Task<ServerFolderList> ListServerFoldersAsync(string path, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var reply = new ServerFolderList();
            if (string.IsNullOrWhiteSpace(path))
            {
                reply.Folders.Add(new ServerFolder { Name = "C:", Path = @"C:\", IsRoot = true });
                reply.Folders.Add(new ServerFolder { Name = "Data (D:)", Path = @"D:\", IsRoot = true });
                reply.Problem = ServerFolderTexts.ChooseFolder;
                return Task.FromResult(reply);
            }

            var folder = FakeFolderPath(path);
            reply.Path = folder;
            reply.Parent = folder.Length == 3 ? string.Empty : FakeParent(folder);
            foreach (var name in FakeChildren(folder))
            {
                reply.Folders.Add(new ServerFolder { Name = name, Path = FakeCombine(folder, name) });
            }

            reply.Problem = FakeProblem(folder) ?? string.Empty;
            return Task.FromResult(reply);
        }
    }

    public Task<ServerFolder> CreateServerFolderAsync(string path, string name, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var folder = FakeFolderPath(path);
            if (ServerFolderTexts.CheckName(name) is { } problem)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, problem));
            }

            var trimmed = name.TrimStart();
            if (FakeProblem(folder) is ServerFolderTexts.SystemFolder or ServerFolderTexts.OadmFolder)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, FakeProblem(folder)!));
            }

            var children = FakeChildren(folder);
            if (!children.Add(trimmed))
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, ServerFolderTexts.NameExists));
            }

            var created = FakeCombine(folder, trimmed);
            _serverFolders[created] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new ServerFolder { Name = trimmed, Path = created });
        }
    }

    public Task<ServerFolderCheck> CheckServerFolderAsync(string path, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(new ServerFolderCheck { Problem = ServerFolderTexts.ChooseFolder });
            }

            string folder;
            try
            {
                folder = FakeFolderPath(path);
            }
            catch (RpcException ex)
            {
                return Task.FromResult(new ServerFolderCheck { Problem = ex.Status.Detail });
            }

            return Task.FromResult(new ServerFolderCheck { Path = folder, Problem = FakeProblem(folder) ?? string.Empty });
        }
    }

    /// <summary>The normalized path of a folder the fake server has; else the server's refusal.</summary>
    private string FakeFolderPath(string path)
    {
        var text = path.Trim().Replace('/', '\\');
        if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\' || text.Split('\\').Any(p => p is "." or ".."))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ServerFolderTexts.NotFullPath));
        }

        var folder = text.Length == 3 ? char.ToUpperInvariant(text[0]) + @":\" : char.ToUpperInvariant(text[0]) + text[1..].TrimEnd('\\');
        if (!FakeExists(folder))
        {
            throw new RpcException(new Status(StatusCode.NotFound, ServerFolderTexts.NotFound));
        }

        return folder;
    }

    private bool FakeExists(string folder) =>
        _serverFolders.ContainsKey(folder) || (FakeParent(folder) is { } parent && _serverFolders.TryGetValue(parent, out var names) && names.Contains(folder[(folder.LastIndexOf('\\') + 1)..]));

    private SortedSet<string> FakeChildren(string folder)
    {
        if (!_serverFolders.TryGetValue(folder, out var names))
        {
            names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            _serverFolders[folder] = names;
        }

        return names;
    }

    private static string? FakeParent(string folder)
    {
        if (folder.Length <= 3)
        {
            return null;
        }

        var cut = folder.LastIndexOf('\\');
        return cut <= 2 ? folder[..3] : folder[..cut];
    }

    private static string FakeCombine(string folder, string name) => folder.EndsWith('\\') ? folder + name : folder + "\\" + name;

    private static string? FakeProblem(string folder)
    {
        if (folder.Length == 3)
        {
            return ServerFolderTexts.WholeDrive;
        }

        if (folder.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) || string.Equals(folder, @"C:\ProgramData", StringComparison.OrdinalIgnoreCase))
        {
            return ServerFolderTexts.SystemFolder;
        }

        if (string.Equals(folder, FakeDataFolder, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(FakeDataFolder + @"\plugins", StringComparison.OrdinalIgnoreCase))
        {
            return ServerFolderTexts.OadmFolder;
        }

        return string.Equals(folder, @"D:\Exports", StringComparison.OrdinalIgnoreCase) ? ServerFolderTexts.OthersCanChange : null;
    }
}
