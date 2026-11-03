using Microsoft.Extensions.Logging;

using Oadm.Plugins.Ocr.Frames;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The parts the readers work with: the recognition, the matcher, the camera pictures and the cut-outs. The plugin makes them
/// once per start from its context (<see cref="Real"/>; tests hand fakes); a missing recognizer or frame source makes every
/// reading fail with <see cref="ReaderRules.NotInstalled"/>. The vote of watching readers is always <see cref="FrameVote"/>
/// (<see cref="AreaWatch"/>).
/// </summary>
public sealed class ReaderParts
{
    /// <summary>The pictures of the cameras, through the device route.</summary>
    public Func<ICorePluginContext, IFrameSource>? Frames { get; init; }

    /// <summary>The recognition (disposed on stop when it is disposable); null from the factory: not installed.</summary>
    public Func<ICorePluginContext, ITextRecognizer?>? Recognizer { get; init; }

    /// <summary>The matcher of the expected texts (also <see cref="IAreaValueMatcher"/> when it can).</summary>
    public ITextMatcher? Matcher { get; init; }

    /// <summary>Cuts the found values out of the frames for the page; null: the page cuts by <c>MatchBox</c>.</summary>
    public IPictureCutter? Cutter { get; init; }

    /// <summary>The clock of the readings' times and of the frame source.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// The parts of the installed plugin: the cameras' pictures through VAPIX (<see cref="VapixFrameSource"/> with the
    /// plugin's full device access, the readers work without a user's call), the ONNX recognition with the models of the
    /// plugin folder (none when the folder has no <c>Models</c>: not installed completely), <see cref="TextMatcher"/> and
    /// the SkiaSharp cutter.
    /// </summary>
    public static ReaderParts Real(TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        return new ReaderParts
        {
            Frames = ctx => new VapixFrameSource(ctx.System.Vapix, clock, ctx.Logger),
            Recognizer = ctx => ctx.PluginDirectory is { } folder && Directory.Exists(ModelFiles.Folder(folder))
                ? new OnnxTextRecognizer(new OcrEngineOptions { ModelDirectory = ModelFiles.Folder(folder) }, ctx.Logger)
                : LogNotInstalled(ctx.Logger),
            Matcher = new TextMatcher(),
            Cutter = new PictureCutter(),
            Time = clock,
        };
    }

    private static ITextRecognizer? LogNotInstalled(ILogger logger)
    {
        ReaderLog.ModelsMissing(logger);
        return null;
    }
}

/// <summary>The readers' log lines that are not about one reader.</summary>
internal static partial class ReaderLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: the plugin folder has no Models folder; nothing can be read until the add-on is installed again")]
    public static partial void ModelsMissing(ILogger logger);
}
