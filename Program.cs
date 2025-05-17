using GeminiFreeSearch.Services;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using Serilog;
using Microsoft.AspNetCore.Http.Features;

await ApplicationInitializer.UpdateModelsFromApiAsync();
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
builder.Services.AddHttpClient<MeetingMinutesService>();
builder.Services.AddScoped<MeetingMinutesService>();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[] { new CultureInfo("zh-Hans-CN") };
    options.DefaultRequestCulture = new RequestCulture("zh-Hans-CN");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

builder.Services.AddHealthChecks();

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = 1024 * 1024 * 1024;
});

builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = 1024 * 1024 * 1024;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024 * 1024 * 1024;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapControllers();

app.MapHealthChecks("/health");

app.Run();

public class StreamRequest
{
    public string? Prompt { get; set; }
    public string? Model { get; set; }
    public List<ChatMessage>? History { get; set; }
    public List<ImageData>? Images { get; set; }
}