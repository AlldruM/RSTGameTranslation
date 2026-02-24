using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using System.Windows;

namespace RSTGameTranslation
{
    /// <summary>
    /// Local Silero STT service via HTTP.
    /// Captures audio from loopback/microphone and sends to local Silero STT server.
    /// </summary>
    public class LocalSileroSTTService
    {
        private WasapiLoopbackCapture? loopbackCapture;
        private WasapiCapture? wasapiMicCapture;
        private WaveInEvent? microphoneCapture;
        private BufferedWaveProvider? bufferedProvider;
        private ISampleProvider? processedProvider;
        private WaveFormat? captureWaveFormat;      // native format - send to server as-is
        private readonly List<byte> rawByteBuffer = new List<byte>();
        private StreamWriter? sttLogWriter;
        private readonly string sttLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app", "audio_stt_log_silero.txt");
        private int captureEventCount = 0;
        private HttpClient? httpClient;
        private readonly List<float> audioBuffer = new List<float>();
        private readonly object bufferLock = new object();
        private CancellationTokenSource? _cancellationTokenSource;
        private volatile bool _isStopping = false;
        private Task? processingTask;
        private MMDeviceEnumerator? deviceEnumerator;

        public bool IsRunning => (loopbackCapture != null && loopbackCapture.CaptureState == CaptureState.Capturing) ||
                                 (wasapiMicCapture != null && wasapiMicCapture.CaptureState == CaptureState.Capturing) ||
                                 (microphoneCapture != null);
        
        private float SilenceThreshold => ConfigManager.Instance.GetSilenceThreshold();
        private int SilenceDurationMs => ConfigManager.Instance.GetSilenceDurationMs();
        private DateTime lastVoiceDetected = DateTime.Now;
        private bool isSpeaking = false;
        private int MaxBufferSamples => 16000 * Math.Min(ConfigManager.Instance.GetMaxBufferSamples(), 10);
        private int minBytesToProcess = 192000;
        private int maxRawBufferBytes => captureWaveFormat != null ? captureWaveFormat.AverageBytesPerSecond * Math.Min(ConfigManager.Instance.GetMaxBufferSamples(), 10) : 320000;

