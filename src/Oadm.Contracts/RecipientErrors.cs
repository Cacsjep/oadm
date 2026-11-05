namespace Oadm.Contracts.V1;

/// <summary>What a refused EventService.SaveRecipient carries besides its message.</summary>
public static class RecipientErrors
{
    /// <summary>The trailer with the field the message belongs below: a key of the type's fields or "name".</summary>
    public const string FieldTrailer = "oadm-field";
}
