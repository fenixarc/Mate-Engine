using UnityEngine;
using NAudio.CoreAudioApi;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections;

public class AvatarAnimatorController : MonoBehaviour
{
    [Header("State Values")]
    public Animator animator;
    public float SOUND_THRESHOLD = 0.02f;
    public List<string> allowedApps = new();
    public int totalIdleAnimations = 10;
    public float IDLE_SWITCH_TIME = 12f, IDLE_TRANSITION_TIME = 3f;
    public int DANCE_CLIP_COUNT = 5;

    [Header("Dancing")]
    public bool enableDancing = true;           
    public bool enableDanceSwitch = true;
    public float DANCE_SWITCH_TIME = 15f;
    public float DANCE_TRANSITION_TIME = 2f;       

    public bool BlockDraggingOverride = false;

    private static readonly int danceIndexParam = Animator.StringToHash("DanceIndex");
    private static readonly int isIdleParam = Animator.StringToHash("isIdle");
    private static readonly int isDraggingParam = Animator.StringToHash("isDragging");
    private static readonly int isDancingParam = Animator.StringToHash("isDancing");
    private static readonly int idleIndexParam = Animator.StringToHash("IdleIndex");

    private MMDevice defaultDevice;
    private MMDeviceEnumerator enumerator;
    private Coroutine soundCheckCoroutine, idleTransitionCoroutine, danceTransitionCoroutine;
    private float lastSoundCheckTime, idleTimer, danceTimer;
    private int idleState, danceState;
    private float dragLockTimer;
    private bool mouseHeld;
    public bool isDragging, isDancing, isIdle;

    [Header("Character Mode")]
    public bool enableHusbandoMode = false;
    private static readonly int isMaleParam = Animator.StringToHash("isMale");
    private static readonly int isFemaleParam = Animator.StringToHash("isFemale");

    [Header("BPM Sync")]
    public bool enableBpmSync = true;
    [Tooltip("Tempo estimates below this confidence are ignored (dance plays at normal speed). Once engaged, sync holds down to 2/3 of it.")]
    public float minBpmConfidence = 0.3f;
    [Tooltip("Limits for the Dance state's speed multiplier (music BPM / dance BPM).")]
    public float minDanceSpeed = 0.6f;
    public float maxDanceSpeed = 1.35f;
    [Tooltip("Never drive a dance faster than this many of its own beats per minute; use half time instead.")]
    public float maxVisibleDanceBpm = 160f;
    [Tooltip("How fast the dance speed moves toward its target, in speed units per second.")]
    public float danceSpeedLerpRate = 0.5f;
    [Tooltip("Shifts the dance steps against the detected beats, in ms (positive = later). Compensates audio output latency, e.g. Bluetooth.")]
    public float beatOffsetMs = 30f;
    [Tooltip("Beat-phase estimates weaker than this are ignored (tempo sync only, no beat lock). Once engaged, the lock holds down to 2/3 of it.")]
    public float minBeatPhaseConfidence = 0.3f;

    private static readonly int danceSpeedParam = Animator.StringToHash("DanceSpeed");
    private static readonly int isCustomDancingParam = Animator.StringToHash("isCustomDancing");
    private static readonly int danceStateHash = Animator.StringToHash("Dance");
    private RuntimeAnimatorController paramCacheController;
    private bool hasDanceSpeedParam, hasCustomDancingParam;
    private float currentDanceSpeed = 1f;
    private readonly BeatPhaseLock phaseLock = new BeatPhaseLock();
    private BeatPhaseLock.State lastShownLockState = BeatPhaseLock.State.Off;
    private float lastBeatPos;
    private bool hasLastBeatPos;
    private float observedBeats, observedSeconds;
    private bool tempoUsable, phaseUsable;
    private const float ReleaseFactor = 2f / 3f;   // confidence gates release at 2/3 of their engage level (0.3 -> 0.2)

