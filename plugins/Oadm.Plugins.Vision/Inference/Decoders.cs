using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>The outputs of one model call, read in place (ONNX Runtime's values during the call; tests: arrays).</summary>
internal interface IModelOutputs
{
    int Count { get; }

    string Name(int index);

    long[] Shape(int index);

    bool IsFloat(int index);

    /// <summary>The values of a float output.</summary>
    ReadOnlySpan<float> Floats(int index);

    /// <summary>Value <paramref name="at"/> of any numeric output (labels come as integers).</summary>
    double ValueAt(int index, int at);
}

/// <summary>A box a model found, in input pixels (corners), before the fit is undone.</summary>
internal readonly record struct RawDetection(int ClassId, float Score, float X1, float Y1, float X2, float Y2)
{
    public float Area => Math.Max(0, X2 - X1) * Math.Max(0, Y2 - Y1);
}

/// <summary>
/// Reads the boxes of each detection format (docs/spec/vision-center.md "Output formats") in input pixels, and the class of a
/// classification. YOLO is suppressed per class (<see cref="VisionLimits.SuppressionIoU"/>), the DETR kinds are not.
/// </summary>
internal static class Decoders
{
    /// <summary>The boxes of <paramref name="format"/> with a score of at least <paramref name="minScore"/>, at most <see cref="VisionLimits.MaxObjects"/>, best first.</summary>
    public static List<RawDetection> Detect(ModelFormat format, IModelOutputs outputs, float minScore, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var found = format switch
        {
            ModelFormat.Yolo => Suppress(Yolo(outputs, minScore)),
            ModelFormat.YoloWithSuppression => YoloSuppressed(outputs, minScore),
            ModelFormat.Detr => Detr(outputs, minScore, width, height),
            ModelFormat.RfDetr => RfDetr(outputs, minScore, width, height),
            ModelFormat.DetrWithLabels => WithLabels(outputs, minScore, width, height),
            _ => throw new InvalidOperationException("Not a detection format."),
        };

        found.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (found.Count > VisionLimits.MaxObjects)
        {
            found.RemoveRange(VisionLimits.MaxObjects, found.Count - VisionLimits.MaxObjects);
        }

        return found;
    }

    /// <summary>The best class of a classification output <c>[1, C]</c>: softmax unless the values are already probabilities.</summary>
    public static (int ClassId, float Score) Classify(IModelOutputs outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var values = outputs.Floats(0);
        if (values.IsEmpty)
        {
            throw new InvalidOperationException("The model gave no classes.");
        }

        var best = 0;
        var sum = 0.0;
        var probabilities = true;
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            probabilities &= v is >= 0 and <= 1;
            sum += v;
            if (v > values[best])
            {
                best = i;
            }
        }

        if (probabilities && Math.Abs(sum - 1) <= 0.01)
        {
            return (best, values[best]);
        }

        var total = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            total += Math.Exp(values[i] - values[best]);
        }

        return (best, (float)(1 / total));
    }

    // [1, 4 + C, N] (cx, cy, w, h, scores) or [1, N, 5 + C] (cx, cy, w, h, objectness, scores); input pixels.
    private static List<RawDetection> Yolo(IModelOutputs outputs, float minScore)
    {
        var shape = outputs.Shape(0);
        var data = outputs.Floats(0);
        var found = new List<RawDetection>();
        if (shape.Length != 3)
        {
            return found;
        }

        if (OutputFormats.YoloLayoutOf(shape) == YoloLayout.ChannelsFirst)
        {
            var (values, n) = ((int)shape[1], (int)shape[2]);
            var classes = values - 4;
            for (var i = 0; i < n; i++)
            {
                var best = -1;
                var score = minScore;
                for (var c = 0; c < classes; c++)
                {
                    var s = data[((4 + c) * n) + i];
                    if (s >= score && (best < 0 || s > score))
                    {
                        (best, score) = (c, s);
                    }
                }

                if (best >= 0)
                {
                    found.Add(FromCentre(best, score, data[i], data[n + i], data[(2 * n) + i], data[(3 * n) + i]));
                }
            }
        }
        else
        {
            var (n, values) = ((int)shape[1], (int)shape[2]);
            var classes = values - 5;
            for (var i = 0; i < n; i++)
            {
                var row = data.Slice(i * values, values);
                var objectness = row[4];
                if (objectness < minScore)
                {
                    continue;
                }

                var best = -1;
                var score = minScore;
                for (var c = 0; c < classes; c++)
                {
                    var s = objectness * row[5 + c];
                    if (s >= score && (best < 0 || s > score))
                    {
                        (best, score) = (c, s);
                    }
                }

                if (best >= 0)
                {
                    found.Add(FromCentre(best, score, row[0], row[1], row[2], row[3]));
                }
            }
        }

        return found;
    }

    // [1, N, 6]: x1, y1, x2, y2, score, class; input pixels.
    private static List<RawDetection> YoloSuppressed(IModelOutputs outputs, float minScore)
    {
        var shape = outputs.Shape(0);
        var data = outputs.Floats(0);
        var found = new List<RawDetection>();
        var n = shape.Length == 3 ? (int)shape[1] : 0;
        for (var i = 0; i < n; i++)
        {
            var row = data.Slice(i * 6, 6);
            if (row[4] >= minScore && row[5] >= 0)
            {
                found.Add(new RawDetection((int)row[5], row[4], row[0], row[1], row[2], row[3]));
            }
        }

        return found;
    }

    // pred_logits [1, Q, C + 1] (softmax, the last class = no object), pred_boxes [1, Q, 4] (cx, cy, w, h, 0 to 1).
    private static List<RawDetection> Detr(IModelOutputs outputs, float minScore, int width, int height)
    {
        var (logitsAt, boxesAt) = (0, 1);
        for (var i = 0; i < outputs.Count; i++)
        {
            if (outputs.Name(i).Contains("pred_logits", StringComparison.OrdinalIgnoreCase))
            {
                logitsAt = i;
            }
            else if (outputs.Name(i).Contains("pred_boxes", StringComparison.OrdinalIgnoreCase))
            {
                boxesAt = i;
            }
        }

        var shape = outputs.Shape(logitsAt);
        var (queries, values) = ((int)shape[1], (int)shape[2]);
        var logits = outputs.Floats(logitsAt);
        var boxes = outputs.Floats(boxesAt);
        var found = new List<RawDetection>();
        for (var q = 0; q < queries; q++)
        {
            var row = logits.Slice(q * values, values);
            var max = row[0];
            for (var c = 1; c < values; c++)
            {
                max = Math.Max(max, row[c]);
            }

            var total = 0.0;
            var best = 0;
            for (var c = 0; c < values; c++)
            {
                total += Math.Exp(row[c] - max);
                if (c < values - 1 && row[c] > row[best])
                {
                    best = c;
                }
            }

            var score = (float)(Math.Exp(row[best] - max) / total);
            if (score >= minScore)
            {
                var b = boxes.Slice(q * 4, 4);
                found.Add(FromCentre(best, score, b[0] * width, b[1] * height, b[2] * width, b[3] * height));
            }
        }

        return found;
    }

    // Output 0 boxes [1, Q, 4] (cx, cy, w, h, 0 to 1), output 1 logits [1, Q, C] (a sigmoid per class).
    private static List<RawDetection> RfDetr(IModelOutputs outputs, float minScore, int width, int height)
    {
        var shape = outputs.Shape(1);
        var (queries, classes) = ((int)shape[1], (int)shape[2]);
        var boxes = outputs.Floats(0);
        var logits = outputs.Floats(1);
        var found = new List<RawDetection>();
        for (var q = 0; q < queries; q++)
        {
            var row = logits.Slice(q * classes, classes);
            var best = 0;
            for (var c = 1; c < classes; c++)
            {
                if (row[c] > row[best])
                {
                    best = c;
                }
            }

            var score = (float)(1 / (1 + Math.Exp(-row[best])));
            if (score >= minScore)
            {
                var b = boxes.Slice(q * 4, 4);
                found.Add(FromCentre(best, score, b[0] * width, b[1] * height, b[2] * width, b[3] * height));
            }
        }

        return found;
    }

    // Boxes [1, N, 4] (x1, y1, x2, y2 in input pixels, or 0 to 1), scores [1, N], labels [1, N].
    private static List<RawDetection> WithLabels(IModelOutputs outputs, float minScore, int width, int height)
    {
        var infos = new List<OutputInfo>(outputs.Count);
        for (var i = 0; i < outputs.Count; i++)
        {
            infos.Add(new OutputInfo(outputs.Name(i), outputs.Shape(i), outputs.IsFloat(i)));
        }

        var found = new List<RawDetection>();
        if (OutputFormats.LabelsParts(infos) is not { } parts)
        {
            return found;
        }

        var (boxesAt, scoresAt, labelsAt) = parts;
        var boxes = outputs.Floats(boxesAt);
        var scores = outputs.Floats(scoresAt);
        var n = (int)infos[boxesAt].Shape[1];

        // A model that was not handed the input's size gives its boxes 0 to 1.
        var relative = true;
        for (var i = 0; i < boxes.Length && relative; i++)
        {
            relative = boxes[i] <= 1.0001f;
        }

        var (sx, sy) = relative ? (width, height) : (1, 1);
        for (var i = 0; i < n; i++)
        {
            var score = scores[i];
            var label = outputs.ValueAt(labelsAt, i);
            if (score >= minScore && label >= 0)
            {
                var b = boxes.Slice(i * 4, 4);
                found.Add(new RawDetection((int)label, score, b[0] * sx, b[1] * sy, b[2] * sx, b[3] * sy));
            }
        }

        return found;
    }

    private static RawDetection FromCentre(int classId, float score, float cx, float cy, float w, float h) =>
        new(classId, score, cx - (w / 2), cy - (h / 2), cx + (w / 2), cy + (h / 2));

    /// <summary>Non-maximum suppression per class: of boxes of one class that overlap by more than <see cref="VisionLimits.SuppressionIoU"/>, the best stays.</summary>
    public static List<RawDetection> Suppress(List<RawDetection> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        boxes.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<RawDetection>();
        var byClass = new Dictionary<int, List<RawDetection>>();
        foreach (var box in boxes)
        {
            if (!byClass.TryGetValue(box.ClassId, out var same))
            {
                byClass[box.ClassId] = same = [];
            }

            var overlaps = false;
            foreach (var other in same)
            {
                if (IoU(box, other) > VisionLimits.SuppressionIoU)
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
            {
                same.Add(box);
                kept.Add(box);
                if (kept.Count >= VisionLimits.MaxObjects)
                {
                    break;
                }
            }
        }

        return kept;
    }

    public static double IoU(RawDetection a, RawDetection b)
    {
        var w = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        var h = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var inter = (double)w * h;
        return inter / (a.Area + b.Area - inter);
    }
}

/// <summary><see cref="IModelOutputs"/> over arrays (the tests, and a copy when a call is read later).</summary>
internal sealed class ArrayOutputs(IReadOnlyList<(string Name, long[] Shape, Array Values)> outputs) : IModelOutputs
{
    public int Count => outputs.Count;

    public string Name(int index) => outputs[index].Name;

    public long[] Shape(int index) => outputs[index].Shape;

    public bool IsFloat(int index) => outputs[index].Values is float[];

    public ReadOnlySpan<float> Floats(int index) => outputs[index].Values as float[] ?? throw new InvalidOperationException("Not a float output.");

    public double ValueAt(int index, int at) => outputs[index].Values switch
    {
        float[] f => f[at],
        long[] l => l[at],
        int[] i => i[at],
        double[] d => d[at],
        _ => throw new InvalidOperationException("Not a numeric output."),
    };
}
