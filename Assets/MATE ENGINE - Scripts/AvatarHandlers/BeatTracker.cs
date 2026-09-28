using System;
using System.Diagnostics;
using System.Threading;

// Estimates the tempo of a mono audio stream: spectral-flux onset envelope -> autocorrelation.
// Fed from the audio capture thread; results are read from the main thread without locks.
// All buffers are allocated in Configure(), so Process() does not allocate.
public sealed class BeatTracker
{
    const int FftSize = 512;
    const float EnvelopeSeconds = 8f;
    const float MinFillSeconds = 4f;
    const float EstimateInterval = 0.5f;
    const float MinBpm = 60f, MaxBpm = 200f;
    const float PriorCenterBpm = 120f, PriorOctaves = 0.9f;
    const float OctaveKeepRatio = 0.5f;
    const float MinUsableConfidence = 0.08f;
    const float SilenceResetSeconds = 4f;
    const float LogCompression = 1000f;

    // ---- results (written by the audio thread, read by the main thread) ----
    volatile float bpm;
    volatile float confidence;
    volatile bool hasTempo;
    long lastLoudTicks;

    public float Bpm => bpm;
    public float Confidence => confidence;
    public bool HasTempo => hasTempo;
    public volatile float LoudThreshold = 0.02f;

    public double SecondsSinceLoud
    {
        get
        {
            long t = Interlocked.Read(ref lastLoudTicks);
            return t == 0 ? double.MaxValue : (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
        }
    }

    // ---- FFT tables ----
    readonly float[] window = new float[FftSize];
    readonly float[] cosTable = new float[FftSize / 2];
    readonly float[] sinTable = new float[FftSize / 2];
    readonly int[] bitReverse = new int[FftSize];
    readonly float[] re = new float[FftSize];
    readonly float[] im = new float[FftSize];

    // ---- analysis state (audio thread only) ----
    int hop, decimation;
    bool haveHalf;
    float halfSample;
    float frameRate;
    readonly float[] input = new float[FftSize];
    int inputFill;
    readonly float[] prevLogMag = new float[FftSize / 2];

    float[] envelope = Array.Empty<float>();
    float[] linear = Array.Empty<float>();
    float[] work = Array.Empty<float>();
    float[] acf = Array.Empty<float>();
    int envWrite, envCount, framesSinceEstimate, estimateEvery;
    int minLag, maxLag, quietFrames;
    float pendingBpm;

    public BeatTracker()
    {
        int bits = 0;
        while ((1 << bits) < FftSize) bits++;
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            bitReverse[i] = r;
        }
        for (int i = 0; i < FftSize / 2; i++)
        {
            cosTable[i] = (float)Math.Cos(2.0 * Math.PI * i / FftSize);
            sinTable[i] = (float)-Math.Sin(2.0 * Math.PI * i / FftSize);
        }
    }

    public void Configure(int sampleRate)
    {
        sampleRate = Math.Max(8000, sampleRate);
        // Analyse at ~24 kHz: onsets need nothing above 12 kHz, and it halves the per-frame work.
        decimation = sampleRate >= 32000 ? 2 : 1;
        int rate = sampleRate / decimation;
        hop = FftSize / 2;
        frameRate = rate / (float)hop;

        int envLen = (int)Math.Ceiling(EnvelopeSeconds * frameRate);
        envelope = new float[envLen];
        linear = new float[envLen];
        work = new float[envLen];
        minLag = Math.Max(2, (int)Math.Floor(frameRate * 60f / MaxBpm));
        maxLag = Math.Min(envLen / 3, (int)Math.Ceiling(frameRate * 60f / MinBpm));
        acf = new float[maxLag * 2 + 3];
        estimateEvery = Math.Max(1, (int)Math.Round(EstimateInterval * frameRate));
        Reset();
    }

