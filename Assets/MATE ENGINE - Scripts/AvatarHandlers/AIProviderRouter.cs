using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LLMUnity;
using UnityEngine;

// Routes AI chat either to the local llama.cpp model (LLMUnity) or to the Gemini API.
// Both providers share the LLMCharacter's system prompt and chat history.
[DefaultExecutionOrder(-2000)]
public class AIProviderRouter : MonoBehaviour
{
    public static AIProviderRouter Instance { get; private set; }

    [Header("References")]
    public LLM llm;
    public LLMCharacter llmCharacter;

    [Header("Gemini")]
    [Tooltip("Tokens reserved for the reply when trimming history to the context length.")]
    public int replyTokenReserve = 1024;

    public bool UseGemini { get; private set; }
    public event Action<bool> OnProviderChanged;

    CancellationTokenSource geminiCts;

    void Awake()
    {
        Instance = this;
        if (!llm) llm = FindFirstObjectByType<LLM>(FindObjectsInactive.Include);
        if (!llmCharacter) llmCharacter = FindFirstObjectByType<LLMCharacter>(FindObjectsInactive.Include);

        UseGemini = SaveLoadHandler.Instance != null && SaveLoadHandler.Instance.data.useGeminiAI;

        // LLM.Awake() returns early when disabled, so the local server never starts.
        if (UseGemini && llm != null) llm.enabled = false;
        Debug.Log("[AI] Provider: " + ProviderName);
    }

    public string ProviderName => UseGemini ? "Gemini" : "Local Llama";

    const string LocalUnavailableMessage =
        "[Local model unavailable] The local Llama model could not be loaded (model file missing or failed to start). " +
        "Enable Gemini in Settings > AI, or install the local model.";

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        geminiCts?.Cancel();
    }

    public void ApplyProviderChange(bool useGemini)
    {
        if (useGemini == UseGemini) return;
        UseGemini = useGemini;
        CancelRequests();

        if (llm != null)
        {
            if (useGemini)
            {
                // Free the local model's memory.
                if (llm.started) llm.Destroy();
                llm.enabled = false;
            }
            else EnsureLocalLLMStarted();
        }

        Debug.Log("[AI] Provider: " + ProviderName);
        OnProviderChanged?.Invoke(useGemini);
    }

    void EnsureLocalLLMStarted()
    {
        if (llm == null || llm.started) return;
        if (!llm.enabled)
        {
            llm.enabled = true;
            // Only start manually if Unity already ran Awake on this object (otherwise Unity will call it).
            if (llm.gameObject.activeInHierarchy) llm.Awake();
        }
    }

    public async Task Warmup(EmptyCallback completionCallback)
    {
        if (UseGemini)
        {
            completionCallback?.Invoke();
            return;
        }
        if (!await WaitForLocalLLM())
        {
            if (!UseGemini) Debug.LogWarning("[AI] " + LocalUnavailableMessage);
            completionCallback?.Invoke();
            return;
        }
        await llmCharacter.Warmup(completionCallback);
    }

    // Starts the local server if needed and waits until it is running. False if it failed or the provider changed.
    async Task<bool> WaitForLocalLLM()
    {
        if (llm == null) return false;
        EnsureLocalLLMStarted();
        while (!llm.started && !llm.failed)
        {
            if (UseGemini) return false;
            await Task.Yield();
        }
        return llm.started && !UseGemini;
    }

    public async Task<string> Chat(string query, Callback<string> callback = null, EmptyCallback completionCallback = null)
    {
        if (!UseGemini)
        {
            if (llm == null || llm.failed)
            {
                callback?.Invoke(LocalUnavailableMessage);
                completionCallback?.Invoke();
                return null;
            }
            return await llmCharacter.Chat(query, callback, completionCallback);
        }

        var data = SaveLoadHandler.Instance.data;
        string key = SecureStore.Unprotect(data.geminiApiKeyEncrypted);

        geminiCts?.Cancel();
        geminiCts = new CancellationTokenSource();

        string result = null;
        try
        {
            var history = BuildGeminiHistory(query, data.contextLength);
            result = await GeminiClient.StreamChat(data.geminiApiUrl, key, data.geminiModel, llmCharacter.prompt,
                history, partial => callback?.Invoke(partial), geminiCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.LogWarning("[Gemini] " + e.Message);
            callback?.Invoke("[Gemini error] " + e.Message);
            result = null;
        }

        if (!string.IsNullOrEmpty(result))
        {
            llmCharacter.AddPlayerMessage(query);
            llmCharacter.AddAIMessage(result);
            SaveHistory();
        }

        completionCallback?.Invoke();
        return result;
    }

    public void CancelRequests()
    {
        geminiCts?.Cancel();
        if (!UseGemini && llmCharacter != null && llm != null && llm.started) llmCharacter.CancelRequests();
    }

    List<GeminiClient.Message> BuildGeminiHistory(string query, int contextLength)
    {
        int budget = Mathf.Max(512, (contextLength > 0 ? contextLength : 4096) - replyTokenReserve);
        budget -= EstimateTokens(llmCharacter.prompt) + EstimateTokens(query);

        var picked = new List<GeminiClient.Message>();
        var chat = llmCharacter.chat;
        // chat[0] is the system prompt; walk back from the newest message.
        for (int i = chat.Count - 1; i >= 1; i--)
        {
            int cost = EstimateTokens(chat[i].content);
            if (cost > budget) break;
            budget -= cost;
            picked.Insert(0, new GeminiClient.Message { isUser = chat[i].role == llmCharacter.playerName, text = chat[i].content });
        }
        // Gemini expects the conversation to start with a user turn.
        while (picked.Count > 0 && !picked[0].isUser) picked.RemoveAt(0);

        picked.Add(new GeminiClient.Message { isUser = true, text = query });
        return picked;
    }

    static int EstimateTokens(string s) => string.IsNullOrEmpty(s) ? 0 : s.Length / 4 + 1;

    // Mirrors LLMCharacter.Save() without the llama.cpp cache slot (which needs the local server).
    void SaveHistory()
    {
        if (string.IsNullOrEmpty(llmCharacter.save)) return;
        try
        {
            string path = llmCharacter.GetJsonSavePath(llmCharacter.save);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var chat = llmCharacter.chat;
            string json = JsonUtility.ToJson(new ChatListWrapper { chat = chat.GetRange(1, chat.Count - 1) });
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            Debug.LogError("[Gemini] Failed to save chat history: " + e);
        }
    }
}
