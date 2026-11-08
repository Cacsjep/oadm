using System.Buffers;
using System.Diagnostics;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Models;

/// <summary>What the model check found out about an uploaded file, before the library names and stores it.</summary>
internal sealed record CheckedModel(
    ModelFormat Format,
    int InputWidth,
    int InputHeight,
    bool FixedInput,
    IReadOnlyList<string> ClassNames,
    double CpuMs,
    NamesSource NamesFrom = NamesSource.None);

/// <summary>Where the class names of a checked model came from.</summary>
internal enum NamesSource
{
    /// <summary>Neither the model nor a labels file named them: "Class 0", ... (or a built-in list by the class count).</summary>
    None,

    /// <summary>The model's metadata.</summary>
    Model,

    /// <summary>The labels file uploaded with it.</summary>
    Labels,
}

/// <summary>
/// The model check of an upload (docs/spec/vision-center.md "Models", rule 4): first <see cref="OnnxScan"/> on the file's bytes
/// (refused domains and external data) before ONNX Runtime ever opens it; then a session on the CPU, the picture input (one
/// <c>[1, 3, H, W]</c> tensor of floats, fixed or free), one call with a blank picture, the output format from the outputs'
/// names and shapes, the class names, and a second call timed (the first one warms the session).
/// </summary>
internal static class ModelChecker
{
    /// <summary>The model check of the file at <paramref name="path"/>; <paramref name="labels"/> an optional labels file's text.</summary>
    /// <returns>The model's facts, or the refusal ("This model cannot run here: ...").</returns>
    public static (CheckedModel? Model, string? Refusal) Check(string path, string? labels, CancellationToken ct)
    {
        if (OnnxScan.Refusal(path) is { } scan)
        {
            return (null, ModelTexts.CannotRun(scan));
        }

        ct.ThrowIfCancellationRequested();
        InferenceSession session;
        try
        {
            session = SessionFactory.Create(path, ExecutionTarget.Cpu, threads: 1);
        }
        catch (OnnxRuntimeException ex)
        {
            return (null, ModelTexts.CannotRun(ModelTexts.DoesNotLoad(ex.Message)));
        }

        using (session)
        {
            if (PictureInput.Of(session) is not { } input)
            {
                return (null, ModelTexts.CannotRun(InputProblem(session)));
            }

            ct.ThrowIfCancellationRequested();
            List<OutputInfo> outputs;
            double ms;
            try
            {
                outputs = BlankCall(session, input, out _);
                ct.ThrowIfCancellationRequested();
                BlankCall(session, input, out ms);
            }
            catch (OnnxRuntimeException ex)
            {
                return (null, ModelTexts.CannotRun(ModelTexts.DoesNotLoad(ex.Message)));
            }

            if (OutputFormats.Recognise(outputs, input.Width, input.Height) is not { } format)
            {
                return (null, ModelTexts.CannotRun(ModelTexts.UnknownOutputs));
            }

            if (format.Format == ModelFormat.Yolox && CentresInPixels(session, input))
            {
                format = format with { Format = ModelFormat.Yolo };
            }

            if (format.Classes > VisionLimits.MaxClasses)
            {
                return (null, ModelTexts.CannotRun(ModelTexts.TooManyClasses(format.Classes.Value)));
            }

            var metadata = session.ModelMetadata.CustomMetadataMap.TryGetValue(ClassNames.MetadataKey, out var names) ? names : null;
            var (classNames, refusal, source) = NamesOf(format.Classes, metadata, labels);
            if (refusal is not null)
            {
                return (null, ModelTexts.CannotRun(refusal));
            }

            return (new CheckedModel(format.Format, input.Width, input.Height, input.Fixed, classNames!, Math.Round(ms, 1), source), null);
        }
    }

