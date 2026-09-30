using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

// Binds the AI system prompt box in the settings to ZomeAI_prompt.txt and the LLMCharacter's system prompt.
// The file always holds real prompt text: when it is missing or empty, or the box is cleared, the default prompt
// (the LLMCharacter's serialized prompt, the same text as the box's placeholder) is written back.
// Saves when editing ends (clicking away, or the menu closing while typing), so it does not rewrite the file
// or touch the chat on every keystroke.
[DefaultExecutionOrder(-1000)]
public class AISystemPromptBinder : MonoBehaviour
{
    [Header("References")]
    public InputField input;
    public LLMUnity.LLMCharacter target;

    private string defaultPrompt = "";
    private string savedPrompt = "";

    void Reset()
    {
        if (!input) input = GetComponent<InputField>();
        if (!target) target = FindObjectOfType<LLMUnity.LLMCharacter>();
    }

    void Awake()
    {
        if (!input) input = GetComponent<InputField>();
        // A binder without an LLM target has no default prompt to fall back on, so it must not touch the file.
        if (!target || !input)
        {
            enabled = false;
            return;
        }

        // Runs before LLMCharacter.Start replaces its prompt with the file's, so this is still the serialized default.
        defaultPrompt = target.prompt != null ? target.prompt.TrimStart('\r', '\n') : "";

        string txt = "";
        try
        {
            string path = GetFixedPromptPath();
            if (File.Exists(path)) txt = File.ReadAllText(path);
        }
        catch (Exception e) { Debug.LogError("[AI Prompt] Read failed: " + e); }

        if (string.IsNullOrWhiteSpace(txt))
        {
            txt = defaultPrompt;
            WriteFile(txt);
        }
        savedPrompt = txt;

        input.onEndEdit.RemoveListener(OnEndEdit);
        input.SetTextWithoutNotify(txt);
        ApplyToLLM(txt);
        input.onEndEdit.AddListener(OnEndEdit);
    }

    void OnDestroy()
    {
        if (input != null) input.onEndEdit.RemoveListener(OnEndEdit);
    }

    // Fallback in case the app quits while the box is still being edited.
    void OnApplicationQuit()
    {
        if (enabled && input != null && input.text != savedPrompt) Save(input.text);
    }

    void OnEndEdit(string s) => Save(s);

    void Save(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            s = defaultPrompt;
            input.SetTextWithoutNotify(s);
        }
        if (s == savedPrompt) return;

        savedPrompt = s;
        WriteFile(s);
        ApplyToLLM(s);
    }

    void WriteFile(string s)
    {
        try
        {
            string path = GetFixedPromptPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, s);
        }
        catch (Exception e) { Debug.LogError("[AI Prompt] Write failed: " + e); }
    }

    // Replaces only the system message, so the loaded conversation is kept.
    void ApplyToLLM(string s)
    {
        if (target != null) target.SetPrompt(s, target.chat == null || target.chat.Count == 0);
    }

    static string GetFixedPromptPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Path.GetFullPath(Path.Combine(localAppData, @"..\LocalLow"));
        var dir = Path.Combine(localLow, "Shinymoon", "MateEngineX");
        return Path.Combine(dir, "ZomeAI_prompt.txt");
    }
}
