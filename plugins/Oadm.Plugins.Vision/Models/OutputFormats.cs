using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Models;

/// <summary>One output of a model as its blank call gave it: name, shape and whether it holds floating point numbers.</summary>
internal sealed record OutputInfo(string Name, long[] Shape, bool IsFloat);

/// <summary>The format the outputs show and the class count they give (null: the outputs do not tell, the class names do).</summary>
internal sealed record RecognisedFormat(ModelFormat Format, int? Classes);

/// <summary>
/// Recognises a model's output format from the names and shapes of its outputs (docs/spec/vision-center.md "Output
/// formats"), and gives each format its defaults. The user may pick another format afterwards.
/// </summary>
internal static class OutputFormats
{
    /// <summary>The largest N of <c>[1, N, 6]</c> that is read as YOLO with suppression (end-to-end exports keep 300).</summary>
    private const long MaxSuppressedBoxes = 1000;

    /// <summary>The format of <paramref name="outputs"/>, or null when it matches none.</summary>
    public static RecognisedFormat? Recognise(IReadOnlyList<OutputInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count == 0)
        {
            return null;
        }

        return Detr(outputs) ?? RfDetr(outputs) ?? DetrWithLabels(outputs) ?? Single(outputs);
    }

    /// <summary>How the picture is fitted for <paramref name="format"/>: Letterbox (YOLO), Keep shape and pad (DETR), else Stretch.</summary>
    public static ModelFit DefaultFit(ModelFormat format) => format switch
    {
        ModelFormat.Yolo or ModelFormat.YoloWithSuppression => ModelFit.Letterbox,
        ModelFormat.Detr => ModelFit.KeepShapePad,
        _ => ModelFit.Stretch,
    };

    /// <summary>ImageNet for DETR and RF-DETR (trained that way), else 0 to 1.</summary>
    public static ModelScaling DefaultScaling(ModelFormat format) =>
        format is ModelFormat.Detr or ModelFormat.RfDetr ? ModelScaling.ImageNet : ModelScaling.ZeroToOne;

    public static ModelKind KindOf(ModelFormat format) => format == ModelFormat.Classification ? ModelKind.Classification : ModelKind.Detection;

    // pred_logits [1, Q, C + 1] and pred_boxes [1, Q, 4] by name; without the names two outputs, logits first.
    private static RecognisedFormat? Detr(IReadOnlyList<OutputInfo> outputs)
    {
        var logits = outputs.FirstOrDefault(o => o.Name.Contains("pred_logits", StringComparison.OrdinalIgnoreCase));
        var boxes = outputs.FirstOrDefault(o => o.Name.Contains("pred_boxes", StringComparison.OrdinalIgnoreCase));
        if (logits is null || boxes is null)
        {
            if (outputs.Count != 2 || IsBoxes(outputs[0]) || !IsBoxes(outputs[1]))
            {
                return null;
            }

            (logits, boxes) = (outputs[0], outputs[1]);
        }

        return IsQueries(logits) && IsBoxes(boxes) && logits.Shape[1] == boxes.Shape[1] && logits.Shape[2] >= 2
            ? new RecognisedFormat(ModelFormat.Detr, (int)Math.Min(logits.Shape[2] - 1, int.MaxValue))
            : null;
    }

    // Output 0 boxes [1, Q, 4], output 1 logits [1, Q, C]; a third output (masks) is left out.
    private static RecognisedFormat? RfDetr(IReadOnlyList<OutputInfo> outputs)
    {
        if (outputs.Count is not (2 or 3) || !IsBoxes(outputs[0]) || !IsQueries(outputs[1]) || outputs[0].Shape[1] != outputs[1].Shape[1])
        {
            return null;
        }

        return new RecognisedFormat(ModelFormat.RfDetr, (int)Math.Min(outputs[1].Shape[2], int.MaxValue));
    }

    // Boxes [1, N, 4], scores [1, N] and labels [1, N] (integers), by name or by shape.
    private static RecognisedFormat? DetrWithLabels(IReadOnlyList<OutputInfo> outputs)
    {
        if (outputs.Count != 3)
        {
            return null;
        }

        var parts = LabelsParts(outputs);
        return parts is null ? null : new RecognisedFormat(ModelFormat.DetrWithLabels, null);
    }

    /// <summary>The indexes of the boxes, scores and labels outputs of a DETR with labels, or null.</summary>
    public static (int Boxes, int Scores, int Labels)? LabelsParts(IReadOnlyList<OutputInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        int boxes = -1, scores = -1, labels = -1;
        for (var i = 0; i < outputs.Count; i++)
        {
            var o = outputs[i];
            if (IsBoxes(o) && boxes < 0)
            {
                boxes = i;
            }
            else if (o.Shape.Length == 2 && o.Shape[0] == 1)
            {
                var named = o.Name.Contains("label", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("class", StringComparison.OrdinalIgnoreCase);
                var scored = o.Name.Contains("score", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("conf", StringComparison.OrdinalIgnoreCase);
                if (labels < 0 && (named || (!scored && !o.IsFloat)))
                {
                    labels = i;
                }
                else if (scores < 0 && o.IsFloat)
                {
                    scores = i;
                }
            }
        }

        if (boxes < 0 || scores < 0 || labels < 0)
        {
            return null;
        }

        var n = outputs[boxes].Shape[1];
        return outputs[scores].Shape[1] == n && outputs[labels].Shape[1] == n ? (boxes, scores, labels) : null;
    }

    // One output: [1, 4 + C, N], [1, N, 5 + C], [1, N, 6] or [1, C].
    private static RecognisedFormat? Single(IReadOnlyList<OutputInfo> outputs)
    {
        if (outputs.Count != 1 || !outputs[0].IsFloat || outputs[0].Shape is not [1, ..] shape)
        {
            return null;
        }

        if (shape.Length == 2)
        {
            return shape[1] >= 1 ? new RecognisedFormat(ModelFormat.Classification, (int)Math.Min(shape[1], int.MaxValue)) : null;
        }

        if (shape.Length != 3)
        {
            return null;
        }

        var (a, b) = (shape[1], shape[2]);
        if (b == 6 && a <= MaxSuppressedBoxes)
        {
            return new RecognisedFormat(ModelFormat.YoloWithSuppression, null);
        }

        var layout = YoloLayoutOf(shape);
        var classes = layout == YoloLayout.ChannelsFirst ? a - 4 : b - 5;
        return classes >= 1 ? new RecognisedFormat(ModelFormat.Yolo, (int)Math.Min(classes, int.MaxValue)) : null;
    }

    /// <summary>YOLOv8 and later put the 4 + C values first (<c>[1, 4 + C, N]</c>, fewer values than boxes); YOLOv5 the boxes.</summary>
    public static YoloLayout YoloLayoutOf(ReadOnlySpan<long> shape) =>
        shape.Length == 3 && shape[1] < shape[2] ? YoloLayout.ChannelsFirst : YoloLayout.BoxesFirst;

    private static bool IsBoxes(OutputInfo o) => o.IsFloat && o.Shape is [1, > 0, 4];

    private static bool IsQueries(OutputInfo o) => o.IsFloat && o.Shape is [1, > 0, > 0];
}

/// <summary>The two layouts of the YOLO format.</summary>
internal enum YoloLayout
{
    /// <summary><c>[1, 4 + C, N]</c> (YOLOv8 and later), no objectness.</summary>
    ChannelsFirst,

    /// <summary><c>[1, N, 5 + C]</c> (YOLOv5), with objectness.</summary>
    BoxesFirst,
}
