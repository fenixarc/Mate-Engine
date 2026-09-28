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
    [Tooltip("Tempo estimates below this confidence are ignored (dance plays at normal speed).")]
    public float minBpmConfidence = 0.3f;
    [Tooltip("Limits for the Dance state's speed multiplier (music BPM / dance BPM).")]
    public float minDanceSpeed = 0.6f;
    public float maxDanceSpeed = 1.35f;
    [Tooltip("Never drive a dance faster than this many of its own beats per minute; use half time instead.")]
    public float maxVisibleDanceBpm = 160f;
    [Tooltip("How fast the dance speed moves toward its target, in speed units per second.")]
    public float danceSpeedLerpRate = 0.5f;

    private static readonly int danceSpeedParam = Animator.StringToHash("DanceSpeed");
    private static readonly int isCustomDancingParam = Animator.StringToHash("isCustomDancing");
    private RuntimeAnimatorController paramCacheController;
    private bool hasDanceSpeedParam, hasCustomDancingParam;
    private float currentDanceSpeed = 1f;

    [Header("BPM Sync (read only, for testing)")]
    public float detectedMusicBpm;
    public float bpmConfidence;
    public string bpmCaptureMode = "Off";

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
        float musicBpm = ReadMusicBpm();

        float target = 1f;
        bool customDancing = hasCustomDancingParam && animator.GetBool(isCustomDancingParam);
        if (!customDancing && musicBpm > 0f)
        {
            var table = DanceTempoTable.Instance;
            float danceBpm = table != null ? table.GetIndexBpm(animator.GetFloat(danceIndexParam), enableHusbandoMode) : 0f;
            if (danceBpm > 0f) target = GetTargetDanceSpeed(musicBpm, danceBpm);
        }

        currentDanceSpeed = Mathf.MoveTowards(currentDanceSpeed, target, danceSpeedLerpRate * Time.deltaTime);
        animator.SetFloat(danceSpeedParam, currentDanceSpeed);
    }

    AppAudioCapture.CaptureMode lastShownMode = AppAudioCapture.CaptureMode.Stopped;

    // Music tempo from the capture thread, or 0 when unknown / not confident.
    float ReadMusicBpm()
    {
        if (bpmCapture == null) return 0f;
        var tracker = bpmCapture.Tracker;
        detectedMusicBpm = tracker.HasTempo ? tracker.Bpm : 0f;
        bpmConfidence = tracker.Confidence;
        var mode = bpmCapture.Mode;
        if (mode != lastShownMode) { lastShownMode = mode; bpmCaptureMode = mode.ToString(); }
        return tracker.HasTempo && tracker.Confidence >= minBpmConfidence ? tracker.Bpm : 0f;
    }

    float GetTargetDanceSpeed(float musicBpm, float danceBpm)
    {
        // Fold into half/double time so the dance is stretched as little as possible.
        float r = musicBpm / danceBpm;
        while (r >= 1.41421f) r *= 0.5f;
        while (r < 0.70711f) r *= 2f;
        // Double time on an already-fast dance looks frantic; drop to half time instead.
        if (danceBpm * r > maxVisibleDanceBpm) r *= 0.5f;
        return Mathf.Clamp(r, minDanceSpeed, maxDanceSpeed);
    }

    void ResetDanceSpeed()
    {
        currentDanceSpeed = 1f;
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
