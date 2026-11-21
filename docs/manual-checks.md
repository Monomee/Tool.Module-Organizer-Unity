# Manual checks

Checks that cannot run as EditMode tests (domain reload, interactive Editor). Run them in the Unity Editor on `TestHost~`.

## Task 9 — Apply result after domain reload

`ApplyResultWatcher` keeps the Apply result in `SessionState` across the reload that follows a successful compile.
These steps drive the watcher directly from a throwaway Editor script you delete afterwards.

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
    After a real Apply that hit compile errors the config still owes its asset migration (`owedReserializeKeys` in the
    `ModuleConfig` asset, Apply blocked with "pending asset migration"): this reload re-serializes those assets, logs
    `[Module Organizer] Re-serialized assets an earlier Apply still owed: ...` (when any matched), empties
    `owedReserializeKeys` and unblocks Apply. The probe's direct `BeginPending` owes nothing, so here nothing is logged.

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

## Task 13 — End-to-end in the Editor and Visual Studio 2026

Fixture: `TestHost~/Assets/_E2E/` (đã commit). Scope là `Assets/_E2E/Scripts`: `E2EShooter` (dùng trong
`Content/E2EShooter.prefab`, `power = 42`), `E2EPayload` (`[SerializeReference]` trong `Content/E2EStore.asset`,
`value = 7`), `E2EStore`, `E2EEmitter` + `E2ETarget` (UnityEvent `onFire → Target.Ping` trong `Content/E2EScene.unity`),
`Editor/E2EShooterEditor.cs`, `Misc/E2ELoose.cs` (namespace `E2E.Legacy`). Driver headless nằm ở `Assets/_E2E/Editor/`,
ngoài scope nên tool không bao giờ chuyển nó.

### Chạy E2E headless (không cần giao diện)

Đóng mọi Unity đang mở `TestHost~`, `git status --porcelain` phải trống, rồi trong Git Bash ở thư mục gốc repo:

```bash
UNITY="/c/Program Files/Unity/Hub/Editor/6000.3.18f1/Editor/Unity.exe"
"$UNITY" -batchmode -nographics -projectPath "$PWD/TestHost~" -executeMethod E2EDriver.Run -logFile "$PWD/TestHost~/e2e-run.log"
echo exit=$?   # 0 = mọi phase PASS
grep "^E2E " "TestHost~/e2e-run.log"
# Dọn: trả TestHost~ về đúng trạng thái đã commit (chỉ trong TestHost~)
git checkout -- "TestHost~" && git clean -fd -- "TestHost~/Assets"
```

Không dùng `-quit`: các phase chạy nối nhau trong cùng một tiến trình qua các lần domain reload (kết quả Apply nằm trong
`SessionState`), driver tự `Exit`. Kết quả mong đợi, năm dòng `E2E A PASS`, `E2E B PASS`, `E2E C PASS`, `E2E D PASS`,
`E2E E PASS`:
A = gán 2 module + 1 file Editor và Apply; B = compile Editor/player OK, prefab không mất script, `[SerializeReference]`
còn dữ liệu và YAML được serialize lại, UnityEvent còn listener và gọi được; C = đổi tên module, chuyển file, bỏ gán,
Apply lại; D = file bỏ gán về đúng chỗ + namespace gốc; E = file thêm bằng tay được Sync phát hiện và "Take from disk".
Muốn tạo lại asset fixture: `-executeMethod E2EDriver.Setup -quit`.

### Kiểm tra tay trong Editor và Visual Studio 2026

> Các bước này chỉ đụng tới `TestHost~`; không chạy lệnh git phá hủy nào trên toàn repo.

0. Ở thư mục gốc repo chạy `git status --porcelain`. Nếu có dòng nào, **dừng lại**: commit hoặc stash trước (bước 12
   sẽ xóa mọi thay đổi trong `TestHost~`).
1. Mở `TestHost~` bằng Unity 6000.3, đợi import xong, Console không có lỗi. **Tools > Monome > Module Organizer** >
   **Create ModuleConfig**. Thêm scope `Assets/_E2E/Scripts`.
   Mong đợi: cây hiện `E2EEmitter.cs`, `E2EPayload.cs`, `E2EShooter.cs`, `E2EStore.cs`, `E2ETarget.cs`, `Editor/`
   (có `E2EShooterEditor.cs` gắn nhãn `ed`) và `Misc/E2ELoose.cs`.
