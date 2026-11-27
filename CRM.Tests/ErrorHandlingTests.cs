using System.Collections.Concurrent;
using System.Security.Claims;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using CRM.Controllers;
using CRM.ErrorHandling;
using CRM.Mapping;
using CRM.Models;
using CRM.Services.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace CRM.Tests;

// Exercise real middleware, routing, model binding, controllers and JSON serialization
// with a service stub. No database, network listener or additional test packages are needed.
public sealed class ErrorHandlingTests : IAsyncLifetime
{
    private const string SensitiveMessage = "private-database-detail-do-not-send";
    private const string TraceId = "error-handling-test-trace";
    private readonly PropertyServiceStub _properties = new();
    private readonly ContactServiceStub _contacts = new();
    private readonly UserServiceStub _users = new();
    private readonly RecordingLoggerProvider _logs = new();
    private readonly WebApplication _application;
    private readonly RequestDelegate _pipeline;

    public ErrorHandlingTests()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PropertiesController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_logs);
        builder.Services.AddControllers().AddApplicationPart(typeof(PropertiesController).Assembly);
        builder.Services.AddAutoMapper(typeof(MappingProfile));
        builder.Services.AddSingleton<IPropertyService>(_properties);
        _contacts.Records[5] = new Contact { Id = 5, Type = "Seller", FirstName = "Sam", LastName = "Owner" };
        _contacts.Records[6] = new Contact { Id = 6, Type = "Seller", FirstName = "Alex", LastName = "Owner" };
        _contacts.Records[7] = new Contact { Id = 7, Type = "Buyer", FirstName = "Pat", LastName = "Buyer" };
        _users.Records[1] = new User { Id = 1, FullName = "Taylor Agent", Role = new Role { Name = "agent" } };
        _users.Records[12] = new User { Id = 12, FullName = "Sam Registered Seller", Role = new Role { Name = "Seller" } };
        _users.Records[5] = new User { Id = 5, FullName = "Pat Buyer", Role = new Role { Name = "buyer" } };
        builder.Services.AddSingleton<IContactService>(_contacts);
        builder.Services.AddSingleton<IUserService>(_users);
        builder.Services.AddApiErrorHandling();
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();

        _application = builder.Build();
        _application.UseApiErrorHandling();
        _application.UseRouting();
        _application.UseAuthorization();
        ((IApplicationBuilder)_application).UseEndpoints(endpoints => endpoints.MapControllers());
        _pipeline = ((IApplicationBuilder)_application).Build();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _application.DisposeAsync();

    public static IEnumerable<object[]> ExceptionCases()
    {
        yield return new object[] { new InvalidOperationException(SensitiveMessage), 500 };
        yield return new object[] { new ArgumentException(SensitiveMessage), 500 };
        yield return new object[] { new OperationCanceledException(SensitiveMessage), 500 };
        yield return new object[] { new DbUpdateException(SensitiveMessage), 500 };
        yield return new object[] { new DbUpdateConcurrencyException(SensitiveMessage), 409 };
        yield return new object[] { new BadHttpRequestException(SensitiveMessage), 400 };
        yield return new object[] { new BadHttpRequestException(SensitiveMessage, 413), 413 };
        yield return new object[] { PostgresError(PostgresErrorCodes.UniqueViolation), 409 };
        yield return new object[] { DatabaseError(PostgresErrorCodes.UniqueViolation), 409 };
        yield return new object[] { DatabaseError(PostgresErrorCodes.ForeignKeyViolation), 409 };
        yield return new object[] { DatabaseError(PostgresErrorCodes.DeadlockDetected), 409 };
        yield return new object[] { DatabaseError(PostgresErrorCodes.SerializationFailure), 409 };
        yield return new object[] { DatabaseError(PostgresErrorCodes.LockNotAvailable), 409 };
        yield return new object[] { DatabaseError(PostgresErrorCodes.SyntaxError), 500 };
        yield return new object[] { DatabaseError(PostgresErrorCodes.InvalidPassword), 500 };
        yield return new object[] { new NpgsqlException(SensitiveMessage, new TimeoutException()), 503 };
        yield return new object[]
        {
            new InvalidOperationException(SensitiveMessage,
                new DbUpdateException(SensitiveMessage, new NpgsqlException(SensitiveMessage, new IOException()))),
            503
        };
    }

    [Theory]
    [MemberData(nameof(ExceptionCases))]
    public async Task Exceptions_return_safe_problem_details_and_are_logged(Exception exception, int expectedStatus)
    {
        _properties.Failure = exception;

        var response = await Send("GET", "/api/Properties");
        var problem = AssertProblem(response, expectedStatus, "/api/Properties");

        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.DoesNotContain(SensitiveMessage, response.Body);
        Assert.DoesNotContain(exception.GetType().FullName!, response.Body);
        Assert.DoesNotContain("stackTrace", response.Body);
        Assert.DoesNotContain("private-query-value", response.Body);
        Assert.Contains(_logs.Entries, entry =>
            entry.Category == typeof(GlobalExceptionHandler).FullName &&
            entry.Level == (expectedStatus >= 500 ? LogLevel.Error : LogLevel.Warning) &&
            ReferenceEquals(entry.Exception, exception) &&
            entry.Message.Contains(TraceId));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/problem+json")]
    [InlineData("text/html")]
    public async Task Exception_response_is_json_even_when_the_default_writer_cannot_negotiate(string accept)
    {
        _properties.Failure = new InvalidOperationException(SensitiveMessage);

        var response = await Send("GET", "/api/Properties", accept: accept);

        AssertProblem(response, 500, "/api/Properties");
        Assert.DoesNotContain(SensitiveMessage, response.Body);
    }

    [Fact]
    public async Task Invalid_dto_returns_field_errors_without_calling_the_service()
    {
        var response = await Send("POST", "/api/Properties",
            """{"address":"","status":"Invalid","bedrooms":2,"listingAgentId":0}""");
        var problem = AssertProblem(response, 400, "/api/Properties");

        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Address", out _));
        Assert.True(errors.TryGetProperty("Status", out _));
        Assert.True(errors.TryGetProperty("ListingAgentId", out _));
        // Cross-field seller validation runs once individual fields are valid.
        Assert.Equal(0, _properties.WriteCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"sellerContactId\":null")]
    [InlineData(",\"sellerContactId\":0")]
    [InlineData(",\"sellerContactId\":-1")]
    public async Task Creating_property_requires_a_positive_seller_id(string sellerField)
    {
        var response = await Send("POST", "/api/Properties",
            "{\"address\":\"1 Main St\",\"price\":100,\"city\":\"Manila\",\"propertyType\":\"House\",\"status\":\"Available\",\"bedrooms\":2,\"listingAgentId\":1" + sellerField + "}");

        var problem = AssertProblem(response, 400, "/api/Properties");
        Assert.True(problem.GetProperty("errors").TryGetProperty("SellerContactId", out _));
        Assert.Equal(0, _properties.WriteCalls);
    }

    [Theory]
    [InlineData("POST", 999, 1, "SellerContactId")]
    [InlineData("POST", 7, 1, "SellerContactId")]
    [InlineData("POST", 5, 999, "ListingAgentId")]
    [InlineData("PUT", 999, 1, "SellerContactId")]
    [InlineData("PUT", 7, 1, "SellerContactId")]
    [InlineData("PUT", 5, 999, "ListingAgentId")]
    public async Task Invalid_property_assignments_return_field_errors_without_saving(
        string method, int sellerContactId, int listingAgentId, string field)
    {
        var path = method == "POST" ? "/api/Properties" : "/api/Properties/42";
        var response = await Send(method, path, JsonSerializer.Serialize(new
        {
            address = "1 Main St", price = 100, city = "Manila", propertyType = "House", status = "Draft", sellerContactId, bedrooms = 2, listingAgentId
        }));

        var problem = AssertProblem(response, 400, path);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(0, _properties.WriteCalls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Updating_property_keeps_the_selected_seller_contact(int sellerContactId)
    {
        var response = await Send("PUT", "/api/Properties/42", JsonSerializer.Serialize(new
        {
            address = "1 Main St", price = 100, city = "Manila", propertyType = "House", status = "Draft", sellerContactId, bedrooms = 2, listingAgentId = 1
        }));

        Assert.Equal(200, response.Status);
        var body = JsonSerializer.Deserialize<JsonElement>(response.Body);
        Assert.Equal(42, body.GetProperty("id").GetInt32());
        Assert.Equal(sellerContactId, body.GetProperty("sellerContactId").Deserialize<int?>());
        Assert.Equal(1, _properties.WriteCalls);
    }

    [Fact]
    public async Task Malformed_json_returns_validation_problem_details()
    {
        var response = await Send("POST", "/api/Properties", "{invalid-json");

        var problem = AssertProblem(response, 400, "/api/Properties");
        Assert.True(problem.TryGetProperty("errors", out _));
        Assert.Equal(0, _properties.WriteCalls);
    }

    [Theory]
    [InlineData("GET", "/api/Properties/99")]
    [InlineData("PUT", "/api/Properties/99")]
    [InlineData("DELETE", "/api/Properties/99")]
    [InlineData("GET", "/api/route-does-not-exist")]
    public async Task Missing_records_and_routes_return_problem_details(string method, string path)
    {
        var response = await Send(method, path,
            method == "PUT" ? """{"address":"1 Main St","price":100,"city":"Manila","propertyType":"House","status":"Draft","sellerContactId":5,"bedrooms":2,"listingAgentId":1}""" : null);

        AssertProblem(response, 404, path);
    }

    [Fact]
    public async Task Unsupported_method_preserves_allow_header()
    {
        var response = await Send("PATCH", "/api/Properties/99");

        AssertProblem(response, 405, "/api/Properties/99");
        Assert.Contains("GET", response.Allow);
        Assert.Contains("PUT", response.Allow);
        Assert.Contains("DELETE", response.Allow);
    }

    [Fact]
    public async Task Empty_routing_error_uses_json_with_a_non_json_accept_header()
    {
        var response = await Send("GET", "/missing", accept: "text/html");

        AssertProblem(response, 404, "/missing");
    }

    [Theory]
    [InlineData("POST", "/api/Properties")]
    [InlineData("DELETE", "/api/Properties/42")]
    public async Task Missing_reference_and_restricted_delete_return_relationship_conflict(string method, string path)
    {
        _properties.Failure = DatabaseError(PostgresErrorCodes.ForeignKeyViolation);
        var response = await Send(method, path,
            method == "POST" ? """{"address":"1 Main St","price":100,"city":"Manila","propertyType":"House","status":"Draft","sellerContactId":5,"bedrooms":2,"listingAgentId":1}""" : null);

        var problem = AssertProblem(response, 409, path);
        Assert.Contains("related record", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Successful_creation_and_deletion_keep_their_original_responses()
    {
        var created = await Send("POST", "/api/Properties",
            """{"address":"1 Main St","price":100,"city":"Manila","propertyType":"House","status":"Draft","sellerContactId":5,"bedrooms":2,"listingAgentId":1}""");

        Assert.Equal(201, created.Status);
        var body = JsonSerializer.Deserialize<JsonElement>(created.Body);
        Assert.Equal(42, body.GetProperty("id").GetInt32());
        Assert.Equal("1 Main St", body.GetProperty("address").GetString());
        Assert.Equal(5, body.GetProperty("sellerContactId").GetInt32());
        Assert.Equal(1, body.GetProperty("listingAgentId").GetInt32());
        Assert.False(body.TryGetProperty("traceId", out _));

        var deleted = await Send("DELETE", "/api/Properties/42");
        Assert.Equal(204, deleted.Status);
        Assert.Empty(deleted.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_disconnect_does_not_write_an_error_body(bool ioFailure)
    {
        var builder = new ApplicationBuilder(_application.Services);
        builder.UseApiErrorHandling();
        builder.Run(_ =>
        {
            if (ioFailure) throw new IOException(SensitiveMessage);
            throw new OperationCanceledException(SensitiveMessage);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var response = await Send("GET", "/aborted", pipeline: builder.Build(), aborted: cancellation.Token);

        Assert.Equal(499, response.Status);
        Assert.Empty(response.Body);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Category == typeof(GlobalExceptionHandler).FullName);
    }

    [Fact]
    public async Task Started_response_is_not_overwritten()
    {
        var exception = new InvalidOperationException(SensitiveMessage);
        var builder = new ApplicationBuilder(_application.Services);
        builder.UseApiErrorHandling();
        builder.Run(context =>
        {
            context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
            throw exception;
        });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Send("GET", "/started", pipeline: builder.Build()));

        Assert.Same(exception, thrown);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Category == typeof(GlobalExceptionHandler).FullName);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Obsolete_seller_user_payload_is_rejected_without_saving(string method)
    {
        var path = method == "POST" ? "/api/Properties" : "/api/Properties/42";
        var response = await Send(method, path,
            """{"address":"1 Main St","price":100,"city":"Manila","propertyType":"House","status":"Draft","bedrooms":2,"sellerUserId":12,"listingAgentId":1}""");
        AssertProblem(response, 400, path);
        Assert.Equal(0, _properties.WriteCalls);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task A_property_cannot_clear_its_required_seller_contact(string method)
    {
        var path = method == "POST" ? "/api/Properties" : "/api/Properties/42";
        var response = await Send(method, path,
            """{"address":"1 Main St","price":100,"city":"Manila","propertyType":"House","status":"Draft","bedrooms":2,"sellerContactId":null,"listingAgentId":1}""");
        var problem = AssertProblem(response, 400, path);
        Assert.True(problem.GetProperty("errors").TryGetProperty("SellerContactId", out _));
        Assert.Equal(0, _properties.WriteCalls);
    }
    private async Task<TestResponse> Send(
        string method,
        string path,
        string? body = null,
        string accept = "application/json",
        RequestDelegate? pipeline = null,
        CancellationToken aborted = default)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            TraceIdentifier = TraceId,
            RequestAborted = aborted
        };
        context.Request.Method = method;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "agent"), new Claim(ClaimTypes.NameIdentifier, "1") }, "test"));
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;
        context.Request.QueryString = new QueryString("?token=private-query-value");
        context.Request.Headers.Accept = accept;
        await using var output = new MemoryStream();
        context.Response.Body = output;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        if (body != null)
        {
            context.Request.Body = input;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = input.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyFeature());
        }

        await (pipeline ?? _pipeline)(context);

        return new TestResponse(context.Response.StatusCode, context.Response.ContentType ?? "",
            Encoding.UTF8.GetString(output.ToArray()), context.Response.Headers.Allow.ToString());
    }

    private static JsonElement AssertProblem(TestResponse response, int status, string path)
    {
        Assert.Equal(status, response.Status);
        Assert.StartsWith("application/problem+json", response.ContentType);
        var problem = JsonSerializer.Deserialize<JsonElement>(response.Body);
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
        Assert.Equal(path, problem.GetProperty("instance").GetString());
        Assert.Equal(TraceId, problem.GetProperty("traceId").GetString());
        return problem;
    }

    private static PostgresException PostgresError(string sqlState) =>
        new(SensitiveMessage, "ERROR", "ERROR", sqlState);

    private static DbUpdateException DatabaseError(string sqlState) =>
        new(SensitiveMessage, PostgresError(sqlState));

    private sealed record TestResponse(int Status, string ContentType, string Body, string Allow);

    private sealed class RequestBodyFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class PropertyServiceStub : IPropertyService
    {
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public int WriteCalls { get; private set; }

        private void CheckFailure(bool write = false)
        {
            Calls++;
            if (write) WriteCalls++;
            if (Failure != null) throw Failure;
        }

        public Task<IEnumerable<Property>> GetAllAsync(
            IEnumerable<Expression<Func<Property, object>>>? includes = null,
            CancellationToken cancellationToken = default)
        {
            CheckFailure();
            return Task.FromResult<IEnumerable<Property>>(Array.Empty<Property>());
        }

        public Task<Property?> GetByIdAsync(int id,
            IEnumerable<Expression<Func<Property, object>>>? includes = null,
            CancellationToken cancellationToken = default)
        {
            CheckFailure();
            return Task.FromResult<Property?>(id == 42 ? new Property { Id = 42, Address = "1 Main St", City = "Manila", PropertyType = "House", Status = "Draft", SellerContactId = 5, ListingAgentId = 1 } : null);
        }

        public Task<Property> CreateAsync(Property entity, CancellationToken cancellationToken = default)
        {
            CheckFailure(write: true);
            entity.Id = 42;
            return Task.FromResult(entity);
        }

        public Task<Property?> UpdateAsync(int id, Property entity, CancellationToken cancellationToken = default)
        {
            CheckFailure(write: true);
            entity.Id = id;
            return Task.FromResult(id == 42 ? entity : null);
        }

        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            CheckFailure(write: true);
            return Task.FromResult(id == 42);
        }
    }

    private sealed class ContactServiceStub : LookupServiceStub<Contact>, IContactService { }

    private sealed class UserServiceStub : LookupServiceStub<User>, IUserService { }

    private class LookupServiceStub<T> : ICrudService<T> where T : class
    {
        public Dictionary<int, T> Records { get; } = new();

        public Task<T?> GetByIdAsync(int id,
            IEnumerable<Expression<Func<T, object>>>? includes = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Records.GetValueOrDefault(id));
        }

        public Task<IEnumerable<T>> GetAllAsync(
            IEnumerable<Expression<Func<T, object>>>? includes = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<T>>(Records.Values);

        public Task<T> CreateAsync(T entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<T?> UpdateAsync(int id, T entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed record LogEntry(string Category, LogLevel Level, Exception? Exception, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, exception, formatter(state, exception)));
        }
    }
}