    /// <summary>
    /// The class names: the metadata's when they fit the class count (or the outputs do not tell it) and are more than the ids,
    /// else the labels file's (which must hold one name per class), else "Class 0", ...
    /// </summary>
    internal static (IReadOnlyList<string>? Names, string? Refusal, NamesSource Source) NamesOf(int? classes, string? metadata, string? labels)
    {
        var fromModel = ClassNames.FromMetadata(metadata);
        if (fromModel is not null && ClassNames.OnlyIds(fromModel))
        {
            // Names that are only the ids ("0", "1", ...: onnx-community's YOLOv10) name nothing; they still give the count.
            classes ??= fromModel.Count;
            fromModel = null;
        }

        if (fromModel is not null && (classes is null || fromModel.Count == classes))
        {
            return (fromModel, null, NamesSource.Model);
        }

        var fromLabels = ClassNames.FromLabels(labels);
        if (fromLabels is null)
        {
            return (null, ModelTexts.TooManyLabels, NamesSource.None);
        }

        if (fromLabels.Count > 0)
        {
            return classes is { } count && fromLabels.Count != count
                ? (null, ModelTexts.LabelsDoNotMatch(fromLabels.Count, count), NamesSource.None)
                : (fromLabels, null, NamesSource.Labels);
        }

        return (ClassNames.Defaults(classes ?? 0), null, NamesSource.None);
    }

    private static string InputProblem(InferenceSession session)
    {
        var pictures = session.InputMetadata.Where(i => i.Value.IsTensor && i.Value.Dimensions.Length == 4).ToList();
        if (pictures.Count == 0)
        {
            return ModelTexts.NoPictureInput;
        }

        var picture = pictures[0].Value;
        if (picture.Dimensions[1] is not (3 or -1))
        {
            return ModelTexts.NoPictureInput;
        }

        if (!HalfPrecision.IsFloat(picture.ElementDataType))
        {
            return ModelTexts.NotFloatInput;
        }

        if (session.InputMetadata.Count > 1 && pictures.Count == 1)
        {
            return ModelTexts.OtherInputs;
        }

        var (h, w) = (picture.Dimensions[2], picture.Dimensions[3]);
        return ModelTexts.InputSizeNotSupported(w, h);
    }

    /// <summary>
    /// Whether a YOLOX output already holds its box centres in input pixels (exported with the decoding inside): then it is read
    /// as YOLO. Undecoded, the centres are offsets within their cell, never half the input wide; decoded, the cells of the
    /// right half give centres beyond it, also for a black picture.
    /// </summary>
    private static bool CentresInPixels(InferenceSession session, PictureInput input)
    {
        var values = new float[3 * input.Width * input.Height];
        using var inputs = new ModelInputs(session, input, values, input.Width, input.Height);
        using var options = new RunOptions();
        using var results = session.Run(options, inputs.Names, inputs.Values, [session.OutputNames[0]]);
        float[]? converted = null;
        var data = HalfPrecision.Floats(results[0], ref converted);
        var shape = results[0].GetTensorTypeAndShape().Shape;
        var columns = (int)shape[2];
        var max = 0f;
        for (var i = 0; i + columns <= data.Length; i += columns)
        {
            max = Math.Max(max, data[i]);
        }

        return max > input.Width / 2f;
    }

    /// <summary>One call with a black picture; the outputs' names and shapes and the call's time.</summary>
    private static List<OutputInfo> BlankCall(InferenceSession session, PictureInput input, out double ms)
    {
        var values = new float[3 * input.Width * input.Height];
        using var inputs = new ModelInputs(session, input, values, input.Width, input.Height);
        using var options = new RunOptions();
        var watch = Stopwatch.StartNew();
        using var results = session.Run(options, inputs.Names, inputs.Values, session.OutputNames);
        ms = watch.Elapsed.TotalMilliseconds;
        var outputs = new List<OutputInfo>(results.Count);
        for (var i = 0; i < results.Count; i++)
        {
            var info = results[i].GetTensorTypeAndShape();
            outputs.Add(new OutputInfo(session.OutputNames[i], info.Shape, HalfPrecision.IsFloat(info.ElementDataType)));
        }

        return outputs;
    }
}