        // Singleton
        private static LocalSileroSTTService? instance;
        public static LocalSileroSTTService Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new LocalSileroSTTService();
                }
                return instance;
            }
        }

        private LocalSileroSTTService()
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
            httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task StartServiceAsync(Action<string, string> onResult)
        {
            Stop();

            try
            {
                string sttUrl = ConfigManager.Instance.GetLocalSTTUrl();
                Console.WriteLine($"[Silero STT] Service URL: {sttUrl}");
                Console.WriteLine($"[Silero STT] Testing server connectivity...");

                // Test connectivity
                try
                {
                    var testResponse = await httpClient!.GetAsync(sttUrl + "/check_serv", HttpCompletionOption.ResponseHeadersRead);
                    Console.WriteLine($"[Silero STT] Server check response: {testResponse.StatusCode}");
                }
                catch (Exception exTest)
                {
                    Console.WriteLine($"[Silero STT] Warning: Could not reach server at {sttUrl}: {exTest.Message}");
                    // Continue anyway - server might start later
                }

                // Create STT log file
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(sttLogPath) ?? "");
                    if (sttLogWriter != null) sttLogWriter.Close();
                    sttLogWriter = new StreamWriter(sttLogPath, true) { AutoFlush = true };
                    sttLogWriter.WriteLine($"\n=== Silero STT Session Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    captureEventCount = 0;
                }
                catch (Exception exLog)
                {
                    Console.WriteLine($"[Silero STT] Warning: Could not open log file: {exLog.Message}");
                }

                // Initialize audio capture based on mode (loopback or microphone)
                string captureMode = (ConfigManager.Instance.GetAudioCaptureMode() ?? "loopback").Trim().ToLower();
                Console.WriteLine($"[Silero STT] Audio capture mode: {captureMode}");
                sttLogWriter?.WriteLine($"Capture mode: {captureMode}");

                if (captureMode == "microphone")
                {
                    await InitializeMicrophoneCaptureAsync();
                }
                else
                {
                    try
                    {
                        await InitializeLoopbackCaptureAsync();
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("loopback"))
                    {
                        Console.WriteLine("[Silero STT] Loopback failed. Falling back to microphone capture.");
                        sttLogWriter?.WriteLine("Loopback failed; falling back to microphone.");
                        await InitializeMicrophoneCaptureAsync();
                    }
                }

                _cancellationTokenSource = new CancellationTokenSource();
                processingTask = Task.Run(() => ProcessLoop(onResult, _cancellationTokenSource.Token));

                Console.WriteLine("[Silero STT] Service started successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Silero STT] StartServiceAsync failed: {ex.Message}");
                Console.WriteLine($"[Silero STT] Stack trace: {ex.StackTrace}");
                try { Stop(); } catch { }
            }
        }

        private async Task InitializeLoopbackCaptureAsync()
        {
            deviceEnumerator = new MMDeviceEnumerator();
            var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            string selectedDeviceName = ConfigManager.Instance.GetAudioCaptureDevice();
            MMDevice? selectedDevice = null;

            if (!string.IsNullOrEmpty(selectedDeviceName))
            {
                selectedDevice = devices.FirstOrDefault(d => d.FriendlyName == selectedDeviceName);
                if (selectedDevice == null)
                {
                    selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
            }
            else
            {
                selectedDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            loopbackCapture = null;
            List<MMDevice> devicesToTry = new List<MMDevice> { selectedDevice };
            foreach (var dev in devices)
            {
                if (dev.FriendlyName != selectedDevice.FriendlyName)
                    devicesToTry.Add(dev);
            }

            Exception? lastEx = null;
            foreach (var tryDevice in devicesToTry)
            {
                try
                {
                    WasapiLoopbackCapture? testCapture = null;
                    try
                    {
                        testCapture = new WasapiLoopbackCapture(tryDevice);
                        sttLogWriter?.WriteLine($"Wave Format: {testCapture.WaveFormat.SampleRate}Hz, {testCapture.WaveFormat.Channels}ch, {testCapture.WaveFormat.BitsPerSample}bit");

                        testCapture.StartRecording();
                        System.Threading.Thread.Sleep(100);
                        testCapture.StopRecording();
                        testCapture.Dispose();
                        testCapture = null;

                        loopbackCapture = new WasapiLoopbackCapture(tryDevice);
                        lastEx = null;
                    }
                    catch (Exception ex)
                    {
                        try { testCapture?.Dispose(); } catch { }
                        loopbackCapture = null;
                        lastEx = ex;
                    }

                    if (loopbackCapture != null)
                        break;
                }
                catch (Exception) { /* per-device errors already handled inside */ }
            }

            if (loopbackCapture == null)
            {
                string errorDetails = $"Could not initialize loopback on any device (tried {devicesToTry.Count} device(s)). Last error: {lastEx?.GetType().Name}: {lastEx?.Message}";
                Console.WriteLine($"[Silero STT] ERROR: {errorDetails}");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] === LOOPBACK FAILURE DIAGNOSIS ===");
                Console.WriteLine("[Silero STT] Attempted devices:");
                foreach (var dev in devicesToTry)
                    Console.WriteLine($"[Silero STT]   - {dev.FriendlyName}");
                Console.WriteLine("[Silero STT]");
                if (errorDetails.Contains("Value does not fall within the expected range"))
                {
                    Console.WriteLine("[Silero STT] Error Type: WASAPI buffer configuration failed (old Realtek driver issue)");
                }
                else if (errorDetails.Contains("NotImplemented") || errorDetails.Contains("NoInterface"))
                {
                    Console.WriteLine("[Silero STT] Error Type: Device does not support WASAPI Loopback (VoiceMeeter virtual device)");
                }
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] === SUGGESTED FIXES ===");
                Console.WriteLine("[Silero STT] 1) First Priority: Update Realtek Audio drivers to latest version");
                Console.WriteLine("[Silero STT]   - https://www.realtek.com/en/downloads");
                Console.WriteLine("[Silero STT]   - Currently running: 6.0.9520.1 (from ~2015-2017, very outdated)");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] 2) Disable VoiceMeeter from Windows Sound settings");
                Console.WriteLine("[Silero STT]   - Settings > Sound > Volume mixer");
                Console.WriteLine("[Silero STT]   - VoiceMeeter/CABLE intercepts WASAPI and breaks loopback");
                Console.WriteLine("[Silero STT]   - Uninstall or disable it completely");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] 3) Use Microphone mode instead");
                Console.WriteLine("[Silero STT]   - Go to Settings > Audio tab");
                Console.WriteLine("[Silero STT]   - Switch to 'Microphone (Direct Input)' capture mode");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] 4) As last resort: Restart Windows after driver/VoiceMeeter changes");
                Console.WriteLine("[Silero STT]");
                sttLogWriter?.WriteLine($"ERROR: {errorDetails}");
                throw new InvalidOperationException(errorDetails);
            }

            captureWaveFormat = loopbackCapture.WaveFormat;
            minBytesToProcess = Math.Max(captureWaveFormat.AverageBytesPerSecond / 2, 16000);
            bufferedProvider = new BufferedWaveProvider(captureWaveFormat);
            bufferedProvider.DiscardOnBufferOverflow = true;
            bufferedProvider.BufferDuration = TimeSpan.FromSeconds(60);
            var sampleProvider = bufferedProvider.ToSampleProvider();
            var resampler = new WdlResamplingSampleProvider(sampleProvider, 16000);
            processedProvider = resampler.ToMono();

            loopbackCapture.DataAvailable += OnGameAudioReceived;
            Console.WriteLine("[Silero STT] Starting loopback capture...");
            loopbackCapture.StartRecording();
            Console.WriteLine("[Silero STT] ✓ Loopback capture started");
            sttLogWriter?.WriteLine("Loopback capture started");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Initialize microphone: try WASAPI first (native format), then WaveIn.
        /// </summary>
        private async Task InitializeMicrophoneCaptureAsync()
        {
            Console.WriteLine("=== Available Audio Input Devices (Microphones) ===");

            // 1) Try WASAPI capture first - uses device native format, avoids InvalidParameter
            deviceEnumerator = new MMDeviceEnumerator();
            var captureDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            Console.WriteLine($"[Silero STT] WASAPI Capture device count: {captureDevices.Count}");

            foreach (var device in captureDevices)
            {
                WasapiCapture? testCap = null;
                try
                {
                    Console.WriteLine($"[Silero STT] Trying WASAPI device: {device.FriendlyName}");
                    testCap = new WasapiCapture(device);
                    Console.WriteLine($"[Silero STT] WASAPI created, format: {testCap.WaveFormat.SampleRate}Hz, {testCap.WaveFormat.Channels}ch, {testCap.WaveFormat.BitsPerSample}bit");
                    
                    testCap.StartRecording();
                    System.Threading.Thread.Sleep(80);
                    testCap.StopRecording();
                    testCap.Dispose();
                    testCap = null;

                    Console.WriteLine($"[Silero STT] ✓ WASAPI test passed for: {device.FriendlyName}");
                    wasapiMicCapture = new WasapiCapture(device);
                    captureWaveFormat = wasapiMicCapture.WaveFormat;
                    minBytesToProcess = Math.Max(captureWaveFormat.AverageBytesPerSecond / 2, 16000);
                    bufferedProvider = new BufferedWaveProvider(captureWaveFormat);
                    bufferedProvider.DiscardOnBufferOverflow = true;
                    bufferedProvider.BufferDuration = TimeSpan.FromSeconds(60);
                    wasapiMicCapture.DataAvailable += OnGameAudioReceived;
                    Console.WriteLine("[Silero STT] Starting WASAPI microphone capture...");
                    wasapiMicCapture.StartRecording();
                    Console.WriteLine($"[Silero STT] ✓ WASAPI mic started: {captureWaveFormat.SampleRate}Hz, {captureWaveFormat.Channels}ch");
                    sttLogWriter?.WriteLine($"WASAPI microphone: {captureWaveFormat.SampleRate}Hz, {captureWaveFormat.Channels}ch");
                    await Task.CompletedTask;
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Silero STT] ✗ WASAPI device '{device.FriendlyName}' failed: {ex.GetType().Name}: {ex.Message}");
                    try { testCap?.Dispose(); } catch { }
                }
            }

            // If WASAPI failed, show warning and continue to WaveIn
            Console.WriteLine("[Silero STT] ⚠ WASAPI Capture failed on all devices. Falling back to WaveIn (which may not work with your Realtek drivers).");
            Console.WriteLine("[Silero STT] === Detailed Diagnostics ===");
            Console.WriteLine("[Silero STT] Error Pattern: WASAPI objects created but StartRecording() fails with 'Value does not fall within the expected range.'");
            Console.WriteLine("[Silero STT] Likely Cause: Realtek driver 6.0.9520.1 has buffer configuration issues with both WASAPI and WaveIn APIs.");
            Console.WriteLine("[Silero STT]");
            Console.WriteLine("[Silero STT] RECOMMENDED FIXES (try in order):");
            Console.WriteLine("[Silero STT] 1) Update Realtek drivers to latest version (check Realtek website or Windows Update)");
            Console.WriteLine("[Silero STT] 2) Try loopback mode instead: Select 'Стерео микшер' in Speaker Device settings");
            Console.WriteLine("[Silero STT] 3) Disable VoiceMeeter/CABLE: Go to Windows Sound Settings > Volume mixer > disable VoiceMeeter");
            Console.WriteLine("[Silero STT] 4) Do System Restart after any driver changes");
            Console.WriteLine("[Silero STT]");

            // 2) Fallback: WaveIn - but filter out virtual/problematic devices first
            Console.WriteLine($"[Silero STT] WaveIn device count: {WaveIn.DeviceCount}");
            List<int> viableWaveInDevices = new List<int>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    string name = caps.ProductName.ToLower();
                    
                    // Skip known problematic virtual devices
                    if (name.Contains("voicemeeter") || name.Contains("vb-audio") || 
                        name.Contains("cable") || name.Contains("virtual cable"))
                    {
                        Console.WriteLine($"[Silero STT] Skipping virtual device {i}: {caps.ProductName}");
                        continue;
                    }
                    
                    viableWaveInDevices.Add(i);
                    Console.WriteLine($"[Silero STT] Physical device {i}: {caps.ProductName} ({caps.Channels}ch)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Silero STT] Error querying device {i}: {ex.Message}");
                }
            }
            
            if (viableWaveInDevices.Count == 0)
            {
                Console.WriteLine("[Silero STT] Warning: No physical WaveIn devices found. All WaveIn devices are virtual (VoiceMeeter/CABLE).");
            }

            WaveInEvent? micCapture = null;
            Exception? lastEx = null;
            string selectedDeviceName = ConfigManager.Instance.GetAudioMicrophoneDevice();

            // WaveIn (winmm) supports only certain formats; many devices reject 16000/24000/32000 or stereo.
            // Try standard Windows rates first, mono before stereo.
            int[] sampleRates = new[] { 44100, 48000, 22050, 16000, 11025, 8000 };
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
                    testCapture.StartRecording();
                    System.Threading.Thread.Sleep(50);
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

            Console.WriteLine("[Silero STT] Trying all available microphone devices and formats...");
            for (int idx = 0; idx < viableWaveInDevices.Count && micCapture == null; idx++)
            {
                int i = viableWaveInDevices[idx];
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    foreach (var sr in sampleRates)
                    {
                        foreach (var ch in channelOptions)
                        {
                            if (TryInitializeDeviceFormat(i, sr, ch, out Exception? fex))
                            {
                                micCapture = new WaveInEvent { DeviceNumber = i, WaveFormat = new WaveFormat(sr, 16, ch) };
                                Console.WriteLine($"[Silero STT] ✓ Device {i} ({caps.ProductName}) initialized: {sr}Hz, {ch}ch");
                                sttLogWriter?.WriteLine($"Using device {i} ({caps.ProductName}): {sr}Hz, {ch}ch");
                                lastEx = null;
                                break;
                            }
                            lastEx = fex;
                        }
                        if (micCapture != null) break;
                    }
                }
                catch (Exception ex)
                {
                }
            }

            // Fallback: try without explicit WaveFormat (let Windows choose)
            if (micCapture == null)
            {
                Console.WriteLine("[Silero STT] Trying devices with default WaveFormat (no explicit format specified)...");
                for (int idx = 0; idx < viableWaveInDevices.Count && micCapture == null; idx++)
                {
                    int i = viableWaveInDevices[idx];
                    try
                    {
                        var caps = WaveIn.GetCapabilities(i);
                        WaveInEvent? testWave = null;
                        try
                        {
                            testWave = new WaveInEvent { DeviceNumber = i };
                            testWave.StartRecording();
                            System.Threading.Thread.Sleep(50);
                            testWave.StopRecording();
                            testWave.Dispose();
                            
                            micCapture = new WaveInEvent { DeviceNumber = i };
                            var detectedFormat = micCapture.WaveFormat;
                            Console.WriteLine($"[Silero STT] ✓ Device {i} ({caps.ProductName}) using default format: {detectedFormat.SampleRate}Hz, {detectedFormat.Channels}ch");
                            sttLogWriter?.WriteLine($"Using device {i} ({caps.ProductName}) with default format: {detectedFormat.SampleRate}Hz, {detectedFormat.Channels}ch");
                            lastEx = null;
                            break;
                        }
                        catch (Exception ex)
                        {
                            try { testWave?.Dispose(); } catch { }
                            lastEx = ex;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                    }
                }
            }

            if (micCapture == null)
            {
                string phyDevCount = viableWaveInDevices.Count > 0 ? $"{viableWaveInDevices.Count} physical device(s)" : "no physical devices (all virtual)";
                string errorDetails = $"Could not initialize microphone on {phyDevCount} with multiple formats and default formats. Last error: {lastEx?.GetType().Name}: {lastEx?.Message}";
                Console.WriteLine($"[Silero STT] ERROR: {errorDetails}");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] === ROOT CAUSE ANALYSIS ===");
                Console.WriteLine("[Silero STT] Realtek driver 6.0.9520.1 is too old and incompatible with:");
                Console.WriteLine("[Silero STT]   - WASAPI API (fails at StartRecording with 'Value does not fall within expected range')");
                Console.WriteLine("[Silero STT]   - WaveIn API (fails at waveInOpen with 'InvalidParameter')");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] === SUGGESTED WORKAROUNDS ===");
                Console.WriteLine("[Silero STT] Option A: Update Realtek Audio drivers to latest version");
                Console.WriteLine("[Silero STT]   - Visit https://www.realtek.com/en/downloads");
                Console.WriteLine("[Silero STT]   - Or Windows Settings > Update & Security > Optional Updates > Audio drivers");
                Console.WriteLine("[Silero STT]   - Current version: 6.0.9520.1 (very old, from ~2015-2017)");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] Option B: Use Loopback Capture mode");
                Console.WriteLine("[Silero STT]   - Go to Settings > Audio > select 'Loopback Capture' mode");
                Console.WriteLine("[Silero STT]   - Select 'Стерео микшер (Realtek)' as speaker device");
                Console.WriteLine("[Silero STT]   - This captures BOTH microphone input AND speaker output (browser audio, etc)");
                Console.WriteLine("[Silero STT]");
                Console.WriteLine("[Silero STT] Option C: Clean Boot + Disable VoiceMeeter");
                Console.WriteLine("[Silero STT]   - Windows Settings > Sound > Volume mixer");
                Console.WriteLine("[Silero STT]   - Take VoiceMeeter output slider to 0 or uninstall VoiceMeeter");
                Console.WriteLine("[Silero STT]   - Restart Windows");
                Console.WriteLine("[Silero STT]");
                sttLogWriter?.WriteLine($"ERROR: {errorDetails}");
                throw new InvalidOperationException(errorDetails);
            }

            captureWaveFormat = micCapture.WaveFormat;
            minBytesToProcess = Math.Max(captureWaveFormat.AverageBytesPerSecond / 2, 16000);
            bufferedProvider = new BufferedWaveProvider(captureWaveFormat);
            bufferedProvider.DiscardOnBufferOverflow = true;
            bufferedProvider.BufferDuration = TimeSpan.FromSeconds(60);
            processedProvider = null;

            microphoneCapture = micCapture;
            micCapture.DataAvailable += OnGameAudioReceived;
            Console.WriteLine("[Silero STT] Starting microphone capture...");
            micCapture.StartRecording();
            Console.WriteLine("[Silero STT] ✓ Microphone capture started");
            sttLogWriter?.WriteLine("Microphone capture started");

            await Task.CompletedTask;
        }

        private void OnGameAudioReceived(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;

            captureEventCount++;
            if (captureEventCount % 50 == 0)
            {
                Console.WriteLine($"[Silero STT] Audio received: {e.BytesRecorded} bytes (total events: {captureEventCount})");
                sttLogWriter?.WriteLine($"[CAPTURE] {e.BytesRecorded} bytes, events: {captureEventCount}");
            }

            try
            {
                bufferedProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Silero STT] Error in OnGameAudioReceived: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                _isStopping = true;

                try { _cancellationTokenSource?.Cancel(); } catch { }
                try { _cancellationTokenSource?.Dispose(); } catch { }
                _cancellationTokenSource = null;

                try
                {
                    if (processingTask != null && !processingTask.IsCompleted)
                    {
                        processingTask.Wait(3000);
                    }
                }
                catch (AggregateException) { }
                catch (Exception) { }

                if (loopbackCapture != null)
                {
                    try { loopbackCapture.DataAvailable -= OnGameAudioReceived; } catch { }
                    try { loopbackCapture.StopRecording(); } catch { }
                    try { loopbackCapture.Dispose(); } catch { }
                    loopbackCapture = null;
                }

                if (wasapiMicCapture != null)
                {
                    try { wasapiMicCapture.DataAvailable -= OnGameAudioReceived; } catch { }
                    try { wasapiMicCapture.StopRecording(); } catch { }
                    try { wasapiMicCapture.Dispose(); } catch { }
                    wasapiMicCapture = null;
                }

                if (microphoneCapture != null)
                {
                    try { microphoneCapture.DataAvailable -= OnGameAudioReceived; } catch { }
                    try { microphoneCapture.StopRecording(); } catch { }
                    try { microphoneCapture.Dispose(); } catch { }
                    microphoneCapture = null;
                }

                try { deviceEnumerator?.Dispose(); } catch { }
                deviceEnumerator = null;

                try { bufferedProvider?.ClearBuffer(); } catch { }
                bufferedProvider = null;
                processedProvider = null;
                captureWaveFormat = null;

                try { sttLogWriter?.Close(); } catch { }
                sttLogWriter = null;

                audioBuffer.Clear();
                rawByteBuffer.Clear();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Silero STT] Error during Stop(): {ex.Message}");
            }
            finally
            {
                _isStopping = false;
            }
        }

        private async Task ProcessLoop(Action<string, string> onResult, CancellationToken cancellationToken)
        {
            if (captureWaveFormat == null || bufferedProvider == null) return;

            int bytesPerRead = Math.Min(captureWaveFormat.AverageBytesPerSecond / 20, 48000);
            byte[] readBuffer = new byte[bytesPerRead];
            while ((loopbackCapture != null || wasapiMicCapture != null || microphoneCapture != null) && !cancellationToken.IsCancellationRequested && !_isStopping)
            {
                if (bufferedProvider.BufferedBytes >= minBytesToProcess)
                {
                    try
                    {
                        int bytesRead = bufferedProvider.Read(readBuffer, 0, readBuffer.Length);
                        if (bytesRead > 0)
                        {
                            float maxVol = GetMaxAmplitudeFromRawBytes(readBuffer, bytesRead, captureWaveFormat);
                            if (maxVol > SilenceThreshold)
                            {
                                lastVoiceDetected = DateTime.Now;
                                isSpeaking = true;
                            }

                            lock (bufferLock)
                            {
                                for (int i = 0; i < bytesRead; i++)
                                    rawByteBuffer.Add(readBuffer[i]);
                                if (rawByteBuffer.Count > maxRawBufferBytes)
                                {
                                    Console.WriteLine("[Silero STT] Warning: Raw buffer limit, trimming");
                                    rawByteBuffer.RemoveRange(0, rawByteBuffer.Count - maxRawBufferBytes / 2);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Silero STT] Error reading audio: {ex.Message}");
                    }
                }

                bool shouldProcess = isSpeaking && (DateTime.Now - lastVoiceDetected).TotalMilliseconds > SilenceDurationMs;
                if (shouldProcess)
                {
                    byte[] bytesToSend;
                    lock (bufferLock)
                    {
                        bytesToSend = rawByteBuffer.ToArray();
                        rawByteBuffer.Clear();
                    }
                    isSpeaking = false;

                    if (bytesToSend.Length > 0 && !_isStopping)
                    {
                        double durationSec = (double)bytesToSend.Length / captureWaveFormat.AverageBytesPerSecond;
                        Console.WriteLine($"[Silero STT] Sending {bytesToSend.Length} bytes ({durationSec:F1}s, {captureWaveFormat.SampleRate}Hz)");
                        await SendRawWavToSTTServerAsync(bytesToSend, captureWaveFormat, onResult, cancellationToken);
                    }
                }

                try { await Task.Delay(20, cancellationToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        private static float GetMaxAmplitudeFromRawBytes(byte[] buf, int count, WaveFormat fmt)
        {
            float max = 0f;
            if (fmt.BitsPerSample == 32 && fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                for (int i = 0; i + 4 <= count; i += 4)
                {
                    float s = BitConverter.ToSingle(buf, i);
                    float a = Math.Abs(s);
                    if (a > max) max = a;
                }
            }
            else if (fmt.BitsPerSample == 16)
            {
                for (int i = 0; i + 2 <= count; i += 2)
                {
                    short s = BitConverter.ToInt16(buf, i);
                    float a = Math.Abs(s / 32768f);
                    if (a > max) max = a;
                }
            }
            return max;
        }

        private async Task SendRawWavToSTTServerAsync(byte[] rawPcm, WaveFormat format, Action<string, string> onResult, CancellationToken cancellationToken)
        {
            try
            {
                if (httpClient == null || _isStopping) return;

                byte[] wavBytes;
                using (var ms = new MemoryStream())
                {
                    using (var writer = new WaveFileWriter(ms, format))
                    {
                        writer.Write(rawPcm, 0, rawPcm.Length);
                    }
                    wavBytes = ms.ToArray();
                }

                using (var content = new MultipartFormDataContent())
                {
                    var fileContent = new ByteArrayContent(wavBytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                    content.Add(fileContent, "file", "audio.wav");

                    string sttUrl = ConfigManager.Instance.GetLocalSTTUrl();
                    string endpoint = sttUrl.TrimEnd('/') + "/stt_stream";
                    Console.WriteLine($"[Silero STT] POST {wavBytes.Length} bytes WAV ({format.SampleRate}Hz) to {endpoint}");

                    var response = await httpClient.PostAsync(endpoint, content, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                        Console.WriteLine($"[Silero STT] Response: {jsonResponse}");
                        string text = ParseJsonText(jsonResponse);
                        if (!string.IsNullOrEmpty(text) && text.Length > 2)
                        {
                            Console.WriteLine($"[Silero STT] Recognized: {text}");
                            sttLogWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Recognized: {text}");
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                Logic.Instance.AddAudioTextObject(text);
                            });
                            onResult(text, "");
                        }
                    }
                    else
                    {
                        string error = await response.Content.ReadAsStringAsync(cancellationToken);
                        Console.WriteLine($"[Silero STT] Server error {response.StatusCode}: {error}");
                        sttLogWriter?.WriteLine($"Server error {response.StatusCode}: {error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Silero STT] Error sending audio: {ex.Message}");
                sttLogWriter?.WriteLine($"Error: {ex.Message}");
            }
        }

        private string ParseJsonText(string json)
        {
            try
            {
                // Simple JSON parsing for {"text": "value"}
                int textStart = json.IndexOf("\"text\"");
                if (textStart == -1) return "";

                int colonIdx = json.IndexOf(':', textStart);
                int quoteStart = json.IndexOf('"', colonIdx);
                if (quoteStart == -1) return "";

                int quoteEnd = json.IndexOf('"', quoteStart + 1);
                if (quoteEnd == -1) return "";

                return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
            }
            catch
            {
                return "";
            }
        }
    }
}
