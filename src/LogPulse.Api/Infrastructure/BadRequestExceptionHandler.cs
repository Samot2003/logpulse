using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LogPulse.Api.Infrastructure;

/// <summary>
/// Kestrel reports client errors such as an oversized body (413) as <see cref="BadHttpRequestException"/>.
/// Without this handler they would surface as 500; here they keep their real status code.
/// </summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Request body too large" : "Bad request",
            },
        });
    }
}
