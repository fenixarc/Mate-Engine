using UnityEngine;
using UnityEngine.UI;
using System.IO;

public class DeleteAIHistory : MonoBehaviour
{
    [Header("UI Button to delete AI history")]
    public Button deleteButton;

    [Tooltip("Base filename for AI history. Default is 'ZomeAI'.")]
    public string fileName = "ZomeAI";

    void Start()
    {
        if (deleteButton != null)
        {
            deleteButton.onClick.AddListener(DeleteHistoryFiles);
        }
        else
        {
            Debug.LogWarning("[DeleteAIHistory] Delete Button is not assigned.");
        }
    }

    public void DeleteHistoryFiles()
    {
        // Clear the conversation held in memory first, otherwise the next message writes it straight back to disk.
        ClearLoadedHistory();

        string jsonPath = Path.Combine(Application.persistentDataPath, fileName + ".json");
        string cachePath = Path.Combine(Application.persistentDataPath, fileName + ".cache");

        if (File.Exists(jsonPath))
        {
            File.Delete(jsonPath);
            Debug.Log("[DeleteAIHistory] Deleted: " + jsonPath);
        }

        if (File.Exists(cachePath))
        {
            File.Delete(cachePath);
            Debug.Log("[DeleteAIHistory] Deleted: " + cachePath);
        }

        Debug.Log("[DeleteAIHistory] Chat history cleared.");
    }

    void ClearLoadedHistory()
    {
        AIProviderRouter.Instance?.CancelRequests();

        var llmCharacter = FindFirstObjectByType<LLMUnity.LLMCharacter>(FindObjectsInactive.Include);
        if (llmCharacter != null) llmCharacter.ClearChat();

        var chatBot = FindFirstObjectByType<LLMUnitySamples.ChatBot>(FindObjectsInactive.Include);
        if (chatBot != null) chatBot.ClearChatBubbles();
    }
}
