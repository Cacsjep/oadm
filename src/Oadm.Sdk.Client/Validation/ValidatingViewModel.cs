using System.Collections;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Sdk.Client.Validation;

/// <summary>
/// Base class of every form view model (host and plugins): an <see cref="ObservableObject"/> that reports
/// field errors through <see cref="INotifyDataErrorInfo"/> from the rules registered on <see cref="Validation"/>.
/// Setting a field property (one with a rule) marks it edited and shows its error; any property change
/// re-runs the rules (rules may depend on other properties, e.g. a mode choice). Bind the submit button to
/// <see cref="IsFormValid"/> (<c>IsEnabled</c>) and <see cref="FormError"/> (<c>ToolTip.Tip</c>, with
/// <c>ToolTip.ShowOnDisabled</c>), or use the command's CanExecute with <see cref="FormValidator.IsValid"/>
/// and override <see cref="OnValidationChanged"/> to refresh it.
/// </summary>
/// <example>
/// <code>
/// public MyViewModel()
/// {
///     Validation.Rule(nameof(UserName), () => UserName.Trim().Length == 0 ? "Enter a user name." : null);
///     Validation.Validate();
/// }
/// </code>
/// </example>
public abstract class ValidatingViewModel : ObservableObject, INotifyDataErrorInfo
{
    private bool _isFormValid = true;
    private string? _formError;

    protected ValidatingViewModel()
    {
        Validation = new FormValidator(
            property => ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property)),
            RaiseValidationChanged);
    }

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <summary>The rules and the edited / submitted state of this form.</summary>
    protected FormValidator Validation { get; }

    /// <summary>Any error is visible below its field.</summary>
    public bool HasErrors => Validation.HasErrors;

    /// <summary>No field error, shown or not: submit buttons are enabled.</summary>
    public bool IsFormValid => _isFormValid;

    /// <summary>Why the form cannot be submitted (the first field error), for the submit button's tooltip.</summary>
    public string? FormError => _formError;

    public IEnumerable GetErrors(string? propertyName) => Validation.GetErrors(propertyName);

    /// <summary>The visible error of a property (tests, code-behind free views).</summary>
    public string? ErrorOf(string propertyName) => Validation[propertyName];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(HasErrors) or nameof(IsFormValid) or nameof(FormError))
        {
            return;
        }

        Validation.Touch(e.PropertyName);
    }

    /// <summary>Called after any error changed (refresh command CanExecute here).</summary>
    protected virtual void OnValidationChanged()
    {
    }

    private void RaiseValidationChanged()
    {
        bool valid = Validation.IsValid;
        string? error = Validation.FirstError;
        bool validChanged = valid != _isFormValid;
        bool errorChanged = error != _formError;
        _isFormValid = valid;
        _formError = error;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasErrors)));
        if (validChanged)
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsFormValid)));
        }

        if (errorChanged)
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(FormError)));
        }

        OnValidationChanged();
    }
}
