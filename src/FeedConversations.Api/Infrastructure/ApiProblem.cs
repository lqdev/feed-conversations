using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FeedConversations.Api.Infrastructure;

public sealed class ApiProblem(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static ApiProblem Invalid(string code = "invalid-input") => new(400, code, "The request is invalid.");
}

public sealed class ProblemHandler(IProblemDetailsService problems, ILogger<ProblemHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        var known = exception as ApiProblem;
        if (exception is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            known = new ApiProblem(412, "revision-stale", "The resource changed concurrently.");
        if (exception is BadHttpRequestException requestError)
            known = new ApiProblem(requestError.StatusCode, "invalid-request", "The request could not be read.");
        var status = known?.Status ?? 500;
        if (known is null)
            logger.LogError("Request failed with exception type {ExceptionType}; trace {TraceId}.",
                exception.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = status;
        if (status is 429 or 503) context.Response.Headers.RetryAfter = "30";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = known?.Message ?? "An internal operation failed.",
                Type = $"urn:feed-conversations:problem:{known?.Code ?? "internal-error"}",
                Extensions = { ["code"] = known?.Code ?? "internal-error", ["traceId"] = context.TraceIdentifier }
            }
        });
        return true;
    }
}
