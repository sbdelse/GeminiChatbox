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
                _currentKeyIndex = (_currentKeyIndex + 1) % _apiKeys.Length;
                return _apiKeys[_currentKeyIndex];
            }
        }

        private string GetCurrentApiKey()
        {
            lock (_lockObject)
            {
                return _apiKeys[_currentKeyIndex];
            }
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, string operationName)
        {
            var triedKeys = new HashSet<string>();
            var lastKey = string.Empty;

            while (triedKeys.Count < _apiKeys.Length)
            {
                try
                {
                    return await action();
                }
                catch (HttpRequestException ex) when (IsTransientError(ex))
                {
                    if (triedKeys.Count == _apiKeys.Length - 1) throw;
                    
                    var delay = RetryDelays[Math.Min(triedKeys.Count, RetryDelays.Length - 1)];
                    _logger.LogWarning(ex, 
                        "Attempt failed for {Operation}. Retrying in {Delay} seconds", 
                        operationName, delay.TotalSeconds);
                    
                    await Task.Delay(delay);
                }
                catch (Exception ex) when (IsTimeoutError(ex))
                {
                    // For timeout errors, switch to the next API key
                    var currentKey = GetCurrentApiKey();
                    if (!triedKeys.Contains(currentKey))
                    {
                        triedKeys.Add(currentKey);
                    }

                    if (triedKeys.Count >= _apiKeys.Length)
                    {
                        _logger.LogError(ex, "All API keys failed with timeout error for {Operation}", operationName);
                        throw new GeminiException($"All API keys failed with timeout error. The request may be too large or the server is overloaded.");
                    }

                    _logger.LogWarning(ex, "Timeout error for key {KeyIndex}, switching to next key", _currentKeyIndex);
                    while (triedKeys.Contains(GetCurrentApiKey()))
                    {
                        GetNextApiKey();
                    }

                    continue;
                }
                catch (HttpRequestException ex) when (IsModelError(ex))
                {
                    // 对于模型错误（如400 Bad Request），切换到下一个API密钥
                    var currentKey = GetCurrentApiKey();
                    if (!triedKeys.Contains(currentKey))
                    {
                        triedKeys.Add(currentKey);
                    }

                    if (triedKeys.Count >= _apiKeys.Length)
                    {
                        _logger.LogError(ex, "All API keys failed with model error for {Operation}", operationName);
                        throw new GeminiException($"All API keys failed with model error. The model may be invalid or the request format is incorrect.");
                    }

                    _logger.LogWarning("Model error for key {KeyIndex}, switching to next key", _currentKeyIndex);
                    while (triedKeys.Contains(GetCurrentApiKey()))
                    {
                        GetNextApiKey();
                    }

                    continue;
                }
                catch (Exception ex) when (IsRateLimitError(ex))
                {
                    var currentKey = GetCurrentApiKey();
                    if (!triedKeys.Contains(currentKey))
                    {
                        triedKeys.Add(currentKey);
                    }

                    if (triedKeys.Count >= _apiKeys.Length)
                    {
                        _logger.LogError(ex, "All API keys have reached rate limit for {Operation}", operationName);
                        throw new GeminiRateLimitException("All API keys have reached rate limit. Please try again later.");
                    }

                    _logger.LogWarning("Rate limit reached for key {KeyIndex}, switching to next key", _currentKeyIndex);
                    while (triedKeys.Contains(GetCurrentApiKey()))
                    {
                        GetNextApiKey();
                    }

                    continue;
                }
            }
            
            throw new GeminiException($"Operation {operationName} failed after trying all API keys");
        }

        private bool IsTransientError(HttpRequestException ex)
        {
            return ex.StatusCode is 
                System.Net.HttpStatusCode.ServiceUnavailable or 
                System.Net.HttpStatusCode.GatewayTimeout or
                System.Net.HttpStatusCode.RequestTimeout;
        }

        private bool IsModelError(HttpRequestException ex)
        {
            return ex.StatusCode == System.Net.HttpStatusCode.BadRequest;
        }

        private bool IsRateLimitError(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                return httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
            }
            return false;
        }

        public bool IsTimeoutError(Exception ex)
        {
            // Check for TaskCanceledException due to timeout
            if (ex is TaskCanceledException || 
                ex is OperationCanceledException || 
                (ex is HttpRequestException && ex.InnerException is TaskCanceledException) ||
                (ex is HttpRequestException && ex.InnerException is TimeoutException) ||
                ex is TimeoutException)
            {
                return true;
            }
            
            // Check for "The request was canceled due to the configured HttpClient.Timeout" message
            if (ex.Message.Contains("HttpClient.Timeout") || 
                (ex.InnerException?.Message?.Contains("HttpClient.Timeout") == true))
            {
                return true;
            }
            
            return false;
        }

        public async Task StreamGenerateContentAsync(
            string? prompt,
            string? modelName,
            Func<string, Task> onChunkReceived,
            List<ChatMessage>? history = null,
            List<ImageData>? images = null,
            List<DocumentData>? documents = null)
        {
            var currentModel = modelName;
            var triedModels = new HashSet<string>();
            var errorMessages = new List<string>();

            while (true)
            {
                string finalModel;
                try
                {
                    finalModel = ResolveModelName(currentModel);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error resolving model name for {ModelName}", currentModel);
                    await onChunkReceived($"[Error] 无法解析模型名称 {currentModel}: {ex.Message}");
                    return;
                }

                if (triedModels.Contains(finalModel))
                {
                    await onChunkReceived($"[Error] 所有可用模型均已尝试失败。错误信息：\n{string.Join("\n", errorMessages)}");
                    return;
                }

                triedModels.Add(finalModel);
                
                if (triedModels.Count > 1)
                {
                    await onChunkReceived($"[System] 正在使用备用模型 {finalModel} 重试请求...\n");
                }

                // Try all API keys with current model
                var triedKeys = new HashSet<string>();
                bool modelSucceeded = false;
                
                while (triedKeys.Count < _apiKeys.Length && !modelSucceeded)
                {
                    var currentKey = GetCurrentApiKey();
                    triedKeys.Add(currentKey);
                    
                    try
                    {
                        var (success, response, error) = await TryExecuteStreamRequest(
                            finalModel, prompt, history, images, documents);

                        if (success && response != null)
                        {
                            try
                            {
                                await ProcessStreamResponseWithCallback(response, onChunkReceived);
                                return; // Success, we're done
                            }
                            catch (Exception ex) when (IsTimeoutError(ex))
                            {
                                _logger.LogError(ex, "Timeout error processing stream response from model {Model}", finalModel);
                                errorMessages.Add($"处理 {finalModel} 响应时超时: {ex.Message}");
                                // Continue to next key or model
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error processing stream response from model {Model}", finalModel);
                                errorMessages.Add($"处理 {finalModel} 响应时出错: {ex.Message}");
                                // Continue to next key or model
                            }
                        }
                        else
                        {
                            errorMessages.Add(error);
                        }
                    }
                    catch (GeminiException ex)
                    {
                        _logger.LogError(ex, "GeminiException with model {Model}", finalModel);
                        errorMessages.Add($"模型 {finalModel} 错误: {ex.Message}");
                    }
                    catch (Exception ex) when (IsTimeoutError(ex))
                    {
                        _logger.LogError(ex, "Timeout error with model {Model}", finalModel);
                        errorMessages.Add($"模型 {finalModel} 请求超时: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error with model {Model}", finalModel);
                        errorMessages.Add($"模型 {finalModel} 意外错误: {ex.Message}");
                    }
                    
                    // Try next key if available
                    if (triedKeys.Count < _apiKeys.Length)
                    {
                        GetNextApiKey();
                        await onChunkReceived($"[System] 正在使用下一个API密钥重试请求...\n");
                    }
                }

                // If all keys failed with current model, try fallback model
                try
                {
                    if (!TryGetFallbackModel(currentModel, out var fallbackModel) || 
                        string.IsNullOrEmpty(fallbackModel))
                    {
                        await onChunkReceived($"[Error] {string.Join("\n", errorMessages)}\n没有可用的备用模型。");
                        return;
                    }
                    
                    currentModel = fallbackModel;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting fallback model for {Model}", currentModel);
                    await onChunkReceived($"[Error] {string.Join("\n", errorMessages)}\n获取备用模型时出错: {ex.Message}");
                    return;
                }
            }
        }

        private async Task ProcessStreamResponseWithCallback(
            HttpResponseMessage response, 
            Func<string, Task> onChunkReceived)
        {
            ArgumentNullException.ThrowIfNull(response);

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
                            await onChunkReceived(textChunk);
                        }
                    }
                }
            }
        }

        private async Task<(bool success, HttpResponseMessage? response, string error)> TryExecuteStreamRequest(
            string modelName,
            string? prompt,
            List<ChatMessage>? history,
            List<ImageData>? images,
            List<DocumentData>? documents)
        {
            try 
            {
                var response = await ExecuteStreamRequest(modelName, prompt, history, images, documents);
                return (true, response, string.Empty);
            }
            catch (HttpRequestException ex)
            {
                var statusCode = ex.StatusCode;
                var error = $"模型 {modelName} 请求失败 (HTTP {(int?)statusCode}): {ex.Message}";
                _logger.LogError(ex, error);
                return (false, null, error);
            }
            catch (Exception ex) when (IsTimeoutError(ex))
            {
                var error = $"模型 {modelName} 请求超时: {ex.Message}";
                _logger.LogError(ex, error);
                return (false, null, error);
            }
            catch (Exception ex)
            {
                var error = $"模型 {modelName} 发生意外错误: {ex.Message}";
                _logger.LogError(ex, error);
                return (false, null, error);
            }
        }

        private async Task<HttpResponseMessage> ExecuteStreamRequest(
            string modelName,
            string? prompt,
            List<ChatMessage>? history,
            List<ImageData>? images,
            List<DocumentData>? documents)
        {
            var apiKey = GetNextApiKey();
            var requestUrl = $"{_baseUrl}/models/{modelName}:streamGenerateContent?alt=sse&key={apiKey}";

            var contents = new List<object>();

            // Add history if present
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

            // Build user request parts
            var partsList = new List<object>();

            // Add images if present
            if (images?.Count > 0)
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

            // Add documents if present
            if (documents?.Count > 0)
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

            // Add text prompt if present
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

            return await ExecuteWithRetryAsync(async () =>
            {
                using var newRequestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = requestContent
                };
                
                var resp = await _httpClient.SendAsync(
                    newRequestMessage, 
                    HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                return resp;
            }, "StreamGenerateContent");
        }

        private bool TryGetFallbackModel(string? currentModel, out string fallbackModel)
        {
            fallbackModel = string.Empty;
            
            if (string.IsNullOrEmpty(currentModel))
            {
                return false;
            }

            // 检查当前模型是否有直接的 fallback
            if (_models.TryGetValue(currentModel, out var config) && 
                !string.IsNullOrEmpty(config.FallbackModel))
            {
                fallbackModel = config.FallbackModel;
                return true;
            }

            return false;
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

        // 自定义异步迭代器类，用于处理流式内容
        private class StreamContentAsyncEnumerator : IAsyncEnumerable<string>, IAsyncEnumerator<string>
        {
            private readonly GeminiService _service;
            private readonly string? _prompt;
            private readonly string? _modelName;
            private readonly List<ChatMessage>? _history;
            private readonly List<ImageData>? _images;
            private readonly List<DocumentData>? _documents;
            private readonly Queue<string> _buffer = new Queue<string>();
            private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(0);
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private bool _completed = false;
            private Exception? _exception = null;
            private string _current = string.Empty;

            public StreamContentAsyncEnumerator(
                GeminiService service,
                string? prompt,
                string? modelName,
                List<ChatMessage>? history,
                List<ImageData>? images,
                List<DocumentData>? documents)
            {
                _service = service;
                _prompt = prompt;
                _modelName = modelName;
                _history = history;
                _images = images;
                _documents = documents;

                // 启动处理任务
                _ = ProcessAsync();
            }

            private async Task ProcessAsync()
            {
                try
                {
                    await ProcessStreamContentAsync();
                }
                catch (Exception ex)
                {
                    _exception = ex;
                }
                finally
                {
                    _completed = true;
                    _semaphore.Release(); // 确保等待的MoveNextAsync可以继续
                }
            }

            private async Task ProcessStreamContentAsync()
            {
                var currentModel = _modelName;
                var triedModels = new HashSet<string>();
                var errorMessages = new List<string>();

                while (true)
                {
                    // 1. Resolve model name
                    string finalModel;
                    try
                    {
                        finalModel = _service.ResolveModelName(currentModel);
                    }
                    catch (Exception ex)
                    {
                        _service._logger.LogError(ex, "Error resolving model name for {ModelName}", currentModel);
                        AddChunk($"[Error] 无法解析模型名称 {currentModel}: {ex.Message}");
                        return;
                    }

                    if (triedModels.Contains(finalModel))
                    {
                        AddChunk($"[Error] 所有可用模型均已尝试失败。错误信息：\n{string.Join("\n", errorMessages)}");
                        return;
                    }

                    triedModels.Add(finalModel);
                    
                    if (triedModels.Count > 1)
                    {
                        AddChunk($"[System] 正在使用备用模型 {finalModel} 重试请求...\n");
                    }

                    // 2. Try all API keys with current model
                    var triedKeys = new HashSet<string>();
                    bool modelSucceeded = false;
                    
                    while (triedKeys.Count < _service._apiKeys.Length && !modelSucceeded)
                    {
                        var currentKey = _service.GetCurrentApiKey();
                        triedKeys.Add(currentKey);
                        
                        try
                        {
                            var result = await _service.TryExecuteStreamRequest(
                                finalModel, _prompt, _history, _images, _documents);

                            if (result.success && result.response != null)
                            {
                                try
                                {
                                    await ProcessResponseStream(result.response);
                                    return; // Success, we're done
                                }
                                catch (Exception ex) when (_service.IsTimeoutError(ex))
                                {
                                    _service._logger.LogError(ex, "Timeout error processing stream response from model {Model}", finalModel);
                                    errorMessages.Add($"处理 {finalModel} 响应时超时: {ex.Message}");
                                    // Continue to next key or model
                                }
                                catch (Exception ex)
                                {
                                    _service._logger.LogError(ex, "Error processing stream response from model {Model}", finalModel);
                                    errorMessages.Add($"处理 {finalModel} 响应时出错: {ex.Message}");
                                    // Continue to next key or model
                                }
                            }
                            else
                            {
                                errorMessages.Add(result.error);
                            }
                        }
                        catch (GeminiException ex)
                        {
                            _service._logger.LogError(ex, "GeminiException with model {Model}", finalModel);
                            errorMessages.Add($"模型 {finalModel} 错误: {ex.Message}");
                        }
                        catch (Exception ex) when (_service.IsTimeoutError(ex))
                        {
                            _service._logger.LogError(ex, "Timeout error with model {Model}", finalModel);
                            errorMessages.Add($"模型 {finalModel} 请求超时: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            _service._logger.LogError(ex, "Unexpected error with model {Model}", finalModel);
                            errorMessages.Add($"模型 {finalModel} 意外错误: {ex.Message}");
                        }
                        
                        // Try next key if available
                        if (triedKeys.Count < _service._apiKeys.Length)
                        {
                            _service.GetNextApiKey();
                            AddChunk($"[System] 正在使用下一个API密钥重试请求...\n");
                        }
                    }

                    // 3. If all keys failed with current model, try fallback model
                    try
                    {
                        if (!_service.TryGetFallbackModel(currentModel, out var fallbackModel) || 
                            string.IsNullOrEmpty(fallbackModel))
                        {
                            AddChunk($"[Error] {string.Join("\n", errorMessages)}\n没有可用的备用模型。");
                            return;
                        }
                        
                        currentModel = fallbackModel;
                    }
                    catch (Exception ex)
                    {
                        _service._logger.LogError(ex, "Error getting fallback model for {Model}", currentModel);
                        AddChunk($"[Error] {string.Join("\n", errorMessages)}\n获取备用模型时出错: {ex.Message}");
                        return;
                    }
                }
            }

            private async Task ProcessResponseStream(HttpResponseMessage response)
            {
                try
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
                                    AddChunk(textChunk);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) when (_service.IsTimeoutError(ex))
                {
                    _service._logger.LogError(ex, "Timeout error while processing response stream");
                    throw new GeminiException("请求处理超时，可能是因为响应过大或网络不稳定", ex);
                }
                catch (Exception ex)
                {
                    _service._logger.LogError(ex, "Error processing response stream");
                    throw;
                }
            }

            private void AddChunk(string chunk)
            {
                lock (_buffer)
                {
                    _buffer.Enqueue(chunk);
                }
                _semaphore.Release();
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                while (true)
                {
                    // 检查缓冲区中是否有数据
                    lock (_buffer)
                    {
                        if (_buffer.Count > 0)
                        {
                            _current = _buffer.Dequeue();
                            return true;
                        }

                        if (_completed)
                        {
                            if (_exception != null)
                            {
                                throw _exception;
                            }
                            return false;
                        }
                    }

                    // 等待新数据或完成信号
                    await _semaphore.WaitAsync();
                }
            }

            public string Current => _current;

            public ValueTask DisposeAsync()
            {
                _cts.Cancel();
                _semaphore.Dispose();
                _cts.Dispose();
                return ValueTask.CompletedTask;
            }

            public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return this;
            }
        }

        public IAsyncEnumerable<string> StreamGenerateContentAsync(
            string? prompt,
            string? modelName,
            List<ChatMessage>? history = null,
            List<ImageData>? images = null,
            List<DocumentData>? documents = null)
        {
            return new StreamContentAsyncEnumerator(
                this, prompt, modelName, history, images, documents);
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