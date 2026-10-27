using NAudio.Wave;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Whisper.net;
using Whisper.net.Ggml;

namespace LiveTranslator;

public partial class MainWindow : Window
{
    private const string ModelFile = "ggml-small.bin";

    // ---- Tuning knobs (safe to experiment with) ----
    private const float SpeechRmsThreshold = 0.008f;        // below this volume = silence
    private const double PauseSeconds = 0.6;                // this much silence = end of a phrase
    private const double MinSpeechSeconds = 0.5;            // shorter blips are ignored
    private const double MaxChunkSeconds = 7.0;             // force a cut so subtitles never lag
    private const double ForgetLanguageAfterSeconds = 10.0; // quiet this long = detect language again

    private double _selectedOpacity = 0.7;
    private string _selectedColor = "#FFF2F2F2";

    private static readonly Dictionary<string, string> SupportedLanguages = new()
    {
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["de"] = "German",
        ["it"] = "Italian",
        ["pt"] = "Portuguese",
        ["ru"] = "Russian",
        ["nl"] = "Dutch",
        ["pl"] = "Polish",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
        ["zh"] = "Chinese",
        ["tr"] = "Turkish",
        ["uk"] = "Ukrainian",
        ["sv"] = "Swedish",
        ["id"] = "Indonesian",
        ["ca"] = "Catalan",
        ["ar"] = "Arabic",
        ["vi"] = "Vietnamese",
        ["hi"] = "Hindi",
        ["cs"] = "Czech"
    };

    // Lines Whisper tends to invent when there is no real speech
    private static readonly HashSet<string> JunkPhrases = new()
    {
        "thanks for watching", "thank you for watching", "please subscribe",
        "subtitles by the amara.org community", "you"
    };

    private OverlayWindow? _overlay;
    private CancellationTokenSource? _cts;
    private bool _isTranslating;

    public MainWindow()
    {
        InitializeComponent();

        // Preload the model in the background as soon as the app opens so the first translation starts instantly
        Loaded += async (s, e) => await PreloadModelAsync();
    }

    private async Task PreloadModelAsync()
    {
        try
        {
            string modelPath = Path.Combine(AppContext.BaseDirectory, ModelFile);
            if (!File.Exists(modelPath))
            {
                Dispatcher.Invoke(() => OutputBox.Text = "Downloading model in background...");
                string tempPath = modelPath + ".part";
                using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small))
                using (var fileStream = File.Create(tempPath))
                {
                    await modelStream.CopyToAsync(fileStream);
                }
                File.Move(tempPath, modelPath);
            }

            // Warm up the factory in the background so it's cached and ready
            await Task.Run(() =>
            {
                using var factory = WhisperFactory.FromPath(modelPath);
            });

            Dispatcher.Invoke(() => OutputBox.Text = "Ready. Model preloaded. Click 'Start Translation' to begin.");
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => OutputBox.Text = $"Model preload warning: {ex.Message}");
        }
    }

    private void TransparencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _selectedOpacity = e.NewValue;
        if (OpacityText != null) OpacityText.Text = $"{Math.Round(_selectedOpacity * 100)}%";
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            _selectedColor = hex;

            // Dim all color buttons, make the selected one bright
            BtnWhite.Opacity = (btn == BtnWhite) ? 1.0 : 0.4;
            BtnGreen.Opacity = (btn == BtnGreen) ? 1.0 : 0.4;
            BtnYellow.Opacity = (btn == BtnYellow) ? 1.0 : 0.4;
            BtnRed.Opacity = (btn == BtnRed) ? 1.0 : 0.4;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating)
        {
            _cts?.Cancel();
            return;
        }

        _isTranslating = true;
        StartButton.Content = "Stop Translation";
        ExitButton.IsEnabled = false;
        Hide();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            _overlay = new OverlayWindow(() => _cts?.Cancel(), _selectedOpacity, _selectedColor);
            _overlay.Show();
            _overlay.UpdateSubtitle("Loading...");

            await Task.Run(() => RunPipelineAsync(token), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OutputBox.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ResetUiState();
        }
    }

    private async Task RunPipelineAsync(CancellationToken token)
    {
        // 1. Make sure the model exists
        string modelPath = Path.Combine(AppContext.BaseDirectory, ModelFile);
        if (!File.Exists(modelPath))
        {
            _overlay?.UpdateSubtitle("Downloading model...");
            string tempPath = modelPath + ".part";
            using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small))
            using (var fileStream = File.Create(tempPath))
            {
                await modelStream.CopyToAsync(fileStream, token);
            }
            File.Move(tempPath, modelPath);
        }

        _overlay?.UpdateSubtitle("Loading model...");
        using var factory = WhisperFactory.FromPath(modelPath);

        // 2. The processor is rebuilt only when the language changes
        WhisperProcessor? processor = null;
        string activeLanguage = "";
        string desiredLanguage = "auto";

        void BuildProcessor()
        {
            processor?.Dispose();
            activeLanguage = desiredLanguage;
            processor = factory.CreateBuilder()
                .WithLanguage(activeLanguage)
                .WithTranslate()
                .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
                .Build();
        }

        // 3. Capture PC audio, converted to mono as it arrives