    [Header("BPM Sync (read only, for testing)")]
    public float detectedMusicBpm;
    public float bpmConfidence;
    public string bpmCaptureMode = "Off";
    public string beatLockState = "Off";
    [Tooltip("Dance beat minus music beat, in ms (positive = dance late).")]
    public float beatPhaseErrorMs;
    public float beatPhaseConfidence;
    [Tooltip("Dance beats per minute measured from the Dance state's normalized time (should match the music or a x2 / /2 of it).")]
    public float observedDanceBpm;

    private int activeAudioPid;
    private AppAudioCapture bpmCapture;


    void OnEnable()
    {
        animator ??= GetComponent<Animator>();
        Application.runInBackground = true;
        enumerator = new MMDeviceEnumerator();
        defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        animator.SetFloat(isFemaleParam, enableHusbandoMode ? 0f : 1f);
        animator.SetFloat(isMaleParam, enableHusbandoMode ? 1f : 0f);

        soundCheckCoroutine = StartCoroutine(CheckSoundContinuously());
    }

    void OnDisable() => CleanupAudioResources();
    void OnDestroy() => CleanupAudioResources();
    void OnApplicationQuit() => CleanupAudioResources();

    IEnumerator CheckSoundContinuously()
    {
        var wait = new WaitForSeconds(2f);
        while (true) { CheckForSound(); yield return wait; }
    }

    void CheckForSound()
    {
        if (MenuActions.IsMovementBlocked() || !enableDancing)
        {
            if (isDancing) SetDancing(false);
            return;
        }
        if (defaultDevice == null) return;
        if (!isDragging)
        {
            bool valid = IsValidAppPlaying();
            if (valid && !isDancing) StartDancing();
            else if (!valid && isDancing) SetDancing(false);
            if (isDancing) EnsureBpmCapture();
        }
        // Other code (e.g. SaveLoadHandler.ApplyAllSettingsToAllAvatars) can clear isDancing directly;
        // never leave a capture running while not dancing.
        if (!isDancing) PauseBpmCapture();
    }

    // Captures the playing allowed app's audio while dancing; switches if a different app takes over.
    // Captures are pooled by AppAudioCapture and paused (not destroyed) when not needed.
    void EnsureBpmCapture()
    {
        if (!enableBpmSync) { PauseBpmCapture(); return; }
        if (activeAudioPid == 0) return; // keep the current capture through the silence grace period
        if (bpmCapture != null && bpmCapture.TargetPid == activeAudioPid) return;

        PauseBpmCapture();
        bpmCapture = AppAudioCapture.Acquire(activeAudioPid);
        bpmCapture.Tracker.LoudThreshold = SOUND_THRESHOLD;
    }

    void PauseBpmCapture()
    {
        if (bpmCapture == null) return;
        bpmCapture.Pause();
        bpmCapture = null;
        tempoUsable = phaseUsable = false;
        detectedMusicBpm = 0f;
        bpmConfidence = 0f;
        bpmCaptureMode = "Off";
        lastShownMode = AppAudioCapture.CaptureMode.Stopped;
    }

    void StartDancing()
    {
        isDancing = true;
        danceTimer = 0f;
        danceState = Random.Range(0, DANCE_CLIP_COUNT);
        animator.SetBool(isDancingParam, true);
        animator.SetFloat(danceIndexParam, danceState);
    }
    void SetDancing(bool value)
    {
        isDancing = value;
        animator.SetBool(isDancingParam, value);
        if (!value)
        {
            ResetDanceSpeed();
            PauseBpmCapture();
            if (danceTransitionCoroutine != null)
            {
                StopCoroutine(danceTransitionCoroutine);
                danceTransitionCoroutine = null;
            }
        }
    }

