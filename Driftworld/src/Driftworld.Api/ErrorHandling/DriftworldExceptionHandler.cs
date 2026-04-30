using Driftworld.Core.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Driftworld.Api.ErrorHandling;

public sealed class DriftworldExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<DriftworldExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DriftworldException dex)
            return false;

        logger.LogInformation(
            "Domain exception {Code} on {Path}: {Detail}",
            dex.Code, context.Request.Path, dex.Message);

        var problem = new ProblemDetails
        {
            Type = $"https://driftworld/errors/{dex.Code.Replace('_', '-')}",
            Title = dex.Title,
            Status = dex.HttpStatus,
            Detail = dex.Message,
            Instance = context.Request.Path,
        };
        problem.Extensions["code"] = dex.Code;
        foreach (var (k, v) in dex.Extensions)
            problem.Extensions[k] = v;

        context.Response.StatusCode = dex.HttpStatus;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });
    }
}
