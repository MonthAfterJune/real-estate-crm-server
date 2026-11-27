using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace CRM.ErrorHandling;

internal static class ApiProblemDetails
{
    public static void AddRequestMetadata(HttpContext context, ProblemDetails problem)
    {
        // .NET's defaults don't cover every HTTP error (for example, 413).
        problem.Type ??= "about:blank";
        problem.Title ??= ReasonPhrases.GetReasonPhrase(problem.Status ?? context.Response.StatusCode);
        // Leave query strings out: they may contain personal or sensitive values.
        problem.Instance ??= context.Request.PathBase.Add(context.Request.Path).Value;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
    }
}
