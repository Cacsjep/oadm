namespace Oadm.Core.Tasks;

/// <summary>No task starts now (the server is updating, <see cref="TaskEngine.HoldStarts"/>); the message says why.</summary>
public sealed class TasksHeldException : InvalidOperationException
{
    public TasksHeldException()
    {
    }

    public TasksHeldException(string message)
        : base(message)
    {
    }

    public TasksHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