    bool IsValidAppPlaying()
    {
        if (Time.time - lastSoundCheckTime < 2f) return isDancing;
        lastSoundCheckTime = Time.time;
        try
        {
            defaultDevice?.Dispose();
            defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = defaultDevice.AudioSessionManager.Sessions;
            for (int i = 0, count = sessions.Count; i < count; i++)
            {
                var s = sessions[i];
                if (s.AudioMeterInformation.MasterPeakValue > SOUND_THRESHOLD)
                {
                    int pid = (int)s.GetProcessID;
                    if (pid == 0) continue;
                    try
                    {
                        string pname = Process.GetProcessById(pid)?.ProcessName;
                        if (string.IsNullOrEmpty(pname)) continue;
                        for (int j = 0; j < allowedApps.Count; j++)
                            if (pname.StartsWith(allowedApps[j], System.StringComparison.OrdinalIgnoreCase))
                            {
                                activeAudioPid = pid;
                                return true;
                            }
                    }
                    catch { continue; }
                }
            }
        }
        catch { defaultDevice?.Dispose(); defaultDevice = null; }
        
        activeAudioPid = 0;

        // If we are currently dancing and the captured app was loud recently, ignore this silence.
        if (isDancing && bpmCapture != null && bpmCapture.Tracker.SecondsSinceLoud < 1.5)
            return true;

        return false;
    }

    void Update()
    {
        animator.SetFloat(isFemaleParam, enableHusbandoMode ? 0f : 1f);
        animator.SetFloat(isMaleParam, enableHusbandoMode ? 1f : 0f);

        if (BlockDraggingOverride || MenuActions.IsMovementBlocked() || TutorialMenu.IsActive)
        {
            if (isDragging) SetDragging(false);
            if (isDancing) SetDancing(false);
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            SetDragging(true);
            mouseHeld = true;
            dragLockTimer = 0.30f;
            SetDancing(false);
        }
        if (Input.GetMouseButtonUp(0)) mouseHeld = false;
        if (dragLockTimer > 0f)
        {
            dragLockTimer -= Time.deltaTime;
            animator.SetBool(isDraggingParam, true);
        }
        else if (!mouseHeld && isDragging) SetDragging(false);

        idleTimer += Time.deltaTime;
        if (idleTimer > IDLE_SWITCH_TIME)
        {
            idleTimer = 0f;
            int next = (idleState + 1) % totalIdleAnimations;
            if (next == 0) animator.SetFloat(idleIndexParam, 0);
            else
            {
                if (idleTransitionCoroutine != null) StopCoroutine(idleTransitionCoroutine);
                idleTransitionCoroutine = StartCoroutine(SmoothIdleTransition(next));
            }
            idleState = next;
        }
        UpdateIdleStatus();

        if (isDancing) UpdateDanceSpeed();

        if (isDancing && enableDanceSwitch)
        {
            danceTimer += Time.deltaTime;
            if (danceTimer > DANCE_SWITCH_TIME)
            {
                danceTimer = 0f;
                int nextDance = (danceState + 1) % DANCE_CLIP_COUNT;
                if (nextDance == 0) animator.SetFloat(danceIndexParam, 0);
                else
                {
                    if (danceTransitionCoroutine != null) StopCoroutine(danceTransitionCoroutine);
                    danceTransitionCoroutine = StartCoroutine(SmoothDanceTransition(nextDance));
                }
                danceState = nextDance;
            }
        }
    }
    void SetDragging(bool value)
    {
        isDragging = value;
        animator.SetBool(isDraggingParam, value);
    }

    // Scales only the Dance state (via its DanceSpeed multiplier), leaving animator.speed and other layers alone.
    void UpdateDanceSpeed()
    {
        RefreshParamCache();
        if (!hasDanceSpeedParam) return;

        if (!enableBpmSync) PauseBpmCapture();
        float musicBpm = ReadMusicBpm(out bool hasBeatClock, out long beatAnchor, out double beatPeriod);

        float target = 1f, danceBpm = 0f, switchWarp = 1f;
        bool customDancing = hasCustomDancingParam && animator.GetBool(isCustomDancingParam);
        var table = DanceTempoTable.Instance;
        if (!customDancing && table != null)
        {
            float index = animator.GetFloat(danceIndexParam);
            if (musicBpm > 0f)
            {
                danceBpm = table.GetIndexBpm(index, enableHusbandoMode);
                target = GetBlendedTargetSpeed(table, musicBpm, index);
            }
            if (enableBpmSync) switchWarp = GetSwitchWarpCompensation(table, index);
        }

        float dt = Time.deltaTime;
        currentDanceSpeed = Mathf.MoveTowards(currentDanceSpeed, target, danceSpeedLerpRate * dt);
        float correction = danceBpm > 0f && hasBeatClock
            ? UpdateBeatLock(beatAnchor, beatPeriod, danceBpm, dt)
            : phaseLock.Release(dt, true);
        ShowLockState();
        animator.SetFloat(danceSpeedParam, currentDanceSpeed * (1f + correction) * switchWarp);
    }

