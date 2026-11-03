using Oadm.Plugins.Ocr.Api;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// A save refused by checks that belong to fields of the page (<see cref="OcrFields"/>); the page backend answers them as
/// <see cref="SaveReaderReply.Errors"/>, each shown below its field. The message is the first error's.
/// </summary>
public sealed class ReaderFieldException : ArgumentException
{
    public ReaderFieldException(IReadOnlyList<FieldError> errors)
        : base(First(errors).Message)
    {
        Errors = errors;
    }

    public ReaderFieldException(string field, string message)
        : this([new FieldError(field, message)])
    {
    }

    public ReaderFieldException()
    {
        Errors = [];
    }

    public ReaderFieldException(string message)
        : base(message)
    {
        Errors = [];
    }

    public ReaderFieldException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    /// <summary>The refused fields with their texts.</summary>
    public IReadOnlyList<FieldError> Errors { get; }

    private static FieldError First(IReadOnlyList<FieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count > 0 ? errors[0] : throw new ArgumentException("No field error.", nameof(errors));
    }
}
