using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LocKit.App.Core
{
    public class LlmService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<string> GetAiResponseAsync(string baseUrl, string apiKey, string model, string systemPrompt, string userMessage)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "Error: LLM Base URL is not configured. Please fill it in the Settings panel.";
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return "Error: LLM API Key is not configured. Please fill it in the Settings panel.";
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                return "Error: LLM Model is not configured. Please fill it in the Settings panel.";
            }

            try
            {
                string escapedUserMessage = TextProcessor.EscapeTags(userMessage, out var tags);

                // Ensure URL ends with a slash if needed, and construct /chat/completions endpoint
                string url = baseUrl.Trim();
                if (!url.EndsWith("/"))
                {
                    url += "/";
                }
                url += "chat/completions";

                var payload = new
                {
                    model = model.Trim(),
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = escapedUserMessage }
                    }
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return $"Error (HTTP {response.StatusCode}): {responseBody}";
                }

                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var message = choices[0].GetProperty("message");
                    if (message.TryGetProperty("content", out var contentElement))
                    {
                        string result = contentElement.GetString() ?? "Error: Empty response content.";
                        return TextProcessor.UnescapeTags(result, tags);
                    }
                }

                return $"Error: Invalid response structure.\nRaw response:\n{responseBody}";
            }
            catch (Exception ex)
            {
                return $"Exception occurred: {ex.Message}";
            }
        }

        public async Task<Dictionary<int, string>> TranslateBatchAsync(string baseUrl, string apiKey, string model, string systemPrompt, Dictionary<int, string> batchItems)
        {
            var result = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
            {
                return result;
            }

            try
            {
                string url = baseUrl.Trim();
                if (!url.EndsWith("/"))
                {
                    url += "/";
                }
                url += "chat/completions";

                var itemsList = new List<object>();
                foreach (var kv in batchItems)
                {
                    itemsList.Add(new { id = kv.Key, text = kv.Value });
                }

                string itemsJson = JsonSerializer.Serialize(itemsList);
                string userMessage = $"Translate the following texts:\n{itemsJson}";

                var payload = new
                {
                    model = model.Trim(),
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    },
                    response_format = new { type = "json_object" }
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return result;
                }

                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var message = choices[0].GetProperty("message");
                    if (message.TryGetProperty("content", out var contentElement))
                    {
                        string content = contentElement.GetString() ?? "";
                        content = CleanJsonResponse(content);

                        using var contentDoc = JsonDocument.Parse(content);
                        if (contentDoc.RootElement.TryGetProperty("translations", out var translationsProp))
                        {
                            foreach (var item in translationsProp.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var idProp) && item.TryGetProperty("translation", out var transProp))
                                {
                                    result[idProp.GetInt32()] = transProp.GetString() ?? "";
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        private static string CleanJsonResponse(string content)
        {
            content = content.Trim();
            if (content.StartsWith("```"))
            {
                int firstNewLine = content.IndexOf('\n');
                if (firstNewLine != -1)
                {
                    content = content.Substring(firstNewLine).Trim();
                }
                if (content.EndsWith("```"))
                {
                    content = content.Substring(0, content.Length - 3).Trim();
                }
            }
            return content;
        }

        public async Task<string> TranslateWithGoogleFreeAsync(string text, string targetLanguage = "ru")
        {
            try
            {
                string escapedText = TextProcessor.EscapeTags(text, out var tags);
                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={targetLanguage}&dt=t&q={Uri.EscapeDataString(escapedText)}";
                string response = await _httpClient.GetStringAsync(url);
                
                using var doc = JsonDocument.Parse(response);
                var firstArray = doc.RootElement[0];
                
                var translationBuilder = new StringBuilder();
                foreach (var item in firstArray.EnumerateArray())
                {
                    if (item.GetArrayLength() > 0)
                    {
                        var translatedPart = item[0].GetString();
                        if (translatedPart != null)
                        {
                            translationBuilder.Append(translatedPart);
                        }
                    }
                }
                return TextProcessor.UnescapeTags(translationBuilder.ToString(), tags);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
