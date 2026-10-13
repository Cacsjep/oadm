namespace Oadm.Sdk.Client.Validation;

/// <summary>
/// A form view model that wants to know when the user leaves a field (<see cref="Controls.FormField"/> calls it on lost
/// focus with the bound property's name); <see cref="ValidatingViewModel"/> shows that field's error then.
/// </summary>
public interface IFormFieldVisits
{
    /// <summary>The user left the input bound to <paramref name="propertyName"/>: its error may show now.</summary>
    void FieldLeft(string propertyName);
}
