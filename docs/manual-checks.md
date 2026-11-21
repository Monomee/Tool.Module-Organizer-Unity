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
   - `[MenuItem("Probe/Make Asset")]`: `CreateInstance<ProbeHolder>()`, set
     `p = System.Activator.CreateInstance(System.Type.GetType("Probe.Old.ProbePayload, Assembly-CSharp"))` (by reflection:
     a compile-time `new Probe.Old.ProbePayload()` would break the compile once step 6 removes that namespace),
     `AssetDatabase.CreateAsset(h, "Assets/ProbeTmp/ProbeAsset.asset")`, `SaveAssets()`.
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

## Task 11 — Module Organizer window

Open `TestHost~` in the Unity Editor (no compile errors). Covered headlessly by `ModuleOrganizerWindowTests`
(no mouse, no dialogs): the named UI elements, and the logic of steps 2, 5-7 and 10's first click (issues listed, Apply
gate, Ctrl+Z of an assign re-validates, Apply arms then disarms on re-validate). Manual only: the real drag and drop in
5-8, the folder picker, layout, dialogs, and the whole Apply / reload / Sync part (steps 10-15).

> Các bước này chỉ đụng tới `TestHost~`; không chạy lệnh git phá hủy nào trên toàn repo.
> These steps only touch `TestHost~`; never run a destructive repo-wide git command for this check.

0. In the repo root run `git status --porcelain`. If it prints anything, **stop**: commit or stash your work first
   (cleanup in step 15 discards everything under `TestHost~`).
1. **Tools > Monome > Module Organizer**. Expected: HelpBox "No ModuleConfig yet", toolbar shows **Create ModuleConfig**
   (no config dropdown), Scan disabled.
2. Click **Create ModuleConfig**. Expected: `Assets/ModuleConfig.asset` is created and selected in the dropdown; the scan
   runs, the HelpBox disappears and the tree header reads `Unassigned (0)` (no scope yet).
3. Click **+** next to *Scope*, pick `Assets/_Fixtures/Scope`. Expected: it is listed; the tree shows
   `Assets/_Fixtures/Scope/` with `Editor/` and the `Fx*.cs` files (`FxAEditor.cs` has an `ed` badge). Picking a folder
   outside `Assets/` shows "Scope folders must be inside Assets/."
4. Click **+ Module**, select the card, rename it `Alpha` in the panel (Enter), set Namespace to `Game.Alpha` (Enter).
   Expected: card title `Alpha`, subtitle `Game.Alpha`.
5. Drag `FxA.cs` from the tree onto the Alpha card. Expected: FxA leaves the tree and appears in the card with ✗;
   Issues tab lists `✗ [OutOfScopeRef] ... FxOut ...` and `✗ [UnassignedInScopeRef] ... FxB ...` (also FxLambdaOnly);
   Apply is disabled (tooltip "Fix the Error issues first.").
6. Ctrl+Z. Expected: FxA is back in the tree, issues gone. Ctrl+Y to redo it.
7. Drag `FxB.cs` and `FxLambdaOnly.cs` (multi-select, one drag) onto Alpha; add scope `Assets/_Fixtures/Outside`, drag
   `FxOut.cs` onto Alpha. Expected: no Error issues, Plan tab lists the moves into `Assets/_Fixtures/Scope/Alpha`,
   asmdef `Game.Alpha.asmdef` and the namespace edits; **Apply** is enabled.
8. Drag `FxB.cs` from the Alpha card back onto the tree. Expected: it is back in the tree in normal text (a draft is just
   dropped; only an unassigned *applied* file shows in italics with the tooltip "Unassigned: Apply moves it back to where
   it was.") and the FxB error is back. Drag the `Scope/` folder node onto Alpha: every unassigned file under it is
   assigned. Ctrl+Z twice, then drag `FxB.cs` onto Alpha again: back to step 7's state.
9. Drag `FxB.cs` within the Alpha card (onto Alpha itself). Expected: nothing changes.
10. Click **Apply**. Expected: the Plan tab opens and the button reads `Confirm Apply (N moves, 1 asmdefs)`. Edit the
    module description (Enter). Expected: the button is back to `Apply`.
11. Click **Apply**, then **Confirm Apply**. Expected: dialog "The project has uncommitted git changes..." (the new
    `ModuleConfig.asset` is uncommitted). Click **Cancel**: nothing on disk changes, button back to `Apply`. Click
    **Apply**, **Confirm Apply**, then **Apply** in the dialog.
12. Expected: the files move to `Assets/_Fixtures/Scope/Alpha/` with `Game.Alpha.asmdef`. While Unity compiles, the
    HelpBox reads "Scripts are compiling: Scan is locked..." and Scan is disabled. After the domain reload the Result tab
    opens with `Editor compile: OK` above `Player compile (<active target>): OK`; the window rescans and the Plan tab
    reads "Nothing to apply".
13. Ctrl+Z. Expected: the config does not change (Alpha still holds the four files, Plan still empty): Apply cleared
    the config's undo history.
14. In the Project window rename the folder `Assets/_Fixtures/Scope/Alpha` to `AlphaX` (this moves the asmdef; renaming
    only the `.asmdef` file would keep drifting, since Take from disk reads the asmdef's `name`). Expected: after the
    reload the toolbar shows `⚠ N drift → Sync`; the Sync tab lists `AsmdefMovedOrRenamed · Alpha` and the note
    "Re-apply applies the whole config". Click **Take from disk** on that item. Expected: the item is gone and the Alpha
    module's folder is `Assets/_Fixtures/Scope/AlphaX`.
15. Cleanup: close Unity. Run `git status --porcelain` and check that every listed path is under `TestHost~/` (if
    anything else is listed, stop and sort it out by hand). Then, only for the test project:
    `git checkout -- "TestHost~"` and `git clean -fd -- "TestHost~/Assets"`. Reopen `TestHost~` and let it reimport.
    `git status --porcelain` prints nothing.