    // During a dance switch the 1D blend tree plays both dances on one clock whose loop length is the weighted
    // average of theirs, so a long dance blending into a short one first speeds up, and the short one fades in
    // in slow motion (up to ~4x either way). Scale the speed so the outgoing dance keeps its natural rate for the
    // first 30% of the blend and the incoming one from 70% on, handing off smoothly in between. The midpoint warp
    // (sqrt of the length ratio) can't be removed while both share the clock.
    float GetSwitchWarpCompensation(DanceTempoTable table, float index)
    {
        int lo = Mathf.FloorToInt(index);
        float t = index - lo;
        if (t < 1e-4f || t > 1f - 1e-4f) return 1f;
        if (!table.TryGetIndexDuration(lo, enableHusbandoMode, out float dLo) ||
            !table.TryGetIndexDuration(lo + 1, enableHusbandoMode, out float dHi)) return 1f;
        float blended = Mathf.Lerp(dLo, dHi, t);                    // Unity's state loop length at this index
        float handoff = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.7f, t));
        float natural = Mathf.Exp(Mathf.Lerp(Mathf.Log(dLo), Mathf.Log(dHi), handoff));
        return blended / natural;
    }

    // Stage 3: nudges the dance so its beats land on the music's beats. Only on a settled built-in dance
    // (Dance state, no transition, integer DanceIndex with a baked beat grid); otherwise eases off.
    float UpdateBeatLock(long beatAnchor, double beatPeriod, float danceBpm, float dt)
    {
        var table = DanceTempoTable.Instance;
        float index = animator.GetFloat(danceIndexParam);
        int whole = Mathf.RoundToInt(index);
        if (Mathf.Abs(index - whole) > 1e-4f || animator.IsInTransition(0) || Mathf.Abs(animator.speed - 1f) > 0.01f ||
            !table.TryGetIndexBeatGrid(whole, enableHusbandoMode, out float beatsPerLoop, out float firstBeat))
        {
            hasLastBeatPos = false;
            return phaseLock.Release(dt, false);
        }
        var st = animator.GetCurrentAnimatorStateInfo(0);
        if (st.shortNameHash != danceStateHash) { hasLastBeatPos = false; return phaseLock.Release(dt, false); }

        // Update runs before the Animator, so normalizedTime is last frame's pose. Step it forward to the pose this
        // frame will display (st.length already includes the speed multiplier); otherwise the error is biased by
        // one frame time, which jitters with uneven frame rates.
        float norm = st.normalizedTime;
        if (st.length > 1e-3f && !float.IsInfinity(st.length)) norm += dt / st.length;
        norm -= Mathf.Floor(norm);
        float beatPos = (norm - firstBeat) * beatsPerLoop;
        MeasureDanceBpm(beatPos, beatsPerLoop, dt);

        double sinceBeat = (BeatTracker.Now100ns() - beatAnchor) / BeatTracker.TicksPerSecond - beatOffsetMs * 0.001;
        double dancePeriod = 60.0 / (danceBpm * currentDanceSpeed);
        float c = phaseLock.Step(sinceBeat, beatPeriod, beatPos, dancePeriod, dt);
        beatPhaseErrorMs = (float)(phaseLock.ErrorSeconds * 1000.0);
        return c;
    }

    // Debug readout: the dance's actual beat rate, from how far its beat position moves over ~1 s windows
    // (a per-frame average would be skewed by uneven frame times).
    void MeasureDanceBpm(float beatPos, float beatsPerLoop, float dt)
    {
        if (hasLastBeatPos)
        {
            float d = beatPos - lastBeatPos;
            if (d < 0f) d += beatsPerLoop;   // wrapped to the next loop
            observedBeats += d;
            observedSeconds += dt;
            if (observedSeconds >= 1f)
            {
                observedDanceBpm = observedBeats / observedSeconds * 60f;
                observedBeats = observedSeconds = 0f;
            }
        }
        else observedBeats = observedSeconds = 0f;
        lastBeatPos = beatPos;
        hasLastBeatPos = true;
    }

    void ShowLockState()
    {
        var s = phaseLock.Mode;
        if (s == lastShownLockState) return;
        lastShownLockState = s;
        beatLockState = s.ToString();
        if (s == BeatPhaseLock.State.Off || s == BeatPhaseLock.State.Hold) beatPhaseErrorMs = 0f;
    }

    AppAudioCapture.CaptureMode lastShownMode = AppAudioCapture.CaptureMode.Stopped;

    // Music tempo from the capture thread, or 0 when unknown / not confident. Also returns the beat clock
    // when its phase is usable; its self-refined period then gives a more exact tempo than the estimate.
    float ReadMusicBpm(out bool hasBeatClock, out long beatAnchor, out double beatPeriod)
    {
        hasBeatClock = false;
        beatAnchor = 0;
        beatPeriod = 0;
        if (bpmCapture == null) return 0f;
        var tracker = bpmCapture.Tracker;
        detectedMusicBpm = tracker.HasTempo ? tracker.Bpm : 0f;
        bpmConfidence = tracker.Confidence;
        var mode = bpmCapture.Mode;
        if (mode != lastShownMode) { lastShownMode = mode; bpmCaptureMode = mode.ToString(); }
        bool clockOk = tracker.TryGetBeatClock(out beatAnchor, out beatPeriod, out float phaseConf) && beatPeriod > 0.0;
        if (!clockOk) phaseConf = 0f;
        beatPhaseConfidence = phaseConf;

        // Hysteresis: engage at the minimum confidence, let go only well below it, so estimates hovering around
        // the threshold don't switch the tempo match / beat lock on and off every few beats.
        // A clear beat position is evidence for the tempo too (the phase fold only peaks sharply at the true
        // period), and sparse beats (e.g. hip-hop) keep the periodicity score low even when the tempo is right,
        // so either signal can engage the tempo match.
        float tempoGate = tempoUsable ? minBpmConfidence * ReleaseFactor : minBpmConfidence;
        float phaseGate = tempoUsable ? minBeatPhaseConfidence * ReleaseFactor : minBeatPhaseConfidence;
        tempoUsable = tracker.HasTempo && (tracker.Confidence >= tempoGate || phaseConf >= phaseGate);
        if (!tempoUsable) { phaseUsable = false; return 0f; }

        if (clockOk)
        {
            phaseUsable = phaseConf >= (phaseUsable ? minBeatPhaseConfidence * ReleaseFactor : minBeatPhaseConfidence);
            hasBeatClock = phaseUsable;
            // Even while the phase is too weak to lock, the clock's period (refined only from clear beats) is a more
            // exact tempo than the estimate, so the dance coasts through gaps with minimal drift.
            return (float)(60.0 / beatPeriod);
        }
        phaseUsable = false;
        return tracker.Bpm;
    }

    // While DanceIndex glides between two dances, blend the two dances' own target speeds. Blending their BPMs
    // instead would cross the x2 / /2 fold points of GetTargetDanceSpeed and make the target jump mid-switch
    // (e.g. 1.11 -> 0.71 -> 1.35 -> 1.04). A dance without a known tempo plays at 1x.
    float GetBlendedTargetSpeed(DanceTempoTable table, float musicBpm, float index)
    {
        int lo = Mathf.FloorToInt(index);
        float t = index - lo;
        float bpmLo = table.GetIndexBpm(lo, enableHusbandoMode);
        float speedLo = bpmLo > 0f ? GetTargetDanceSpeed(musicBpm, bpmLo) : 1f;
        if (t < 1e-4f) return speedLo;
        float bpmHi = table.GetIndexBpm(lo + 1, enableHusbandoMode);
        float speedHi = bpmHi > 0f ? GetTargetDanceSpeed(musicBpm, bpmHi) : 1f;
        return Mathf.Lerp(speedLo, speedHi, t);
    }

    float GetTargetDanceSpeed(float musicBpm, float danceBpm)
    {
        // Among half/double-time options (music / dance x 2^k), take the least stretched one that fits the speed
        // limits and doesn't make the dance faster than maxVisibleDanceBpm (double time on an already-fast dance
        // looks frantic). Clamping instead would leave the dance slightly off tempo, where the beat lock can't hold.
        float r = musicBpm / danceBpm;
        float best = -1f, bestStretch = float.MaxValue;
        for (float s = r * 0.25f; s <= r * 4.01f; s *= 2f)
        {
            if (s < minDanceSpeed || s > maxDanceSpeed || danceBpm * s > maxVisibleDanceBpm) continue;
            float stretch = Mathf.Abs(Mathf.Log(s));
            if (stretch < bestStretch) { bestStretch = stretch; best = s; }
        }
        if (best > 0f) return best;

        // Nothing fits (unusual limits): fall back to the nearest allowed speed.
        while (r >= 1.41421f) r *= 0.5f;
        while (r < 0.70711f) r *= 2f;
        if (danceBpm * r > maxVisibleDanceBpm) r *= 0.5f;
        return Mathf.Clamp(r, minDanceSpeed, maxDanceSpeed);
    }

    void ResetDanceSpeed()
    {
        currentDanceSpeed = 1f;
        phaseLock.Reset();
        hasLastBeatPos = false;
        ShowLockState();
        RefreshParamCache();
        if (hasDanceSpeedParam) animator.SetFloat(danceSpeedParam, 1f);
    }

    void RefreshParamCache()
    {
        var rc = animator.runtimeAnimatorController;
        if (rc == paramCacheController) return;
        paramCacheController = rc;
        hasDanceSpeedParam = hasCustomDancingParam = false;
        if (rc == null) return;
        foreach (var p in animator.parameters)
        {
            if (p.nameHash == danceSpeedParam && p.type == AnimatorControllerParameterType.Float) hasDanceSpeedParam = true;
            else if (p.nameHash == isCustomDancingParam && p.type == AnimatorControllerParameterType.Bool) hasCustomDancingParam = true;
        }
    }

    void UpdateIdleStatus()
    {
        bool inIdle = animator.GetCurrentAnimatorStateInfo(0).IsName("Idle");
        if (isIdle != inIdle)
        {
            isIdle = inIdle;
            animator.SetBool(isIdleParam, isIdle);
        }
    }

    IEnumerator SmoothIdleTransition(int newIdle)
    {
        float elapsed = 0f, start = animator.GetFloat(idleIndexParam);
        while (elapsed < IDLE_TRANSITION_TIME)
        {
            elapsed += Time.deltaTime;
            animator.SetFloat(idleIndexParam, Mathf.Lerp(start, newIdle, elapsed / IDLE_TRANSITION_TIME));
            yield return null;
        }
        animator.SetFloat(idleIndexParam, newIdle);
    }

    IEnumerator SmoothDanceTransition(int newDance)
    {
        float elapsed = 0f, start = animator.GetFloat(danceIndexParam);
        while (elapsed < DANCE_TRANSITION_TIME)
        {
            elapsed += Time.deltaTime;
            animator.SetFloat(danceIndexParam, Mathf.Lerp(start, newDance, elapsed / DANCE_TRANSITION_TIME));
            yield return null;
        }
        animator.SetFloat(danceIndexParam, newDance);
    }

    public bool IsInIdleState() => isIdle;

    void CleanupAudioResources()
    {
        if (soundCheckCoroutine != null) { StopCoroutine(soundCheckCoroutine); soundCheckCoroutine = null; }
        if (idleTransitionCoroutine != null) { StopCoroutine(idleTransitionCoroutine); idleTransitionCoroutine = null; }
        if (danceTransitionCoroutine != null) { StopCoroutine(danceTransitionCoroutine); danceTransitionCoroutine = null; }
        PauseBpmCapture();
        defaultDevice?.Dispose(); defaultDevice = null;
        enumerator?.Dispose(); enumerator = null;
    }
}
