using Microsoft.AspNetCore.Mvc;

namespace Driftworld.Api.ErrorHandling;

public static class ProblemDetailsWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        IProblemDetailsService problemDetails,
        int status,
        string code,
        string title,
        string detail,
        IDictionary<string, object?>? extensions = null,
        CancellationToken ct = default)
    {
        context.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Type = $"https://driftworld/errors/{code.Replace('_', '-')}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["code"] = code;

        if (extensions is not null)
        {
            foreach (var (k, v) in extensions)
                problem.Extensions[k] = v;
        }

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });

        _ = ct;
    }
}
