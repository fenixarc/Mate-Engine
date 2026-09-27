# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Mate Engine (product name `MateEngineX`) is a Unity desktop-mascot app: a transparent, always-on-top window showing a VRM avatar that idles, dances to system audio, sits on windows/taskbar, tracks the mouse, and supports mods, Steam Workshop, and a local LLM chat. The main target is Windows x64 (Mono backend). A community Linux port lives in a separate repo.

- **Unity version:** `6000.2.6f2` (see `ProjectSettings/ProjectVersion.txt`). Open the project with that exact version in Unity Hub.
- **Main scene:** `Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`. `EditorBuildSettings` has no scenes listed, so add the main scene before building.
- **Scripting defines (Standalone):** `STEAMWORKS_NET;UNITY_POST_PROCESSING_STACK_V2`.
- **License:** mixed AGPL v3 and MateProv2. Do not redistribute the default avatar (Yorshka Shop, all rights reserved).

## Build / compile / test

There is no CLI build script. Builds are made from the Unity Editor (File → Build Profiles → Windows x64). `Assets/Editor/PostBuildCopy.cs` copies `steam_api64.dll` and `steam_appid.txt` from `Assets/Plugins/` into the build folder after every build.

To check that scripts compile without opening the editor (the editor must be closed on this project):

```bash
"C:/Program Files/Unity/Hub/Editor/6000.2.6f2/Editor/Unity.exe" -batchmode -quit -projectPath . -logFile -
```

The only automated tests are LLMUnity's own tests (`Assets/LLMUnity/Tests`). Run them from Window → General → Test Runner, or in batch mode with `-runTests -testPlatform EditMode|PlayMode -testFilter <name>`. Mate Engine's own code has no tests, so verify changes by entering Play Mode in the main scene.

The LLM runtime and `.gguf` models in `Assets/StreamingAssets/` are gitignored and must be supplied locally for AI chat to work.

## Code layout

All first-party runtime code compiles into **Assembly-CSharp**, because it has no asmdefs. Editor tooling is in `Assets/Editor/` and the other `Editor/` folders. Most folders prefixed `MATE ENGINE -` are first-party. Everything else is vendored third-party code, and `Assets/MATE ENGINE - Packages/` holds vendored UniVRM/VRM10/UniGLTF, Steamworks.NET, UniWindowController (Kirurobo), StandaloneFileBrowser, and NuGet DLLs. Other vendored code includes LLMUnity, DiscordRPC, DynamicBone, uWindowCapture, UMotion, and the Poiyomi/lilToon shaders. Avoid editing vendored code unless the task requires it.

`Assets/csc.rsp` adds `System.Drawing` (`SYSTEM_DRAWING` define), and `Assets/Editor/CsprojPostprocessor.cs` adds `System.Windows.Forms` to the generated csproj. Some Windows-only APIs (WinForms, NAudio, P/Invoke in `APIs/WinApi.cs` and `APIs/DwmApi.cs`) are used directly in runtime code.

Code comments are sometimes in German. Keep the surrounding style, which is plain MonoBehaviours, public inspector fields, and heavy use of `FindObjectsByType` / `Resources.FindObjectsOfTypeAll`.

## Architecture (big picture)

**Settings flow.** `Settings/SaveLoadHandler.cs` is a `DontDestroyOnLoad` singleton that holds `SettingsData` and serializes it with Newtonsoft JSON to `Application.persistentDataPath/settings.json`. `ApplyAllSettingsToAllAvatars()` pushes settings into every `AvatarAnimatorController` and its child components (mouse tracking, IK, particles, hand holding, food, window sitting, locomotion). To add a setting:
1. Add a field to `SettingsData`.
2. Handle migration in `MigrateAfterLoad()` if needed.
3. Apply it in `ApplyAllSettingsToAllAvatars()`.
4. Wire the UI in the matching `Settings/SettingsMenu/SettingsHandler*.cs` (Toggles, Sliders, Dropdowns, and so on), with `AvatarSettingsMenu.cs` as the menu root.

**Multi-instance.** Up to 9 avatars run as separate processes. `VRMLoader/LaunchMateEngineInstance.cs` launches copies of the app with `--instance N --savefile <file> --datadir <dir>`, and `SaveLoadHandler` reads `--savefile` and `--datadir` to isolate each instance's settings. Dance sync between instances goes through `AvatarDanceSync` / `AvatarSyncDanceTools` and `Sync/dance_sync.json`.

