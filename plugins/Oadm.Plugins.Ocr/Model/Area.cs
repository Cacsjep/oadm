namespace Oadm.Plugins.Ocr.Model;

/// <summary>What an area's text must look like (docs/spec/ocr.md "Expected text").</summary>
public enum ExpectedTextKind
{
    /// <summary>Any text: the value is all lines.</summary>
    Any = 0,

    /// <summary>A format: the value is the first match of <see cref="ExpectedText.Format"/>.</summary>
    Format = 1,
}

/// <summary>
/// The expected text of an area: <see cref="ExpectedTextKind.Any"/> or a <see cref="ExpectedTextKind.Format"/> where
/// <c>#</c> is a digit, <c>A</c> a letter, <c>*</c> a letter or digit and every other character must appear as written
/// (spaces and dashes may be missing). Example: container number <c>AAAU ###### #</c>.
/// </summary>
public sealed record ExpectedText
{
    /// <summary>Any text (the default).</summary>
    public static ExpectedText AnyText { get; } = new();

    /// <summary>Any or Format.</summary>
    public ExpectedTextKind Kind { get; init; } = ExpectedTextKind.Any;

    /// <summary>The format while <see cref="Kind"/> is <see cref="ExpectedTextKind.Format"/>; null otherwise.</summary>
    public string? Format { get; init; }

    /// <summary>An expected text with <paramref name="format"/>.</summary>
    public static ExpectedText WithFormat(string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        return new ExpectedText { Kind = ExpectedTextKind.Format, Format = format };
    }
}

/// <summary>A rectangle drawn on the picture with a name and an expected text.</summary>
public sealed record Area
{
    /// <summary>The name in the results and the events' Area field ("Container number").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Where it is, relative to the picture.</summary>
    public RelativeBox Box { get; init; } = RelativeBox.Whole;

    /// <summary>Any text or a format.</summary>
    public ExpectedText Expected { get; init; } = ExpectedText.AnyText;
}
