namespace Driftworld.Core.Exceptions;

public sealed class NoOpenCycleException : DriftworldException
{
    public NoOpenCycleException()
        : base(
            code: "no_open_cycle",
            httpStatus: 503,
            title: "No cycle is currently open",
            detail: "The world has no open cycle to accept decisions. This indicates the cycle-close worker has failed to open a successor.")
    {
    }
}
