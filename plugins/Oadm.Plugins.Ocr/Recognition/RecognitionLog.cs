using Microsoft.Extensions.Logging;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>The log lines of recognition: what runs it, and why not the graphics card (one line, never an error).</summary>
internal static partial class RecognitionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Text recognition runs on {Device}")]
    public static partial void RunsOn(ILogger logger, string device);

    [LoggerMessage(Level = LogLevel.Information, Message = "Text recognition runs on the CPU: no graphics card provider for this platform")]
    public static partial void NoGraphicsCardProvider(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition runs on the CPU: the graphics card could not be used ({Reason})")]
    public static partial void GraphicsCardFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Text recognition keeps at most {Max} model sessions on the graphics card: the least recently used input size is freed and made again when needed")]
    public static partial void GraphicsCardSessionsCapped(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition stopped while a reading was still running: the graphics card's models are left to the process")]
    public static partial void StillReading(ILogger logger);
}
