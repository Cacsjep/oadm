using Microsoft.Extensions.Logging;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>The model files in the plugin's <c>Models</c> folder (Models/README.md: sources, versions, SHA-256, licenses).</summary>
public static class ModelFiles
{
    /// <summary>PP-OCRv5 mobile text detection.</summary>
    public const string DetectionMobile = "det_mobile.onnx";

    /// <summary>PP-OCRv5 server text detection.</summary>
    public const string DetectionServer = "det_server.onnx";

    /// <summary>PP-OCRv5 Latin mobile recognition.</summary>
    public const string RecognitionLatin = "rec_latin_mobile.onnx";

    /// <summary>The character list of <see cref="RecognitionLatin"/>.</summary>
    public const string RecognitionLatinCharacters = "rec_latin_mobile.txt";

    /// <summary>PP-LCNet x1.0 text line orientation (0 or 180 degrees).</summary>
    public const string Orientation = "textline_ori.onnx";

    /// <summary>The <c>Models</c> folder next to <paramref name="pluginDirectory"/>'s assemblies.</summary>
    public static string Folder(string pluginDirectory) => Path.Combine(pluginDirectory, "Models");
}

/// <summary>Text recognition's graphics card provider (<see cref="SessionFactory.GraphicsCardTarget"/> with its log line).</summary>
internal static class GraphicsCards
{
    /// <summary>
    /// The provider to try, or null with one log line. No session here: the test of each model builds its own, so a card that
    /// cannot run fails there, and the start spends no time on a session nothing uses.
    /// </summary>
    public static ExecutionTarget? Find(string testModel, ILogger logger)
    {
        var target = SessionFactory.GraphicsCardTarget();
        if (target is null)
        {
            RecognitionLog.NoGraphicsCardProvider(logger);
        }

        return target;
    }
}
