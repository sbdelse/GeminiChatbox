using System.Text.Json;
using Microsoft.Extensions.Options;
using System.Text;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Threading.Channels;
using System.Collections.Concurrent;

namespace GeminiFreeSearch.Services
{
    public class MeetingMinutesService
    {
        private readonly ILogger<MeetingMinutesService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly GeminiService _geminiService;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly long _maxFileSize;
        private readonly long _chunkSize;
        private const int MaxRetries = 3;
        private static readonly TimeSpan[] RetryDelays = {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4)
        };
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _processLocks = new();

        public MeetingMinutesService(
            IHttpClientFactory httpClientFactory,
            ILogger<MeetingMinutesService> logger,
            IConfiguration configuration,
            GeminiService geminiService)
        {
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
            _configuration = configuration;
            _geminiService = geminiService;

            _httpClient.Timeout = TimeSpan.FromMinutes(10);

            _baseUrl = configuration["MeetingMinutes:BaseUrl"] ?? throw new ArgumentNullException(nameof(configuration));
            var apiKeys = configuration.GetSection("MeetingMinutes:ApiKeys").Get<string[]>() ?? throw new ArgumentNullException(nameof(configuration));
            _apiKey = apiKeys[0]; // 使用第一个API密钥
            
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var section = configuration.GetSection("MeetingMinutes");
            _model = section["Model"] ?? throw new InvalidOperationException("MeetingMinutes:Model not configured");
            _maxFileSize = section.GetValue<long>("MaxFileSize");
            _chunkSize = section.GetValue<long>("ChunkSize");
        }

