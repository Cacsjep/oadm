namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// A choice of a select backed by one of ALPV's lists: the value OADM sends or filters by ("AUT", "5", "CAR"; null for an
/// "All" or "Any" entry) and the name shown. A value the camera has that the list does not know shows as its code.
/// </summary>
public sealed record CodeChoice(string? Value, string Name)
{
    public override string ToString() => Name;
}
