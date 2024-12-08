using Microsoft.AspNetCore.Mvc;
using GeminiFreeSearch.Services;

namespace GeminiFreeSearch.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class StreamController : ControllerBase
    {
        private readonly GeminiService _geminiService;

        public StreamController(GeminiService geminiService)
        {
            _geminiService = geminiService;
        }

        [HttpPost]
        public async Task Stream([FromBody] StreamRequest request)
        {
            Response.Headers["Content-Type"] = "text/event-stream";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";

            try
            {
                await foreach (var chunk in _geminiService.StreamGenerateContentAsync(
                    request.Prompt, 
                    request.Model, 
                    request.History))
                {
                    await Response.WriteAsync($"data: {chunk}\n\n");
                    await Response.Body.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                await Response.WriteAsync($"event: error\ndata: {ex.Message}\n\n");
                throw;
            }
        }

        public class StreamRequest
        {
            public string Prompt { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public List<ChatMessage> History { get; set; } = new();
        }
    }
} 