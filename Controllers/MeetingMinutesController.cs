using Microsoft.AspNetCore.Mvc;
using GeminiFreeSearch.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Collections.Concurrent;

namespace GeminiFreeSearch.Controllers
{
    [ApiController]
    public class MeetingMinutesController : Controller
    {
        private readonly MeetingMinutesService _meetingMinutesService;
        private readonly ILogger<MeetingMinutesController> _logger;
        private static readonly ConcurrentDictionary<string, DateTime> _requestTracker = new();

        public MeetingMinutesController(
            MeetingMinutesService meetingMinutesService,
            ILogger<MeetingMinutesController> logger)
        {
            _meetingMinutesService = meetingMinutesService;
            _logger = logger;
        }

        [HttpGet("/MeetingMinutes/Process")]
        public async Task Process([FromQuery] string fileName, [FromQuery] string? startTime, [FromQuery] string? endTime)
        {
            Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";

            using var cts = new CancellationTokenSource();
            var heartbeatTask = KeepAlive(cts.Token);

            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "MeetingMinutes");
                var filePath = Path.Combine(tempDir, fileName);
                
                if (!System.IO.File.Exists(filePath))
                {
                    await SendEvent("status", "文件未找到");
                    return;
                }

                using var stream = System.IO.File.OpenRead(filePath);
                
                await foreach (var chunk in _meetingMinutesService.ProcessAudioFileAsync(
                    stream, fileName))
                {
                    var (type, content) = ParseChunk(chunk);
                    await SendEvent(type, content);
                    await Task.Delay(50);
                }

                await SendEvent("done", "处理完成");
                cts.Cancel();

                try
                {
                    stream.Close();
                    System.IO.File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary file: {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing file: {FileName}", fileName);
                await SendEvent("error", ex.Message);
                cts.Cancel();
            }
            finally
            {
                try { await heartbeatTask; } catch (OperationCanceledException) { }
            }
        }

        private (string type, string content) ParseChunk(string chunk)
        {
            if (chunk.StartsWith("[STATUS]")) return ("status", chunk[8..]);
            if (chunk.StartsWith("[TRANSCRIPTION]")) return ("transcription", chunk[15..]);
            if (chunk.StartsWith("[ANALYSIS]")) return ("analysis", chunk[10..]);
            return ("unknown", chunk);
        }

        private async Task SendEvent(string type, string content)
        {
            var response = new { type, content };
            var serialized = JsonSerializer.Serialize(response);
            await Response.WriteAsync($"data: {serialized}\n\n");
            await Response.Body.FlushAsync();
        }

        private async Task KeepAlive(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await SendEvent("heartbeat", "keep-alive");
                    await Task.Delay(30000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 忽略取消异常
            }
        }

        [HttpPost("/MeetingMinutes/Upload")]
        [RequestSizeLimit(1024 * 1024 * 1024)]
        public async Task<IActionResult> Upload(IFormFile file)
        {
            var userAgent = HttpContext.Request.Headers.UserAgent.ToString();
            var requestKey = $"{file.FileName}-{HttpContext.Connection.RemoteIpAddress}-{userAgent}";

            if (_requestTracker.TryGetValue(requestKey, out var lastTime))
            {
                if ((DateTime.Now - lastTime).TotalSeconds < 5)
                {
                    return Conflict("请求过于频繁");
                }
            }
            _requestTracker.AddOrUpdate(requestKey, DateTime.Now, (k, v) => DateTime.Now);

            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No file uploaded" });
            }

            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "MeetingMinutes");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                var fileName = $"{Path.GetFileNameWithoutExtension(file.FileName)}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
                var filePath = Path.Combine(tempDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                return Ok(new { fileName = fileName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file: {FileName}", file.FileName);
                return StatusCode(500, new { message = "File upload failed" });
            }
        }
    }
} 