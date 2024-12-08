using GeminiFreeSearch.Services;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using System.Text.Json;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 添加 Serilog 日志
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/app-.log", rollingInterval: RollingInterval.Day));

// 添加配置验证
builder.Services.AddOptions<GeminiApiOptions>()
    .Bind(builder.Configuration.GetSection("GeminiApi"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddRazorPages();
builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddScoped<GeminiService>();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[] { new CultureInfo("zh-Hans-CN") };
    options.DefaultRequestCulture = new RequestCulture("zh-Hans-CN");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

builder.Services.AddHealthChecks();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

// POST /stream 接口：接收 Prompt、Model、History 和 Images
app.MapPost("/stream", async (HttpContext context, GeminiService geminiService) =>
{
    try
    {
        if (context.Request.ContentLength == null || context.Request.ContentLength == 0)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Request body is empty");
            return;
        }

        using var reader = new StreamReader(context.Request.Body);
        var requestBody = await reader.ReadToEndAsync();

        var request = JsonSerializer.Deserialize<StreamRequest>(requestBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (request == null || (string.IsNullOrEmpty(request.Prompt) && (request.Images == null || request.Images.Count == 0)))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid request format: 'prompt' or 'images' required.");
            return;
        }

        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Content-Type"] = "text/event-stream";
        context.Response.StatusCode = 200;

        await foreach (var chunk in geminiService.StreamGenerateContentAsync(
            request.Prompt, 
            request.Model, 
            request.History,
            request.Images))
        {
            var msg = $"data: {JsonSerializer.Serialize(chunk)}\n\n";
            await context.Response.WriteAsync(msg);
            await context.Response.Body.FlushAsync();
        }
    }
    catch (JsonException ex)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync($"Invalid JSON format: {ex.Message}");
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
    }
});

app.MapHealthChecks("/health");

app.Run();

public class StreamRequest
{
    public string? Prompt { get; set; }
    public string? Model { get; set; }
    public List<ChatMessage>? History { get; set; }
    public List<ImageData>? Images { get; set; }
}
