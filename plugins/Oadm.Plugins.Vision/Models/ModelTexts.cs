using System.Globalization;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Models;

/// <summary>The texts of the models (docs/spec/vision-center.md "Models"): one sentence each, in plain words.</summary>
public static class ModelTexts
{
    /// <summary>A model that fails the model check: "This model cannot run here: it needs data in other files."</summary>
    public static string CannotRun(string reason) => $"This model cannot run here: {reason}";

    /// <summary>The file is no ONNX model, or a damaged one.</summary>
    public const string NotOnnx = "it is not an ONNX model or the file is damaged.";

    /// <summary>A tensor with external data.</summary>
    public const string ExternalData = "it needs data in other files. Export it as one file.";

    /// <summary>An operator domain other than the standard ones.</summary>
    public static string OtherOperators(string? domain) => string.IsNullOrEmpty(domain)
        ? "it uses operators OADM does not run."
        : $"it uses operators of \"{domain}\", which OADM does not run.";

    /// <summary>ONNX Runtime could not open or run it.</summary>
    public static string DoesNotLoad(string detail) => $"it does not load ({Shorten(detail)}).";

    public const string NoPictureInput = "it does not take one picture of 3 colour channels.";

    public const string OtherInputs = "it needs inputs other than one picture.";

    public const string NotFloatInput = "its picture input is not in floating point numbers.";

    public static string InputSizeNotSupported(long width, long height) =>
        string.Create(CultureInfo.InvariantCulture, $"its input of {width} x {height} pixels is not supported.");

    public const string UnknownOutputs = "its outputs match no format Vision Center reads.";

    public static string TooManyClasses(long classes) =>
        string.Create(CultureInfo.InvariantCulture, $"it has {classes:N0} classes, at most {VisionLimits.MaxClasses:N0} are supported.");

    public static string LabelsDoNotMatch(int names, int classes) =>
        string.Create(CultureInfo.InvariantCulture, $"the labels file has {names:N0} {(names == 1 ? "name" : "names")}, the model has {classes:N0} {(classes == 1 ? "class" : "classes")}.");

    // Upload and changes.
    public static readonly string TooLarge = string.Create(CultureInfo.InvariantCulture, $"A model is at most {VisionLimits.MaxModelBytes / (1024 * 1024):N0} MB.");

    public const string Empty = "The file is empty.";

    public const string NotOnnxFile = "Choose an ONNX file (.onnx).";

    public static readonly string TooMany = string.Create(CultureInfo.InvariantCulture, $"OADM keeps at most {VisionLimits.MaxModels} models. Delete one first.");

    public static readonly string TooMuchTogether = string.Create(CultureInfo.InvariantCulture, $"The models take at most {VisionLimits.MaxAllModelsBytes / (1024 * 1024 * 1024)} GB together. Delete one first.");

    public static readonly string TooManyLabels = string.Create(CultureInfo.InvariantCulture, $"A labels file has at most {VisionLimits.MaxClasses:N0} names.");

    public const string UploadGone = "The upload ended. Upload the model again.";

    public const string UploadOutOfOrder = "A part of the upload got lost. Upload the model again.";

    public const string UploadTooLong = "The file is larger than its upload said. Upload the model again.";

    public const string UploadTooShort = "The file is smaller than its upload said. Upload the model again.";

    public static string AlreadyStored(string name) => $"This model is already stored as {name}.";

    public const string Gone = "The model no longer exists.";

    /// <summary>A model run with a format its outputs do not have (the user picked another one).</summary>
    public const string OutputsDoNotFit = "The model's outputs do not fit its format. Choose another format.";

    public const string NameMissing = "Enter a name.";

    public static readonly string NameTooLong = string.Create(CultureInfo.InvariantCulture, $"A name has at most {VisionLimits.MaxNameLength} characters.");

    public static string NameTaken(string name) => $"A model named {name} already exists.";

    public const string InputSizeFixed = "This model's input size cannot be changed.";

    public static readonly string InputSizeRule = string.Create(CultureInfo.InvariantCulture, $"The input size is a multiple of 32 from {ModelLimits.MinInputSide} to {ModelLimits.MaxInputSide}.");

    public static readonly string ClassNameTooLong = string.Create(CultureInfo.InvariantCulture, $"A class name has at most {VisionLimits.MaxNameLength} characters.");

    public const string FormatOfOtherKind = "Choose a format of the same kind as the model.";

    /// <summary>Delete of a model pipelines use: "Used by 2 pipelines: Gate 1, Lane 2."</summary>
    public static string UsedBy(IReadOnlyList<string> pipelines)
    {
        ArgumentNullException.ThrowIfNull(pipelines);
        return string.Create(CultureInfo.InvariantCulture, $"Used by {pipelines.Count} {(pipelines.Count == 1 ? "pipeline" : "pipelines")}: {string.Join(", ", pipelines)}.");
    }

    /// <summary>A pipeline whose model is Missing: "Upload the model Gate people again."</summary>
    public static string UploadAgain(string name) => $"Upload the model {name} again.";

    /// <summary>The default name of class <paramref name="id"/>.</summary>
    public static string ClassName(int id) => string.Create(CultureInfo.InvariantCulture, $"Class {id}");

    private static string Shorten(string detail)
    {
        var line = (detail ?? string.Empty).ReplaceLineEndings(" ").Trim().TrimEnd('.');
        return line.Length <= 200 ? line : line[..200] + " ...";
    }
}

/// <summary>Limits of the models that are not in <see cref="VisionLimits"/>: the input sizes Vision Center feeds.</summary>
public static class ModelLimits
{
    public const int MinInputSide = 32;

    public const int MaxInputSide = 2048;

    /// <summary>A fixed input of a model may be at most this large on a side (larger ones are refused at the check).</summary>
    public const int MaxFixedInputSide = 4096;

    /// <summary>The smallest fixed input side.</summary>
    public const int MinFixedInputSide = 8;
}
