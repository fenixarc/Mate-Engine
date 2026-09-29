using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    const float StickRatio = 0.7f;
    const float TripletStickRatio = 0.5f;
    const float MinUsableConfidence = 0.08f;
    const float SilenceResetSeconds = 4f;
    const float LogCompression = 1000f;
    const float BeatBandHz = 2800f;
    const int FoldBins = 64;

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

    // ---- beat clock (beat phase): a recent beat's QPC time (100 ns units, see Now100ns) + the beat period ----
    const float PhaseWeightSeconds = 3f;   // exponential weighting of the envelope toward recent frames
    const double PhaseBlend = 0.3;         // how much of each new phase measurement enters the clock
    const double PeriodGain = 0.03;        // how much of the per-beat phase drift corrects the clock period
    const double MaxPeriodDrift = 0.03;    // restart the clock if its period strays this far from the tempo
    const float PeriodRefineMinStrength = 0.3f;
    // Onset lag of the spectral-flux frame: a beat's flux peak lands in the frame ending this long after it.
    // Calibrated offline with synthetic clicks (BeatTrackerTestWindow / RunBatchTests).
    public const double OnsetLagSeconds = 0.010;

    // WASAPI packet timestamps are QueryPerformanceCounter time in 100 ns units. Mono's Stopwatch uses a
    // different base, so the beat clock and its readers use QPC directly.
    public const double TicksPerSecond = 1e7;
    [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long count);
    [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long frequency);
    static readonly double qpcTo100ns = QueryPerformanceFrequency(out long f) && f > 0 ? TicksPerSecond / f : 0;

    public static long Now100ns()
    {
        QueryPerformanceCounter(out long c);
        return (long)(c * qpcTo100ns);
    }

    readonly object clockLock = new object();
    long clockAnchorTicks;      // QPC time (100 ns) of a beat (capture clock, before output latency)
    double clockPeriod;         // seconds per beat
    float clockConfidence;
    // audio thread only: smoothed fold-peak clarity and mean |phase error| of new measurements (fraction of a beat)
    float peakConfidence, phaseJitter;
    const float FreshJitter = 0.125f;   // 1/8 beat average error = no consistency
    bool clockValid;

    // Main thread: the latest beat clock. False until a tempo and a beat phase are known.
    public bool TryGetBeatClock(out long anchorTicks, out double periodSeconds, out float phaseConfidence)
    {
        lock (clockLock)
        {
            anchorTicks = clockAnchorTicks;
            periodSeconds = clockPeriod;
            phaseConfidence = clockConfidence;
            return clockValid && hasTempo;
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

    // Onset envelopes: below BeatBandHz (kick, snare, bass, vocals) and full band. Tempo and beat phase use the
    // band-limited one: cowbells/hi-hats above it often sit on off-beats or triplets and pull the tempo to 2/3
    // and the phase to the off-beat. Full band is the fallback when the low band has no clear periodicity.
    float[] envelope = Array.Empty<float>();
    float[] envelopeFull = Array.Empty<float>();
    int beatBandTop;
    readonly float[] foldBins = new float[FoldBins];
    readonly float[] foldSmooth = new float[FoldBins];
    float[] linear = Array.Empty<float>();
    float[] work = Array.Empty<float>();
    float[] acf = Array.Empty<float>();
    int envWrite, envCount, framesSinceEstimate, estimateEvery;
    int minLag, maxLag, quietFrames;
    float pendingBpm;
    bool tempoReplaced;

    // QPC timing (100 ns units) of the input: ticks per input sample, the next sample's time, and the
    // time of the last sample of the frame currently being analysed.
    double ticksPerSample;
    double nextSampleTicks;
    long frameEndTicks;
    double hopTicks;
    float phaseDecay;

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

        beatBandTop = Math.Max(2, Math.Min(FftSize / 2 - 1, (int)Math.Round(BeatBandHz * FftSize / rate)));
        int envLen = (int)Math.Ceiling(EnvelopeSeconds * frameRate);
        envelope = new float[envLen];
        envelopeFull = new float[envLen];
        linear = new float[envLen];
        work = new float[envLen];
        minLag = Math.Max(2, (int)Math.Floor(frameRate * 60f / MaxBpm));
        maxLag = Math.Min(envLen / 3, (int)Math.Ceiling(frameRate * 60f / MinBpm));
        acf = new float[maxLag * 2 + 3];
        estimateEvery = Math.Max(1, (int)Math.Round(EstimateInterval * frameRate));
        ticksPerSample = TicksPerSecond / sampleRate;
        hopTicks = ticksPerSample * hop * decimation;
        phaseDecay = (float)Math.Exp(-1.0 / (PhaseWeightSeconds * frameRate));
        Reset();
    }

    public void Reset()
    {
        Array.Clear(input, 0, input.Length);
        Array.Clear(prevLogMag, 0, prevLogMag.Length);
        if (envelope.Length > 0) Array.Clear(envelope, 0, envelope.Length);
        if (envelopeFull.Length > 0) Array.Clear(envelopeFull, 0, envelopeFull.Length);
        inputFill = envWrite = envCount = framesSinceEstimate = quietFrames = 0;
        haveHalf = false;
        pendingBpm = 0f;
        tempoReplaced = false;
        nextSampleTicks = 0;
        hasTempo = false;
        bpm = 0f;
        confidence = 0f;
        lock (clockLock) { clockValid = false; clockConfidence = 0f; }
    }

    // mono: samples in [-1, 1]. Called on the capture thread. firstSampleTicks is the QPC time (100 ns) of
    // mono[0] (e.g. WASAPI's QPC position); 0 = continue from the previous buffer (or "just now" if none).
    public void Process(float[] mono, int count, long firstSampleTicks = 0)
    {
        if (envelope.Length == 0) return;

        if (firstSampleTicks != 0) nextSampleTicks = firstSampleTicks;
        else
        {
            // No timestamp: continue the sample clock, but re-anchor to "received just now" after gaps
            // (e.g. an app that stops delivering packets while silent).
            double arrived = Now100ns() - count * ticksPerSample;
            if (nextSampleTicks == 0 || Math.Abs(arrived - nextSampleTicks) > 0.03 * TicksPerSecond) nextSampleTicks = arrived;
        }
        double bufferStart = nextSampleTicks;
        nextSampleTicks += count * ticksPerSample;

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
                frameEndTicks = (long)(bufferStart + i * ticksPerSample);
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

        float flux = 0f, beatFlux = 0f;
        const float norm = 2f / FftSize;
        for (int k = 1; k < FftSize / 2; k++)
        {
            if (k == beatBandTop) beatFlux = flux;
            float mag = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * norm;
            float logMag = (float)Math.Log(1.0 + LogCompression * mag);
            float d = logMag - prevLogMag[k];
            if (d > 0f) flux += d;
            prevLogMag[k] = logMag;
        }

        envelope[envWrite] = beatFlux;
        envelopeFull[envWrite] = flux;
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
        if (!EstimateFrom(envelope)) EstimateFrom(envelopeFull);
    }

    // Tempo (and then beat phase) from one onset envelope. False if it has no usable periodicity.
    bool EstimateFrom(float[] ring)
    {
        int n = envCount;
        int start = (envWrite - n + ring.Length) % ring.Length;
        for (int i = 0; i < n; i++) linear[i] = ring[(start + i) % ring.Length];

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
        if (zero <= 1e-9f) return false;

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
        if (best < 0) return false;

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

        // Stickiness: syncopation (e.g. 3-3-2 kick patterns in trap/phonk) can briefly make a 3:2-related period
        // the strongest. Keep the established tempo while its own periodicity is still nearly as strong.
        // Triplet-related candidates (2/3, 3/2, 3/4, 4/3 of the current tempo) are the usual false switch, so they
        // need the current tempo's periodicity to have clearly faded (a real song change removes it entirely).
        if (hasTempo)
        {
            int current = PeakNear(60f * frameRate / bpm, top);
            if (current >= minLag && current <= maxLag && current != chosen)
            {
                float ratio = LagToBpm(chosen) / bpm;
                bool triplet = Near(ratio, 2f / 3f) || Near(ratio, 1.5f) || Near(ratio, 0.75f) || Near(ratio, 4f / 3f);
                if (acf[current] >= (triplet ? TripletStickRatio : StickRatio) * acf[chosen]) chosen = current;
            }
        }

        float period = chosen;
        float a = acf[chosen - 1], b = acf[chosen], c2 = acf[chosen + 1];
        float denom = a - 2f * b + c2;
        if (denom < -1e-6f) period = chosen + Math.Max(-0.5f, Math.Min(0.5f, 0.5f * (a - c2) / denom));

        float conf = Math.Max(0f, Math.Min(1f, b));
        if (conf < MinUsableConfidence) { confidence = conf; return false; }
        Accept(60f * frameRate / period, conf);
        if (hasTempo) UpdateBeatClock(n);
        return true;
    }

    // Beat phase: fold the (rectified) onset envelope over one beat period, weighted toward recent frames, and
    // take the strongest position. Folding keeps the 8th/16th structure that is locked to the beat, which
    // matters for syncopated music where the plain beat frequency is weak.
    void UpdateBeatClock(int n)
    {
        // The clock refines its own period from phase drift (the ACF tempo is only good to ~0.2%);
        // fall back to the tempo when there is no clock yet or the two have drifted apart.
        double tempoPeriod = 60.0 / bpm;
        bool restart = !clockValid || tempoReplaced || Math.Abs(Math.Log(clockPeriod / tempoPeriod)) > MaxPeriodDrift;
        tempoReplaced = false;
        double periodSeconds = restart ? tempoPeriod : clockPeriod;
        double periodFrames = periodSeconds * frameRate;

        // Histogram of onset strength by age within the beat (age 0 = newest frame), linearly split between bins.
        Array.Clear(foldBins, 0, FoldBins);
        double binsPerFrame = FoldBins / periodFrames, pos = 0;
        float weight = 1f;
        for (int j = 0; j < n; j++)
        {
            float v = work[n - 1 - j] * weight;
            int b0 = (int)pos;
            float fr = (float)(pos - b0);
            foldBins[b0] += v * (1f - fr);
            foldBins[b0 + 1 == FoldBins ? 0 : b0 + 1] += v * fr;
            pos += binsPerFrame;
            if (pos >= FoldBins) pos -= FoldBins;
            weight *= phaseDecay;
        }
        // Triangular smoothing (+-2 bins), then the peak.
        int best = 0;
        for (int b = 0; b < FoldBins; b++)
        {
            float s = 0f;
            for (int k = -2; k <= 2; k++) s += foldBins[(b + k + FoldBins) % FoldBins] * (3 - Math.Abs(k));
            foldSmooth[b] = s;
            if (s > foldSmooth[best]) best = b;
        }
        if (foldSmooth[best] <= 1e-9f) return;

        // Confidence: how clearly the peak beats the strongest alternative more than 1/8 beat away (e.g. the off-beat).
        float alt = 0f;
        for (int b = 0; b < FoldBins; b++)
        {
            int dist = Math.Abs(b - best);
            if (dist > FoldBins / 2) dist = FoldBins - dist;
            if (dist > FoldBins / 8 && foldSmooth[b] > alt) alt = foldSmooth[b];
        }
        float strength = 1f - alt / foldSmooth[best];

        // Sub-bin peak position (parabola); the newest beat is that many bins' worth of frames before the newest frame.
        float l = foldSmooth[(best - 1 + FoldBins) % FoldBins], m = foldSmooth[best], r = foldSmooth[(best + 1) % FoldBins];
        float den = l - 2f * m + r;
        double peak = best + (den < -1e-9f ? Math.Max(-0.5f, Math.Min(0.5f, 0.5f * (l - r) / den)) : 0f);
        double ageFrames = (peak / FoldBins) * periodFrames;
        if (ageFrames < 0) ageFrames += periodFrames;

        const double hz = TicksPerSecond;
        long measured = frameEndTicks - (long)(ageFrames * hopTicks + OnsetLagSeconds * hz);

        lock (clockLock)
        {
            if (restart)
            {
                // New tempo (or the clock drifted too far from it): start over from this measurement. Consistency
                // starts pessimistic, so a fresh clock must agree with itself for a second or two before it's trusted.
                clockAnchorTicks = measured;
                clockPeriod = periodSeconds;
                clockValid = true;
                peakConfidence = strength;
                phaseJitter = FreshJitter;
                clockConfidence = 0.5f * peakConfidence;
                return;
            }

            // Advance the old anchor by whole beats to the measurement, then blend in the wrapped difference;
            // a consistent difference means the period is off, so nudge it too (second-order loop).
            double periodTicks = clockPeriod * hz;
            double beats = Math.Round((measured - clockAnchorTicks) / periodTicks);
            double predicted = clockAnchorTicks + beats * periodTicks;
            double diff = measured - predicted;
            clockAnchorTicks = (long)(predicted + PhaseBlend * diff);

            // Confidence = half peak clarity, half consistency. Dense music (distorted guitars between the beats)
            // gives an unclear fold peak but a steady phase; a wrong tempo gives neither.
            phaseJitter = phaseJitter * 0.8f + 0.2f * (float)(Math.Abs(diff) / periodTicks);
            float consistency = Math.Max(0f, 1f - phaseJitter / FreshJitter);
            peakConfidence = peakConfidence * 0.7f + strength * 0.3f;
            clockConfidence = 0.5f * (peakConfidence + consistency);

            // Only trust confident measurements to refine the period; noisy ones made it wander by several percent.
            if (beats >= 1 && clockConfidence >= PeriodRefineMinStrength) clockPeriod += PeriodGain * diff / beats / hz;
        }
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

    static bool Near(float ratio, float target) => Math.Abs(ratio / target - 1f) < 0.03f;

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
            tempoReplaced = true;
        }
        else pendingBpm = est;
    }
}
