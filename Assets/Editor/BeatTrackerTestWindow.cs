using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NAudio.Wave;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Runs audio (synthetic or a WAV file) through BeatTracker offline so tempo detection can be checked without Play Mode.
public class BeatTrackerTestWindow : EditorWindow
{
    const int Rate = 48000;
    const int Chunk = 480; // 10 ms, similar to a capture packet

    float syntheticBpm = 120f;
    bool swing = true;
    string log = "";
    Vector2 scroll;

    [MenuItem("MateEngine/ME Beat Tracker Test")]
    static void Open() => GetWindow<BeatTrackerTestWindow>("ME Beat Tracker Test");

    void OnGUI()
    {
        EditorGUILayout.LabelField("Synthetic drum pattern", EditorStyles.boldLabel);
        syntheticBpm = EditorGUILayout.Slider("BPM", syntheticBpm, 60f, 200f);
        swing = EditorGUILayout.Toggle("Drums + pad (else clicks)", swing);
        if (GUILayout.Button("Run synthetic (30 s)")) log = RunSynthetic(syntheticBpm, swing, 30f);

        EditorGUILayout.Space();
        if (GUILayout.Button("Analyze WAV file..."))
        {
            string path = EditorUtility.OpenFilePanel("WAV file", "", "wav");
            if (!string.IsNullOrEmpty(path)) log = RunFile(path);
        }
        if (GUILayout.Button("Probe per-app capture (this process)")) log = ProbeCapture();

        EditorGUILayout.Space();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.TextArea(log, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ---------- runners ----------

    static string RunSynthetic(float bpm, bool drums, float seconds)
    {
        var samples = drums ? MakeDrums(bpm, seconds) : MakeClicks(bpm, seconds);
        return $"Synthetic {(drums ? "drums+pad" : "clicks")} {bpm:0.#} BPM\n" + Feed(samples, Rate);
    }

    static string RunFile(string path)
    {
        using (var file = new WaveFileReader(path))
        {
            var reader = file.ToSampleProvider();
            int ch = reader.WaveFormat.Channels;
            var all = new List<float>();
            var buf = new float[reader.WaveFormat.SampleRate * ch];
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i + ch - 1 < read; i += ch)
                {
                    float s = 0f;
                    for (int c = 0; c < ch; c++) s += buf[i + c];
                    all.Add(s / ch);
                }
            return $"{System.IO.Path.GetFileName(path)}\n" + Feed(all.ToArray(), reader.WaveFormat.SampleRate);
        }
    }

    static string Feed(float[] samples, int rate)
    {
        var tracker = new BeatTracker();
        tracker.Configure(rate);
        var chunk = new float[Chunk];
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        int nextReport = rate * 2;
        for (int pos = 0; pos < samples.Length; pos += Chunk)
        {
            int n = Mathf.Min(Chunk, samples.Length - pos);
            System.Array.Copy(samples, pos, chunk, 0, n);
            tracker.Process(chunk, n);
            if (pos >= nextReport)
            {
                sb.AppendLine($"  t={pos / (float)rate,5:0.0}s  bpm={(tracker.HasTempo ? tracker.Bpm.ToString("0.0") : "-"),6}  conf={tracker.Confidence:0.00}");
                nextReport += rate * 2;
            }
        }
        sw.Stop();
        double audioSec = samples.Length / (double)rate;
        sb.AppendLine($"  FINAL bpm={(tracker.HasTempo ? tracker.Bpm.ToString("0.0") : "none")} conf={tracker.Confidence:0.00}  " +
                      $"(cpu {sw.Elapsed.TotalMilliseconds:0} ms for {audioSec:0} s audio = {100.0 * sw.Elapsed.TotalSeconds / audioSec:0.00}% of one core)");
        return sb.ToString();
    }

    static string ProbeCapture()
    {
        var cap = AppAudioCapture.Acquire(Process.GetCurrentProcess().Id);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000 && cap.Mode == AppAudioCapture.CaptureMode.Starting)
            System.Threading.Thread.Sleep(20);
        var mode = cap.Mode;
        AppAudioCapture.ShutdownAll();
        return $"Capture probe for PID {Process.GetCurrentProcess().Id}: mode = {mode}";
    }

    // ---------- signals ----------

    static float[] MakeClicks(float bpm, float seconds)
    {
        var x = new float[(int)(seconds * Rate)];
        float period = 60f / bpm * Rate;
        for (float t = 0; t < x.Length; t += period)
            for (int i = 0; i < 240 && (int)t + i < x.Length; i++)
                x[(int)t + i] += 0.8f * Mathf.Exp(-i / 40f) * Mathf.Sin(i * 0.9f);
        return x;
    }

    // Kick on 1 and 3, snare on 2 and 4, hi-hat eighths, over a sustained chord (tests loudness robustness).
    static float[] MakeDrums(float bpm, float seconds)
    {
        var rng = new System.Random(1);
        var x = new float[(int)(seconds * Rate)];
        float beat = 60f / bpm * Rate;
        for (int i = 0; i < x.Length; i++)
        {
            float t = i / (float)Rate;
            x[i] = 0.15f * (Mathf.Sin(2 * Mathf.PI * 220f * t) + Mathf.Sin(2 * Mathf.PI * 277f * t) + Mathf.Sin(2 * Mathf.PI * 330f * t)) / 3f;
        }
        int beatIndex = 0;
        for (float b = 0; b < x.Length; b += beat, beatIndex++)
        {
            int start = (int)b;
            if (beatIndex % 2 == 0)
                for (int i = 0; i < 4800 && start + i < x.Length; i++)
                    x[start + i] += 0.9f * Mathf.Exp(-i / 1200f) * Mathf.Sin(2 * Mathf.PI * (60f + 60f * Mathf.Exp(-i / 600f)) * i / Rate);
            else
                for (int i = 0; i < 3600 && start + i < x.Length; i++)
                    x[start + i] += 0.5f * Mathf.Exp(-i / 700f) * (float)(rng.NextDouble() * 2 - 1);
            for (int e = 0; e < 2; e++)
            {
                int hs = start + (int)(e * beat / 2);
                for (int i = 0; i < 900 && hs + i < x.Length; i++)
                    x[hs + i] += 0.2f * Mathf.Exp(-i / 150f) * (float)(rng.NextDouble() * 2 - 1);
            }
        }
        for (int i = 0; i < x.Length; i++) x[i] = Mathf.Clamp(x[i], -1f, 1f);
        return x;
    }

    // Batch entry point: -executeMethod BeatTrackerTestWindow.RunBatchTests
    public static void RunBatchTests()
    {
        var sb = new StringBuilder("[BeatTrackerTest]\n");
        foreach (float bpm in new[] { 90f, 120f, 150f })
        {
            sb.Append(RunSynthetic(bpm, false, 20f));
            sb.Append(RunSynthetic(bpm, true, 20f));
        }
        sb.AppendLine("Silence:");
        sb.Append(Feed(new float[Rate * 10], Rate));
        sb.AppendLine(ProbeCapture());
        Debug.Log(sb.ToString());
    }
}
