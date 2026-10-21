using Grpc.Core;

namespace Oadm.SiteNode.State;

/// <summary>A call the node refuses: the gRPC status and the plain text the server shows.</summary>
public sealed class NodeRefusalException : Exception
{
    public NodeRefusalException()
    {
    }

    public NodeRefusalException(string message)
        : base(message)
    {
    }

    public NodeRefusalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NodeRefusalException(StatusCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public StatusCode Code { get; } = StatusCode.FailedPrecondition;

    public RpcException ToRpc() => new(new Status(Code, Message));
}