#pragma warning disable CS0618
        using var capture = new WasapiLoopbackCapture();
#pragma warning restore CS0618
        var format = capture.WaveFormat;
        int sampleRate = format.SampleRate;
        var pending = new List<float>();
        var gate = new object();

        capture.DataAvailable += (s, a) =>
        {
            int bytesPerSample = format.BitsPerSample / 8;
            int frameSize = bytesPerSample * format.Channels;
            lock (gate)
            {
                for (int i = 0; i + frameSize <= a.BytesRecorded; i += frameSize)
                {
                    float sum = 0;
                    for (int c = 0; c < format.Channels; c++)
                    {
                        int offset = i + c * bytesPerSample;
                        sum += format.BitsPerSample == 32
                            ? BitConverter.ToSingle(a.Buffer, offset)
                            : BitConverter.ToInt16(a.Buffer, offset) / 32768f;
                    }
                    pending.Add(sum / format.Channels);
                }
            }
        };

        capture.StartRecording();
        _overlay?.UpdateSubtitle("Listening...");

        double idleSeconds = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token);

                // 4. Decide whether a full phrase is ready
                float[]? snapshot = null;
                lock (gate)
                {
                    Analyze(pending, sampleRate, out int voiced, out int trailingSilence, out int total);

                    if (voiced == 0)
                    {
                        int keep = sampleRate * 3 / 10;
                        if (pending.Count > keep) pending.RemoveRange(0, pending.Count - keep);

                        idleSeconds += 0.1;
                        if (idleSeconds >= ForgetLanguageAfterSeconds) desiredLanguage = "auto";
                        continue;
                    }
                    idleSeconds = 0;

                    bool phraseEnded = trailingSilence / 10.0 >= PauseSeconds;
                    if (phraseEnded && voiced / 10.0 < MinSpeechSeconds)
                    {
                        pending.Clear();
                        continue;
                    }

                    if (phraseEnded || total / 10.0 >= MaxChunkSeconds)
                    {
                        snapshot = pending.ToArray();
                        pending.Clear();
                    }
                    else
                    {
                        continue;
                    }
                }

                // 5. Translate the phrase
                float[] samples = Resample(snapshot, sampleRate, 16000);

                if (processor == null || activeLanguage != desiredLanguage) BuildProcessor();

                var text = new StringBuilder();
                string detected = "";
                await foreach (var segment in processor!.ProcessAsync(samples, token))
                {
                    if (detected.Length == 0) detected = segment.Language ?? "";
                    string cleaned = segment.Text.Trim();
                    if (!IsJunk(cleaned)) text.Append(cleaned).Append(' ');
                }

                // 6. Language check
                if (desiredLanguage == "auto" && detected.Length > 0)
                {
                    if (!SupportedLanguages.ContainsKey(detected)) continue;
                    desiredLanguage = detected;
                }

                string line = text.ToString().Trim();
                if (line.Length > 0)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        OutputBox.Text = line;
                        _overlay?.UpdateSubtitle(line);
                    });
                }
            }
        }
        finally
        {
            capture.StopRecording();
            processor?.Dispose();
        }
    }

    private static void Analyze(List<float> buffer, int sampleRate, out int voiced, out int trailingSilence, out int total)
    {
        int block = sampleRate / 10;
        total = buffer.Count / block;
        voiced = 0;
        trailingSilence = 0;

        for (int b = 0; b < total; b++)
        {
            double sumSquares = 0;
            int start = b * block;
            for (int i = start; i < start + block; i++) sumSquares += buffer[i] * buffer[i];

            if (Math.Sqrt(sumSquares / block) > SpeechRmsThreshold)
            {
                voiced++;
                trailingSilence = 0;
            }
            else
            {
                trailingSilence++;
            }
        }
    }

    private static bool IsJunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.StartsWith('[') || text.StartsWith('(') || text.Contains('♪')) return true;
        return JunkPhrases.Contains(text.TrimEnd('.', '!', ' ').ToLowerInvariant());
    }

    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;
        double ratio = (double)fromRate / toRate;
        int outLength = (int)(input.Length / ratio);
        var output = new float[outLength];
        for (int i = 0; i < outLength; i++)
        {
            int start = (int)(i * ratio);
            int end = Math.Min((int)((i + 1) * ratio), input.Length);
            if (end <= start) end = start + 1;
            float sum = 0;
            for (int j = start; j < end; j++) sum += input[j];
            output[i] = sum / (end - start);
        }
        return output;
    }

    private void ResetUiState()
    {
        _isTranslating = false;
        StartButton.Content = "Start Translation";
        ExitButton.IsEnabled = true;

        try { _overlay?.Close(); } catch { }
        _overlay = null;

        Show();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }
}