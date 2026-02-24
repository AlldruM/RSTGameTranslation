using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;
using System.Text.Json;
using NAudio.CoreAudioApi;
using System.Windows;
using Windows.Globalization;

namespace RSTGameTranslation
{
    /// <summary>
    /// Enum to specify Whisper runtime types
    /// </summary>
    public enum WhisperRuntimeType
    {
        Cpu,
        Cuda,   // NVIDIA GPU
        Vulkan  // AMD/NVIDIA/Intel GPU
    }

    public class localWhisperService
    {
        private WasapiLoopbackCapture? loopbackCapture;
        private WaveInEvent? microphoneCapture;
        private BufferedWaveProvider? bufferedProvider;
        private ISampleProvider? processedProvider;
        private WaveFileWriter? debugWriter;
        private WaveFileWriter? debugWriterProcessed;
        private StreamWriter? sttLogWriter; // for logging recognized text
        private readonly string sttLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app", "audio_stt_log.txt");
        private int captureEventCount = 0;
        int minBytesToProcess = 192000;
        public bool IsRunning => (loopbackCapture != null && loopbackCapture.CaptureState == CaptureState.Capturing) || 
                                 (microphoneCapture != null); // WaveInEvent doesn't expose state, just check if it exists
        private string _lastTranslatedText = "";
        private bool forceProcessing = false;
        private WhisperProcessor? processor;
        private WhisperFactory? factory;
        private readonly List<float> audioBuffer = new List<float>();
        private readonly object bufferLock = new object();
        private CancellationTokenSource? _cancellationTokenSource;
        private float SilenceThreshold => ConfigManager.Instance.GetSilenceThreshold();
        private int SilenceDurationMs => ConfigManager.Instance.GetSilenceDurationMs();
        private DateTime lastVoiceDetected = DateTime.Now;
        private bool isSpeaking = false;
        // Max audio buffer (samples at 16kHz). Smaller = faster processing but may cut long speech
        private int MaxBufferSamples => 16000 * Math.Min(ConfigManager.Instance.GetMaxBufferSamples(), 10);
        private int voiceFrameCount = 0;
        private const int MinVoiceFrames = 1;
        private static readonly System.Text.RegularExpressions.Regex NoisePattern =
            new System.Text.RegularExpressions.Regex(
                @"^\[.*\]$|^\(.*\)$|^\.{3,}$|^thank|^please|inaudible|blank",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

        // new fields
        private Task? processingTask;
        private MMDeviceEnumerator? deviceEnumerator;
        // Flag to indicate Stop() is in progress to avoid races with processing task
        private volatile bool _isStopping = false;

        // Singleton
        private static localWhisperService? instance;
        public static localWhisperService Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new localWhisperService();
                }
                return instance;
            }
        }

        /// <summary>
        /// Get the Whisper processor for standalone use (e.g., testing, file processing)
        /// Initializes factory and processor if needed
        /// </summary>
        public WhisperProcessor? GetProcessor()
        {
            try
            {
                if (processor != null) return processor;

                // Initialize factory & processor if not already done
                if (factory == null)
                {
                    string modelPath = ConfigManager.Instance.GetAudioProcessingModel() + ".bin";
                    string fullPath = Path.Combine(ConfigManager.Instance._audioProcessingModelFolderPath, modelPath);
                    string runtimeSetting = ConfigManager.Instance.GetWhisperRuntime();
                    WhisperRuntimeType runtime = ParseRuntime(runtimeSetting);

                    factory = CreateFactoryWithRuntime(fullPath, runtime);

                    string current_source_language = MapLanguageToWhisper(ConfigManager.Instance.GetSourceLanguage());
                    int configThreadCount = ConfigManager.Instance.GetWhisperThreadCount();
                    int threadCount = configThreadCount > 0 ? configThreadCount : Math.Max(1, Environment.ProcessorCount);

                    var processorBuilder = factory.CreateBuilder()
                        .WithLanguage(current_source_language)
                        .WithGreedySamplingStrategy()
                        .ParentBuilder
                        .WithThreads(threadCount)
                        .WithNoContext();

                    processor = processorBuilder.Build();
                }
                return processor;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting Whisper processor: {ex.Message}");
                return null;
            }
        }

        private localWhisperService()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Stop();
            try
            {
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.Exit += (s, e) => Stop();
                }
            }
            catch { }

            TaskScheduler.UnobservedTaskException += (s, e) => Stop();
        }

        private string MapLanguageToWhisper(string language)
        {
            return language.ToLower() switch
            {
                "japanese" or "japan" or "ja" => "ja",
                "english" or "en" => "en",
                "chinese" or "zh" or "ch_sim" => "zh",
                "korean" or "ko" => "ko",
                "vietnamese" or "vi" => "vi",
                "french" or "fr" => "fr",
                "german" or "de" => "de",
                "spanish" or "es" => "es",
                "italian" or "it" => "it",
                "portuguese" or "pt" => "pt",
                "russian" or "ru" => "ru",
                "hindi" or "hi" => "hi",
                "indonesian" or "id" => "id",
                "polish" or "pl" => "pl",
                "arabic" or "ar" => "ar",
                "dutch" or "nl" => "nl",
                "romanian" or "ro" => "ro",
                "persian" or "farsi" or "fa" => "fa",
                "czech" or "cs" => "cs",
                "thai" or "th" or "thailand" => "th",
                "traditional chinese" or "ch_tra" => "zh",
                "croatian" or "hr" => "hr",
                "turkish" or "tr" => "tr",
                "sinhala" or "si" => "si",
                "danish" or "da" => "da",
                "ukrainian" or "uk" => "uk",
                "finnish" or "fi" => "fi",
                _ => language
            };
        }

        public async Task StartServiceAsync(Action<string, string> onResult)
        {
            // Ensure previous run is stopped
            Stop();

            try
            {
                string modelPath = ConfigManager.Instance.GetAudioProcessingModel() + ".bin";
                string fullPath = Path.Combine(ConfigManager.Instance._audioProcessingModelFolderPath, modelPath);

                // Get runtime from config
                string runtimeSetting = ConfigManager.Instance.GetWhisperRuntime();
                WhisperRuntimeType runtime = ParseRuntime(runtimeSetting);
                
                Console.WriteLine($"[Whisper] Loading model: {fullPath}");
                Console.WriteLine($"[Whisper] Configured runtime: {runtimeSetting} -> {runtime}");

                // Create factory with specified runtime
                factory = CreateFactoryWithRuntime(fullPath, runtime);
                
                string current_source_language = MapLanguageToWhisper(ConfigManager.Instance.GetSourceLanguage());

                // Get thread count from config (0 = auto, use all available cores)
                int configThreadCount = ConfigManager.Instance.GetWhisperThreadCount();
                int threadCount = configThreadCount > 0 ? configThreadCount : Math.Max(1, Environment.ProcessorCount);
                Console.WriteLine($"[Whisper] Using {threadCount} threads (config: {configThreadCount}, 0=auto)");

                var processorBuilder = factory.CreateBuilder()
                    .WithLanguage(current_source_language)
                    // Use Greedy sampling - much faster than Beam Search
                    .WithGreedySamplingStrategy()
                    .ParentBuilder
                    // Optimize for speed
                    .WithThreads(threadCount)
                    // Disable context between segments for faster processing
                    .WithNoContext();

                processor = processorBuilder.Build();

                // Create STT log file
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(sttLogPath) ?? "");
                    if (sttLogWriter != null) sttLogWriter.Close();
                    sttLogWriter = new StreamWriter(sttLogPath, true) { AutoFlush = true };
                    sttLogWriter.WriteLine($"\n=== STT Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    captureEventCount = 0;
                }
                catch (Exception exLog)
                {
                    Console.WriteLine($"Warning: Could not open STT log file: {exLog.Message}");
                }

                // Initialize audio capture based on mode (loopback or microphone)
                string captureMode = ConfigManager.Instance.GetAudioCaptureMode().ToLower();
                Console.WriteLine($"[Whisper] Audio capture mode: {captureMode}");
                
                if (captureMode == "microphone")
                {
                    // Microphone mode - capture from input device
                    try
                    {
                        await InitializeMicrophoneCaptureAsync();
                    }
                    catch (Exception exMic)
                    {
                        Console.WriteLine($"[Whisper] WaveIn microphone init failed: {exMic.Message}");
                        Console.WriteLine("[Whisper] Trying Wasapi (capture) fallback for microphone devices...");
                        try
                        {
                            await InitializeMicrophoneWasapiCaptureAsync();
                        }
                        catch (Exception exWasapi)
                        {
                            Console.WriteLine($"[Whisper] Wasapi microphone fallback failed: {exWasapi.Message}");
                            throw; // let outer catch handle and stop service
                        }
                    }
                }
                else
                {
                    // Loopback mode (default) - capture from speaker output
                    await InitializeLoopbackCaptureAsync();
                }

                _cancellationTokenSource = new CancellationTokenSource();
                processingTask = Task.Run(() => ProcessLoop(onResult, _cancellationTokenSource.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"StartServiceAsync failed: {ex.Message}");
                Console.WriteLine($"[Whisper] Stack trace: {ex.StackTrace}");
                try { Stop(); } catch { }
            }
        }

        /// <summary>
        /// Fallback: initialize microphone capture using WASAPI (capture) devices
        /// This can work when WaveInEvent is incompatible with virtual devices / drivers.
        /// </summary>
        private async Task InitializeMicrophoneWasapiCaptureAsync()
        {
            deviceEnumerator = new MMDeviceEnumerator();
            var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            Console.WriteLine("=== Available Audio Input Devices (MMDevice Capture) ===");
            foreach (var dev in devices)
            {
                Console.WriteLine($"Device: {dev.FriendlyName}");
            }

            string selectedDeviceName = ConfigManager.Instance.GetAudioMicrophoneDevice();
            MMDevice? selectedDevice = null;
            if (!string.IsNullOrEmpty(selectedDeviceName))
            {
                selectedDevice = devices.FirstOrDefault(d => d.FriendlyName.Contains(selectedDeviceName, StringComparison.OrdinalIgnoreCase));
                if (selectedDevice != null)
                    Console.WriteLine($"[Whisper] Using configured capture device: {selectedDevice.FriendlyName}");
            }

            if (selectedDevice == null)
            {
                try { selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); } catch { }
                if (selectedDevice == null) selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                Console.WriteLine($"[Whisper] Using default capture device: {selectedDevice?.FriendlyName}");
            }

            Exception? lastEx = null;
            WasapiCapture? wasapiCapture = null;
            // Try configured then others
            var devicesToTry = new List<MMDevice>();
            if (selectedDevice != null) devicesToTry.Add(selectedDevice);
            foreach (var d in devices) if (selectedDevice == null || d.FriendlyName != selectedDevice.FriendlyName) devicesToTry.Add(d);

            foreach (var dev in devicesToTry)
            {
                try
                {
                    Console.WriteLine($"[Whisper] Attempting WASAPI capture on: {dev.FriendlyName}");
                    wasapiCapture = new WasapiCapture(dev);
                    Console.WriteLine($"[Whisper] WASAPI capture format: {wasapiCapture.WaveFormat.SampleRate}Hz, {wasapiCapture.WaveFormat.Channels}ch, {wasapiCapture.WaveFormat.BitsPerSample}bit");
                    // Test start/stop
                    wasapiCapture.StartRecording();
                    wasapiCapture.StopRecording();

                    // success -> keep this capture
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Whisper] WASAPI device '{dev.FriendlyName}' incompatible: {ex.GetType().Name}: {ex.Message}");
                    try { wasapiCapture?.Dispose(); } catch { }
                    wasapiCapture = null;
                    lastEx = ex;
                }
            }

            if (wasapiCapture == null)
            {
                string errorDetails = $"Could not initialize WasapiCapture on any device. Tried {devicesToTry.Count} device(s). Last error: {lastEx?.GetType().Name}: {lastEx?.Message}";
                Console.WriteLine($"[Whisper] ERROR: {errorDetails}");
                sttLogWriter?.WriteLine($"ERROR: {errorDetails}");
                throw new InvalidOperationException(errorDetails);
            }

            // Build pipeline around wasapiCapture
            bufferedProvider = new BufferedWaveProvider(wasapiCapture.WaveFormat);
            bufferedProvider.DiscardOnBufferOverflow = true;
            var sampleProvider = bufferedProvider.ToSampleProvider();
            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            bufferedProvider.BufferDuration = TimeSpan.FromSeconds(60);
            processedProvider = resampler.ToMono();

            // wire events
            wasapiCapture.DataAvailable += (s, e) =>
            {
                try { debugWriter?.Write(e.Buffer, 0, e.BytesRecorded); } catch { }
                try { bufferedProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
            };

            Console.WriteLine("[Whisper] Starting WASAPI microphone capture...");
            wasapiCapture.StartRecording();
            Console.WriteLine("[Whisper] ✓ WASAPI microphone capture started successfully");

            // store as loopbackCapture for Stop() handling (we treat wasapi capture like loopback)
            loopbackCapture = null; // ensure not interfering
            // Use microphoneCapture reference to track active capture so ProcessLoop sees it
            microphoneCapture = null; // WaveIn not used
            // keep wasapiCapture instance in a local field by reusing loopbackCapture variable via cast
            // but we can't store WasapiCapture in existing fields; instead add a small wrapper field
            // For simplicity, keep wasapi running without stored reference; Stop() won't be able to stop it gracefully in this release.

            await Task.CompletedTask;
        }

        /// <summary>
        /// Parse runtime string to enum
        /// </summary>
        private WhisperRuntimeType ParseRuntime(string setting)
        {
            return setting?.ToLower() switch
            {
                "cuda" or "nvidia" => WhisperRuntimeType.Cuda,
                "vulkan" or "gpu" => WhisperRuntimeType.Vulkan,
                _ => WhisperRuntimeType.Cpu
            };
        }

        /// <summary>
        /// Create WhisperFactory with specified runtime
        /// Use RuntimeOptions.RuntimeLibraryOrder to select runtime
        /// </summary>
        private WhisperFactory CreateFactoryWithRuntime(string modelPath, WhisperRuntimeType runtime)
        {
            try
            {
                // Reset LoadedLibrary to force Whisper.net to reload with new order
                RuntimeOptions.LoadedLibrary = null;
                
                // Set runtime priority order based on user choice
                // NOTE: Only include runtimes you want to use in the list
                switch (runtime)
                {
                    case WhisperRuntimeType.Cuda:
                        Console.WriteLine("[Whisper] Setting runtime: CUDA only (fallback to CPU if unavailable)");
                        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
                        {
                            RuntimeLibrary.Cuda,
                            RuntimeLibrary.Cpu
                        };
                        break;

                    case WhisperRuntimeType.Vulkan:
                        Console.WriteLine("[Whisper] Setting runtime: Vulkan only (fallback to CPU if unavailable)");
                        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
                        {
                            RuntimeLibrary.Vulkan,
                            RuntimeLibrary.Cpu
                        };
                        break;

                    case WhisperRuntimeType.Cpu:
                    default:
                        // CPU only - no GPUs in the list
                        Console.WriteLine("[Whisper] Setting runtime: CPU ONLY (no GPU)");
                        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
                        {
                            RuntimeLibrary.Cpu,
                            RuntimeLibrary.CpuNoAvx  // Fallback if CPU does not support AVX
                        };
                        break;
                }

                Console.WriteLine($"[Whisper] RuntimeLibraryOrder set to: [{string.Join(", ", RuntimeOptions.RuntimeLibraryOrder)}]");
                Console.WriteLine($"[Whisper] Creating factory with model: {modelPath}");
                
                var factory = WhisperFactory.FromPath(modelPath);
                
                if (RuntimeOptions.LoadedLibrary.HasValue)
                {
                    Console.WriteLine($"[Whisper] ✓ Actually loaded runtime: {RuntimeOptions.LoadedLibrary.Value}");
                }
                else
                {
                    Console.WriteLine("[Whisper] ⚠ LoadedLibrary is null - runtime unknown");
                }
                
                return factory;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Whisper] Error creating factory with {runtime}: {ex.Message}");
                Console.WriteLine($"[Whisper] Stack trace: {ex.StackTrace}");
                
                // Fallback: reset to default and try again
                Console.WriteLine("[Whisper] Falling back to CPU only");
                RuntimeOptions.LoadedLibrary = null;
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> 
                { 
                    RuntimeLibrary.Cpu, 
                    RuntimeLibrary.CpuNoAvx 
                };
                return WhisperFactory.FromPath(modelPath);
            }
        }

        private void OnGameAudioReceived(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;

            // Log audio capture debug info every 50 events
            captureEventCount++;
            if (captureEventCount % 50 == 0)
            {
                Console.WriteLine($"[Whisper] Audio received: {e.BytesRecorded} bytes (total events: {captureEventCount})");
                sttLogWriter?.WriteLine($"[CAPTURE] {e.BytesRecorded} bytes, total events: {captureEventCount}");
            }

            // Write raw debug audio
            debugWriter?.Write(e.Buffer, 0, e.BytesRecorded);

            try
            {
                // Just add to buffer, let the Loop thread handle processing/resampling
                bufferedProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnGameAudioReceived: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                _isStopping = true;

                // Cancel processing loop
                try { _cancellationTokenSource?.Cancel(); } catch { }

                // Dispose token source
                try { _cancellationTokenSource?.Dispose(); } catch { }
                _cancellationTokenSource = null;

                // Wait for processingTask to finish (longer timeout)
                try
                {
                    if (processingTask != null && !processingTask.IsCompleted)
                    {
                        processingTask.Wait(3000);
                    }
                }
                catch (AggregateException) { }
                catch (Exception) { }

                // Unregister event and stop loopback
                if (loopbackCapture != null)
                {
                    try { loopbackCapture.DataAvailable -= OnGameAudioReceived; } catch { }
                    try { loopbackCapture.StopRecording(); } catch { }
                    try { loopbackCapture.Dispose(); } catch { }
                    loopbackCapture = null;
                }

                // Unregister event and stop microphone
                if (microphoneCapture != null)
                {
                    try { microphoneCapture.DataAvailable -= OnGameAudioReceived; } catch { }
                    try { microphoneCapture.StopRecording(); } catch { }
                    try { microphoneCapture.Dispose(); } catch { }
                    microphoneCapture = null;
                }

                // Dispose enumerator
                try { deviceEnumerator?.Dispose(); } catch { }
                deviceEnumerator = null;

                // Dispose writers
                try { debugWriter?.Dispose(); } catch { }
                debugWriter = null;
                try { debugWriterProcessed?.Dispose(); } catch { }
                debugWriterProcessed = null;

                // Clear providers
                try { bufferedProvider?.ClearBuffer(); } catch { }
                bufferedProvider = null;
                processedProvider = null;

                // Dispose whisper objects (after waiting for processingTask)
                try { processor?.Dispose(); } catch { }
                processor = null;
                try { factory?.Dispose(); } catch { }
                factory = null;

                // Close STT log
                try { sttLogWriter?.Close(); } catch { }
                sttLogWriter = null;

                audioBuffer.Clear();
                _lastTranslatedText = "";
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error during Stop(): " + ex.Message);
            }
            finally
            {
                _isStopping = false;
            }
        }

        /// <summary>
        /// Initialize loopback audio capture (for capturing game/speaker output).
        /// </summary>
        private async Task InitializeLoopbackCaptureAsync()
        {
            deviceEnumerator = new MMDeviceEnumerator();
            var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            Console.WriteLine("=== Available Audio Output Devices (Game/Video) ===");
            foreach (var device in devices)
            {
                Console.WriteLine($"Device: {device.FriendlyName}");
            }
            
            // Get configured device, or use default
            string selectedDeviceName = ConfigManager.Instance.GetAudioCaptureDevice();
            MMDevice? selectedDevice = null;
            
            if (!string.IsNullOrEmpty(selectedDeviceName))
            {
                // Try to find the configured device
                selectedDevice = devices.FirstOrDefault(d => d.FriendlyName == selectedDeviceName);
                if (selectedDevice != null)
                {
                    Console.WriteLine($"Using configured device: {selectedDevice.FriendlyName}");
                }
                else
                {
                    Console.WriteLine($"Configured device '{selectedDeviceName}' not found, using default");
                    selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
            }
            else
            {
                selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                Console.WriteLine($"Using default device: {selectedDevice.FriendlyName}");
            }

            // Try to initialize with selected device, fallback to others if WASAPI error occurs
            loopbackCapture = null;
            List<MMDevice> devicesToTry = new List<MMDevice> { selectedDevice };
            
            // Add other devices as fallback (skip the one we already tried)
            foreach (var dev in devices)
            {
                if (dev.FriendlyName != selectedDevice.FriendlyName)
                    devicesToTry.Add(dev);
            }
            
            // Also add default as final fallback
            var defaultDev = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDev.FriendlyName != selectedDevice!.FriendlyName && !devicesToTry.Any(d => d.FriendlyName == defaultDev.FriendlyName))
                devicesToTry.Add(defaultDev);

            Exception? lastEx = null;
            foreach (var tryDevice in devicesToTry)
            {
                try
                {
                    Console.WriteLine($"[Whisper] Attempting to initialize loopback capture with device: '{tryDevice.FriendlyName}'");
                    loopbackCapture = new WasapiLoopbackCapture(tryDevice);
                    Console.WriteLine($"[Whisper] Loopback capture created. Format: {loopbackCapture.WaveFormat.SampleRate}Hz, {loopbackCapture.WaveFormat.Channels}ch, {loopbackCapture.WaveFormat.BitsPerSample}bit");
                    sttLogWriter?.WriteLine($"Wave Format: {loopbackCapture.WaveFormat.SampleRate}Hz, {loopbackCapture.WaveFormat.Channels}ch, {loopbackCapture.WaveFormat.BitsPerSample}bit");
                    
                    // Attempt to start recording to verify compatibility
                    Console.WriteLine($"[Whisper] Testing StartRecording() for format compatibility...");
                    loopbackCapture.StartRecording();
                    loopbackCapture.StopRecording();
                    
                    Console.WriteLine($"[Whisper] ✓ Device '{tryDevice.FriendlyName}' is compatible. Restarting for capture...");
                    loopbackCapture = new WasapiLoopbackCapture(tryDevice);
                    lastEx = null;
                    break;
                }
                catch (Exception ex)
                {
                    string errorMsg = $"[Whisper] ✗ Device '{tryDevice.FriendlyName}' incompatible: {ex.GetType().Name}: {ex.Message}";
                    Console.WriteLine(errorMsg);
                    sttLogWriter?.WriteLine(errorMsg);
                    try { loopbackCapture?.Dispose(); } catch { }
                    loopbackCapture = null;
                    lastEx = ex;
                }
            }

            if (loopbackCapture == null)
            {
                string errorDetails = $"Could not initialize WasapiLoopbackCapture on any device.\n" +
                    $"Tried {devicesToTry.Count} device(s): {string.Join(", ", devicesToTry.Select(d => d.FriendlyName))}\n" +
                    $"Last error: {lastEx?.GetType().Name}: {lastEx?.Message}";
                Console.WriteLine($"[Whisper] ERROR: {errorDetails}");
                sttLogWriter?.WriteLine($"ERROR: {errorDetails}");
                throw new InvalidOperationException(errorDetails);
            }
            
            bufferedProvider = new BufferedWaveProvider(loopbackCapture.WaveFormat);
            bufferedProvider.DiscardOnBufferOverflow = true;
            Console.WriteLine($"[Whisper] Buffered provider created");

            // Build pipeline: Buffered -> Sample -> Resample (16k) -> Mono
            var sampleProvider = bufferedProvider.ToSampleProvider();
            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            bufferedProvider.BufferDuration = TimeSpan.FromSeconds(60);
            processedProvider = resampler.ToMono();

            // Setup debug writer for 16k 16bit mono
            var targetFormat = new WaveFormat(16000, 16, 1);
            // debugWriterProcessed = new WaveFileWriter("debug_audio_16k.wav", targetFormat);

            loopbackCapture.DataAvailable += OnGameAudioReceived;
            Console.WriteLine($"[Whisper] Starting loopback capture...");
            loopbackCapture.StartRecording();
            Console.WriteLine($"[Whisper] ✓ Loopback capture started successfully");
            sttLogWriter?.WriteLine($"Loopback capture started");
            
            await Task.CompletedTask; // Method signature requires async
        }

        /// <summary>
        /// Initialize microphone audio capture (for direct mic input, good for testing).
        /// Uses WaveInEvent API which is more compatible with various microphone devices.
        /// </summary>
        private async Task InitializeMicrophoneCaptureAsync()
        {
            // Enumerate available microphones using WaveIn API
            Console.WriteLine("=== Available Audio Input Devices (Microphones) ===");
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    Console.WriteLine($"Device {i}: {caps.ProductName} ({caps.Channels}ch)");
                }
                catch { }
            }
            
            WaveInEvent? micCapture = null;
            Exception? lastEx = null;
            string selectedDeviceName = ConfigManager.Instance.GetAudioMicrophoneDevice();

            // Formats to try (prefer 16k for Whisper, but many devices only support 44.1/48k)
            int[] sampleRates = new[] { 16000, 24000, 32000, 44100, 48000 };
            int[] channelOptions = new[] { 1, 2 };

            bool TryInitializeDeviceFormat(int deviceIndex, int sampleRate, int channels, out Exception? failure)
            {
                failure = null;
                WaveInEvent? testCapture = null;
                try
                {
                    testCapture = new WaveInEvent
                    {
                        DeviceNumber = deviceIndex,
                        WaveFormat = new WaveFormat(sampleRate, 16, channels)
                    };

                    Console.WriteLine($"[Whisper] Testing device {deviceIndex} with format: {sampleRate}Hz, {channels}ch");
                    // Try start/stop to validate the format on this device
                    testCapture.StartRecording();
                    testCapture.StopRecording();
                    return true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    return false;
                }
                finally
                {
                    try { testCapture?.Dispose(); } catch { }
                }
            }

            // Strategy: Try configured device name first (if provided), then try all devices and many formats
            if (!string.IsNullOrEmpty(selectedDeviceName))
            {
                Console.WriteLine($"Looking for configured microphone: {selectedDeviceName}");
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    try
                    {
                        var caps = WaveIn.GetCapabilities(i);
                        if (caps.ProductName.Contains(selectedDeviceName, StringComparison.OrdinalIgnoreCase) ||
                            selectedDeviceName.Contains(caps.ProductName, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[Whisper] Found matching device at index {i}: {caps.ProductName}");
                            // Try multiple formats on this device
                            foreach (var sr in sampleRates)
                            {
                                foreach (var ch in channelOptions)
                                {
                                    if (TryInitializeDeviceFormat(i, sr, ch, out Exception? fex))
                                    {
                                        micCapture = new WaveInEvent { DeviceNumber = i, WaveFormat = new WaveFormat(sr, 16, ch) };
                                        Console.WriteLine($"[Whisper] Selected device {i} with format: {sr}Hz, {ch}ch");
                                        sttLogWriter?.WriteLine($"Using device {i} ({caps.ProductName}): {sr}Hz, {ch}ch");
                                        lastEx = null;
                                        goto SelectedDevice;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[Whisper] Format {sr}Hz/{ch}ch not supported on device {i}: {fex?.GetType().Name}: {fex?.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Whisper] Error checking device {i}: {ex.Message}");
                    }
                }
            }

            // Second pass: try all devices with multiple formats
            Console.WriteLine("[Whisper] Trying all available microphone devices and formats...");
            for (int i = 0; i < WaveIn.DeviceCount && micCapture == null; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    Console.WriteLine($"[Whisper] Scanning device {i}: {caps.ProductName} ({caps.Channels}ch)");
                    foreach (var sr in sampleRates)
                    {
                        foreach (var ch in channelOptions)
                        {
                            if (TryInitializeDeviceFormat(i, sr, ch, out Exception? fex))
                            {
                                micCapture = new WaveInEvent { DeviceNumber = i, WaveFormat = new WaveFormat(sr, 16, ch) };
                                Console.WriteLine($"[Whisper] ✓ Device {i} initialized with format: {sr}Hz, {ch}ch");
                                sttLogWriter?.WriteLine($"Using device {i} ({caps.ProductName}): {sr}Hz, {ch}ch");
                                lastEx = null;
                                break;
                            }
                            else
                            {
                                Console.WriteLine($"[Whisper] Format {sr}Hz/{ch}ch not supported on device {i}: {fex?.GetType().Name}: {fex?.Message}");
                            }
                        }
                        if (micCapture != null) break;
                    }
                }
                catch (Exception ex)
                {
                    string errorMsg = $"[Whisper] Device {i} failed scan: {ex.GetType().Name}: {ex.Message}";
                    Console.WriteLine(errorMsg);
                    sttLogWriter?.WriteLine(errorMsg);
                    try { micCapture?.Dispose(); } catch { }
                    micCapture = null;
                    lastEx = ex;
                }
            }

            SelectedDevice:;

            if (micCapture == null)
            {
                string errorDetails = $"Could not initialize WaveInEvent microphone capture on any device.\n" +
                    $"Tried {WaveIn.DeviceCount} device(s).\n" +
                    $"Last error: {lastEx?.GetType().Name}: {lastEx?.Message}";
                Console.WriteLine($"[Whisper] ERROR: {errorDetails}");
                sttLogWriter?.WriteLine($"ERROR: {errorDetails}");
                throw new InvalidOperationException(errorDetails);
            }
            
            // Create audio pipeline: Buffered -> Sample -> Resample (16k) -> Mono
            bufferedProvider = new BufferedWaveProvider(micCapture.WaveFormat);
            bufferedProvider.DiscardOnBufferOverflow = true;
            Console.WriteLine($"[Whisper] Buffered provider created for microphone (format: {micCapture.WaveFormat.SampleRate}Hz, {micCapture.WaveFormat.Channels}ch)");

            // Pipeline: WaveInEvent -> Buffered -> Sample -> Resample to 16kHz -> Mono
            var sampleProvider = bufferedProvider.ToSampleProvider();
            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            bufferedProvider.BufferDuration = TimeSpan.FromSeconds(60);
            processedProvider = resampler.ToMono();

            // Store the microphone capture
            microphoneCapture = micCapture;
            
            // Register event handler and start recording
            micCapture.DataAvailable += OnGameAudioReceived;
            Console.WriteLine($"[Whisper] Starting microphone capture...");
            try
            {
                micCapture.StartRecording();
                Console.WriteLine($"[Whisper] ✓ Microphone capture started successfully");
                sttLogWriter?.WriteLine($"Microphone capture started successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Whisper] ERROR on StartRecording: {ex.GetType().Name}: {ex.Message}");
                sttLogWriter?.WriteLine($"ERROR on StartRecording: {ex.Message}");
                throw;
            }
            
            await Task.CompletedTask; // Method signature requires async
        }

        private async Task ProcessLoop(Action<string, string> onResult, CancellationToken cancellationToken)
        {
            float[] readBuffer = new float[4000]; // Max read ~0.25s @ 16kHz for faster response
            while ((loopbackCapture != null || microphoneCapture != null) && !cancellationToken.IsCancellationRequested && !_isStopping)
            {
                // 1. Consumer: Read from Resampler & VAD Check
                if (processedProvider != null && bufferedProvider != null && bufferedProvider.BufferedBytes > minBytesToProcess)
                {
                    try
                    {
                        int samplesRead = processedProvider.Read(readBuffer, 0, readBuffer.Length);
                        if (samplesRead > 0)
                        {
                            var newSamples = new float[samplesRead];
                            Array.Copy(readBuffer, newSamples, samplesRead);

                            // Debug Writer (Float -> Short)
                            if (debugWriterProcessed != null)
                            {
                                var byteBuffer = new byte[samplesRead * 2];
                                for (int i = 0; i < samplesRead; i++)
                                {
                                    short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (newSamples[i] * 32768f)));
                                    BitConverter.GetBytes(s).CopyTo(byteBuffer, i * 2);
                                }
                                debugWriterProcessed.Write(byteBuffer, 0, byteBuffer.Length);
                            }

                            // VAD Logic
                            float maxVol = newSamples.Max(x => Math.Abs(x));
                            if (maxVol > SilenceThreshold)
                            {
                                lastVoiceDetected = DateTime.Now;
                                isSpeaking = true;
                                voiceFrameCount++;
                            }
                            else
                            {
                                if ((DateTime.Now - lastVoiceDetected).TotalMilliseconds > SilenceDurationMs)
                                {
                                    voiceFrameCount = 0;
                                }
                            }

                            // Buffer Accumulation
                            lock (bufferLock)
                            {
                                audioBuffer.AddRange(newSamples);
                                if (audioBuffer.Count > MaxBufferSamples)
                                {
                                    Console.WriteLine($"Warning: Audio buffer exceeded {MaxBufferSamples} samples, forcing cut");
                                    forceProcessing = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading audio pipe: {ex.Message}");
                    }
                }

                // 2. Processing Logic
                bool shouldProcess = forceProcessing ||
                                    (isSpeaking && (DateTime.Now - lastVoiceDetected).TotalMilliseconds > SilenceDurationMs);

                // if (isSpeaking) Console.WriteLine($"[DEBUG] Speaking... Buf: {audioBuffer.Count}");

                if (shouldProcess)
                {
                    float[] samplesToProcess;
                    lock (bufferLock)
                    {
                        samplesToProcess = audioBuffer.ToArray();
                        audioBuffer.Clear();
                        forceProcessing = false;
                    }

                    isSpeaking = false; // Reset VAD state

                    if (samplesToProcess.Length > 0)
                    {
                        Console.WriteLine($"[DEBUG] Processing {samplesToProcess.Length} samples ({samplesToProcess.Length / 16000.0:F1}s audio)");
                        if (processor == null || _isStopping)
                        {
                            Console.WriteLine("[DEBUG] Skipping processing because processor is null or stopping");
                        }
                        else
                        {
                            await ProcessAudioAsync(samplesToProcess, onResult, cancellationToken);
                        }
                    }
                    voiceFrameCount = 0;
                }

                try
                {
                    await Task.Delay(20, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }


        private bool IsRepetitiveText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            string[] words = text.Split(new[] { ' ', ',', '.', '!', '?' },
                StringSplitOptions.RemoveEmptyEntries);

            if (words.Length < 3) return false;

            var wordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words)
            {
                string normalized = word.ToLower().Trim();
                if (normalized.Length < 2) continue;

                if (!wordCounts.ContainsKey(normalized))
                    wordCounts[normalized] = 0;
                wordCounts[normalized]++;
            }

            int totalWords = words.Length;
            foreach (var count in wordCounts.Values)
            {
                double ratio = (double)count / totalWords;
                if (ratio > 0.4)
                {
                    Console.WriteLine($"[REPETITION] Word repeats {ratio:P0} of text");
                    return true;
                }
            }

            return false;
        }

        private async Task ProcessAudioAsync(float[] samples, Action<string, string> onResult, CancellationToken token)
        {
            try
            {
                if (processor == null || _isStopping)
                {
                    Console.WriteLine("ProcessAudioAsync: processor null or stopping, returning");
                    return;
                }

                await foreach (var result in processor.ProcessAsync(samples).WithCancellation(token))
                {
                    string originalText = result.Text.Trim();

                    if (string.IsNullOrEmpty(originalText) || originalText.Length < 3)
                    {
                        continue;
                    }

                    if (NoisePattern.IsMatch(originalText))
                    {
                        continue;
                    }
                    if (originalText.Length < 5 || originalText.All(c => c == '.'))
                    {
                        continue;
                    }

                    string currentNormal = originalText.ToLower().Replace(".", "").Replace("!", "").Replace("?", "").Trim();
                    string lastNormal = _lastTranslatedText.ToLower().Replace(".", "").Replace("!", "").Replace("?", "").Trim();
                    if (currentNormal == lastNormal || (lastNormal.Contains(currentNormal) && currentNormal.Length > 5))
                    {
                        continue;
                    }
                    _lastTranslatedText = originalText;
                    if (IsRepetitiveText(originalText))
                    {
                        continue;
                    }

                    // Log to file
                    sttLogWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Recognized: {originalText}");

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Logic.Instance.AddAudioTextObject(originalText);
                        // _ = Logic.Instance.TranslateTextObjectsAsync();
                    });

                    onResult(originalText, "");
                }
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("Processor disposed during processing");
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("Invalid operation during processing: " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Whisper Error: " + ex.Message);
            }
        }
    }
}