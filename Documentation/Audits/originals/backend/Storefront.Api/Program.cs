using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.Middleware;
using Storefront.Api.Services;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddHttpClient<IFakeStoreClient, FakeStoreClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<FakeStoreOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Storefront.Api/1.0");
});

// ---- Application services ----
builder.Services.AddScoped<IProductCatalogService, ProductCatalogService>();
builder.Services.AddSingleton<IPricingCalculator, PricingCalculator>();
builder.Services.AddSingleton<ICheckoutRequestValidator, CheckoutRequestValidator>();
builder.Services.AddSingleton<IOrderReferenceGenerator, OrderReferenceGenerator>();
builder.Services.AddScoped<ICheckoutService, CheckoutService>();

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
                ValidationProblemDetails => "VALIDATION_FAILED",
                { Status: StatusCodes.Status404NotFound } => "NOT_FOUND",
                { Status: StatusCodes.Status415UnsupportedMediaType } => "UNSUPPORTED_MEDIA_TYPE",
                _ => "ERROR",
            };
        }
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddCors(options => options.AddPolicy(CorsSettings.PolicyName, policy => policy
    .WithOrigins(corsSettings.AllowedOrigins)
    .WithMethods("GET", "POST")
    .WithHeaders("Content-Type", "Accept")));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsSettings.PolicyName);
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

app.Run();