**Avatar loading.** `VRMLoader/VRMLoader.cs` loads `.vrm` files through UniVRM/VRM10, and loads `.me` files and prefab asset bundles through `LoadAssetBundleModel`. It then:
- disables the default model,
- assigns the shared animator controller,
- copies components from a prefab template onto the new model (`InjectComponentsFromPrefab` / `CopyComponentValues`),
- records the model in `persistentDataPath` (`VRM/` folder, `avatars.json`).

`MEModLoader.AssignHandlersForCurrentAvatar` re-binds handlers after a swap. Components that cache avatar references must tolerate the avatar being replaced at runtime (see `AvatarRebindHandler`).

**Avatar behaviour.** `AvatarHandlers/AvatarAnimatorController.cs` is the core state driver. It samples system audio via NAudio to trigger dancing, checks the foreground app against `allowedApps`, and drives Animator parameters (`isIdle`, `isDragging`, `isDancing`, `IdleIndex`, `DanceIndex`, `isMale`/`isFemale`). Other `AvatarHandlers/*` components each own one feature (window sitting, taskbar, big-screen mode, sleep, hide, food, particles, and so on) and react to those Animator states. Animation clips are in `Assets/MATE ENGINE - Animations/` and grouped by state (`PET_IDLE`, `PET_DANCING`, …).

**Mods (`.me` files).** A `.me` file is a zip holding an AssetBundle plus JSON metadata (mod info, type, reference-path and scene-link maps). The two sides of this format must stay in sync:
- **Export** runs from editor windows under the `MateEngine/` menu (`ModExporterWindow`, `MEModelExporter`, `MESDK`, `MEModInitializer`).
- **Import** is handled by `Settings/MEModHandler.cs`, which loads from `persistentDataPath/Mods` (`.me` and legacy `.unity3d`). It sorts mods into dance mods and object mods, and object mods re-link references into the live scene by hierarchy path.

`Assets/scene_registry.json` (made with `MateEngine/Export Scene Registry`) lists scene hierarchy paths that mods can target. Renaming or moving GameObjects in the main scene can break existing mods. Mod-side runtime components live in `Assets/MATE ENGINE - Mod SDK/`.

**Steam.** `APIs/SteamDRM.cs` checks ownership and DLC through Steamworks. When Steam is unavailable it falls back to a cached signed token (`SteamDRM.token`). `SteamVersionObjects` gates Steam-only content, and `SteamWorkshopHandler` / `SteamWorkshopAutoLoader` handle Workshop items. Code under `#if STEAMWORKS_NET` must still compile when the define is off.

**AI chat.** The chat UI is the project-modified `Assets/LLMUnity/Samples/ChatBot/ChatBot.cs` (`LLMUnitySamples.ChatBot`), and the `LLM`/`LLMCharacter` objects live under `Settings/ChatBot AI/ChatMenuPanel/Chat` (the panel starts inactive, so `LLM.Awake` only runs when chat is first opened). `AISystemPromptBinder` loads the system prompt from `LocalLow/Shinymoon/MateEngineX/ZomeAI_prompt.txt`. There are two providers:
- **Local:** llama.cpp with Qwen `.gguf` models.
- **Gemini API:** `AvatarHandlers/AIProviderRouter.cs` routes `ChatBot`'s warm-up, chat, and cancel calls to either `LLMCharacter` or `APIs/GeminiClient.cs` (REST calls with SSE streaming). When Gemini is on, the router disables `LLM` before its `Awake`, so the local server never starts.
  - Both providers share `llmCharacter.chat` and the `ZomeAI` history file. In Gemini mode the router writes the history JSON itself, because `LLMCharacter.Save` needs the local server for the cache slot.
  - Settings live in `SettingsData` (`useGeminiAI`, `geminiApiUrl`, `geminiApiKeyEncrypted`, `geminiModel`), and the UI is `Settings/SettingsMenu/SettingsHandlerAIProvider.cs`.
  - The API key is encrypted with Windows DPAPI via `APIs/SecureStore.cs` and is never stored in plain text.
  - The settings controls were made by the one-shot Editor tool `MateEngine/Build Gemini AI Settings UI` (`Assets/Editor/MEGeminiSettingsBuilder.cs`).

`SaveLoadHandler` runs at `DefaultExecutionOrder(-3000)` so that other scripts can read settings in `Awake`.

**Localization** uses the Unity Localization package (`Lang/`, `LanguageDropdownHandler`, `TMPFontReplacer` for CJK fonts).
