using Driftworld.Core.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

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

        await ProblemDetailsWriter.WriteAsync(
            context,
            problemDetails,
            status: dex.HttpStatus,
            code: dex.Code,
            title: dex.Title,
            detail: dex.Message,
            extensions: dex.Extensions,
            ct: cancellationToken);

        return true;
    }
}
