using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.Data;
using Storefront.Api.Data.Entities;
using Storefront.Api.Health;
using Storefront.Api.Middleware;
using Storefront.Api.Security;
using Storefront.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// No request body may exceed 64 KiB, the largest any endpoint needs (checkout); Kestrel's default is
// 30 MB. Endpoints set lower limits of their own (Security-Audit.md, SA-10).
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = Storefront.Api.Controllers.CheckoutController.MaxRequestBodyBytes);

// ---- Configuration (validated at startup so misconfiguration fails fast) ----
builder.Services.AddOptions<FakeStoreOptions>()
    .Bind(builder.Configuration.GetSection(FakeStoreOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<PricingOptions>()
    .Bind(builder.Configuration.GetSection(PricingOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => decimal.Round(o.TaxRate, 4) == o.TaxRate, "Pricing:TaxRate supports at most 4 decimal places.")
    .Validate(o => decimal.Round(o.FlatShippingFee, 2) == o.FlatShippingFee
                   && decimal.Round(o.FreeShippingThreshold, 2) == o.FreeShippingThreshold,
        "Pricing amounts support at most 2 decimal places.")
    .ValidateOnStart();

var corsSettings = builder.Configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();

// ---- Infrastructure ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddStorefrontDatabase(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<CatalogueState>();
builder.Services.AddTransient<UpstreamRetryHandler>();
builder.Services.AddStorefrontHealthChecks();

// On shutdown (Ctrl+C, SIGTERM, a service manager stopping the app) Kestrel stops accepting new
// connections, readiness turns 503, and in-flight requests get this long to finish.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("Hosting:ShutdownTimeoutSeconds", 30)));

builder.Services.AddHttpClient<IFakeStoreClient, FakeStoreClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<FakeStoreOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Storefront.Api/1.0");
})
    // Bounded retries for transient upstream failures (GET only), inside the HttpClient.Timeout budget.
    .AddHttpMessageHandler<UpstreamRetryHandler>();

// ---- Security ----
// Read and validated here, so a missing or unreadable certificate stops startup (outside Development).
var dataProtectionCertificates = DataProtectionCertificates.Read(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddStorefrontAuthentication(dataProtectionCertificates);
builder.Services.AddStorefrontRateLimiting(builder.Configuration);
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

// ---- Application services ----
builder.Services.AddScoped<IProductCatalogService, ProductCatalogService>();
builder.Services.AddSingleton<IPricingCalculator, PricingCalculator>();
builder.Services.AddSingleton<ICheckoutRequestValidator, CheckoutRequestValidator>();
builder.Services.AddSingleton<IOrderReferenceGenerator, OrderReferenceGenerator>();
builder.Services.AddScoped<ICheckoutService, CheckoutService>();
builder.Services.AddScoped<IOrderQueryService, OrderQueryService>();

// ---- Web ----
builder.Services.AddControllers();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        if (!problem.Extensions.ContainsKey("code"))
        {
            problem.Extensions["code"] = problem switch
            {
                // Base type: covers MVC model validation and Identity's minimal-API endpoints alike.
                HttpValidationProblemDetails => "VALIDATION_FAILED",
                { Status: StatusCodes.Status401Unauthorized } => "UNAUTHORIZED",
                { Status: StatusCodes.Status403Forbidden } => "FORBIDDEN",
                { Status: StatusCodes.Status404NotFound } => "NOT_FOUND",
                { Status: StatusCodes.Status405MethodNotAllowed } => "METHOD_NOT_ALLOWED",
                { Status: StatusCodes.Status413PayloadTooLarge } => "REQUEST_TOO_LARGE",
                { Status: StatusCodes.Status415UnsupportedMediaType } => "UNSUPPORTED_MEDIA_TYPE",
                { Status: StatusCodes.Status429TooManyRequests } => "TOO_MANY_REQUESTS",
                _ => "ERROR",
            };
        }
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddCors(options => options.AddPolicy(CorsSettings.PolicyName, policy => policy
    .WithOrigins(corsSettings.AllowedOrigins)
    .WithMethods("GET", "POST")
    .WithHeaders("Content-Type", "Accept", "Authorization", IdempotencyKeys.HeaderName, CorrelationIdMiddleware.HeaderName)
    .WithExposedHeaders(
        IdempotencyKeys.ReplayedHeaderName, "Retry-After", CorrelationIdMiddleware.HeaderName,
        Storefront.Api.Controllers.ProductsController.StaleHeader)));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Read and validated before the app is built, so a bad value stops startup.
var forwardedHeaders = ForwardedHeadersSettings.Read(builder.Configuration);

var app = builder.Build();

if (corsSettings.AllowedOrigins.Length == 0)
{
    app.Logger.LogWarning("Cors:AllowedOrigins is empty, so browsers on other origins cannot call this API.");
}

app.LogDataProtectionKeyEncryption(dataProtectionCertificates);

await app.ApplyMigrationsIfConfiguredAsync();

app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
    "Storefront API started in {Environment} on {Runtime}; catalogue source {CatalogueHost}.",
    app.Environment.EnvironmentName,
    // The exact runtime patch level, e.g. ".NET 10.0.13": the base image decides it (Security-Audit.md, SA-11).
    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    new Uri(app.Services.GetRequiredService<IOptions<FakeStoreOptions>>().Value.BaseUrl).Host));
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation(
    "Storefront API stopping: readiness now reports unavailable; finishing in-flight requests."));
app.Lifetime.ApplicationStopped.Register(() => app.Logger.LogInformation("Storefront API stopped."));

// Before anything that uses the client address or scheme (rate limiting, HSTS, HTTPS redirection).
app.UseStorefrontForwardedHeaders(forwardedHeaders);

// First of the app's own middleware, so every later log line (including unhandled-exception logs)
// carries the request's ID.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Development runs on plain http://localhost:5080 for the Vite frontend; everywhere else is HTTPS-only.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors(CorsSettings.PolicyName);
app.UseAccountRequestLimits();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapStorefrontAccount();
app.MapStorefrontHealthChecks();

app.Run();

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
