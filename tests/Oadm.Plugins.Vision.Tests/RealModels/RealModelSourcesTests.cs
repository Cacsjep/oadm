using System.Xml.Linq;

using Oadm.Tests.Shared;

namespace Oadm.Plugins.Vision.Tests.RealModels;

/// <summary>The real model files are pinned in one place and named with their sources (no download, no network).</summary>
public sealed class RealModelSourcesTests
{
    [Fact]
    public void The_readme_names_every_real_model_file_with_its_url_and_hash_as_the_targets_pin_them()
    {
        var targets = XDocument.Load(TestRepository.Path("tests", "Oadm.Plugins.Vision.Tests", "RealModels", "RealModels.targets"));
        var files = targets.Descendants(targets.Root!.GetDefaultNamespace() + "VisionRealModelFile").ToList();
        var readme = File.ReadAllText(TestRepository.Path("plugins", "Oadm.Plugins.Vision", "README.md"));

        Assert.Equal(RealModelFiles.Names.Order(), files.Select(f => (string)f.Attribute("Include")!).Order());
        foreach (var file in files)
        {
            var row = readme.Split('\n').SingleOrDefault(l => l.StartsWith("| `" + (string)file.Attribute("Include")! + "`", StringComparison.Ordinal));
            Assert.NotNull(row);
            Assert.Contains((string)file.Attribute("Sha256")!, row, StringComparison.Ordinal);
            Assert.Contains((string)file.Attribute("Url")!, row, StringComparison.Ordinal);
            Assert.Equal((string)file.Attribute("Sha256")!, RealModelFiles.Sha256Of((string)file.Attribute("Include")!));
        }
    }
}
