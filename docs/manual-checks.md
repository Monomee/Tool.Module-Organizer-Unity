# Manual checks

Checks that cannot run as EditMode tests (domain reload, interactive Editor). Run them in the Unity Editor on `TestHost~`.

## Task 9 — Apply result after domain reload

`ApplyResultWatcher` keeps the Apply result in `SessionState` across the reload that follows a successful compile.
Until the Module Organizer window exists, drive it from a throwaway Editor script you delete afterwards.

Setup:

1. Open `TestHost~` in Unity 6000.3. Wait for the import to finish, Console clear of errors.
2. Create `Assets/ProbeTmp/ProbeHolder.cs`:
   `public class ProbeHolder : UnityEngine.ScriptableObject { [UnityEngine.SerializeReference] public object p; }`
3. Create `Assets/ProbeTmp/ProbePayload.cs`:
   `namespace Probe.Old { [System.Serializable] public class ProbePayload { public int x = 7; } }`
4. Create `Assets/ProbeTmp/Editor/MonomeProbe.cs` with:
   - an `[InitializeOnLoad]` static ctor that subscribes `ApplyResultWatcher.ResultReady += () => Debug.Log("MONOME_RESULT " + JsonUtility.ToJson(ApplyResultWatcher.LastResult));`
   - `[MenuItem("Probe/Make Asset")]`: `CreateInstance<ProbeHolder>()`, set `p = new Probe.Old.ProbePayload()`, `AssetDatabase.CreateAsset(h, "Assets/ProbeTmp/ProbeAsset.asset")`, `SaveAssets()`.
   - `[MenuItem("Probe/Move")]`, `[MenuItem("Probe/Editor Error")]`, `[MenuItem("Probe/Player Error")]`: each calls
     `ApplyResultWatcher.BeginPending(keys)`, writes one file with `File.WriteAllText`, then `AssetDatabase.Refresh()`
     (sources and keys below).
5. Run **Probe > Make Asset**. Expected: `ProbeAsset.asset` (open as text) contains
   `type: {class: ProbePayload, ns: Probe.Old, asm: Assembly-CSharp}`.

Successful compile path:

6. **Probe > Move**: keys `["Probe.Old|Assembly-CSharp|ProbePayload"]`, rewrite `ProbePayload.cs` to
   `namespace Probe.New { [UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: "Probe.Old")] [System.Serializable] public class ProbePayload { public int x = 7; } }`.
   Expected: compile, domain reload, then a short player compile (progress bar), then in the Console
   `MONOME_RESULT` with `editorCompileOk: true`, `playerCompileOk: true`, `buildTarget` = the active target,
   `reserialized: ["Assets/ProbeTmp/ProbeAsset.asset"]`, empty error and warning lists.
7. Open `ProbeAsset.asset` as text. Expected: `type: {class: ProbePayload, ns: Probe.New, asm: Assembly-CSharp}`, `x: 7` kept.
8. Expected: `Temp/MonomePlayerCompile` does not exist.

Compile-error path:

9. **Probe > Editor Error**: no keys, write `Assets/ProbeTmp/ProbeBroken.cs` = `public class ProbeBroken { int x = ; }`.
   Expected: no reload; `MONOME_RESULT` with `editorCompileOk: false`, `editorErrors` holding the `error CS1525` line for
   `ProbeBroken.cs`, `playerCompileOk: false`, `playerErrors` empty.
10. Pending is cleared: delete `ProbeBroken.cs` and let it recompile and reload. Expected: no new `MONOME_RESULT`.

Player-only error path:

11. **Probe > Player Error**: write `ProbeBroken.cs` =
    `public class ProbeBroken {` / `#if !UNITY_EDITOR` / `    int x = ;` / `#endif` / `}` (one per line).
    Expected: Editor compiles, reload, then `MONOME_RESULT` with `editorCompileOk: true`, `playerCompileOk: false`,
    `playerErrors` holding the `ProbeBroken.cs(...): error CS...` line.

Cleanup:

12. Delete `Assets/ProbeTmp` (and its `.meta`). `git status` in `TestHost~` shows nothing.
