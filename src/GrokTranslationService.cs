using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RSTGameTranslation
{
    /// <summary>
    /// Translation service for xAI Grok (chat completions API).
    /// </summary>
    public class GrokTranslationService : ITranslationService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static int _consecutiveFailures = 0;
        private const int DelayMs = 100;

        public async Task<string?> TranslateAsync(string jsonData, string prompt)
        {
            string apiKey = ConfigManager.Instance.GetGrokApiKey();
            string currentService = ConfigManager.Instance.GetCurrentTranslationService();

            try
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    Console.WriteLine("Grok API key not configured");
                    return null;
                }

                string model = ConfigManager.Instance.GetGrokModel();

                // Build messages (OpenAI / xAI compatible)
                var messages = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string>
                    {
                        { "role", "system" },
                        { "content", prompt }
                    },
                    new Dictionary<string, string>
                    {
                        { "role", "user" },
                        { "content", "Here is the input JSON:\n\n" + jsonData }
                    }
                };

                var requestBody = new Dictionary<string, object>
                {
                    { "model", model },
                    { "messages", messages },
                    { "temperature", 0.3 },
                    { "max_tokens", 2000 }
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string requestJson = JsonSerializer.Serialize(requestBody, jsonOptions);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                // Prepare headers
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                const string url = "https://api.x.ai/v1/chat/completions";
                HttpResponseMessage response = await _httpClient.PostAsync(url, content);

                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _consecutiveFailures = 0;

                    // Log raw reply for debugging / history
                    LogManager.Instance.LogLlmReply(responseContent);

                    return responseContent;
                }
                else
                {
                    _consecutiveFailures++;
                    Console.WriteLine($"Grok API error: {response.StatusCode}, {responseContent}, error count: {_consecutiveFailures}");

                    // Rotate key if there is a pool configured for this service
                    string newApiKey = ConfigManager.Instance.GetNextApiKey(currentService, apiKey);
                    if (!string.IsNullOrEmpty(newApiKey) && newApiKey != apiKey)
                    {
                        ConfigManager.Instance.SetGrokApiKey(newApiKey);
                        Console.WriteLine("Switched to next Grok API key from list");
                    }

                    if (_consecutiveFailures > 3)
                    {
                        try
                        {
                            System.IO.File.WriteAllText("grok_last_error.txt",
                                $"Grok API error: {response.StatusCode}\n\nFull response: {responseContent}");
                        }
                        catch { /* ignore file I/O errors */ }

                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            System.Windows.MessageBox.Show(
                                $"Grok API error: {response.StatusCode}\n\n{responseContent}",
                                "Grok Error",
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Error);
                        });
                    }

                    await Task.Delay(DelayMs);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Grok translation error: {ex.Message}");

                try
                {
                    System.IO.File.WriteAllText("grok_last_error.txt",
                        $"Grok API exception: {ex.Message}\n\nStack trace: {ex.StackTrace}");
                }
                catch { /* ignore file I/O errors */ }

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        $"Grok API exception: {ex.Message}",
                        "Grok Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                });

                return null;
            }
        }
    }
}

