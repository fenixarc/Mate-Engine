using System;

// Keeps the built-in dance's beats on the music's beats with small speed corrections (a phase-locked
// loop layered on the tempo-matched DanceSpeed). Plain C# so it can be simulated outside Unity.
public sealed class BeatPhaseLock
{
    public enum State { Off, Hold, Acquire, Lock }

    // Tuning (seconds / speed fractions). Acquire pulls in quickly after a tempo or dance change;
    // Lock holds gently so the correction is invisible.
    public double LockTau = 2.0;
    public double AcquireTau = 0.8;
    public float MaxLockCorrection = 0.06f;
    public float MaxAcquireCorrection = 0.1f;
    public double AcquireAbove = 0.12;   // enter Acquire above this error (fraction of the finer beat)
    public double LockBelow = 0.05;      // back to Lock below this error
    public double DeadbandSeconds = 0.012;
    public float Slew = 0.4f;            // max change of the correction per second

    public State Mode { get; private set; } = State.Off;
    public float Correction { get; private set; }
    public double ErrorSeconds { get; private set; }

    public void Reset()
    {
        Mode = State.Off;
        Correction = 0f;
        ErrorSeconds = 0;
    }

    // Eases the correction back to zero (dance switching, no beat clock, ...). Off = nothing to lock to.
    public float Release(float dt, bool off)
    {
        Mode = off ? State.Off : State.Hold;
        ErrorSeconds = 0;
        Correction = MoveTowards(Correction, 0f, Slew * dt);
        return Correction;
    }

    // musicSinceBeat: seconds since the last (audible) music beat; musicPeriod: seconds per music beat.
    // danceBeatPos: dance position in its own beats (integer = on a beat); dancePeriod: seconds per dance
    // beat at the tempo-matched speed, WITHOUT this correction (so the grid check isn't fooled by it).
    // Returns the speed correction c (DanceSpeed x (1 + c)).
    public float Step(double musicSinceBeat, double musicPeriod, double danceBeatPos, double dancePeriod, float dt)
    {
        if (musicPeriod <= 0 || dancePeriod <= 0) return Release(dt, true);

        // Only lockable when the two grids are a power of two apart (the tempo match folds by x2 / /2).
        double octaves = Math.Log(musicPeriod / dancePeriod, 2.0);
        if (Math.Abs(octaves - Math.Round(octaves)) > 0.03) return Release(dt, false);

        // Time to each side's next beat; wrap by the finer grid: every beat of the coarser grid must
        // land on some beat of the finer one (bars/downbeats are not tracked).
        double musicPhase = musicSinceBeat / musicPeriod;
        musicPhase -= Math.Floor(musicPhase);
        double toMusic = (1.0 - musicPhase) * musicPeriod;
        double toDance = (Math.Ceiling(danceBeatPos) - danceBeatPos) * dancePeriod;
        double fine = Math.Min(musicPeriod, dancePeriod);
        double e = toDance - toMusic;               // > 0: the dance beat comes late, so speed up
        e -= Math.Round(e / fine) * fine;
        ErrorSeconds = e;

        double rel = Math.Abs(e) / fine;
        if (Mode != State.Acquire && Mode != State.Lock) Mode = rel > LockBelow ? State.Acquire : State.Lock;
        else if (Mode == State.Lock && rel > AcquireAbove) Mode = State.Acquire;
        else if (Mode == State.Acquire && rel < LockBelow) Mode = State.Lock;

        double target;
        if (Mode == State.Acquire)
            target = Clamp(e / AcquireTau, MaxAcquireCorrection);
        else
        {
            double over = Math.Max(0.0, Math.Abs(e) - DeadbandSeconds) * Math.Sign(e);
            target = Clamp(over / LockTau, MaxLockCorrection);
        }
        Correction = MoveTowards(Correction, (float)target, Slew * dt);
        return Correction;
    }

    static double Clamp(double v, float limit) => v > limit ? limit : v < -limit ? -limit : v;

    static float MoveTowards(float current, float target, float maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta) return target;
        return current + Math.Sign(target - current) * maxDelta;
    }
}
