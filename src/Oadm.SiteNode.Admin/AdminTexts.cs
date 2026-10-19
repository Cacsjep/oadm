namespace Oadm.SiteNode.Admin;

/// <summary>The node's texts, one phrase per concept (service, command line and app).</summary>
public static class AdminTexts
{
    public const string ServiceNotRunning = "The OADM Site Node service is not running.";
    public const string NeedsAdministrator = "Only an administrator of this computer can do this.";
    public const string BindingClosed = "Binding is closed on this site node. Click Allow binding in the OADM Site Node app.";
    public const string TooManyWrongCodes = "Too many wrong pairing codes. Wait 10 minutes and use the new code.";
    public const string WrongCode = "The pairing code is wrong.";
    public const string BoundToAnotherServer = "This site node is bound to another OADM server.";
    public const string AlreadyBound = "This site node is already bound.";
    public const string NotBoundToCaller = "This site node is not bound to this server.";
    public const string PairStartFirst = "Start the pairing first.";
    public const string TooManyPairingAttempts = "Too many pairing attempts. Wait a minute and try again.";
    public const string InvalidPairingValue = "The pairing message is not valid.";
    public const string NoNewIdentity = "No server presents a new identity.";
    public const string InvalidSignature = "The new identity is not signed by the bound server.";
    public const string SiteBusy = "The site node is busy.";
    public const string OutsideNetworks = "The address is outside the networks of this site node.";

    /// <summary>"Another OADM server with this identity is connected from 10.0.0.5".</summary>
    public static string AnotherSession(string from) => "Another OADM server with this identity is connected from " + from;

    /// <summary>"Port 8443 is not allowed at this site node. Allow it in the OADM Site Node app."</summary>
    public static string PortNotAllowed(PortProtocol protocol, int port) =>
        $"{(protocol == PortProtocol.Udp ? "UDP " : "Port ")}{port} is not allowed at this site node. Allow it in the OADM Site Node app.";

    public static string PortInUse(PortProtocol protocol, int port) => $"{Name(protocol)} {port} is used by another program";

    public static string NoPermission(PortProtocol protocol, int port) => $"No permission to use {Name(protocol)} {port}";

    public static string WaitingForApproval(PortProtocol protocol, int port) =>
        $"{Name(protocol)} {port}: waiting for approval in the OADM Site Node app";

    public static string Connected(string target) => "Connected to " + target;

    public static string CannotReach(string target) => "Cannot reach " + target;

    public static string WaitingOnPort(int port) => $"Waiting for the server on port {port}";

    public static string Name(PortProtocol protocol) => protocol == PortProtocol.Udp ? "UDP" : "TCP";
}
