using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Minimal client for the Google Gemini REST API (generativelanguage.googleapis.com).
// All calls must be made from the Unity main thread.
public static class GeminiClient
{
    public struct Message
    {
        public bool isUser;
        public string text;
    }

    public class ModelListResult
    {
        public bool ok;
        public List<string> models = new List<string>();
        public string error;
    }

    public static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        return string.IsNullOrEmpty(url) ? SaveLoadHandler.SettingsData.DefaultGeminiApiUrl : url;
    }

    public static async Task<ModelListResult> ListModels(string baseUrl, string apiKey)
    {
        var result = new ModelListResult();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.error = "API key is empty.";
            return result;
        }

        baseUrl = NormalizeUrl(baseUrl);
        string pageToken = null;
        try
        {
            do
            {
                string url = baseUrl + "/models?pageSize=1000";
                if (!string.IsNullOrEmpty(pageToken)) url += "&pageToken=" + UnityWebRequest.EscapeURL(pageToken);

                using (var req = UnityWebRequest.Get(url))
                {
                    req.SetRequestHeader("x-goog-api-key", apiKey.Trim());
                    req.timeout = 20;
                    await Send(req);

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        result.error = DescribeError(req.responseCode, req.downloadHandler?.text, req.error);
                        return result;
                    }

                    var json = JObject.Parse(req.downloadHandler.text);
                    if (json["models"] is JArray models)
                    {
                        foreach (var m in models)
                        {
                            var methods = m["supportedGenerationMethods"] as JArray;
                            bool canChat = false;
                            if (methods != null)
                                foreach (var method in methods)
                                    if ((string)method == "generateContent") { canChat = true; break; }
                            string name = (string)m["name"];
                            if (canChat && !string.IsNullOrEmpty(name)) result.models.Add(name);
                        }
                    }
                    pageToken = (string)json["nextPageToken"];
                }
            } while (!string.IsNullOrEmpty(pageToken));
        }
        catch (Exception e)
        {
            result.error = e.Message;
            return result;
        }

        result.ok = true;
        return result;
    }

    // Streams a chat completion. onPartial receives the full text generated so far.
    // Returns the final text; throws on HTTP/API errors.
    public static async Task<string> StreamChat(string baseUrl, string apiKey, string model, string systemPrompt,
        List<Message> history, Action<string> onPartial, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Gemini API key is not set.");
        if (string.IsNullOrWhiteSpace(model)) throw new Exception("No Gemini model selected.");

        if (!model.StartsWith("models/") && !model.StartsWith("tunedModels/")) model = "models/" + model;
        string url = NormalizeUrl(baseUrl) + "/" + model + ":streamGenerateContent?alt=sse";

        var contents = new JArray();
        foreach (var m in history)
        {
            contents.Add(new JObject
            {
                ["role"] = m.isUser ? "user" : "model",
                ["parts"] = new JArray { new JObject { ["text"] = m.text ?? "" } }
            });
        }
        var body = new JObject { ["contents"] = contents };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            body["systemInstruction"] = new JObject { ["parts"] = new JArray { new JObject { ["text"] = systemPrompt } } };

        var handler = new SseHandler(onPartial);
        using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None)));
            req.downloadHandler = handler;
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("x-goog-api-key", apiKey.Trim());
            req.timeout = 120;

            await Send(req, token);
            token.ThrowIfCancellationRequested();

            if (req.result != UnityWebRequest.Result.Success)
                throw new Exception(DescribeError(req.responseCode, handler.RawText, req.error));
            if (!string.IsNullOrEmpty(handler.BlockReason) && string.IsNullOrEmpty(handler.Text))
                throw new Exception("Response blocked by Gemini (" + handler.BlockReason + ").");
            return handler.Text;
        }
    }

    static async Task Send(UnityWebRequest req, CancellationToken token = default)
    {
        var op = req.SendWebRequest();
        while (!op.isDone)
        {
            if (token.IsCancellationRequested)
            {
                req.Abort();
                break;
            }
            await Task.Yield();
        }
    }

    static string DescribeError(long code, string body, string fallback)
    {
        try
        {
            if (!string.IsNullOrEmpty(body))
            {
                // SSE error bodies may be prefixed with "data:"
                string trimmed = body.Trim();
                if (trimmed.StartsWith("data:")) trimmed = trimmed.Substring(5).Trim();
                if (trimmed.StartsWith("[")) trimmed = ((JArray)JArray.Parse(trimmed)).First?.ToString() ?? trimmed;
                var msg = (string)JObject.Parse(trimmed)["error"]?["message"];
                if (!string.IsNullOrEmpty(msg)) return $"HTTP {code}: {msg}";
            }
        }
        catch { }
        return code > 0 ? $"HTTP {code}: {fallback}" : fallback;
    }

    class SseHandler : DownloadHandlerScript
    {
        readonly Action<string> onPartial;
        readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        readonly StringBuilder lineBuffer = new StringBuilder();
        readonly StringBuilder raw = new StringBuilder();
        readonly StringBuilder output = new StringBuilder();

        public string Text => output.ToString();
        public string RawText => raw.ToString();
        public string BlockReason { get; private set; }

        public SseHandler(Action<string> onPartial) : base(new byte[4096])
        {
            this.onPartial = onPartial;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return true;
            var chars = new char[decoder.GetCharCount(data, 0, dataLength)];
            decoder.GetChars(data, 0, dataLength, chars, 0);
            raw.Append(chars);

            foreach (char c in chars)
            {
                if (c == '\n')
                {
                    HandleLine(lineBuffer.ToString().TrimEnd('\r'));
                    lineBuffer.Clear();
                }
                else lineBuffer.Append(c);
            }
            return true;
        }

        protected override void CompleteContent()
        {
            if (lineBuffer.Length > 0)
            {
                HandleLine(lineBuffer.ToString().TrimEnd('\r'));
                lineBuffer.Clear();
            }
        }

        void HandleLine(string line)
        {
            if (!line.StartsWith("data:")) return;
            string payload = line.Substring(5).Trim();
            if (payload.Length == 0) return;

            try
            {
                var json = JObject.Parse(payload);
                var reason = (string)json["promptFeedback"]?["blockReason"];
                if (!string.IsNullOrEmpty(reason)) BlockReason = reason;

                var parts = json["candidates"]?[0]?["content"]?["parts"] as JArray;
                if (parts == null) return;
                bool added = false;
                foreach (var p in parts)
                {
                    if (p["thought"] != null && (bool)p["thought"]) continue;
                    var t = (string)p["text"];
                    if (!string.IsNullOrEmpty(t)) { output.Append(t); added = true; }
                }
                if (added) onPartial?.Invoke(output.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Gemini] Could not parse stream chunk: " + e.Message);
            }
        }
    }
}
