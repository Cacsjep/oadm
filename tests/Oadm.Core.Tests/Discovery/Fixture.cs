using System.Runtime.CompilerServices;

namespace Oadm.Core.Tests.Discovery;

/// <summary>
/// Loads files from the Fixtures folder next to this source file. Reading from the source tree
/// keeps the test project file untouched (no copy-to-output items needed).
/// </summary>
internal static class Fixture
{
    public static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));

    public static string Text(string name) => File.ReadAllText(PathOf(name));

    private static string PathOf(string name, [CallerFilePath] string sourceFile = "")
        => Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", name);
}
