using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NAudio.Wave;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;

namespace RSTGameTranslation
{
    /// <summary>
    /// TTS service for local API (CosyVoice tts_server.py).
    /// Stream mode: GET /tts_stream?text=... returns binary: 4 bytes sample_rate (LE) + raw PCM 16-bit mono chunks.
    /// Playback starts as soon as first chunks arrive (low latency).
    /// </summary>
    public class LocalTtsService
    {
        private static LocalTtsService? _instance;
        private readonly HttpClient _httpClient;

        private static readonly SemaphoreSlim _speechSemaphore = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _playbackSemaphore = new SemaphoreSlim(1, 1);
        private static readonly Queue<string> _audioFileQueue = new Queue<string>();
        private static bool _isPlayingAudio = false;
        private static bool _isProcessingQueue = false;
        private static IWavePlayer? _currentPlayer = null;
        private static AudioFileReader? _currentAudioFile = null;
        private static readonly string _tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        private static readonly List<string> _tempFilesToDelete = new List<string>();
        private static System.Timers.Timer? _cleanupTimer;
        private static CancellationTokenSource? _playbackCancellationTokenSource = null;
        private static readonly HashSet<string> _activeAudioFiles = new HashSet<string>();

        // Your server: GET /tts_stream?text=...
        private const string TtsEndpoint = "tts_stream";
        private const string VoicesEndpoint = "check_serv"; // optional, for servers that expose voice list

