using System.Text.RegularExpressions;
using System.Xml.Linq;

using Oadm.Plugins.Ocr.Recognition;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The models are downloaded at build (plugins/Oadm.Plugins.Ocr/Models/README.md "Download at build"): Models.targets and the
/// README's table name the same files, revisions and SHA-256, and the build put every model next to the tests.
/// </summary>
public sealed partial class ModelSourcesTests
{
    private static readonly string Folder = TestRepository.Path("plugins", "Oadm.Plugins.Ocr", "Models");

    [Fact]
    public void TheReadmeAndTheBuildNameTheSameModels()
    {
        var targets = XDocument.Load(Path.Combine(Folder, "Models.targets"));
        var build = targets.Descendants("OcrModel")
            .Select(m => (File: (string)m.Attribute("Include")!, Url: (string)m.Attribute("Url")!, Sha: (string)m.Attribute("Sha256")!))
            .OrderBy(m => m.File, StringComparer.Ordinal)
            .ToList();
        var readme = File.ReadAllLines(Path.Combine(Folder, "README.md"))
            .Select(l => ReadmeRow().Match(l))
            .Where(m => m.Success)
            .Select(m => (File: m.Groups["file"].Value, Repo: m.Groups["repo"].Value, Revision: m.Groups["rev"].Value, Sha: m.Groups["sha"].Value))
            .OrderBy(m => m.File, StringComparer.Ordinal)
            .ToList();

        string[] expected = [ModelFiles.DetectionMobile, ModelFiles.DetectionServer, ModelFiles.RecognitionLatin, ModelFiles.Orientation];
        Assert.Equal(expected.Order(StringComparer.Ordinal), build.Select(m => m.File));
        Assert.Equal(build.Select(m => m.File), readme.Select(m => m.File));
        foreach (var (model, row) in build.Zip(readme))
        {
            Assert.Equal(row.Sha, model.Sha);
            Assert.Equal($"https://huggingface.co/PaddlePaddle/{row.Repo}/resolve/{row.Revision}/inference.onnx", model.Url);
        }
    }

    [Fact]
    public void TheBuildPutTheModelsNextToThePlugin()
    {
        foreach (var file in new[] { ModelFiles.DetectionMobile, ModelFiles.DetectionServer, ModelFiles.RecognitionLatin, ModelFiles.Orientation, ModelFiles.RecognitionLatinCharacters })
        {
            Assert.True(File.Exists(Path.Combine(RecognizerFixture.ModelFolder, file)), $"{file} is missing in the test output's Models folder.");
        }
    }

    // | `det_mobile.onnx` | ... | [PaddlePaddle/<repo>](...) `<revision>`, `inference.onnx` | size | `<sha>` |
    [GeneratedRegex(@"^\| `(?<file>[a-z_]+\.onnx)` \|.*\[PaddlePaddle/(?<repo>[^\]]+)\]\([^)]*\) `(?<rev>[0-9a-f]{40})`.*\| `(?<sha>[0-9a-f]{64})` \|$")]
    private static partial Regex ReadmeRow();
}
