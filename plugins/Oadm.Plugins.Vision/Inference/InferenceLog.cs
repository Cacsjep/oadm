using Microsoft.Extensions.Logging;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>The log lines of the inference: what runs the models, and why not the graphics card (one line, never an error).</summary>
internal static partial class InferenceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Vision Center runs on {Device}")]
    public static partial void RunsOn(ILogger logger, string device);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision Center runs on the CPU: no graphics card provider for this platform")]
    public static partial void NoGraphicsCardProvider(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center runs on the CPU: the graphics card could not be used ({Reason})")]
    public static partial void GraphicsCardFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: the model {Model} stays on the CPU, the graphics card could not run it ({Reason})")]
    public static partial void CardModelFailed(ILogger logger, string model, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision Center: the model {Model} stays on the CPU, the graphics card took {CardMs} ms against {CpuMs} ms")]
    public static partial void CardModelSlower(ILogger logger, string model, double cardMs, double cpuMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision Center: the model {Model} runs on the graphics card ({CardMs} ms, the CPU {CpuMs} ms)")]
    public static partial void CardModelUsed(ILogger logger, string model, double cardMs, double cpuMs);
}
