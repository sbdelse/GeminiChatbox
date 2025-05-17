using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GeminiFreeSearch.Services
{
    // Helper classes for API response deserialization
    public class ApiModelResponse_FromTool
    {
        public List<ApiModelData_FromTool> Data { get; set; } = default!;
    }

    public class ApiModelData_FromTool
    {
        public string Id { get; set; } = default!;
    }

    // Static class to encapsulate the model update logic
    public static class ApplicationInitializer
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private const string AppSettingsPath = "appsettings.json";
        private const string ApiUrl = "https://generativelanguage.googleapis.com/v1beta/openai/models";

        public static async Task UpdateModelsFromApiAsync()
        {
            Console.WriteLine("Attempting to update models from API before application startup...");
            try
            {
                if (!File.Exists(AppSettingsPath))
                {
                    Console.WriteLine($"Error: {AppSettingsPath} not found. Skipping model update.");
                    return;
                }

                string currentAppSettingsJson = await File.ReadAllTextAsync(AppSettingsPath);
                var appSettingsNode = JsonNode.Parse(currentAppSettingsJson);

                if (appSettingsNode == null)
                {
                    Console.WriteLine($"Error: Could not parse {AppSettingsPath}. Skipping model update.");
                    return;
                }

                var geminiApiNode = appSettingsNode["GeminiApi"];
                if (geminiApiNode == null)
                {
                    Console.WriteLine($"Error: 'GeminiApi' section not found in {AppSettingsPath}. Skipping model update.");
                    return;
                }

                var premiumApiKeysNode = geminiApiNode["PremiumApiKeys"] as JsonArray;
                if (premiumApiKeysNode == null || premiumApiKeysNode.Count == 0)
                {
                    Console.WriteLine($"Error: 'PremiumApiKeys' not found or is empty in 'GeminiApi' section of {AppSettingsPath}. Skipping model update.");
                    return;
                }

                var firstApiKeyNode = premiumApiKeysNode[0];
                if (firstApiKeyNode == null)
                {
                    Console.WriteLine($"Error: First PremiumApiKey is null in {AppSettingsPath}. Skipping model update.");
                    return;
                }

                string apiKey = firstApiKeyNode.GetValue<string>();
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine($"Error: First PremiumApiKey is null or empty in {AppSettingsPath}. Skipping model update.");
                    return;
                }
                
                Console.WriteLine($"Using API Key starting with: {apiKey.Substring(0, Math.Min(apiKey.Length, 7))}...");

                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                HttpResponseMessage response = await httpClient.GetAsync(ApiUrl);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Error fetching models from API: {response.StatusCode}. Details: {errorContent}. Skipping model update.");
                    return;
                }

                string apiResponseJson = await response.Content.ReadAsStringAsync();
                var apiModels = JsonSerializer.Deserialize<ApiModelResponse_FromTool>(apiResponseJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (apiModels == null || apiModels.Data == null)
                {
                    Console.WriteLine("Error: Could not deserialize API response or no model data found. Skipping model update.");
                    return;
                }
                
                Console.WriteLine($"Fetched {apiModels.Data.Count} models from API.");

                var existingModelsNode = geminiApiNode["Models"] as JsonObject;
                var newModelsJsonObject = new JsonObject();

                foreach (var modelData in apiModels.Data)
                {
                    if (string.IsNullOrEmpty(modelData.Id) || !modelData.Id.StartsWith("models/"))
                    {
                        Console.WriteLine($"Warning: Skipping invalid or malformed model ID from API: '{modelData.Id}'");
                        continue;
                    }
                    string modelName = modelData.Id.Substring("models/".Length);

                    if (!modelName.StartsWith("gemini-"))
                    {
                        Console.WriteLine($"Skipping model '{modelName}' as it does not start with 'gemini-'.");
                        continue;
                    }

                    if (existingModelsNode != null && existingModelsNode.ContainsKey(modelName) && existingModelsNode[modelName] is JsonObject existingModelEntry)
                    {
                        newModelsJsonObject[modelName] = existingModelEntry.DeepClone();
                    }
                    else
                    {
                        newModelsJsonObject[modelName] = new JsonObject
                        {
                            ["RPM"] = 0,
                            ["TPM"] = 0,
                            ["RPD"] = 0,
                            ["FallbackModel"] = modelName
                        };
                        Console.WriteLine($"Adding new model '{modelName}' with default config to local settings.");
                    }
                }
                
                geminiApiNode["Models"] = newModelsJsonObject;

                var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
                string updatedAppSettingsJson = appSettingsNode.ToJsonString(serializerOptions);
                await File.WriteAllTextAsync(AppSettingsPath, updatedAppSettingsJson);

                Console.WriteLine($"{AppSettingsPath} updated successfully with the new model list from the API.");
                Console.WriteLine("Model update process finished. Proceeding with application startup.");
            }
            catch (JsonException jsonEx)
            {
                Console.WriteLine($"JSON parsing error during model update: {jsonEx.Message}. Check {AppSettingsPath} and API response format. Skipping model update.");
            }
            catch (HttpRequestException httpEx)
            {
                Console.WriteLine($"HTTP request error during model update: {httpEx.Message}. Skipping model update.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unexpected error occurred during model update: {ex.Message}. Skipping model update.");
            }
        }
    }
} 