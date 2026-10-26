using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The Create CA dialog (decision 2026-10-26, items 2 and 14): Root CA or Intermediate CA, Signed by for an intermediate,
/// RSA 4096 preset, the years; errors directly below their fields once edited or after Create, the server's below its field.
/// </summary>
public sealed class CreateCaDialogTests
{
    private static readonly IReadOnlyList<Choice> Issuers = [new(CertificateAuthorityInfo.OadmId, "OADM Root CA SERVER01"), new("imported:ab", "Site Issuing")];

    [Fact]
    public void A_new_dialog_presets_a_root_ca_with_rsa_4096_and_shows_no_error()
    {
        var dialog = new CreateCaViewModel((_, _) => Task.FromResult(true), Issuers);

        Assert.Equal(["Root CA (self-signed)", "Intermediate CA"], CreateCaViewModel.TypeChoices.Select(c => c.Label));
        Assert.Equal(["ECC P-256", "RSA 2048", "RSA 3072", "RSA 4096"], CreateCaViewModel.KeyChoices.Select(c => c.Label));
        Assert.Equal(("root", CaKeys.Rsa4096, "RSA 4096"), (dialog.Type.Value, dialog.Key!.Value, dialog.Key.Label));
        Assert.False(dialog.IsIntermediate);
        Assert.Equal(PkiPluginInfo.DefaultValidityYears.ToString(System.Globalization.CultureInfo.InvariantCulture), dialog.ValidityYears);
        Assert.Equal(CertificateAuthorityInfo.OadmId, dialog.Issuer?.Value);
        Assert.Null(dialog.ErrorOf(nameof(dialog.CommonName))); // never on an untouched form
        Assert.False(dialog.CreateCommand.CanExecute(null)); // no name yet
    }

    [Fact]
    public async Task Errors_show_below_their_fields_and_create_sends_what_was_chosen()
    {
        CreateCaRequest? sent = null;
        var dialog = new CreateCaViewModel((_, request) =>
        {
            sent = request;
            return Task.FromResult(true);
        }, Issuers);

        dialog.CommonName = "x";
        dialog.CommonName = " ";
        Assert.Equal("Enter a name.", dialog.ErrorOf(nameof(dialog.CommonName)));
        dialog.CommonName = new string('n', PkiPluginInfo.MaxNameLength + 1);
        Assert.NotNull(dialog.ErrorOf(nameof(dialog.CommonName)));
        dialog.CommonName = " Building B CA ";
        Assert.Null(dialog.ErrorOf(nameof(dialog.CommonName)));

        dialog.ValidityYears = "0";
        Assert.Equal(PkiValidation.ValidityYears(0), dialog.ErrorOf(nameof(dialog.ValidityYears)));
        Assert.False(dialog.CreateCommand.CanExecute(null));
        dialog.ValidityYears = "abc";
        Assert.NotNull(dialog.ErrorOf(nameof(dialog.ValidityYears)));
        dialog.ValidityYears = "3";
        Assert.Null(dialog.ErrorOf(nameof(dialog.ValidityYears)));

        // An intermediate CA signed by the chosen CA, an ECC key.
        dialog.Type = CreateCaViewModel.TypeChoices[1];
        dialog.Issuer = Issuers[1];
        dialog.Key = CreateCaViewModel.KeyChoices.Single(k => k.Value == CaKeys.Ecc);
        Assert.True(dialog.IsIntermediate);
        await dialog.CreateCommand.ExecuteAsync(null);

        Assert.Equal(new CreateCaRequest("Building B CA", true, "imported:ab", CaKeys.Ecc, 3), sent);
        Assert.True(dialog.Completed);

        // A root CA sends no Signed by.
        var root = new CreateCaViewModel((_, request) =>
        {
            sent = request;
            return Task.FromResult(true);
        }, Issuers)
        { CommonName = "Contoso Root CA" };
        await root.CreateCommand.ExecuteAsync(null);
        Assert.Equal(new CreateCaRequest("Contoso Root CA", false, null, CaKeys.Rsa4096, PkiPluginInfo.DefaultValidityYears), sent);
    }

    [Fact]
    public void Without_a_ca_that_can_sign_an_intermediate_says_what_to_do_first()
    {
        var dialog = new CreateCaViewModel((_, _) => Task.FromResult(true), []) { CommonName = "Site Issuing" };
        Assert.Null(dialog.Issuer);
        Assert.True(dialog.CreateCommand.CanExecute(null)); // a root CA needs no Signed by

        dialog.Type = CreateCaViewModel.TypeChoices[1];
        dialog.CreateCommand.Execute(null);
        Assert.Equal(CreateCaViewModel.NoIssuerText, dialog.ErrorOf(nameof(dialog.Issuer)));
        Assert.False(dialog.CreateCommand.CanExecute(null));
        Assert.False(dialog.Completed);
    }

    [Fact]
    public async Task The_servers_refusal_shows_below_its_field_and_a_created_ca_joins_the_list()
    {
        await using var pki = await PkiHarness.StartAsync();
        var dialogs = new FakeDialogs();
        using var vm = new PkiViewModel(new PluginPageContext(pki.Plugin, pki.Hub), new FakeClientTrust()) { Dialogs = dialogs };
        await vm.LoadAsync();
        await vm.Authorities.LoadAsync();

        CreateCaViewModel? dialog = null;
        dialogs.Drive = async d =>
        {
            dialog = (CreateCaViewModel)d;
            Assert.Equal([CertificateAuthorityInfo.OadmId], dialog.Issuers.Select(i => i.Value)); // the CAs with their key
            dialog.CommonName = "OADM Root CA SERVER01"; // the first-start CA's name
            dialog.Key = CreateCaViewModel.KeyChoices.Single(k => k.Value == CaKeys.Ecc);
            await dialog.CreateCommand.ExecuteAsync(null);
            Assert.Equal("Another CA has this name.", dialog.ErrorOf(nameof(dialog.CommonName)));
            Assert.False(dialog.Completed);

            dialog.CommonName = "Site Issuing";
            dialog.Type = CreateCaViewModel.TypeChoices[1];
            await dialog.CreateCommand.ExecuteAsync(null);
        };
        await vm.CreateCaCommand.ExecuteAsync(null);

        Assert.True(dialog!.Completed);
        var row = Assert.Single(vm.Authorities.Rows, r => r.Name == "Site Issuing");
        Assert.Equal(["Intermediate CA"], row.Badges);
        Assert.Equal("ECC P-256", row.KeyType);
        Assert.Equal("OADM Root CA SERVER01", row.Issuer);
    }
}
