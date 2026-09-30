using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Settings UI for choosing the AI provider (local Llama or Gemini API).
// Changes are only persisted when the Save button is pressed.
public class SettingsHandlerAIProvider : MonoBehaviour
{
    [Header("Provider")]
    public Toggle useGeminiToggle;
    public GameObject geminiPanel;

    [Header("Gemini")]
    public InputField apiUrlInput;
    public InputField apiKeyInput;
    public Button testConnectionButton;
    public TMP_Dropdown modelDropdown;
    public Button saveButton;
    public TMP_Text statusText;

    readonly List<string> models = new List<string>();
    bool testing;

    void Start()
    {
        if (apiKeyInput != null)
        {
            apiKeyInput.contentType = InputField.ContentType.Password;
            apiKeyInput.lineType = InputField.LineType.SingleLine;
            apiKeyInput.ForceLabelUpdate();
        }
        if (apiUrlInput != null) apiUrlInput.lineType = InputField.LineType.SingleLine;

        useGeminiToggle?.onValueChanged.AddListener(OnToggleChanged);
        testConnectionButton?.onClick.AddListener(OnTestConnection);
        saveButton?.onClick.AddListener(OnSave);

        LoadSettings();
    }

    public void LoadSettings()
    {
        var data = SaveLoadHandler.Instance.data;

        useGeminiToggle?.SetIsOnWithoutNotify(data.useGeminiAI);
        SettingRequires.RefreshAll();
        if (apiUrlInput != null) apiUrlInput.SetTextWithoutNotify(GeminiClient.NormalizeUrl(data.geminiApiUrl));
        if (apiKeyInput != null) apiKeyInput.SetTextWithoutNotify(SecureStore.Unprotect(data.geminiApiKeyEncrypted));

        models.Clear();
        if (!string.IsNullOrEmpty(data.geminiModel)) models.Add(data.geminiModel);
        RebuildModelDropdown(data.geminiModel);

        UpdatePanelVisibility();
        SetStatus(data.useGeminiAI && string.IsNullOrEmpty(data.geminiModel) ? "Test the connection to choose a model." : "");
    }

    // Unchecking switches back to the local model immediately. Checking only switches immediately
    // when a key and model were already saved; otherwise the user must Test + Save first.
    void OnToggleChanged(bool on)
    {
        UpdatePanelVisibility();
        var data = SaveLoadHandler.Instance.data;

        if (!on)
        {
            SetProvider(false);
            SetStatus("Gemini disabled - using the local Llama model.");
            return;
        }

        if (!string.IsNullOrEmpty(data.geminiApiKeyEncrypted) && !string.IsNullOrEmpty(data.geminiModel))
        {
            SetProvider(true);
            SetStatus("Gemini enabled (" + DisplayName(data.geminiModel) + ").");
        }
        else SetStatus("Enter your API key, test the connection, choose a model, then press Save.");
    }

    void SetProvider(bool useGemini)
    {
        SaveLoadHandler.Instance.data.useGeminiAI = useGemini;
        SaveLoadHandler.Instance.SaveToDisk();
        AIProviderRouter.Instance?.ApplyProviderChange(useGemini);
    }

    // Called by the settings Reset button. Switches back to the local model but keeps the saved
    // URL, API key and model so the user does not have to enter them again.
    public void ResetToDefaults()
    {
        useGeminiToggle?.SetIsOnWithoutNotify(false);
        SettingRequires.RefreshAll();
        UpdatePanelVisibility();
        SaveLoadHandler.Instance.data.useGeminiAI = false;
        AIProviderRouter.Instance?.ApplyProviderChange(false);
        SetStatus("");
    }

    void UpdatePanelVisibility()
    {
        // The Gemini fields are always shown; the checkbox only controls whether Gemini is used.
        if (geminiPanel != null && !geminiPanel.activeSelf) geminiPanel.SetActive(true);
    }

    async void OnTestConnection()
    {
        if (testing) return;
        testing = true;
        if (testConnectionButton) testConnectionButton.interactable = false;
        SetStatus("Testing connection...");

        string previous = SelectedModel();
        var result = await GeminiClient.ListModels(apiUrlInput ? apiUrlInput.text : "", apiKeyInput ? apiKeyInput.text : "");

        if (this == null) return;
        testing = false;
        if (testConnectionButton) testConnectionButton.interactable = true;

        if (!result.ok)
        {
            SetStatus("Connection failed: " + result.error);
            return;
        }
        if (result.models.Count == 0)
        {
            SetStatus("Connected, but no chat models are available for this key.");
            return;
        }

        models.Clear();
        models.AddRange(result.models);
        string select = previous;
        if (string.IsNullOrEmpty(select) || !models.Contains(select))
            select = models.Find(m => m.Contains("gemini") && m.Contains("flash")) ?? models[0];
        RebuildModelDropdown(select);
        SetStatus($"Connected - {models.Count} models available.");
    }

    void OnSave()
    {
        var data = SaveLoadHandler.Instance.data;
        bool useGemini = useGeminiToggle == null || useGeminiToggle.isOn;
        string url = GeminiClient.NormalizeUrl(apiUrlInput ? apiUrlInput.text : "");
        string key = apiKeyInput ? apiKeyInput.text.Trim() : "";
        string model = SelectedModel();

        if (useGemini && string.IsNullOrEmpty(key))
        {
            SetStatus("Enter an API key before saving.");
            return;
        }
        if (useGemini && string.IsNullOrEmpty(model))
        {
            SetStatus("Test the connection and select a model before saving.");
            return;
        }

        data.useGeminiAI = useGemini;
        data.geminiApiUrl = url;
        data.geminiApiKeyEncrypted = SecureStore.Protect(key);
        if (!string.IsNullOrEmpty(model)) data.geminiModel = model;
        SaveLoadHandler.Instance.SaveToDisk();

        if (apiUrlInput) apiUrlInput.SetTextWithoutNotify(url);
        AIProviderRouter.Instance?.ApplyProviderChange(useGemini);

        if (!string.IsNullOrEmpty(key) && string.IsNullOrEmpty(data.geminiApiKeyEncrypted))
            SetStatus("Saved, but the API key could not be encrypted.");
        else
            SetStatus(useGemini ? "Saved - using Gemini (" + DisplayName(model) + ")." : "Saved - using local Llama model.");
    }

    string SelectedModel()
    {
        if (modelDropdown == null || models.Count == 0) return "";
        int i = Mathf.Clamp(modelDropdown.value, 0, models.Count - 1);
        return models[i];
    }

    void RebuildModelDropdown(string select)
    {
        if (modelDropdown == null) return;
        var labels = new List<string>();
        foreach (var m in models) labels.Add(DisplayName(m));
        if (labels.Count == 0) labels.Add("(test connection first)");

        modelDropdown.ClearOptions();
        modelDropdown.AddOptions(labels);
        modelDropdown.interactable = models.Count > 0;
        modelDropdown.SetValueWithoutNotify(Mathf.Max(0, models.IndexOf(select)));
        modelDropdown.RefreshShownValue();
    }

    static string DisplayName(string model) =>
        string.IsNullOrEmpty(model) ? "" : model.StartsWith("models/") ? model.Substring(7) : model;

    void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
    }
}
