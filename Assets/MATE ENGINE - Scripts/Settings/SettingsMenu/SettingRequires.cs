using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Greys out and disables a settings row (checkbox, slider, note) unless every toggle in `requires` is on.
// Uses a CanvasGroup so the label dims too. Raycasts stay on, so the row's tooltip still shows while it is disabled.
// Toggle clicks refresh automatically; code that uses SetIsOnWithoutNotify (load, reset) must call RefreshAll.
[RequireComponent(typeof(CanvasGroup))]
public class SettingRequires : MonoBehaviour
{
    public Toggle[] requires;
    [Range(0f, 1f)] public float disabledAlpha = 0.35f;

    private static readonly List<SettingRequires> active = new List<SettingRequires>();
    private CanvasGroup group;

    public static void RefreshAll()
    {
        for (int i = 0; i < active.Count; i++) active[i].Refresh();
    }

    private void Awake()
    {
        group = GetComponent<CanvasGroup>();
        foreach (var t in requires)
            if (t != null) t.onValueChanged.AddListener(OnRequiredChanged);
    }

    private void OnDestroy()
    {
        foreach (var t in requires)
            if (t != null) t.onValueChanged.RemoveListener(OnRequiredChanged);
    }

    private void OnEnable()
    {
        active.Add(this);
        Refresh();
    }

    private void OnDisable() => active.Remove(this);

    private void OnRequiredChanged(bool _) => Refresh();

    public void Refresh()
    {
        bool on = true;
        foreach (var t in requires)
            if (t != null && !t.isOn) { on = false; break; }
        group.interactable = on;
        group.alpha = on ? 1f : disabledAlpha;
    }
}
