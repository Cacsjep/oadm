using System.Globalization;

using Oadm.Contracts.V1;

namespace Oadm.Contracts.Sites;

/// <summary>
/// The plain texts of the Sites page and the binding (Wording: one phrase per concept), shared by the server, the client
/// and fake mode, so a refusal reads the same wherever it comes from. Texts plugins use too live in
/// <c>Oadm.Sdk.Sites.SiteTexts</c>; the texts of discovery at sites in <see cref="SiteDiscoveryTexts"/>.
/// </summary>
public static class SiteTexts
{
    // ---------- refusals and states of the server ----------

    public const string WrongCode = "The pairing code is wrong.";
    public const string CodeFormat = "Enter the 8 characters the OADM Site Node app shows.";
    public const string NameRequired = "Enter a site name.";
    public const string NameTooLong = "Use at most 64 characters for the name.";
    public const string NameInvalid = "Use letters, digits and spaces.";
    public const string NameTaken = "A site with this name exists.";
    public const string TooManySites = "OADM keeps at most 100 sites. Remove a site first.";
    public const string NodeGone = "The site node is no longer connected. Check it in the OADM Site Node app.";
    public const string NodeNotAnswering = "The site node does not answer.";
    public const string NotBound = "The site node was reset. Replace or remove the site.";

    /// <summary>Accept site nodes was turned on while another program holds the site port: nothing changed.</summary>
    public static string ListenPortInUse(int port) =>
        string.Create(CultureInfo.InvariantCulture, $"Another program uses TCP {port}, the port site nodes connect to. Site nodes stay off.");
    public const string Connecting = "Connecting";
    public const string NodeDidNotAnswer = "The site node did not answer. Reset it in the OADM Site Node app.";
    public const string WaitingToBeBound = "Waiting to be bound";
    public const string BindingClosed = "Binding is closed on this site node. Click Allow binding in the OADM Site Node app.";
    public const string BoundElsewhere = "Bound to another OADM server.";
    public const string RemovedSite = "This site node is bound to this server, but its site was removed. Click Reset in the OADM Site Node app.";
    public const string TooOld = "Site node too old. Update it.";
    public const string SiteGone = "The site no longer exists.";
    public const string NewSiteGone = "The new site no longer exists.";
    public const string ChooseAnotherSite = "Choose another site.";

    /// <summary>A removed site whose devices were kept: the Sites page lists it until its devices are replaced or removed.</summary>
    public const string RemovedState = "Removed";

    /// <summary>The message of a removed site's row.</summary>
    public const string RemovedMessage = "Its devices stay until you replace or remove them.";

    /// <summary>A removed site with kept devices takes only Replace with and Remove devices.</summary>
    public const string RemovedKeepsDevices = "The site is removed already. Replace it or remove its devices.";

    public static string CopyOf(string site) => $"This site node is a copy of {site}. Click Reset on the copy.";

    public const string InvalidAnswer = "The site node sent an answer that is not valid.";
    public const string NotStored = "The site could not be stored. Bind it again.";
    public const string NodeIdAmbiguous = "Several site nodes report this id. Click Bind on the row of the node you mean.";

    /// <summary>The node of a site accepted this server's new identity: Bind with its code binds the site again.</summary>
    public static string BindAgain(string site) => $"The site node of {site} accepted this server's new identity. Bind it with its pairing code.";

    // ---------- the Sites page ----------

    public const string AcceptOffHint = "Turn on Accept site nodes, then enter this server's address in the OADM Site Node app.";

    /// <summary>The tooltip of the row action Replace with.</summary>
    public const string ReplaceTip = "Replace with another site";

    public static string AcceptOnHint(string listenAddress) =>
        string.IsNullOrEmpty(listenAddress)
            ? "Enter this server's address in the OADM Site Node app."
            : $"Enter {listenAddress} as the server address in the OADM Site Node app.";

    public static string State(SiteState state) => state switch
    {
        SiteState.Online => "Online",
        SiteState.Offline => "Offline",
        SiteState.NotBound => "Not bound",
        SiteState.Waiting => WaitingToBeBound,
        SiteState.BoundElsewhere => "Bound to another server",
        SiteState.BindingClosed => "Binding closed",
        SiteState.Removed => RemovedState,
        _ => "Unknown",
    };

    public static string DefaultDetail(SiteState state) => state switch
    {
        SiteState.NotBound => NotBound,
        SiteState.BindingClosed => "Click Allow binding in the OADM Site Node app.",
        SiteState.Removed => RemovedMessage,
        _ => string.Empty,
    };

    public static string Replaced(ReplaceSiteReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var moved = reply.Moved == 1 ? "1 device moved" : string.Create(CultureInfo.CurrentCulture, $"{reply.Moved:N0} devices moved");
        var stayed = reply.StayedDeviceIds.Count;
        return stayed == 0 ? moved + "." : string.Create(CultureInfo.CurrentCulture, $"{moved}, {stayed:N0} did not answer through the new site and stayed.");
    }

    // ---------- names ----------

    /// <summary>Why a typed site name cannot be used (the server's rule: 1..64 characters, trimmed, no control characters); null = fine.</summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return NameRequired;
        }

        if (trimmed.Length > MaxNameLength)
        {
            return NameTooLong;
        }

        return trimmed.Any(char.IsControl) ? NameInvalid : null;
    }

    /// <summary>Longest site name.</summary>
    public const int MaxNameLength = 64;
}

/// <summary>
/// The pairing code as typed (docs/spec/sites.md, Server side: Binding): 8 characters of Crockford base32 (no I, L, O, U); case, spaces and
/// dashes ignored; I and L read as 1, O as 0. Shared by the server's binding, the node and the client's form.
/// </summary>
public static class PairingCodeText
{
    public const int Length = 8;

    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>The typed code in its stored form, or null when it is not 8 characters of the alphabet.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
        {
            return null;
        }

        Span<char> code = stackalloc char[Length];
        var n = 0;
        foreach (var raw in typed)
        {
            if (raw is '-' or ' ')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Alphabet.Contains(c, StringComparison.Ordinal) || n == Length)
            {
                return null;
            }

            code[n++] = c;
        }

        return n == Length ? new string(code) : null;
    }
}
