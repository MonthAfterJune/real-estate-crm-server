using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using CRM.Services;

namespace CRM.ErrorHandling;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, detail) = DescribeError(exception);
        var problem = new ProblemDetails();
        if (exception is WorkflowException { Field: not null } validation)
            problem.Extensions["errors"] = new Dictionary<string, string[]> { [validation.Field] = [validation.Message] };
        problem.Status = statusCode;
        problem.Detail = detail;
        if (exception is WorkflowException { ExistingContactId: int contactId })
        {
            problem.Extensions["existingContactId"] = contactId;
        }
        ApiProblemDetails.AddRequestMetadata(httpContext, problem);

        // Keep exception details in server logs; only the safe description goes to the client.
        _logger.Log(
            statusCode >= 500 ? LogLevel.Error : LogLevel.Warning,
            exception,
            "Request {Method} {Path} failed with status {StatusCode}. TraceId: {TraceId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            statusCode,
            problem.Extensions["traceId"]);

        // Results.Problem supplies a JSON fallback for non-JSON Accept headers.
        await Results.Problem(problem).ExecuteAsync(httpContext);
        return true;
    }

    private static (int StatusCode, string Detail) DescribeError(Exception exception)
    {
        if (exception is WorkflowException workflow) return (workflow.Status, workflow.Message);
        if (exception is BadHttpRequestException badRequest)
        {
            return (badRequest.StatusCode, "The HTTP request could not be processed. Check its format and size.");
        }

        if (exception is DbUpdateConcurrencyException)
        {
            return (StatusCodes.Status409Conflict,
                "The record was changed or removed by another request. Reload it before making further changes.");
        }

        // EF may wrap database failures in DbUpdateException or an execution-strategy exception.
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is not NpgsqlException databaseException)
            {
                continue;
            }

            if (databaseException is PostgresException postgresException)
            {
                switch (postgresException.SqlState)
                {
                    case PostgresErrorCodes.DeadlockDetected:
                    case PostgresErrorCodes.SerializationFailure:
                    case PostgresErrorCodes.LockNotAvailable:
                        return (StatusCodes.Status409Conflict,
                            "A concurrent workflow changed this record. Reload before trying again.");
                    case PostgresErrorCodes.UniqueViolation:
                        return (StatusCodes.Status409Conflict,
                            "A record with the same unique value already exists.");
                    case PostgresErrorCodes.ForeignKeyViolation:
                        return (StatusCodes.Status409Conflict,
                            "A related record does not exist or is still in use. Check the referenced IDs before saving, or remove dependent records before deleting.");
                }
            }

            if (databaseException.IsTransient)
            {
                return (StatusCodes.Status503ServiceUnavailable,
                    "The database is temporarily unavailable. Please try again later.");
            }
        }

        // Unknown database failures, coding errors and non-request cancellations remain server errors.
        return (StatusCodes.Status500InternalServerError,
            "An unexpected error occurred. Use the trace ID when reporting the problem.");
    }
}