    public void Reset()
    {
        Array.Clear(input, 0, input.Length);
        Array.Clear(prevLogMag, 0, prevLogMag.Length);
        if (envelope.Length > 0) Array.Clear(envelope, 0, envelope.Length);
        inputFill = envWrite = envCount = framesSinceEstimate = quietFrames = 0;
        haveHalf = false;
        pendingBpm = 0f;
        hasTempo = false;
        bpm = 0f;
        confidence = 0f;
    }

    // mono: samples in [-1, 1]. Called on the capture thread.
    public void Process(float[] mono, int count)
    {
        if (envelope.Length == 0) return;

        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float s = mono[i];
            float a = s < 0f ? -s : s;
            if (a > peak) peak = a;

            if (decimation == 2)
            {
                if (!haveHalf) { halfSample = s; haveHalf = true; continue; }
                s = (s + halfSample) * 0.5f;
                haveHalf = false;
            }

            input[inputFill++] = s;
            if (inputFill == FftSize)
            {
                AnalyzeFrame();
                Array.Copy(input, hop, input, 0, FftSize - hop);
                inputFill = FftSize - hop;
            }
        }
        if (peak > LoudThreshold) Interlocked.Exchange(ref lastLoudTicks, Stopwatch.GetTimestamp());
    }

    void AnalyzeFrame()
    {
        float energy = 0f;
        for (int i = 0; i < FftSize; i++)
        {
            float s = input[i] * window[i];
            int j = bitReverse[i];
            re[j] = s;
            im[j] = 0f;
            energy += s * s;
        }
        Fft();

        float flux = 0f;
        const float norm = 2f / FftSize;
        for (int k = 1; k < FftSize / 2; k++)
        {
            float mag = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * norm;
            float logMag = (float)Math.Log(1.0 + LogCompression * mag);
            float d = logMag - prevLogMag[k];
            if (d > 0f) flux += d;
            prevLogMag[k] = logMag;
        }

        envelope[envWrite] = flux;
        envWrite = (envWrite + 1) % envelope.Length;
        if (envCount < envelope.Length) envCount++;

        // Long silence (e.g. paused): forget the tempo so the next song starts fresh.
        if (energy < 1e-7f) { if (++quietFrames > SilenceResetSeconds * frameRate && hasTempo) Reset(); }
        else quietFrames = 0;

        if (++framesSinceEstimate >= estimateEvery && envCount >= MinFillSeconds * frameRate)
        {
            framesSinceEstimate = 0;
            Estimate();
        }
    }

    // In-place iterative radix-2 FFT on re/im (input already bit-reversed).
    void Fft()
    {
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int halfSize = size >> 1, step = FftSize / size;
            for (int start = 0; start < FftSize; start += size)
            {
                for (int k = 0, t = 0; k < halfSize; k++, t += step)
                {
                    int a = start + k, b = a + halfSize;
                    float wr = cosTable[t], wi = sinTable[t];
                    float xr = re[b] * wr - im[b] * wi;
                    float xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
            }
        }
    }

    void Estimate()
    {
        int n = envCount;
        int start = (envWrite - n + envelope.Length) % envelope.Length;
        for (int i = 0; i < n; i++) linear[i] = envelope[(start + i) % envelope.Length];

        // Subtract a local mean (~0.25 s) so sustained loudness doesn't count, then half-wave rectify.
        int half = Math.Max(1, (int)(frameRate * 0.125f));
        float runSum = 0f;
        int runCount = 0;
        for (int i = 0; i < Math.Min(half, n); i++) { runSum += linear[i]; runCount++; }
        for (int i = 0; i < n; i++)
        {
            int add = i + half, drop = i - half - 1;
            if (add < n) { runSum += linear[add]; runCount++; }
            if (drop >= 0) { runSum -= linear[drop]; runCount--; }
            float v = linear[i] - runSum / runCount;
            work[i] = v > 0f ? v : 0f;
        }
        float mean = 0f;
        for (int i = 0; i < n; i++) mean += work[i];
        mean /= n;
        float zero = 0f;
        for (int i = 0; i < n; i++) { linear[i] = work[i] - mean; zero += linear[i] * linear[i]; }
        if (zero <= 1e-9f) return;

        int top = Math.Min(maxLag * 2 + 2, n - 1);
        for (int lag = 1; lag <= top; lag++)
        {
            float s = 0f;
            for (int i = lag; i < n; i++) s += linear[i] * linear[i - lag];
            acf[lag] = s / zero;
        }

        // Strongest periodicity, gently weighted toward typical tempos.
        int best = -1;
        float bestScore = 0f;
        for (int lag = minLag; lag <= maxLag && lag < top; lag++)
        {
            if (!IsPeak(lag, top) || acf[lag] <= 0f) continue;
            float score = acf[lag] * Prior(LagToBpm(lag));
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        if (best < 0) return;

        // Octave check: hi-hats can make the half-beat strongest and kick/snare patterns the two-beat
        // period. If the double or half tempo is nearly as periodic and closer to 120 BPM, prefer it.
        int chosen = best;
        float bestAc = acf[best];
        for (int o = 0; o < 2; o++)
        {
            int c = PeakNear(o == 0 ? best * 0.5f : best * 2f, top);
            if (c < minLag || c > maxLag || acf[c] < OctaveKeepRatio * bestAc) continue;
            if (Math.Abs(Math.Log(LagToBpm(c) / PriorCenterBpm)) < Math.Abs(Math.Log(LagToBpm(chosen) / PriorCenterBpm)))
                chosen = c;
        }

        float period = chosen;
        float a = acf[chosen - 1], b = acf[chosen], c2 = acf[chosen + 1];
        float denom = a - 2f * b + c2;
        if (denom < -1e-6f) period = chosen + Math.Max(-0.5f, Math.Min(0.5f, 0.5f * (a - c2) / denom));

        float conf = Math.Max(0f, Math.Min(1f, b));
        if (conf < MinUsableConfidence) { confidence = conf; return; }
        Accept(60f * frameRate / period, conf);
    }

    bool IsPeak(int lag, int top) => lag > 1 && lag < top && acf[lag] >= acf[lag - 1] && acf[lag] >= acf[lag + 1];

    int PeakNear(float lag, int top)
    {
        int center = (int)Math.Round(lag), found = -1;
        for (int l = center - 2; l <= center + 2; l++)
            if (l > 1 && l < top && IsPeak(l, top) && (found < 0 || acf[l] > acf[found])) found = l;
        return found;
    }

    float LagToBpm(float lag) => 60f * frameRate / lag;

    static float Prior(float lagBpm)
    {
        double oct = Math.Log(lagBpm / PriorCenterBpm, 2.0) / PriorOctaves;
        return (float)Math.Exp(-0.5 * oct * oct);
    }

    void Accept(float est, float conf)
    {
        if (hasTempo)
        {
            // Stay in the current octave when the estimate flips to half/double time.
            double lr = Math.Log(est / bpm, 2.0);
            if (Math.Abs(lr - 1.0) < 0.06) est *= 0.5f;
            else if (Math.Abs(lr + 1.0) < 0.06) est *= 2f;

            if (Math.Abs(Math.Log(est / bpm)) < 0.04)
            {
                bpm = (float)Math.Exp(Math.Log(bpm) * 0.7 + Math.Log(est) * 0.3);
                confidence = confidence * 0.7f + conf * 0.3f;
                pendingBpm = 0f;
                return;
            }
        }

        // A new tempo must be seen twice in a row before it replaces the current one.
        if (pendingBpm > 0f && Math.Abs(Math.Log(est / pendingBpm)) < 0.04)
        {
            bpm = (est + pendingBpm) * 0.5f;
            confidence = conf;
            hasTempo = true;
            pendingBpm = 0f;
        }
        else pendingBpm = est;
    }
}
