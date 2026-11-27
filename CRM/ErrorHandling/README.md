# API error handling

`Program.cs` registers `AddApiErrorHandling()` and installs `UseApiErrorHandling()` before Swagger and the controller pipeline. This uses .NET 8's built-in exception middleware and `IExceptionHandler`.

`GlobalExceptionHandler` translates exceptions into `application/problem+json` responses. `ApiProblemDetails` adds the request path as `instance` and a `traceId` to exception responses, MVC validation errors, and ordinary error results. Request query strings and exception messages are not copied into error responses.

| Failure | Status | Behavior |
| --- | --- | --- |
| Invalid request DTO or malformed JSON | 400 | `[ApiController]` returns `ValidationProblemDetails` with field-level `errors`. |
| Bad HTTP request detected during application processing | Original status, such as 400 or 413 | Safe request-format message. |
| Missing record or unknown route | 404 | Existing `NotFound()` results and empty routing errors become Problem Details. |
| Unsupported HTTP method | 405 | Problem Details; the routing `Allow` header is preserved. |
| Duplicate unique value, missing referenced record, or restricted deletion | 409 | Safe conflict message; database constraint details stay in logs. |
| EF optimistic concurrency conflict | 409 | Ask the caller to reload the record. |
| Database failure marked transient by Npgsql | 503 | Safe availability message. No automatic write retries are added. |
| Unexpected failure | 500 | Generic description and a trace ID. |

For example, saving a property with a mock `listingAgentId` that is absent from `Users` now returns a 409 relationship-conflict response instead of exposing a database exception. The frontend's existing `ApiError` reads the `detail` message and validation `errors` without changes.

## Where to handle errors

- Keep expected outcomes explicit: return `NotFound()` for a missing record and use DTO validation for invalid input. `[ApiController]` validates DTOs before invoking an action, so repeated `ModelState.IsValid` checks are unnecessary.
- Let unexpected service/database exceptions reach the global handler. Avoid adding identical try/catch blocks to every controller or returning `Ok()` after a failed operation.
- Add a local catch only when that layer can recover, clean up a resource, or translate a specific error meaningfully. When rethrowing the same exception, use `throw;` to preserve its stack trace. Prefer `using`/`await using` or `finally` for cleanup.
- Add future exception mappings only for known, intentional cases. An `ArgumentException` or `InvalidOperationException` is not automatically a client error.
- The handler logs the original exception with method, path, status, and trace ID through `ILogger`. Keep detailed logs server-side. .NET 8's exception middleware also emits its own diagnostic log.
- A disconnected client's `OperationCanceledException`/`IOException` is handled by the built-in middleware as request cancellation (499 when possible), without attempting to send an error body. Cancellation without an aborted request remains a server error.
- The middleware cannot replace a response after its headers have been sent. Startup failures and background-service failures are outside this HTTP handler's scope.

The same safe API response format is used in Development and Production. Use the trace ID in server logs to investigate errors. Existing successful response bodies and status codes are unchanged.

References: [ASP.NET Core exception handling](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0), [API Problem Details](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-8.0), [Npgsql exceptions](https://www.npgsql.org/doc/diagnostics/exceptions_notices.html).
