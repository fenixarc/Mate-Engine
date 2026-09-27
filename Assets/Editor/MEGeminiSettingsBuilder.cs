using System.Collections.Generic;
using System.Linq;
using LLMUnity;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// One-shot tool that adds the Gemini provider controls to the "= AI" settings section of the open scene.
// Clones existing controls so the style matches, then pushes the sections below down.
// Everything is registered with Undo; save the scene afterwards.
//
// Layout is computed in the local space of the settings Canvas, NOT world space: the canvas is saved
// inactive with a scale of 0 (Unity only scales it while active), so world-space sizes are all zero in the Editor.
public static class MEGeminiSettingsBuilder
{
    const string RootName = "Gemini Settings";

    // Layout space: the root canvas transform of the settings menu.
    static Transform space;

    [MenuItem("MateEngine/Build Gemini AI Settings UI")]
    public static void Build()
    {
        var dropdowns = Find<SettingsHandlerDropdowns>();
        if (dropdowns == null || dropdowns.contextLengthDropdown == null) { Fail("SettingsHandlerDropdowns with a Context Length dropdown was not found. Open 'Mate Engine Main' first."); return; }

        var contextDropdown = dropdowns.contextLengthDropdown;
        var aiHeader = FindAncestor(contextDropdown.transform, "= AI") as RectTransform;
        if (aiHeader == null) { Fail("Could not find the '= AI' section above the Context Length dropdown."); return; }
        var mainMenu = aiHeader.parent;

        var canvases = aiHeader.GetComponentsInParent<Canvas>(true);
        space = canvases.Length > 0 ? canvases[canvases.Length - 1].transform : mainMenu;

        var aiInput = contextDropdown.transform.parent as RectTransform;
        var promptInput = FindAll<AISystemPromptBinder>().Select(b => b.input).FirstOrDefault(i => i != null && i.transform.IsChildOf(aiHeader));
        var toggles = Find<SettingsHandlerToggles>();
        var toggleTemplate = toggles == null ? null : new[] { toggles.enableIKToggle, toggles.enableDanceSwitchToggle, toggles.enableDancingToggle, toggles.enableMouseTrackingToggle }.FirstOrDefault(t => t != null);
        var buttonTemplate = mainMenu.parent != null ? mainMenu.parent.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "Apply") : null;
        var labelTemplate = contextDropdown.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.name == "Title");

        if (promptInput == null || toggleTemplate == null || buttonTemplate == null || labelTemplate == null)
        {
            Fail($"Missing template(s): prompt input={promptInput != null}, toggle={toggleTemplate != null}, Apply button={buttonTemplate != null}, dropdown Title={labelTemplate != null}.");
            return;
        }

        Undo.SetCurrentGroupName("Build Gemini AI Settings UI");
        int undoGroup = Undo.GetCurrentGroup();

        // Remove a previous build (and undo the space it made) so the tool can be re-run.
        var existing = aiHeader.Find(RootName) as RectTransform;
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Build Gemini AI Settings UI",
                "Gemini settings already exist under '= AI'. Remove them and rebuild?", "Rebuild", "Cancel")) return;
            float previous = existing.sizeDelta.y * ScaleInSpace(existing).y;
            if (previous > 0f)
            {
                PushDown(mainMenu, aiHeader, -previous);
                GrowScroll(aiHeader, -previous);
            }
            Undo.DestroyObjectImmediate(existing.gameObject);
        }

        // Measure the existing AI section.
        Rect section = SpaceBounds(aiInput);
        float colLeft = section.xMin;
        float colWidth = section.width;
        float rowH = SpaceRect((RectTransform)contextDropdown.transform).height;
        if (colWidth <= 0f || rowH <= 0f) { Fail($"Could not measure the AI section (width {colWidth}, row height {rowH})."); return; }
        float gap = rowH * 0.35f;
        float labelH = rowH * 0.6f;
        float cursor = section.yMin - gap;

        // Containers
        var root = NewContainer(RootName, aiHeader);
        var panel = NewContainer("Gemini Panel", root);

        // Provider toggle
        var toggle = Clone(toggleTemplate.gameObject, root, "Use Gemini Toggle").GetComponent<Toggle>();
        toggle.isOn = false;
        SetText(toggle.gameObject, "USE GEMINI (ONLINE API)");
        cursor = Place(toggle.transform, colLeft, cursor) - gap;

        // URL
        cursor = Place(MakeLabel(labelTemplate, panel, "API URL Label", "GEMINI API URL", colWidth, labelH), colLeft, cursor) - gap * 0.3f;
        var urlInput = MakeInput(promptInput, panel, "Gemini API URL", SaveLoadHandler.SettingsData.DefaultGeminiApiUrl, colWidth, rowH);
        urlInput.text = SaveLoadHandler.SettingsData.DefaultGeminiApiUrl;
        cursor = Place(urlInput.transform, colLeft, cursor) - gap;

        // API key
        cursor = Place(MakeLabel(labelTemplate, panel, "API Key Label", "GEMINI API KEY", colWidth, labelH), colLeft, cursor) - gap * 0.3f;
        var keyInput = MakeInput(promptInput, panel, "Gemini API Key", "Paste your Gemini API key", colWidth, rowH);
        keyInput.contentType = InputField.ContentType.Password;
        cursor = Place(keyInput.transform, colLeft, cursor) - gap;

        // Test connection
        var testButton = MakeButton(buttonTemplate, panel, "Test Connection Button", "TEST CONNECTION", colWidth * 0.5f);
        cursor = Place(testButton.transform, colLeft, cursor) - gap;

        // Model dropdown
        var modelDropdown = Clone(contextDropdown.gameObject, panel, "Gemini Model").GetComponent<TMP_Dropdown>();
        modelDropdown.ClearOptions();
        modelDropdown.AddOptions(new List<string> { "(test connection first)" });
        var title = modelDropdown.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.name == "Title");
        if (title != null) title.text = "GEMINI MODEL";
        cursor = Place(modelDropdown.transform, colLeft, cursor) - gap;

        // Save
        var saveButton = MakeButton(buttonTemplate, panel, "Save Gemini Button", "SAVE", colWidth * 0.5f);
        cursor = Place(saveButton.transform, colLeft, cursor) - gap;

        // Status
        var status = MakeLabel(labelTemplate, panel, "Gemini Status", "", colWidth, labelH * 2f);
        status.GetComponent<TMP_Text>().textWrappingMode = TextWrappingModes.Normal;
        cursor = Place(status, colLeft, cursor) - gap;

        float added = section.yMin - cursor;
        PushDown(mainMenu, aiHeader, added);
        GrowScroll(aiHeader, added);
        // Remember how much space was added (the container's own size is otherwise unused) so a rebuild can revert it.
        root.sizeDelta = new Vector2(0f, added / Safe(ScaleInSpace(root).y));

        // Wire the settings handler
        var handler = dropdowns.GetComponent<SettingsHandlerAIProvider>();
        if (handler == null) handler = Undo.AddComponent<SettingsHandlerAIProvider>(dropdowns.gameObject);
        Undo.RecordObject(handler, "Wire Gemini settings");
        handler.useGeminiToggle = toggle;
        handler.geminiPanel = panel.gameObject;
        handler.apiUrlInput = urlInput;
        handler.apiKeyInput = keyInput;
        handler.testConnectionButton = testButton;
        handler.modelDropdown = modelDropdown;
        handler.saveButton = saveButton;
        handler.statusText = status.GetComponent<TMP_Text>();
        EditorUtility.SetDirty(handler);

        // Router next to the chat's prompt binder (always-active "ChatBot AI" object)
        var llmCharacter = FindAll<LLMCharacter>().FirstOrDefault();
        var llm = FindAll<LLM>().FirstOrDefault();
        var binderWithTarget = FindAll<AISystemPromptBinder>().FirstOrDefault(b => b.target != null);
        var routerHost = binderWithTarget != null ? binderWithTarget.gameObject : dropdowns.gameObject;
        var router = FindAll<AIProviderRouter>().FirstOrDefault();
        if (router == null) router = Undo.AddComponent<AIProviderRouter>(routerHost);
        Undo.RecordObject(router, "Wire AI provider router");
        router.llm = llm;
        router.llmCharacter = llmCharacter;
        EditorUtility.SetDirty(router);

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(aiHeader.gameObject.scene);
        Selection.activeGameObject = root.gameObject;
        Debug.Log($"[Gemini UI] Built under '{GetPath(aiHeader)}'. Sections below were moved down by {added / Safe(ScaleInSpace(mainMenu).y):0.#} units. Review the layout and save the scene.");
    }

    // ---------- construction helpers ----------

    static RectTransform NewContainer(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;
        rt.localScale = Vector3.one;
        go.layer = parent.gameObject.layer;
        return rt;
    }

    // Clones template under parent, keeping its size and scale as seen in the canvas space.
    static GameObject Clone(GameObject template, Transform parent, string name)
    {
        var tRt = (RectTransform)template.transform;
        Rect tRect = SpaceRect(tRt);
        Vector2 tScale = ScaleInSpace(tRt);

        var go = Object.Instantiate(template, parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.name = name;
        go.SetActive(true);
        StripProjectComponents(go);
        ClearPersistentListeners(go);

        var rt = (RectTransform)go.transform;
        Vector2 pScale = ScaleInSpace(parent);
        rt.localScale = new Vector3(tScale.x / Safe(pScale.x), tScale.y / Safe(pScale.y), 1f);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        SetSpaceSize(rt, tRect.width, tRect.height);
        rt.anchoredPosition = Vector2.zero;
        return go;
    }

    static RectTransform MakeLabel(TMP_Text template, Transform parent, string name, string text, float w, float h)
    {
        var go = Clone(template.gameObject, parent, name);
        var rt = (RectTransform)go.transform;
        SetSpaceSize(rt, w, h);
        var tmp = go.GetComponent<TMP_Text>();
        tmp.text = text;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        return rt;
    }

    static InputField MakeInput(InputField template, Transform parent, string name, string placeholder, float w, float h)
    {
        var input = Clone(template.gameObject, parent, name).GetComponent<InputField>();
        var rt = (RectTransform)input.transform;
        SetSpaceSize(rt, w, h);
        input.lineType = InputField.LineType.SingleLine;
        input.text = "";

        int fontSize = Mathf.Max(8, Mathf.RoundToInt(rt.rect.height * 0.45f));
        if (input.textComponent != null) input.textComponent.fontSize = fontSize;
        if (input.placeholder is Text ph)
        {
            ph.fontSize = fontSize;
            ph.text = placeholder;
        }
        return input;
    }

    static Button MakeButton(Button template, Transform parent, string name, string label, float w)
    {
        var button = Clone(template.gameObject, parent, name).GetComponent<Button>();
        var rt = (RectTransform)button.transform;
        var r = SpaceRect(rt);
        SetSpaceSize(rt, Mathf.Max(r.width, w), r.height);
        foreach (var t in button.GetComponentsInChildren<TMP_Text>(true))
        {
            t.text = label;
            t.enableAutoSizing = true;
        }
        return button;
    }

    static void SetText(GameObject go, string text)
    {
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = text;
        foreach (var t in go.GetComponentsInChildren<Text>(true)) t.text = text;
    }

    // Removes Mate Engine scripts and Localization components (which would overwrite the new label text).
    static void StripProjectComponents(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (c == null) continue;
            string asm = c.GetType().Assembly.GetName().Name;
            if (asm == "Assembly-CSharp" || asm.StartsWith("Unity.Localization"))
                Object.DestroyImmediate(c);
        }
    }

    static void ClearPersistentListeners(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var so = new SerializedObject(c);
            var it = so.GetIterator();
            bool changed = false;
            while (it.Next(true))
            {
                if (it.isArray && it.propertyPath.EndsWith("m_PersistentCalls.m_Calls") && it.arraySize > 0)
                {
                    it.arraySize = 0;
                    changed = true;
                }
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    // ---------- canvas-space layout helpers ----------

    static float Safe(float v) => Mathf.Abs(v) < 1e-6f ? 1e-6f : v;

    // Maps a point from t's local rect space to its parent's local rect space.
    static Vector2 StepUp(Transform t, Vector2 p)
    {
        if (!(t is RectTransform rt))
            return (Vector2)t.localPosition + Vector2.Scale((Vector2)t.localScale, p);

        Vector2 pivotPos;
        if (rt.parent is RectTransform prt)
        {
            Rect pr = prt.rect;
            Vector2 aMin = pr.min + Vector2.Scale(pr.size, rt.anchorMin);
            Vector2 aMax = pr.min + Vector2.Scale(pr.size, rt.anchorMax);
            pivotPos = new Vector2(Mathf.Lerp(aMin.x, aMax.x, rt.pivot.x), Mathf.Lerp(aMin.y, aMax.y, rt.pivot.y)) + rt.anchoredPosition;
        }
        else pivotPos = rt.anchoredPosition;
        return pivotPos + Vector2.Scale((Vector2)rt.localScale, p);
    }

    static Vector2 ToSpace(Transform t, Vector2 p)
    {
        for (; t != null && t != space; t = t.parent) p = StepUp(t, p);
        return p;
    }

    static Vector2 ScaleInSpace(Transform t)
    {
        Vector2 s = Vector2.one;
        for (; t != null && t != space; t = t.parent) s = Vector2.Scale(s, (Vector2)t.localScale);
        return s;
    }

    static Rect SpaceRect(RectTransform rt)
    {
        Rect r = rt.rect;
        Vector2 a = ToSpace(rt, r.min), b = ToSpace(rt, r.max);
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    // Requires collapsed anchors (anchorMin == anchorMax), so sizeDelta equals the rect size.
    static void SetSpaceSize(RectTransform rt, float w, float h)
    {
        Vector2 s = ScaleInSpace(rt);
        rt.sizeDelta = new Vector2(Mathf.Abs(w / Safe(s.x)), Mathf.Abs(h / Safe(s.y)));
    }

    static void MoveInSpace(RectTransform rt, Vector2 delta)
    {
        Vector2 ps = ScaleInSpace(rt.parent);
        rt.anchoredPosition += new Vector2(delta.x / Safe(ps.x), delta.y / Safe(ps.y));
    }

    // Moves t so the top-left of its visible bounds is at (left, top). Returns the new bottom.
    static float Place(Transform t, float left, float top)
    {
        var rt = (RectTransform)t;
        var b = SpaceBounds(rt);
        MoveInSpace(rt, new Vector2(left - b.xMin, top - b.yMax));
        return top - b.height;
    }

    // Union of the rects of all descendants that are active relative to root.
    static Rect SpaceBounds(RectTransform root)
    {
        bool any = false;
        Rect u = default;
        foreach (var rt in root.GetComponentsInChildren<RectTransform>(true))
        {
            if (!ActiveUnder(rt, root)) continue;
            var r = SpaceRect(rt);
            if (r.width <= 0f || r.height <= 0f) continue;
            u = any ? Rect.MinMaxRect(Mathf.Min(u.xMin, r.xMin), Mathf.Min(u.yMin, r.yMin), Mathf.Max(u.xMax, r.xMax), Mathf.Max(u.yMax, r.yMax)) : r;
            any = true;
        }
        return any ? u : SpaceRect(root);
    }

    static bool ActiveUnder(Transform t, Transform root)
    {
        for (; t != null; t = t.parent)
        {
            if (!t.gameObject.activeSelf) return false;
            if (t == root) return true;
        }
        return true;
    }

    // Moves every section below "= AI" down by amount (negative moves up), and resizes the AI section's background panel.
    static void PushDown(Transform mainMenu, RectTransform aiHeader, float amount)
    {
        float aiY = SpaceRect(aiHeader).center.y;
        foreach (Transform child in mainMenu)
        {
            if (child == aiHeader || child.name == "Category Background" || !(child is RectTransform crt)) continue;
            if (SpaceRect(crt).center.y < aiY)
            {
                Undo.RecordObject(crt, "Move section");
                MoveInSpace(crt, Vector2.down * amount);
            }
        }

        var bg = mainMenu.Find("Category Background");
        if (bg == null) return;
        foreach (Transform panelT in bg)
        {
            if (!(panelT is RectTransform prt)) continue;
            var r = SpaceRect(prt);
            if (r.height <= 0f) continue;
            Undo.RecordObject(prt, "Adjust section background");
            if (r.yMax >= aiY && r.yMin <= aiY)
            {
                prt.sizeDelta += new Vector2(0f, amount / Safe(ScaleInSpace(prt).y));
                var after = SpaceRect(prt);
                MoveInSpace(prt, new Vector2(0f, r.yMax - after.yMax));
            }
            else if (r.center.y < aiY)
            {
                MoveInSpace(prt, Vector2.down * amount);
            }
        }
    }

    static void GrowScroll(RectTransform aiHeader, float amount)
    {
        var scroll = aiHeader.GetComponentInParent<ScrollRect>(true);
        if (scroll == null || scroll.content == null) return;
        var vlg = scroll.content.GetComponent<VerticalLayoutGroup>();
        float local = amount / Safe(ScaleInSpace(scroll.content).y);
        if (vlg != null)
        {
            Undo.RecordObject(vlg, "Grow settings scroll");
            var p = vlg.padding;
            vlg.padding = new RectOffset(p.left, p.right, p.top, Mathf.Max(0, p.bottom + Mathf.CeilToInt(local)));
            EditorUtility.SetDirty(vlg);
        }
        else
        {
            Undo.RecordObject(scroll.content, "Grow settings scroll");
            scroll.content.sizeDelta += new Vector2(0f, local);
        }
    }

    // ---------- lookup helpers ----------

    static T Find<T>() where T : Object => FindAll<T>().FirstOrDefault();

    static IEnumerable<T> FindAll<T>() where T : Object =>
        Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);

    static Transform FindAncestor(Transform t, string name)
    {
        for (; t != null; t = t.parent) if (t.name.Trim() == name) return t;
        return null;
    }

    static string GetPath(Transform t) => t.parent == null ? t.name : GetPath(t.parent) + "/" + t.name;

    static void Fail(string msg)
    {
        Debug.LogError("[Gemini UI] " + msg);
        EditorUtility.DisplayDialog("Build Gemini AI Settings UI", msg, "OK");
    }
}
