using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Tests;

/// <summary>Leaving an empty required field shows its error below it (<see cref="FormField"/> + <see cref="ValidatingViewModel"/>).</summary>
public sealed partial class ValidationBlurTests
{
    public sealed partial class Form : ValidatingViewModel
    {
        public Form()
        {
            Validation.Rule(nameof(Name), () => string.IsNullOrWhiteSpace(Name) ? "Enter a name." : null);
            Validation.Rule(nameof(Other), () => string.IsNullOrWhiteSpace(Other) ? "Enter something." : null);
            Validation.Validate();
        }

        [ObservableProperty] public partial string Name { get; set; } = string.Empty;

        [ObservableProperty] public partial string Other { get; set; } = string.Empty;
    }

    [Fact]
    public async Task Leaving_an_empty_field_shows_its_error_and_only_its_own()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            var form = new Form();
            var name = new TextBox();
            name.Bind(TextBox.TextProperty, new Binding(nameof(Form.Name)));
            var other = new TextBox();
            other.Bind(TextBox.TextProperty, new Binding(nameof(Form.Other)));
            var nameField = new FormField { Label = "Name", Input = name };
            var otherField = new FormField { Label = "Other", Input = other };
            var window = new Window { DataContext = form, Content = new StackPanel { Children = { nameField, otherField } } };
            window.Show();

            name.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(form.ErrorOf(nameof(Form.Name))); // untouched: nothing yet

            other.Focus(); // leave the name
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Enter a name.", form.ErrorOf(nameof(Form.Name)));
            Assert.Null(form.ErrorOf(nameof(Form.Other))); // the field the user is in stays quiet
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }
}
