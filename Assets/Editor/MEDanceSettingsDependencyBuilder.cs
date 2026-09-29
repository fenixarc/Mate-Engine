using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using K = MESettingsUIKit;

// One-shot tool that adds SettingRequires to the "= DANCING" rows so they grey out when the checkbox they depend on is off:
//   AVATAR CAN DANCE off         -> every other row in the section (not the DANCING dropdown or the settings button)
//   ENABLE DANCE TRANSITIONS off -> DANCE CHANGE TIME, DANCE TRANSITION TIME
//   BPM DANCE SYNC off           -> BEAT OFFSET
// Safe to re-run: existing SettingRequires components are updated in place. Save the scene afterwards.
public static class MEDanceSettingsDependencyBuilder
{
    const string BpmRootName = "BPM Sync Settings";
    const string ScenePath = "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity";
    const string Title = "Build Dancing Settings Dependencies";

    [MenuItem("MateEngine/" + Title)]
    public static void Build() => Run(interactive: true);

    // -executeMethod MEDanceSettingsDependencyBuilder.BuildBatch
    public static void BuildBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        if (Run(interactive: false)) EditorSceneManager.SaveScene(scene);
    }

    static bool Run(bool interactive)
    {
        var log = new StringBuilder("[Dancing UI] DEPENDENCIES\n");
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

        Undo.SetCurrentGroupName(Title);
        int undoGroup = Undo.GetCurrentGroup();

        Require(transitions, log, dancing);
        Require(sliders.soundThresholdSlider, log, dancing);
        Require(sliders.danceSwitchTimeSlider, log, dancing, transitions);
        Require(sliders.danceTransitionTimeSlider, log, dancing, transitions);
        Require(bpm, log, dancing);
        if (note != null) Require(note, log, dancing);
        else log.AppendLine("  BPM Sync Note not found (skipped)");
        Require(sliders.bpmBeatOffsetSlider, log, dancing, bpm);

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(dancing.gameObject.scene);
        Debug.Log(log.ToString());
        return true;
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
        Debug.LogError("[Dancing UI] " + msg);
        if (interactive) EditorUtility.DisplayDialog(Title, msg, "OK");
        return false;
    }
}
