using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GeminiFreeSearch.Services
{
    public class GeminiService
    {
        private readonly ILogger<GeminiService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string[] _apiKeys;
        private readonly string _baseUrl;
        private readonly Dictionary<string, ModelConfig> _models;
        private int _currentKeyIndex = 0;
        private readonly object _lockObject = new object();
        private const int MaxRetries = 3;
        private static readonly TimeSpan[] RetryDelays = {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4)
        };

        public GeminiService(
            HttpClient httpClient, 
            IConfiguration configuration,
            ILogger<GeminiService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            
            // 从配置中获取 API keys 数组
            _apiKeys = configuration.GetSection("GeminiApi:ApiKeys").Get<string[]>() 
                ?? throw new InvalidOperationException("No API keys configured.");
            
            if (_apiKeys.Length == 0)
            {
                throw new InvalidOperationException("At least one API key must be configured.");
            }

            _baseUrl = configuration["GeminiApi:BaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

            _models = new Dictionary<string, ModelConfig>();
            var modelsSection = configuration.GetSection("GeminiApi:Models");
            foreach (var modelSection in modelsSection.GetChildren())
            {
                var key = modelSection.Key;
                var fallbackModel = modelSection["FallbackModel"];
                _models[key] = new ModelConfig
                {
                    FallbackModel = fallbackModel ?? string.Empty
                };
            }
        }

        private string ResolveModelName(string? modelName)
        {
            if (string.IsNullOrEmpty(modelName))
            {
                return "gemini-1.5-flash-latest";
            }

            if (_models.ContainsKey(modelName))
            {
                return modelName;
            }

            foreach (var kvp in _models)
            {
                if (kvp.Value.FallbackModel == modelName && _models.ContainsKey(kvp.Value.FallbackModel))
                {
                    return kvp.Value.FallbackModel;
                }
            }

            if (_models.ContainsKey("gemini-1.5-flash-latest"))
            {
                return "gemini-1.5-flash-latest";
            }

            throw new InvalidOperationException($"Cannot resolve model name for {modelName}.");
        }

        private string GetNextApiKey()
        {
            lock (_lockObject)
            {
                var key = _apiKeys[_currentKeyIndex];
                _currentKeyIndex = (_currentKeyIndex + 1) % _apiKeys.Length;
                return key;
            }
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, string operationName)
        {
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    return await action();
                }
                catch (HttpRequestException ex) when (IsTransientError(ex))
                {
                    if (i == MaxRetries - 1) throw;
                    
                    var delay = RetryDelays[i];
                    _logger.LogWarning(ex, 
                        "Attempt {Attempt} failed for {Operation}. Retrying in {Delay} seconds", 
                        i + 1, operationName, delay.TotalSeconds);
                    
                    await Task.Delay(delay);
                }
                catch (Exception ex) when (IsRateLimitError(ex))
                {
                    _logger.LogError(ex, "Rate limit exceeded for {Operation}", operationName);
                    throw new GeminiRateLimitException("API rate limit exceeded. Please try again later.");
                }
            }
            throw new GeminiException($"Operation {operationName} failed after {MaxRetries} attempts");
        }

        private bool IsTransientError(HttpRequestException ex)
        {
            return ex.StatusCode is 
                System.Net.HttpStatusCode.ServiceUnavailable or 
                System.Net.HttpStatusCode.GatewayTimeout or
                System.Net.HttpStatusCode.RequestTimeout;
        }

        private bool IsRateLimitError(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                return httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
            }
            return false;
        }

        public async IAsyncEnumerable<string> StreamGenerateContentAsync(
            string? prompt,
            string? modelName,
            List<ChatMessage>? history = null,
            List<ImageData>? images = null,
            List<DocumentData>? documents = null)
        {
            HttpResponseMessage response;
            try
            {
                var finalModel = ResolveModelName(modelName);
                var apiKey = GetNextApiKey();
                var requestUrl = $"{_baseUrl}/models/{finalModel}:streamGenerateContent?alt=sse&key={apiKey}";

                var contents = new List<object>();

                // 历史记录
                if (history != null)
                {
                    foreach (var msg in history)
                    {
                        contents.Add(new
                        {
                            role = msg.Role,
                            parts = new[]
                            {
                                new { text = msg.Content }
                            }
                        });
                    }
                }

                // 构建用户请求部分
                var partsList = new List<object>();

                // 图片 inline_data
                if (images != null && images.Count > 0)
                {
                    foreach (var img in images)
                    {
                        partsList.Add(new
                        {
                            inline_data = new
                            {
                                mime_type = img.MimeType,
                                data = img.Data
                            }
                        });
                    }
                }

                // 文档 file_data
                // 将多个文档全部列在此 parts 中
                if (documents != null && documents.Count > 0)
                {
                    foreach (var doc in documents)
                    {
                        partsList.Add(new
                        {
                            file_data = new
                            {
                                mime_type = doc.MimeType,
                                file_uri = doc.FileUri
                            }
                        });
                    }
                }

                // 文本提示
                if (!string.IsNullOrEmpty(prompt))
                {
                    partsList.Add(new
                    {
                        text = prompt
                    });
                }

                contents.Add(new
                {
                    role = "user",
                    parts = partsList.ToArray()
                });

                var requestBody = new { contents = contents.ToArray() };
                var json = JsonSerializer.Serialize(requestBody);
                var requestContent = new StringContent(json, Encoding.UTF8, "application/json");

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = requestContent
                };

                response = await ExecuteWithRetryAsync(async () => 
                {
                    var resp = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    return resp;
                }, "StreamGenerateContent");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in StreamGenerateContentAsync");
                throw new GeminiException("Failed to generate content", ex);
            }

            using (response)
            {
                await foreach (var chunk in ProcessStreamAsync(response))
                {
                    yield return chunk;
                }
            }
        }

        private async IAsyncEnumerable<string> ProcessStreamAsync(HttpResponseMessage response)
        {
            using var responseStream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(responseStream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:")) continue;

                var jsonLine = line["data:".Length..].Trim();
                if (string.IsNullOrEmpty(jsonLine)) continue;

                using var doc = JsonDocument.Parse(jsonLine);
                if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
                    candidates.ValueKind != JsonValueKind.Array ||
                    candidates.GetArrayLength() == 0) continue;

                var firstCandidate = candidates[0];
                if (!firstCandidate.TryGetProperty("content", out var contentObj) ||
                    !contentObj.TryGetProperty("parts", out var parts) ||
                    parts.ValueKind != JsonValueKind.Array) continue;

                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textElement))
                    {
                        var textChunk = textElement.GetString();
                        if (!string.IsNullOrEmpty(textChunk))
                        {
                            yield return textChunk;
                        }
                    }
                }
            }
        }

        // 上传文档到 File API
        public async Task<string> UploadDocumentAsync(Stream fileStream, string fileName, string mimeType, string displayName)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var apiKey = GetNextApiKey();
                    // Step 1: start resumable upload
                    var metadata = new
                    {
                        file = new
                        {
                            display_name = string.IsNullOrEmpty(displayName) ? fileName : displayName
                        }
                    };
                    var metadataJson = JsonSerializer.Serialize(metadata);
                    fileStream.Seek(0, SeekOrigin.End);
                    long fileSize = fileStream.Position;
                    fileStream.Seek(0, SeekOrigin.Begin);

                    var startRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/upload/v1beta/files?key={apiKey}")
                    {
                        Content = new StringContent(metadataJson, Encoding.UTF8, "application/json")
                    };
                    startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                    startRequest.Headers.Add("X-Goog-Upload-Command", "start");
                    startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", fileSize.ToString());
                    startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);

                    using var startResponse = await _httpClient.SendAsync(startRequest);
                    startResponse.EnsureSuccessStatusCode();

                    // 从响应头中获取上传URL
                    if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
                    {
                        throw new InvalidOperationException("Upload URL not found in response headers");
                    }
                    var uploadUrl = uploadUrls.First();

                    // Step 2: Upload file bytes
                    var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
                    {
                        Content = new StreamContent(fileStream)
                    };
                    uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
                    uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
                    uploadRequest.Headers.Add("Content-Length", fileSize.ToString());

                    var uploadResponse = await _httpClient.SendAsync(uploadRequest);
                    uploadResponse.EnsureSuccessStatusCode();

                    var jsonResponse = await uploadResponse.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonResponse);
                    if (!doc.RootElement.TryGetProperty("file", out var fileObj))
                    {
                        throw new InvalidOperationException("File information not found in upload response.");
                    }
                    if (!fileObj.TryGetProperty("uri", out var uriElem))
                    {
                        throw new InvalidOperationException("file_uri not found in response.");
                    }

                    return uriElem.GetString() ?? throw new InvalidOperationException("file_uri is null or empty");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error uploading document: {FileName}", fileName);
                    throw new GeminiException($"Failed to upload document {fileName}", ex);
                }
            }, $"UploadDocument_{fileName}");
        }

        private class ModelConfig
        {
            public string FallbackModel { get; set; } = string.Empty;
        }
    }

    public class ChatMessage
    {
        public string Role { get; set; } = string.Empty; // "user" or "model"
        public string Content { get; set; } = string.Empty;
    }

    public class ImageData
    {
        public string MimeType { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty; // Base64 only
    }

    public class DocumentData
    {
        public string MimeType { get; set; } = string.Empty; // application/pdf, text/plain, etc.
        public string FileUri { get; set; } = string.Empty;  // obtained from /upload-doc
    }

    public class GeminiException : Exception
    {
        public GeminiException(string message) : base(message) { }
        public GeminiException(string message, Exception inner) : base(message, inner) { }
    }

    public class GeminiRateLimitException : GeminiException
    {
        public GeminiRateLimitException(string message) : base(message) { }
    }
}
