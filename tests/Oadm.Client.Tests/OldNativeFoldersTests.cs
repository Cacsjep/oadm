using Oadm.Client.Infrastructure;

namespace Oadm.Client.Tests;

public sealed class OldNativeFoldersTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static string Folder(string root, string name, TimeSpan age)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "avcodec.dll"), "x");
        Directory.SetLastWriteTimeUtc(folder, Now - age);
        return folder;
    }

    [Fact]
    public void OldFoldersGoAndAFolderOfToday()
    {
        var root = Directory.CreateTempSubdirectory("oadm-native-").FullName;
        try
        {
            var old = Folder(root, "old", TimeSpan.FromDays(3));
            var recent = Folder(root, "recent", TimeSpan.FromHours(2));

            Assert.Equal(1, OldNativeFolders.Clean([root, root], Now));

            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(recent));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void APlaceThatDoesNotExistIsNoProblem()
    {
        Assert.Equal(0, OldNativeFolders.Clean([Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], Now));
    }

    [Fact]
    public void TheSearchStaysInsideTheClientsOwnFolders()
    {
        Assert.All(OldNativeFolders.Places(), place => Assert.EndsWith(Path.Combine(".net", "Oadm.Client"), place, StringComparison.Ordinal));
    }
}
