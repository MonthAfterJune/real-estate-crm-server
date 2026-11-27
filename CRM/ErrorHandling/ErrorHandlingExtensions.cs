using Microsoft.AspNetCore.Mvc;

namespace CRM.ErrorHandling;

public static class ErrorHandlingExtensions
{
    public static IServiceCollection AddApiErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
                ApiProblemDetails.AddRequestMetadata(context.HttpContext, context.ProblemDetails);
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        // Document the common exception responses for every controller in Swagger.
        services.Configure<MvcOptions>(options =>
        {
            foreach (var statusCode in new[]
            {
                StatusCodes.Status409Conflict,
                StatusCodes.Status500InternalServerError,
                StatusCodes.Status503ServiceUnavailable
            })
            {
                options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), statusCode));
            }
        });

        return services;
    }

    public static IApplicationBuilder UseApiErrorHandling(this IApplicationBuilder app)
    {
        // Use the same safe response format in Development and Production.
        app.UseExceptionHandler();

        // Cover errors that don't throw, such as unknown routes (404) and wrong HTTP methods (405).
        app.UseStatusCodePages(async context =>
        {
            var problem = new ProblemDetails { Status = context.HttpContext.Response.StatusCode };
            ApiProblemDetails.AddRequestMetadata(context.HttpContext, problem);
            await Results.Problem(problem).ExecuteAsync(context.HttpContext);
        });

        return app;
    }
}
