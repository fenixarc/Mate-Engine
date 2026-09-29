using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using K = MESettingsUIKit;

// One-shot tool that adds SettingRequires to settings rows so they grey out when the checkbox they depend on is off.
// "= DANCING":
//   AVATAR CAN DANCE off         -> every other row in the section (not the DANCING dropdown or the settings button)
//   ENABLE DANCE TRANSITIONS off -> DANCE CHANGE TIME, DANCE TRANSITION TIME
//   BPM DANCE SYNC off           -> BEAT OFFSET
// "= AI":
//   USE GEMINI off               -> API URL and API KEY (with their labels), TEST CONNECTION, GEMINI MODEL
//                                   (Save and the status line stay enabled)
// Safe to re-run: existing SettingRequires components are updated in place. Save the scene afterwards.
public static class MESettingsDependencyBuilder
{
    const string BpmRootName = "BPM Sync Settings";
    const string ScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
    const string Title = "Build Settings Dependencies";

    [MenuItem("MateEngine/" + Title)]
    public static void Build() => Run(interactive: true);

    // -executeMethod MESettingsDependencyBuilder.BuildBatch
    public static void BuildBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        if (Run(interactive: false)) EditorSceneManager.SaveScene(scene);
    }

    static bool Run(bool interactive)
    {
        var log = new StringBuilder("[Settings UI] DEPENDENCIES\n");
        Undo.SetCurrentGroupName(Title);
        int undoGroup = Undo.GetCurrentGroup();

        bool ok = Dancing(log, interactive) && Gemini(log, interactive);

        Undo.CollapseUndoOperations(undoGroup);
        if (ok) EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log(log.ToString());
        return ok;
    }

    static bool Dancing(StringBuilder log, bool interactive)
    {
        var toggles = K.Find<SettingsHandlerToggles>();
        var sliders = K.Find<SettingsHandlerSliders>();
        if (toggles == null || sliders == null) return Fail("SettingsHandlerToggles/SettingsHandlerSliders not found. Open 'Mate Engine Main' first.", interactive);

        var dancing = toggles.enableDancingToggle;
        var transitions = toggles.enableDanceSwitchToggle;
        var bpm = toggles.enableBpmSyncToggle;
        if (dancing == null || transitions == null || bpm == null || sliders.soundThresholdSlider == null ||
            sliders.danceSwitchTimeSlider == null || sliders.danceTransitionTimeSlider == null || sliders.bpmBeatOffsetSlider == null)
            return Fail("One of the dancing toggles/sliders is not wired on SettingsHandlerToggles/SettingsHandlerSliders.", interactive);

        var bpmRoot = bpm.transform.parent;
        var note = bpmRoot != null && bpmRoot.name == BpmRootName ? bpmRoot.Find("BPM Sync Note") : null;

        Require(transitions, log, dancing);
        Require(sliders.soundThresholdSlider, log, dancing);
        Require(sliders.danceSwitchTimeSlider, log, dancing, transitions);
        Require(sliders.danceTransitionTimeSlider, log, dancing, transitions);
        Require(bpm, log, dancing);
        if (note != null) Require(note, log, dancing);
        else log.AppendLine("  BPM Sync Note not found (skipped)");
        Require(sliders.bpmBeatOffsetSlider, log, dancing, bpm);
        return true;
    }

    static bool Gemini(StringBuilder log, bool interactive)
    {
        var ai = K.Find<SettingsHandlerAIProvider>();
        if (ai == null || ai.useGeminiToggle == null || ai.apiUrlInput == null || ai.apiKeyInput == null ||
            ai.testConnectionButton == null || ai.modelDropdown == null)
            return Fail("SettingsHandlerAIProvider or one of its Gemini controls is not wired.", interactive);

        var gemini = ai.useGeminiToggle;
        RequireLabel(ai.apiUrlInput.transform.parent, "API URL Label", log, gemini);
        Require(ai.apiUrlInput, log, gemini);
        RequireLabel(ai.apiKeyInput.transform.parent, "API Key Label", log, gemini);
        Require(ai.apiKeyInput, log, gemini);
        Require(ai.testConnectionButton, log, gemini);
        Require(ai.modelDropdown, log, gemini);
        return true;
    }

    static void RequireLabel(Transform parent, string name, StringBuilder log, params Toggle[] requires)
    {
        var label = parent != null ? parent.Find(name) : null;
        if (label != null) Require(label, log, requires);
        else log.AppendLine($"  {name} not found (skipped)");
    }

    static void Require(Component row, StringBuilder log, params Toggle[] requires)
    {
        var go = row.gameObject;
        if (go.GetComponent<CanvasGroup>() == null) Undo.AddComponent<CanvasGroup>(go);
        var dep = go.GetComponent<SettingRequires>();
        if (dep == null) dep = Undo.AddComponent<SettingRequires>(go);
        Undo.RecordObject(dep, "Wire setting dependency");
        dep.requires = requires;
        EditorUtility.SetDirty(dep);

        var names = new string[requires.Length];
        for (int i = 0; i < requires.Length; i++) names[i] = requires[i].name;
        log.AppendLine($"  {K.GetPath(go.transform)} requires {string.Join(" + ", names)}");
    }

    static bool Fail(string msg, bool interactive)
    {
        Debug.LogError("[Settings UI] " + msg);
        if (interactive) EditorUtility.DisplayDialog(Title, msg, "OK");
        return false;
    }
}
