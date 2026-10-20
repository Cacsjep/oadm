namespace Oadm.Server.Sites;

public sealed partial class SiteManager
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Site {Site} is asked to allow TCP {Port} (device {Host})")]
    private partial void LogPortRequested(string site, int port, string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "The connection to the site node of {Site} ended")]
    private partial void LogSessionEnded(string site);

    [LoggerMessage(Level = LogLevel.Information, Message = "Site node of {Site} connected ({Remote})")]
    private partial void LogSessionStarted(string site, string remote);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A second site node with the key of {Site} connected from {Remote} while the one at {Current} answers; refused (a copied node?)")]
    private partial void LogSecondSession(string site, string remote, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "Site node {Node} waited 24 hours without being bound and was dropped")]
    private partial void LogWaitingDropped(string node);

    [LoggerMessage(Level = LogLevel.Information, Message = "Site {Site} is online")]
    private partial void LogOnline(string site);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Site {Site} is offline: its site node does not answer")]
    private partial void LogOffline(string site);

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepting site nodes on {Url} (port {Port})")]
    private partial void LogListening(string url, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "No longer accepting site nodes")]
    private partial void LogListenerClosed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A site could not be written")]
    private partial void LogWriteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The site node of {Site} was reset and is no longer bound")]
    private partial void LogNotBound(string site);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Too many new site node connections from {Remote}; refused for a minute")]
    private partial void LogTooManyConnections(string remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "A site node connection from {Remote} failed: {Reason}")]
    private partial void LogLinkFailed(string remote, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Too many site nodes from {Remote} wait to be bound; one more was refused")]
    private partial void LogTooManyWaiting(string remote);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Too many site nodes wait to be bound; {Node} from {Remote}, waiting longest, was dropped")]
    private partial void LogWaitingEvicted(string node, string remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "Site node {Node} from {Remote} waits to be bound")]
    private partial void LogWaiting(string node, string remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "Site {Site} bound (site node from {Remote})")]
    private partial void LogBound(string site, string remote);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wrong pairing code for site node {Node} from {Remote}")]
    private partial void LogWrongCode(string node, string remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "Site node {Node} from {Remote} no longer waits (its connection ended)")]
    private partial void LogWaitingGone(string node, string remote);
}
