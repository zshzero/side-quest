namespace Driftworld.Core.Exceptions;

public abstract class DriftworldException : Exception
{
    protected DriftworldException(string code, int httpStatus, string title, string detail)
        : base(detail)
    {
        Code = code;
        HttpStatus = httpStatus;
        Title = title;
    }

    public string Code { get; }
    public int HttpStatus { get; }
    public string Title { get; }
    public Dictionary<string, object?> Extensions { get; } = new();
}
