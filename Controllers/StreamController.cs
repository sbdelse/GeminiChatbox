using Microsoft.AspNetCore.Mvc;
using GeminiFreeSearch.Services;
using System.Text.Json;

namespace GeminiFreeSearch.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class StreamController : ControllerBase
    {
        private readonly GeminiService _geminiService;
        private readonly ILogger<StreamController> _logger;

        public StreamController(
            GeminiService geminiService,
            ILogger<StreamController> logger)
        {
            _geminiService = geminiService;
            _logger = logger;
        }

        [HttpPost]
        public async Task Stream([FromBody] StreamRequest request)
        {
            try
            {
                if (request == null || (string.IsNullOrEmpty(request.Prompt) && (request.Images == null || request.Images.Count == 0)))
                {
                    Response.StatusCode = 400;
                    await Response.WriteAsync("Invalid request format: 'prompt' or 'images' required.");
                    return;
                }

                Response.Headers["Cache-Control"] = "no-cache";
                Response.Headers["Content-Type"] = "text/event-stream";
                Response.Headers["Connection"] = "keep-alive";
                Response.StatusCode = 200;

                await foreach (var chunk in _geminiService.StreamGenerateContentAsync(
                    request.Prompt,
                    request.Model,
                    request.History,
                    request.Images))
                {
                    var msg = $"data: {JsonSerializer.Serialize(chunk)}\n\n";
                    await Response.WriteAsync(msg);
                    await Response.Body.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing stream request");
                await Response.WriteAsync($"event: error\ndata: {ex.Message}\n\n");
                throw;
            }
        }
    }

    public class StreamRequest
    {
        public string? Prompt { get; set; }
        public string? Model { get; set; }
        public List<ChatMessage>? History { get; set; }
        public List<ImageData>? Images { get; set; }
    }
} 