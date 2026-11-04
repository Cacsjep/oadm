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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: {Model} stays on the CPU, the graphics card could not run it ({Reason})")]
    public static partial void CardModelFailed(ILogger logger, string model, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Text recognition on the graphics card: text finder {Finder}, reader {Reader}, angle model {Angle}")]
    public static partial void CardRoute(ILogger logger, bool finder, bool reader, bool angle);

    [LoggerMessage(Level = LogLevel.Information, Message = "Text recognition keeps at most {MaxMegabytes} MB of model sessions on the graphics card: the least recently used picture size is freed and made again when needed")]
    public static partial void GraphicsCardSessionsCapped(ILogger logger, long maxMegabytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Text recognition: a picture size too large for the graphics card reads on the CPU")]
    public static partial void GraphicsCardCallTooLarge(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Text recognition: the graphics card's test took {Milliseconds} ms: find the card {Find} ms, build the models {Build} ms, test calls {Calls} ms")]
    public static partial void CardStarted(ILogger logger, long milliseconds, long find, long build, long calls);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition stopped while a reading was still running: the graphics card's models are left to the process")]
    public static partial void StillReading(ILogger logger);
}