        public static LocalTtsService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new LocalTtsService();
                return _instance;
            }
        }

        private LocalTtsService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(600);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            Directory.CreateDirectory(_tempDir);
            CleanupTempFiles();
            _cleanupTimer = new System.Timers.Timer(30000);
            _cleanupTimer.Elapsed += (_, _) => CleanupTempFiles();
            _cleanupTimer.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupAllTempFiles();
        }

        private void CleanupAllTempFiles()
        {
            try
            {
                StopCurrentPlayback();
                lock (_audioFileQueue) _audioFileQueue.Clear();
                lock (_activeAudioFiles) _activeAudioFiles.Clear();
                if (Directory.Exists(_tempDir))
                {
                    foreach (var file in Directory.GetFiles(_tempDir, "tts_local_*.wav"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                lock (_tempFilesToDelete) _tempFilesToDelete.Clear();
            }
            catch (Exception ex) { Console.WriteLine($"Local TTS cleanup: {ex.Message}"); }
        }

        private void CleanupTempFiles()
        {
            try
            {
                lock (_tempFilesToDelete)
                {
                    foreach (var file in new List<string>(_tempFilesToDelete))
                    {
                        lock (_activeAudioFiles) { if (_activeAudioFiles.Contains(file)) continue; }
                        try { if (File.Exists(file)) File.Delete(file); _tempFilesToDelete.Remove(file); } catch { }
                    }
                }
                if (Directory.Exists(_tempDir))
                {
                    foreach (var file in Directory.GetFiles(_tempDir, "tts_local_*.wav"))
                    {
                        lock (_activeAudioFiles) { if (_activeAudioFiles.Contains(file)) continue; }
                        try
                        {
                            var fi = new FileInfo(file);
                            if (DateTime.Now - fi.CreationTime > TimeSpan.FromMinutes(10))
                                File.Delete(file);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Local TTS temp cleanup: {ex.Message}"); }
        }

        /// <summary>
        /// Fetch available voice IDs from the local API (e.g. GET /sft_spk for CosyVoice).
        /// Returns list of display names; if the API returns object with "data" or array, we adapt.
        /// </summary>
        public static async Task<List<string>> GetAvailableVoicesAsync(string? baseUrl = null)
        {
            var list = new List<string>();
            try
            {
                string url = string.IsNullOrWhiteSpace(baseUrl) ? ConfigManager.Instance.GetLocalTtsUrl() : baseUrl.Trim();
                url = url.TrimEnd('/');
                var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/{VoicesEndpoint}");
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.SendAsync(req);
                if (!response.IsSuccessStatusCode)
                    return list;
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // CosyVoice-api returns list of speaker names directly
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in root.EnumerateArray())
                        list.Add(el.GetString() ?? "");
                }
                else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in data.EnumerateArray())
                        list.Add(el.GetString() ?? "");
                }
                else if (root.TryGetProperty("audio_files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in files.EnumerateArray())
                        list.Add(el.GetString() ?? "");
                }
                else
                {
                    list.Add(root.GetString() ?? "");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Local TTS get voices: {ex.Message}");
            }
            return list;
        }

        private static readonly string[] SileroMaleVoices = { "aidar", "eugene" };
        private static readonly string[] SileroFemaleVoices = { "baya", "kseniya", "xenia" };
        private static readonly Random _voiceRandom = new Random();

        /// <summary>Resolve voice for character: main -> main char voice; male/female -> random from list.</summary>
        private (string? promptWav, string? promptTxt, string? sileroSpeaker) ResolveVoiceForCharacter(string? characterName)
        {
            string mode = ConfigManager.Instance.GetLocalTtsMode();
            string mainName = ConfigManager.Instance.GetLocalTtsMainCharName()?.Trim() ?? "";
            bool isMain = !string.IsNullOrEmpty(characterName) && !string.IsNullOrEmpty(mainName) &&
                string.Equals(characterName.Trim(), mainName, StringComparison.OrdinalIgnoreCase);

            if (mode.Equals("Pretrained", StringComparison.OrdinalIgnoreCase))
            {
                string defaultVoice = ConfigManager.Instance.GetLocalTtsVoice()?.Trim() ?? "";
                if (string.IsNullOrEmpty(defaultVoice)) defaultVoice = "kseniya";
                if (string.Equals(characterName, "__test_male__", StringComparison.OrdinalIgnoreCase))
                    return (null, null, SileroMaleVoices[_voiceRandom.Next(SileroMaleVoices.Length)]);
                if (string.Equals(characterName, "__test_female__", StringComparison.OrdinalIgnoreCase))
                    return (null, null, SileroFemaleVoices[_voiceRandom.Next(SileroFemaleVoices.Length)]);
                if (isMain || string.IsNullOrEmpty(characterName))
                    return (null, null, defaultVoice);
                string gender = GetCharacterGender(characterName);
                if (gender == "male")
                    return (null, null, SileroMaleVoices[_voiceRandom.Next(SileroMaleVoices.Length)]);
                if (gender == "female")
                    return (null, null, SileroFemaleVoices[_voiceRandom.Next(SileroFemaleVoices.Length)]);
                return (null, null, defaultVoice);
            }

            // VoiceClone (CosyVoice) - special test character names
            if (string.Equals(characterName, "__test_male__", StringComparison.OrdinalIgnoreCase))
            {
                var (wm, tm) = PickRandomVoiceClone("male");
                return (wm, tm, null);
            }
            if (string.Equals(characterName, "__test_female__", StringComparison.OrdinalIgnoreCase))
            {
                var (wf, tf) = PickRandomVoiceClone("female");
                return (wf, tf, null);
            }
            if (isMain)
            {
                string wav = ConfigManager.Instance.GetLocalTtsMainCharWav()?.Trim() ?? "";
                string txt = ConfigManager.Instance.GetLocalTtsMainCharTxt()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(wav) && File.Exists(wav))
                    return (wav, string.IsNullOrEmpty(txt) ? null : txt, null);
            }
            string gender2 = GetCharacterGender(characterName);
            var (w, t) = PickRandomVoiceClone(gender2);
            // Fallback to main char when voice clone files not found
            if (w == null && t == null)
            {
                string mainWav = ConfigManager.Instance.GetLocalTtsMainCharWav()?.Trim() ?? "";
                string mainTxt = ConfigManager.Instance.GetLocalTtsMainCharTxt()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(mainWav) && File.Exists(mainWav))
                    return (mainWav, string.IsNullOrEmpty(mainTxt) ? null : mainTxt, null);
            }
            return (w, t, null);
        }

        private string GetCharacterGender(string? characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName)) return "";
            string list = ConfigManager.Instance.GetLocalTtsCharacterGenders() ?? "";
            string name = characterName.Trim();
            foreach (var pair in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && string.Equals(parts[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return parts[1].Trim().ToLowerInvariant();
            }
            return "";
        }

        /// <summary>Get base dir for RussianArtistsDub. Tries app/LocalTTS and ../LocalTTS (sibling of app).</summary>
        private static string GetRussianArtistsDubBaseDir()
        {
            string baseExe = AppDomain.CurrentDomain.BaseDirectory;
            string path1 = Path.GetFullPath(Path.Combine(baseExe, "LocalTTS", "asset", "RussianArtistsDub"));
            string path2 = Path.GetFullPath(Path.Combine(baseExe, "..", "LocalTTS", "asset", "RussianArtistsDub"));
            if (Directory.Exists(path1)) return path1;
            if (Directory.Exists(path2)) return path2;
            return path2; // prefer sibling of app
        }

        private (string? wav, string? txt) PickRandomVoiceClone(string gender)
        {
            string baseDir = GetRussianArtistsDubBaseDir();
            string list = gender == "male" ? ConfigManager.Instance.GetLocalTtsMaleVoices() :
                gender == "female" ? ConfigManager.Instance.GetLocalTtsFemaleVoices() : "";
            // Split by comma, newline; fix typo "men-X.men-Y" (dot instead of comma) -> men-X, men-Y
            var raw = list?.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList() ?? new List<string>();
            var items = new List<string>();
            foreach (var s in raw)
            {
                if (s.Contains(".men-"))
                {
                    var parts = s.Split(new[] { ".men-" }, StringSplitOptions.None);
                    if (parts[0].Length > 0 && (parts[0].StartsWith("men-", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("wom-", StringComparison.OrdinalIgnoreCase)))
                        items.Add(parts[0].Trim());
                    for (int i = 1; i < parts.Length; i++)
                        if (parts[i].Trim().Length > 0)
                            items.Add("men-" + parts[i].Trim());
                }
                else if (s.Contains(".wom-"))
                {
                    var parts = s.Split(new[] { ".wom-" }, StringSplitOptions.None);
                    if (parts[0].Length > 0 && (parts[0].StartsWith("men-", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("wom-", StringComparison.OrdinalIgnoreCase)))
                        items.Add(parts[0].Trim());
                    for (int i = 1; i < parts.Length; i++)
                        if (parts[i].Trim().Length > 0)
                            items.Add("wom-" + parts[i].Trim());
                }
                else
                    items.Add(s);
            }
            var itemsArr = items.Where(s => s.Length > 0).ToArray();
            if (itemsArr.Length == 0 && Directory.Exists(baseDir))
            {
                // Auto-scan RussianArtistsDub: men-* for male, wom-* for female
                string prefix = gender == "male" ? "men-" : (gender == "female" ? "wom-" : "");
                if (!string.IsNullOrEmpty(prefix))
                {
                    var files = Directory.GetFiles(baseDir, prefix + "*.wav").Select(f => Path.GetFileNameWithoutExtension(f) ?? "").Where(s => s.Length > 0).ToArray();
                    if (files.Length > 0)
                        itemsArr = files;
                }
            }
            if (itemsArr.Length == 0)
            {
                string mainWav = ConfigManager.Instance.GetLocalTtsMainCharWav()?.Trim() ?? "";
                string mainTxt = ConfigManager.Instance.GetLocalTtsMainCharTxt()?.Trim() ?? "";
                return (string.IsNullOrEmpty(mainWav) || !File.Exists(mainWav) ? null : mainWav, string.IsNullOrEmpty(mainTxt) ? null : mainTxt);
            }
            // Try up to 10 random picks in case some files are missing
            for (int attempt = 0; attempt < Math.Min(5, itemsArr.Length); attempt++)
            {
                string baseName = itemsArr[_voiceRandom.Next(itemsArr.Length)];
                if (!baseName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    baseName += ".wav";
                string wav = Path.Combine(baseDir, baseName);
                if (!File.Exists(wav)) continue;
                string nameNoExt = Path.GetFileNameWithoutExtension(baseName);
                string txtName = (nameNoExt.StartsWith("men-", StringComparison.OrdinalIgnoreCase) || nameNoExt.StartsWith("wom-", StringComparison.OrdinalIgnoreCase))
                    ? nameNoExt.Substring(4) + ".txt" : nameNoExt + ".txt";
                string txtPath = Path.Combine(baseDir, txtName);
                if (!File.Exists(txtPath)) txtPath = Path.Combine(baseDir, nameNoExt + ".txt");
                return (wav, File.Exists(txtPath) ? txtPath : null);
            }
            return (null, null);
        }

        public async Task<bool> SpeakText(string text, string? characterName = null)
        {
            if (!await _speechSemaphore.WaitAsync(0))
            {
                Console.WriteLine("Local TTS: another speech request in progress, skipping.");
                return false;
            }
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("Local TTS: cannot speak empty text");
                    return false;
                }
                string processedText = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                string baseUrl = ConfigManager.Instance.GetLocalTtsUrl()?.Trim() ?? "";
                if (string.IsNullOrEmpty(baseUrl))
                {
                    MessageBox.Show("Local TTS URL is not set. Use Settings → TTS → Local API.", "Local TTS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                // Only trim trailing slashes, keep '?' if it's part of the base URL
                baseUrl = baseUrl.TrimEnd('/');
                string mode = ConfigManager.Instance.GetLocalTtsMode()?.Trim() ?? "Pretrained";

                var (promptWav, promptTxt, sileroSpeaker) = ResolveVoiceForCharacter(characterName);

                HttpResponseMessage response;
                string? contentType = "";

                // Use POST for F5TTS (supports file upload), GET for others
                if (mode.Equals("F5TTS", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(promptWav) && File.Exists(promptWav) && !string.IsNullOrEmpty(promptTxt) && File.Exists(promptTxt))
                {
                    Console.WriteLine($"Local TTS GET request: {baseUrl}/{TtsEndpoint}, text len={processedText.Length}, speaker={sileroSpeaker ?? "VoiceClone"}, prompt_wav={promptWav ?? "(none)"}, prompt_txt={promptTxt ?? "(none)"}");
                    
                    // Build multipart form data for POST
                    using (var multipart = new MultipartFormDataContent())
                    {
                        multipart.Add(new StringContent(processedText), "text");
                        if (!string.IsNullOrEmpty(promptTxt))
                            multipart.Add(new StringContent(promptTxt), "ref_text");
                        
                        // Add ref_audio file
                        try
                        {
                            var audioContent = new ByteArrayContent(await File.ReadAllBytesAsync(promptWav));
                            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                            multipart.Add(audioContent, "ref_audio", Path.GetFileName(promptWav));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to read ref_audio: {ex.Message}");
                            return false;
                        }
                        
                        multipart.Add(new StringContent("32"), "nfe_step");
                        multipart.Add(new StringContent("1.0"), "speed");
                        
                        string postUrl = $"{baseUrl}/basic_tts";
                        response = await _httpClient.PostAsync(postUrl, multipart, CancellationToken.None);
                        contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
                    }
                }
                else
                {
                    // Use GET for Pretrained/VoiceClone (CosyVoice-compatible)
                    var query = new List<string> { $"text={Uri.EscapeDataString(processedText)}" };
                    if (!string.IsNullOrEmpty(sileroSpeaker))
                        query.Add($"speaker={Uri.EscapeDataString(sileroSpeaker)}");
                    if (!string.IsNullOrEmpty(promptWav))
                        query.Add($"prompt_wav={Uri.EscapeDataString(promptWav)}");
                    if (!string.IsNullOrEmpty(promptTxt))
                        query.Add($"prompt_txt={Uri.EscapeDataString(promptTxt)}");
                    
                    string requestUrl = $"{baseUrl}/{TtsEndpoint}?{string.Join("&", query)}";
                    Console.WriteLine($"Local TTS GET request: {baseUrl}/{TtsEndpoint}, text len={processedText.Length}, speaker={sileroSpeaker ?? "VoiceClone"}, prompt_wav={promptWav ?? "(none)"}, prompt_txt={promptTxt ?? "(none)"}");

                    response = await _httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead);
                    contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                }

                if (!response.IsSuccessStatusCode)
                {
                    string err = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Local TTS failed: {response.StatusCode} - {err}");
                    WpfApplication.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Local TTS server error: {response.StatusCode}. Ensure the server is running and URL is correct.\n{err}", "Local TTS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    response.Dispose();
                    return false;
                }

                try
                {
                    // Stream: first 4 bytes = sample rate (LE), then raw PCM 16-bit mono
                    if (contentType.Contains("octet-stream") || contentType.Contains("stream"))
                    {
                        bool played = await PlayStreamedPcmAsync(response);
                        return played;
                    }

                    // Fallback: full body (e.g. WAV from /tts)
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    
                    string ext = (contentType.Contains("wav") || (bytes.Length > 4 && bytes[0] == 'R' && bytes[1] == 'I')) ? "wav" : "mp3";
                    string audioFile = Path.Combine(_tempDir, $"tts_local_{DateTime.Now.Ticks}.{ext}");
                    await File.WriteAllBytesAsync(audioFile, bytes);
                    lock (_tempFilesToDelete) { if (!_tempFilesToDelete.Contains(audioFile)) _tempFilesToDelete.Add(audioFile); }
                    EnqueueAudioFile(audioFile);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Local TTS error: {ex.Message}");
                    WpfApplication.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Local TTS: {ex.Message}. Check that the server is running and URL in Settings is correct.", "Local TTS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return false;
                }
                finally
                {
                    response?.Dispose();
                    _speechSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Local TTS JSON parse error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read stream: 4 bytes sample_rate (LE) + raw PCM 16-bit mono. Play via BufferedWaveProvider as chunks arrive.
        /// </summary>
        private async Task<bool> PlayStreamedPcmAsync(HttpResponseMessage response)
        {
            const int sampleRateHeaderSize = 4;
            int sampleRate = 16000;
            var cts = new CancellationTokenSource();
            try
            {
                await _playbackSemaphore.WaitAsync(cts.Token);
                StopCurrentPlayback();

                using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                byte[] header = new byte[sampleRateHeaderSize];
                int read = await ReadExactlyAsync(stream, header, 0, sampleRateHeaderSize, cts.Token);
                if (read < sampleRateHeaderSize)
                {
                    Console.WriteLine("Local TTS stream: no sample rate header");
                    return false;
                }
                sampleRate = BitConverter.ToInt32(header, 0);
                if (sampleRate <= 0 || sampleRate > 192000) sampleRate = 16000;

                var waveFormat = new WaveFormat(sampleRate, 16, 1);
                var buffered = new BufferedWaveProvider(waveFormat) { BufferDuration = TimeSpan.FromSeconds(30) };

                var buffer = new byte[8192];
                int firstRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (firstRead <= 0)
                {
                    Console.WriteLine("Local TTS stream: no PCM data");
                    return false;
                }
                buffered.AddSamples(buffer, 0, firstRead);

                _currentPlayer = new WaveOutEvent { DesiredLatency = 150 };
                _currentPlayer.Volume = ConfigManager.Instance.GetTtsVolume();
                _currentPlayer.Init(buffered);
                _isPlayingAudio = true;
                _playbackCancellationTokenSource = cts;
                _currentPlayer.Play();

                while (true)
                {
                    int n = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                    if (n <= 0) break;
                    buffered.AddSamples(buffer, 0, n);
                }

                while (buffered.BufferedBytes > 0 && _currentPlayer != null && _isPlayingAudio && !cts.Token.IsCancellationRequested)
                    await Task.Delay(50, CancellationToken.None);

                _currentPlayer?.Stop();
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                Console.WriteLine($"Local TTS stream playback: {ex.Message}");
                return false;
            }
            finally
            {
                _isPlayingAudio = false;
                if (_currentPlayer != null) { _currentPlayer.Dispose(); _currentPlayer = null; }
                _playbackSemaphore.Release();
            }
        }

        private static async Task<int> ReadExactlyAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        private void EnqueueAudioFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            lock (_audioFileQueue)
            {
                lock (_activeAudioFiles) _activeAudioFiles.Add(path);
                _audioFileQueue.Enqueue(path);
                if (!_isProcessingQueue)
                    Task.Run(ProcessAudioQueueAsync);
            }
        }

        private async Task ProcessAudioQueueAsync()
        {
            lock (_audioFileQueue) { if (_isProcessingQueue) return; _isProcessingQueue = true; }
            try
            {
                while (true)
                {
                    string? path = null;
                    lock (_audioFileQueue)
                    {
                        if (_audioFileQueue.Count == 0) { _isProcessingQueue = false; return; }
                        path = _audioFileQueue.Dequeue();
                    }
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        StopCurrentPlayback();
                        await _playbackSemaphore.WaitAsync();
                        try { await PlayAudioFileAsync(path); }
                        finally { _playbackSemaphore.Release(); }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Local TTS queue: {ex.Message}"); }
            finally { lock (_audioFileQueue) _isProcessingQueue = false; }
        }

        private void StopCurrentPlayback()
        {
            if (!_isPlayingAudio) return;
            try
            {
                _playbackCancellationTokenSource?.Cancel();
                _playbackCancellationTokenSource?.Dispose();
                _playbackCancellationTokenSource = null;
                _currentPlayer?.Stop();
                _currentPlayer?.Dispose();
                _currentPlayer = null;
                _currentAudioFile?.Dispose();
                _currentAudioFile = null;
                _isPlayingAudio = false;
            }
            catch (Exception ex) { Console.WriteLine($"Local TTS stop: {ex.Message}"); }
        }

        private async Task PlayAudioFileAsync(string filePath)
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _isPlayingAudio = true;
                _playbackCancellationTokenSource = new CancellationTokenSource();
                _currentPlayer = new WaveOutEvent { DesiredLatency = 100 };
                _currentPlayer.Volume = ConfigManager.Instance.GetTtsVolume();
                _currentPlayer.PlaybackStopped += (_, _) =>
                {
                    _isPlayingAudio = false;
                    _currentPlayer?.Dispose(); _currentPlayer = null;
                    _currentAudioFile?.Dispose(); _currentAudioFile = null;
                    lock (_activeAudioFiles) _activeAudioFiles.Remove(filePath);
                    DeleteFileWithRetry(filePath);
                    tcs.TrySetResult(true);
                };
                _currentAudioFile = new AudioFileReader(filePath);
                _currentPlayer.Init(_currentAudioFile);
                _currentPlayer.Play();
                _playbackCancellationTokenSource.Token.Register(() => { if (_isPlayingAudio) _currentPlayer?.Stop(); });
                await tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Local TTS playback: {ex.Message}");
                _isPlayingAudio = false;
                _currentAudioFile?.Dispose(); _currentAudioFile = null;
                _currentPlayer?.Dispose(); _currentPlayer = null;
                lock (_activeAudioFiles) _activeAudioFiles.Remove(filePath);
                DeleteFileWithRetry(filePath);
                tcs.TrySetResult(false);
            }
        }

        private static void DeleteFileWithRetry(string path, int maxRetries = 3)
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        if (File.Exists(path)) { File.Delete(path); }
                        lock (_tempFilesToDelete) _tempFilesToDelete.Remove(path);
                        return;
                    }
                    catch { if (i < maxRetries - 1) await Task.Delay(500 * (i + 1)); }
                }
                lock (_tempFilesToDelete) { if (!_tempFilesToDelete.Contains(path)) _tempFilesToDelete.Add(path); }
            });
        }

        public static void StopAllTTS()
        {
            try
            {
                if (_instance != null) _instance.StopCurrentPlayback();
                lock (_audioFileQueue) _audioFileQueue.Clear();
                lock (_activeAudioFiles) _activeAudioFiles.Clear();
            }
            catch (Exception ex) { Console.WriteLine($"Local TTS StopAll: {ex.Message}"); }
        }

        /// <summary>Test TTS server connection.</summary>
        public static async Task<(bool ok, string message)> TestConnectionAsync(string? urlOverride = null)
        {
            string url = urlOverride ?? ConfigManager.Instance.GetLocalTtsUrl() ?? "";
            if (string.IsNullOrEmpty(url))
                return (false, "URL не задан. Укажите URL в настройках TTS.");
            url = url.TrimEnd('/');
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/{VoicesEndpoint}");
                var response = await client.SendAsync(req);
                if (response.IsSuccessStatusCode)
                    return (true, "Сервер доступен.");
                return (false, $"Ошибка: {response.StatusCode}");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>Test main hero voice with short phrase.</summary>
        public static async Task<bool> TestMainHeroVoiceAsync()
        {
            var svc = Instance;
            string mainName = ConfigManager.Instance.GetLocalTtsMainCharName()?.Trim() ?? "";
            return await svc.SpeakText("Привет, это проверка голоса главного героя. Я буду вещать ТАК!", string.IsNullOrEmpty(mainName) ? null : mainName);
        }

        /// <summary>Test male voice - plays random men-* from RussianArtistsDub.</summary>1
        public static async Task<bool> TestMaleVoiceAsync()
        {
            var svc = Instance;
            return await svc.SpeakText("Привет, это проверка мужского голоса. Я буду говорить возможно ТАК!", "__test_male__");
        }

        /// <summary>Test female voice - plays random wom-* from RussianArtistsDub.</summary>
        public static async Task<bool> TestFemaleVoiceAsync()
        {
            var svc = Instance;
            return await svc.SpeakText("Привет, это проверка женского голоса. Я буду говорить примерно ТАК!", "__test_female__");
        }
    }
}