2. Tạo module `Alpha` (namespace `Game.Alpha`) và `Beta` (namespace `Game.Beta`). **Kéo thả** `E2EShooter.cs`,
   `E2EPayload.cs`, `E2EStore.cs` và `Editor/E2EShooterEditor.cs` vào thẻ Alpha; kéo `E2EEmitter.cs`, `E2ETarget.cs`,
   `Misc/E2ELoose.cs` vào thẻ Beta.
   Mong đợi: tab Issues không có lỗi ✗; tab Plan có 7 lượt chuyển file, 3 asmdef (`Game.Alpha`, `Game.Alpha.Editor`,
   `Game.Beta`) và dòng `+using Editor = global::UnityEditor.Editor` cho `E2EShooterEditor.cs`.
3. **Apply** > **Confirm Apply** (hộp thoại cảnh báo git hiện vì `ModuleConfig.asset` chưa commit: chọn **Apply**).
   Mong đợi: sau khi compile và reload, tab Result báo `Editor compile: OK` và `Player compile (<target>): OK`, có dòng
   `Re-serialized Assets/_E2E/Content/E2EStore.asset`. Console không có lỗi.
4. Mở `Content/E2EShooter.prefab`: component `E2EShooter` không bị "Missing script", `Power = 42`. Chọn
   `Content/E2EStore.asset`: trường `Payload` vẫn có `Value = 7`. Mở `Content/E2EScene.unity`, chọn `Emitter`: danh sách
   `On Fire ()` vẫn có một listener trỏ tới `Target` > `E2ETarget.Ping`.
5. Unity: **Edit > Preferences > External Tools**, chọn Visual Studio 2026, bấm **Regenerate project files**, rồi
   **Assets > Open C# Project** (hoặc mở `TestHost~/TestHost~.sln` trong Visual Studio 2026).
   Mong đợi: Solution Explorer có các project `Game.Alpha`, `Game.Alpha.Editor`, `Game.Beta` (cạnh `Assembly-CSharp`,
   `Assembly-CSharp-Editor`, `E2E.Driver`...). Trong `Game.Alpha`, `E2EShooter.cs` có `namespace Game.Alpha`; trong
   `Game.Alpha.Editor`, `E2EShooterEditor.cs` có `namespace Game.Alpha.Editor`; trong `Game.Beta`, `Misc/E2ELoose.cs` có
   `namespace Game.Beta.Misc`. Build Solution trong Visual Studio không có lỗi; không có gạch đỏ ở `: Editor`.
6. Trở lại Module Organizer: đổi tên module `Beta` thành `Gamma` và namespace thành `Game.Gamma`; kéo
   `E2EPayload.cs` từ thẻ Alpha sang thẻ Gamma; kéo `E2ELoose.cs` từ thẻ Gamma về cây bên trái (nó hiện chữ nghiêng).
   Mong đợi: không có lỗi ✗; Plan có chuyển thư mục `Beta` → `Gamma`, `E2ELoose.cs` về `Assets/_E2E/Scripts/Misc/`.
7. **Apply** > **Confirm Apply**. Mong đợi: Result báo compile Editor và player OK. `Misc/E2ELoose.cs` nằm lại đúng
   `Assets/_E2E/Scripts/Misc/` với `namespace E2E.Legacy` (namespace gốc) và có thêm `using Game.Gamma;`. Thư mục
   `Beta` không còn; có `Gamma/Game.Gamma.asmdef`. Prefab, asset và scene ở bước 4 vẫn đúng như cũ.
8. Visual Studio 2026 (sau khi Unity regenerate, hoặc bấm **Regenerate project files** lần nữa): project `Game.Beta`
   biến mất, có `Game.Gamma`; `E2ELoose.cs` trở lại `Assembly-CSharp`.
9. Trong cửa sổ Project, tạo script mới `E2EAdded.cs` trong `Assets/_E2E/Scripts/Gamma/` (chuột phải > Create > Scripting
   > Empty C# Script, hoặc tạo file bằng Visual Studio), nội dung `namespace Game.Gamma { public class E2EAdded { } }`.
   Mong đợi: sau compile, toolbar của Module Organizer hiện `⚠ 1 drift → Sync`; tab Sync liệt kê
   `ScriptAddedInModuleFolder · Gamma: Assets/_E2E/Scripts/Gamma/E2EAdded.cs is in ... but not in the module.`
10. Bấm **Take from disk** trên dòng đó. Mong đợi: dòng biến mất, `E2EAdded.cs` xuất hiện trong thẻ Gamma, tab Plan
    báo "Nothing to apply".
11. Thử hoàn tác bằng git: đóng Unity, chạy `git status` (chỉ có thay đổi trong `TestHost~/`).
12. Dọn dẹp, chỉ cho project test: `git checkout -- "TestHost~"` và `git clean -fd -- "TestHost~/Assets"`. Mở lại
    `TestHost~` để import lại. Mong đợi: các file quay về chỗ cũ, không lỗi compile; `git status --porcelain` trống.