        public async IAsyncEnumerable<string> ProcessAudioFileAsync(
            Stream audioStream,
            string fileName)
        {
            var processLock = _processLocks.GetOrAdd(fileName, _ => new SemaphoreSlim(1, 1));
            
            if (!await processLock.WaitAsync(0))
            {
                yield return "[ERROR]该文件正在处理中，请勿重复提交";
                yield break;
            }

            try
            {
                var fullText = new StringBuilder();
                var pendingTasks = new List<Task>();
                var resultChannel = Channel.CreateUnbounded<string>();
                var semaphore = new SemaphoreSlim(3);

                yield return "[STATUS]正在处理音频文件...";

                if (audioStream.Length > _maxFileSize)
                {
                    yield return "[STATUS]文件较大，正在分片处理...";

                    var processingTask = Task.Run(async () =>
                    {
                        await foreach (var chunk in CompressAndSplitAudioAsync(audioStream, fileName))
                        {
                            await semaphore.WaitAsync();
                            pendingTasks.Add(ProcessChunkAsync(chunk, semaphore, resultChannel.Writer, fullText));
                        }
                        
                        await Task.WhenAll(pendingTasks);
                        resultChannel.Writer.Complete();
                    });

                    await foreach (var result in resultChannel.Reader.ReadAllAsync())
                    {
                        yield return $"[TRANSCRIPTION]{result}";
                    }

                    if (fullText.Length > 0)
                    {
                        yield return "[STATUS]所有分片处理完成，正在进行AI分析...";
                        var analysis = new StringBuilder();
                        var analysisResult = "";
                        
                        var analysisPrompt = $"请对以下会议内容进行精要总结，去掉语气词，将口语化表达转化为书面表达，并给出会议纪要。输出形式以【精要总结】{{content}}，【会议纪要】{{content}}。\n\n{fullText}";
                        
                        await foreach (var chunk in _geminiService.StreamGenerateContentAsync(
                            analysisPrompt,
                            "gemini-2.0-flash-lite-preview-02-05"))
                        {
                            analysis.Append(chunk);
                            analysisResult = analysis.ToString();
                            yield return $"[ANALYSIS]{analysisResult}";
                        }
                    }
                }
                else
                {
                    string? text = null;
                    try
                    {
                        text = await TranscribeAudioChunkWithRetryAsync(audioStream);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Transcription error for file: {FileName}", fileName);
                        yield break;
                    }
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return $"[TRANSCRIPTION]{text}";
                        
                        yield return "[STATUS]正在进行AI分析...";
                        var analysis = new StringBuilder();
                        await foreach (var chunk in _geminiService.StreamGenerateContentAsync(
                            $"请对以下会议内容进行总结：\n\n{text}",
                            "gemini-2.0-flash-lite-preview-02-05"))
                        {
                            analysis.Append(chunk);
                            yield return $"[ANALYSIS]{analysis}";
                        }
                    }
                }
            }
            finally
            {
                processLock.Release();
                _processLocks.TryRemove(fileName, out _);
            }
        }

        private async IAsyncEnumerable<Stream> CompressAndSplitAudioAsync(
            Stream audioStream,
            string fileName)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var outputDir = Path.Combine(tempDir, "output_segments");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(outputDir);

            try
            {
                var originalPath = Path.Combine(tempDir, fileName);
                using (var fileStream = File.Create(originalPath))
                {
                    await audioStream.CopyToAsync(fileStream);
                }

                var arguments = $"-i \"{originalPath}\" -c:a libopus -b:a 22k -f segment -segment_time 600 " +
                               $"-reset_timestamps 1 \"{Path.Combine(outputDir, "output_%03d.opus")}\"";

                _logger.LogInformation("Starting FFmpeg with arguments: {Arguments}", arguments);
                
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = arguments,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                var processedFiles = new HashSet<string>();
                process.Start();

                while (!process.HasExited || Directory.GetFiles(outputDir, "*.opus").Length > processedFiles.Count)
                {
                    var files = Directory.GetFiles(outputDir, "*.opus").OrderBy(f => f).ToList();
                    _logger.LogInformation("Found {Count} files, processed {Processed}", 
                        files.Count, processedFiles.Count);

                    foreach (var file in files)
                    {
                        if (!processedFiles.Contains(file))
                        {
                            byte[]? bytes = await ReadFileWithRetry(file);
                            if (bytes != null && bytes.Length > 0)
                            {
                                processedFiles.Add(file);
                                _logger.LogInformation("Successfully read file: {File}, size: {Size} bytes", 
                                    Path.GetFileName(file), bytes.Length);
                                yield return new MemoryStream(bytes);
                            }
                            else
                            {
                                _logger.LogWarning("Failed to read file or file is empty: {File}", file);
                            }
                        }
                    }
                    await Task.Delay(1000);
                }

                if (!process.HasExited)
                {
                    _logger.LogWarning("FFmpeg process still running, killing it");
                    process.Kill();
                }
            }
            finally
            {
                await CleanupTempDirectory(tempDir);
            }
        }

        private async Task ProcessChunkAsync(
            Stream chunk, 
            SemaphoreSlim semaphore,
            ChannelWriter<string> resultWriter,
            StringBuilder fullText)
        {
            try
            {
                _logger.LogInformation("Starting to process new chunk");
                
                // 先将chunk完整读入内存
                byte[] chunkData;
                using (var memoryStream = new MemoryStream())
                {
                    await chunk.CopyToAsync(memoryStream);
                    chunkData = memoryStream.ToArray();
                }
                
                _logger.LogInformation("Chunk data size: {Size} bytes", chunkData.Length);
                
                if (chunkData.Length == 0)
                {
                    _logger.LogError("Received empty chunk data!");
                    return;
                }

                using var chunkStream = new MemoryStream(chunkData);
                var chunkText = await TranscribeAudioChunkWithRetryAsync(chunkStream);
                
                if (!string.IsNullOrEmpty(chunkText))
                {
                    lock (fullText)
                    {
                        fullText.Append(chunkText).Append('\n');
                    }
                    await resultWriter.WriteAsync(chunkText);
                }
                else
                {
                    _logger.LogWarning("Empty transcription result received");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing chunk");
                throw;
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task<string> TranscribeAudioAsync(Stream audioStream)
        {
            if (!audioStream.CanRead)
            {
                throw new InvalidOperationException("Audio stream is not readable");
            }

            using var content = new MultipartFormDataContent();
            
            // 直接使用传入的流，不再复制到新的MemoryStream
            using var streamContent = new StreamContent(audioStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/opus");
            content.Add(streamContent, "file", "audio.opus");
            content.Add(new StringContent("FunAudioLLM/SenseVoiceSmall"), "model");

            _logger.LogInformation("Stream position: {Position}, Length: {Length}", 
                audioStream.Position, audioStream.Length);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/transcriptions")
            {
                Content = content
            };

            var response = await _httpClient.SendAsync(request);
            _logger.LogInformation("Response status code: {StatusCode}", response.StatusCode);
            
            var jsonResponse = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Raw response: {Response}", jsonResponse);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Request failed with status {StatusCode}: {Response}", 
                    response.StatusCode, jsonResponse);
                throw new Exception($"Failed with status {response.StatusCode}: {jsonResponse}");
            }

            var result = JsonSerializer.Deserialize<TranscriptionResponse>(jsonResponse);
            _logger.LogInformation("Transcription successful, text length: {Length}", 
                result?.Text?.Length ?? 0);
            return result?.Text ?? string.Empty;
        }

        private async Task<string> TranscribeAudioChunkWithRetryAsync(Stream audioChunk)
        {
            Exception? lastException = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    audioChunk.Position = 0;
                    return await TranscribeAudioAsync(audioChunk);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogError(ex, "Transcription attempt {Attempt} failed", attempt + 1);
                    if (attempt == MaxRetries - 1) break;
                    await Task.Delay(RetryDelays[attempt]);
                }
            }

            throw lastException ?? new Exception("Transcription failed after all retries");
        }

        private async Task<byte[]?> ReadFileWithRetry(string path, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    await WaitForFile(path);
                    
                    _logger.LogInformation("Reading file: {Path}, attempt {Attempt}", path, i + 1);
                    
                    using var stream = new FileStream(
                        path, 
                        FileMode.Open, 
                        FileAccess.Read, 
                        FileShare.ReadWrite
                    );
                    
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length == 0)
                    {
                        _logger.LogWarning("File is empty, waiting for content...");
                        await Task.Delay(1000 * (i + 1));
                        continue;
                    }

                    using var memoryStream = new MemoryStream();
                    await stream.CopyToAsync(memoryStream);
                    var bytes = memoryStream.ToArray();
                    
                    _logger.LogInformation("Successfully read {Count} bytes from file", bytes.Length);
                    return bytes;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to read file on attempt {Attempt}", i + 1);
                    if (i < maxRetries - 1)
                    {
                        await Task.Delay(1000 * (i + 1));
                    }
                }
            }
            return null;
        }

        private async Task WaitForFile(string path, int timeoutSeconds = 30)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                try
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Exists && !IsFileLocked(fileInfo))
                    {
                        _logger.LogInformation("File is ready: {Path}", path);
                        return;
                    }
                }
                catch (IOException)
                {
                    // File might be in use
                }
                await Task.Delay(100);
            }
            throw new TimeoutException($"Timeout waiting for file: {path}");
        }

        private static bool IsFileLocked(FileInfo file)
        {
            try
            {
                using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private static async Task DeleteFileWithRetry(string path, int maxRetries = 5)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    await Task.Delay(100 * (i + 1));
                }
            }
        }

        private async Task CleanupTempDirectory(string tempDir)
        {
            const int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        // Delete all files first
                        foreach (var file in Directory.GetFiles(tempDir))
                        {
                            await DeleteFileWithRetry(file);
                        }
                        Directory.Delete(tempDir, true);
                    }
                    return;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    await Task.Delay(2000 * (i + 1));
                }
                catch (UnauthorizedAccessException) when (i < maxRetries - 1)
                {
                    await Task.Delay(2000 * (i + 1));
                }
            }
            _logger.LogWarning("Failed to cleanup temp directory: {TempDir}", tempDir);
        }

        private class TranscriptionResponse
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;
        }
    }
} 