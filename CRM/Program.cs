using System.Reflection;
using CRM.Data;
using CRM.Authentication;
using CRM.ErrorHandling;
using CRM.Mapping;
using CRM.Models;
using CRM.Services;
using CRM.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddAutoMapper(typeof(MappingProfile));

builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IContactService, ContactService>();
builder.Services.AddScoped<IPipelineStageService, PipelineStageService>();
builder.Services.AddScoped<IOpportunityService, OpportunityService>();
builder.Services.AddScoped<IActivityService, ActivityService>();
builder.Services.AddScoped<INoteService, NoteService>();
builder.Services.AddScoped<IPropertyService, PropertyService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<StaffAccess>();
builder.Services.AddScoped<Part01Workflow>();

builder.Services.AddCrmAuthentication(builder.Environment.IsDevelopment());
builder.Services.AddControllers(options => options.Filters.AddService<ApiAntiforgeryFilter>());
builder.Services.AddApiErrorHandling();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Version = "v1",
        Title = "CRM API",
        Description = "ASP.NET Core Web API for a real-estate CRM portfolio project — manages Users, Roles, Contacts, Properties, Opportunities, PipelineStages, Activities, and Notes.",
        Contact = new OpenApiContact
        {
            Name = "CRM Portfolio"
        }
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

// Register before downstream middleware so controller, database and middleware failures are handled.
app.UseApiErrorHandling();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "CRM API v1");
    options.RoutePrefix = "swagger";
    options.DisplayOperationId();
    options.DefaultModelsExpandDepth(-1);
});

app.UseHttpsRedirection();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();