/// <summary>
/// The picture input of a session: one <c>[1, 3, H, W]</c> tensor of floats, 32 or 16 bits (the batch and the size may be
/// free), and the optional size input of RT-DETR-like exports (<c>orig_target_sizes [1, 2]</c>, integers), which gets the
/// input's size so their boxes come in input pixels.
/// </summary>
internal sealed record PictureInput(string Name, int Width, int Height, bool Fixed, string? SizeName, TensorElementType SizeType, TensorElementType PictureType = TensorElementType.Float)
{
    /// <summary>The picture input of <paramref name="session"/>, or null when its inputs are no single picture.</summary>
    public static PictureInput? Of(InferenceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string? picture = null;
        var pictureType = TensorElementType.Float;
        int[] dims = [];
        string? size = null;
        var sizeType = TensorElementType.Int64;
        foreach (var (name, meta) in session.InputMetadata)
        {
            if (!meta.IsTensor)
            {
                return null;
            }

            var d = meta.Dimensions;
            if (d.Length == 4 && picture is null)
            {
                if (!HalfPrecision.IsFloat(meta.ElementDataType) || d[0] is not (1 or -1) || d[1] is not (3 or -1))
                {
                    return null;
                }

                picture = name;
                pictureType = meta.ElementDataType;
                dims = d;
            }
            else if (d.Length == 2 && d[0] is 1 or -1 && d[1] is 2 && size is null
                && meta.ElementDataType is TensorElementType.Int64 or TensorElementType.Int32 or TensorElementType.Float)
            {
                size = name;
                sizeType = meta.ElementDataType;
            }
            else
            {
                return null;
            }
        }

        if (picture is null)
        {
            return null;
        }

        var (h, w) = (dims[2], dims[3]);
        var fixedSize = h > 0 && w > 0;
        if ((h > 0 && h is < ModelLimits.MinFixedInputSide or > ModelLimits.MaxFixedInputSide)
            || (w > 0 && w is < ModelLimits.MinFixedInputSide or > ModelLimits.MaxFixedInputSide))
        {
            return null;
        }

        return new PictureInput(
            picture,
            w > 0 ? w : VisionLimits.DefaultInputSize,
            h > 0 ? h : VisionLimits.DefaultInputSize,
            fixedSize,
            size,
            sizeType,
            pictureType);
    }
}

/// <summary>The input values of one call: the picture tensor over <paramref name="values"/> (not copied) and the size input when the model has one.</summary>
internal sealed class ModelInputs : IDisposable
{
    private readonly List<OrtValue> _values = new(2);
    private Float16[]? _half;

    public ModelInputs(InferenceSession session, PictureInput input, float[] values, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);
        Names = input.SizeName is null ? [input.Name] : [input.Name, input.SizeName];
        _values.Add(input.PictureType == TensorElementType.Float16
            ? HalfInput(values.AsSpan(0, 3 * width * height), width, height)
            : OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, values.AsMemory(0, 3 * width * height), [1, 3, height, width]));
        if (input.SizeName is not null)
        {
            // RT-DETR and D-FINE scale their boxes to this size: the input's own, so they come in input pixels.
            _values.Add(input.SizeType switch
            {
                TensorElementType.Int32 => OrtValue.CreateTensorValueFromMemory(new[] { width, height }, [1, 2]),
                TensorElementType.Float => OrtValue.CreateTensorValueFromMemory(new float[] { width, height }, [1, 2]),
                _ => OrtValue.CreateTensorValueFromMemory(new long[] { width, height }, [1, 2]),
            });
        }
    }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<OrtValue> Values => _values;

    public void Dispose()
    {
        foreach (var value in _values)
        {
            value.Dispose();
        }

        if (_half is not null)
        {
            ArrayPool<Float16>.Shared.Return(_half);
            _half = null;
        }
    }

    /// <summary>The picture as 16-bit floats in a rented buffer (a model exported in half precision).</summary>
    private OrtValue HalfInput(ReadOnlySpan<float> values, int width, int height)
    {
        _half = ArrayPool<Float16>.Shared.Rent(values.Length);
        HalfPrecision.Write(values, _half);
        return OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, _half.AsMemory(0, values.Length), [1, 3, height, width]);
    }
}
