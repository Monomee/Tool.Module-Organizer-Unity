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

## Lỗi có thể gặp khi thử trong Unity

Mỗi mục: **Triệu chứng**, **Nguyên nhân có thể**, **Gửi lại gì** (để mình sửa nhanh). Với mọi mục, gửi kèm phiên bản
Unity, và nếu được thì file `ModuleConfig.asset` (mở bằng trình soạn thảo text) cùng `git status --porcelain`.
Console: bấm chuột phải vào dòng lỗi > **Copy**, hoặc gửi file `Editor.log`
(`%LOCALAPPDATA%\Unity\Editor\Editor.log` trên Windows).

1. **Kéo không bắt đầu, hoặc thả không được nhận** (con trỏ hiện dấu cấm, thẻ module không đổi).
   - Nguyên nhân có thể: chưa Scan (cửa sổ chỉ nhận file có trong lần scan); file nằm ngoài scope, thuộc
     firstpass hoặc thuộc một asmdef không do tool tạo (không gán được); kéo từ cửa sổ Project một thứ không phải `.cs`
     hay thư mục; danh sách trong thẻ chưa được chọn trước khi kéo (phải bấm chọn dòng rồi mới kéo).
   - Gửi lại: ảnh chụp cửa sổ lúc đang kéo (thấy con trỏ), đường dẫn file đã kéo, kéo từ đâu (cây bên trái, thẻ khác,
     cửa sổ Project), và Console có lỗi `[ModuleOrganizer]` hay exception nào không.

2. **Bố cục cửa sổ vỡ hoặc bị ép khi cửa sổ nhỏ** (cột chồng lên nhau, nút bị che, tab Issues/Plan không cuộn được).
   - Nguyên nhân có thể: USS/UXML không được nạp (cửa sổ hiện hộp lỗi `Missing ...uxml`), hoặc kích thước tối thiểu
     của các cột chưa hợp với màn hình/DPI của bạn.
   - Gửi lại: ảnh chụp ở kích thước bị vỡ và ở kích thước bình thường, độ phân giải màn hình và mức Scale của
     Windows, và dòng lỗi UI Toolkit trong Console nếu có.

3. **Tab Result cứ báo "Waiting for the compile result…"**.
   - Nguyên nhân có thể: Unity chưa compile xong (thanh tiến trình góc phải dưới); compile lỗi mà Unity không báo về
     (hãy xem Console); hoặc Apply không làm Unity compile gì nên phải đợi lần reload kế tiếp. Dòng chờ hiện
     `domain reload k of at most 3`: sau 3 lần reload mà chưa xong, kiểm tra tự bỏ và Result có cảnh báo.
   - Gửi lại: ảnh tab Result (cả số `k`), các dòng Console từ lúc bấm Confirm Apply, và trạng thái `Edit > Preferences
     > Asset Pipeline > Auto Refresh` (bật hay tắt). Nếu tắt Auto Refresh, bấm `Ctrl+R` rồi xem lại.

4. **Confirm Apply vẫn bị tắt (xám)**. Rê chuột lên nút Apply: tooltip nói lý do.
   - `Pending asset migration — fix compile errors first.`: một lần Apply trước còn nợ việc serialize lại asset
     (`owedReserializeKeys` trong config). Nguyên nhân: compile lỗi, Editor đóng trước khi reload, hoặc có asset không
     đọc được (Result/Console có dòng `Could not read ... stays owed`). Sửa lỗi compile, đóng file đang khóa asset đó,
     rồi bấm **Scan**.
   - `The plan has errors (see the Plan tab).` với `[WriteBoundary]`: config trỏ ra ngoài các scope folder và modules
     root (thường do đổi scope hoặc modulesRoot sau khi đã Apply). Tab Issues có nút **Add as scope** để thêm thư mục của
     module vào scope; hoặc đưa module về chỗ cũ.
   - `Fix the Error issues first.` với `[CapturedByModule]`: có file `.cs` nằm trong thư mục module mà không thuộc
     module đó (asmdef của module sẽ compile nó mà không sửa namespace). Gán nó vào module, chuyển nó ra ngoài, hoặc
     dùng Sync > Take from disk.
   - Gửi lại: nội dung tooltip, ảnh tab Issues và tab Plan, và `ModuleConfig.asset`.

5. **Chỉ lỗi compile player** (Editor compile OK, `Player compile (...)`: failed).
   - Nguyên nhân có thể: một file runtime dùng kiểu Editor (`UnityEditor`) mà không nằm trong `#if UNITY_EDITOR`; code
     trong `#if` của build target hiện tại dùng kiểu mà module asmdef không tham chiếu; một DLL chỉ dành cho Editor.
   - Gửi lại: các dòng `error CS...` trong tab Result (bấm chuột phải để copy, hoặc lấy từ Console), build target đang
     chọn (`File > Build Profiles`), và file `.asmdef` của module có lỗi.

6. **Inspector hiện "Missing script" hoặc mất dữ liệu `[SerializeReference]` sau Apply**.
   - Nguyên nhân có thể: Unity chưa reload xong (đợi compile); file bị chuyển mà thiếu `[MovedFrom]` (Apply lỗi giữa
     chừng); asset lưu dạng binary/Mixed nên không được serialize lại; kiểu bị chuyển hai lần trước khi asset được
     serialize lại. **Đừng lưu scene/prefab đó** cho tới khi rõ nguyên nhân: lưu lúc này làm mất dữ liệu thật sự. Quay
     lại bằng git nếu cần.
   - Gửi lại: ảnh Inspector, đường dẫn asset, đoạn YAML quanh dòng `type: {class: ..., ns: ..., asm: ...}` của asset
     (mở bằng trình soạn thảo text), đầu file `.cs` của kiểu đó (thấy `[MovedFrom(...)]` hay không), tab Result, và giá
     trị *Project Settings > Editor > Asset Serialization*.

7. **Visual Studio 2026 không thấy các project mới** (`Game.Alpha`, `Game.Alpha.Editor`...).
   - Nguyên nhân có thể: Unity chưa sinh lại file `.csproj`/`.sln`. Vào **Edit > Preferences > External Tools**, chọn
     Visual Studio 2026, bấm **Regenerate project files**, rồi **Assets > Open C# Project**. Nếu vẫn thiếu, kiểm tra
     package *Visual Studio Editor* trong Package Manager.
   - Gửi lại: ảnh Solution Explorer, ảnh trang External Tools, và danh sách file `*.csproj` trong thư mục project.

8. **Unity báo trùng tên asmdef** (`Assembly with name 'Game.X' already exists` hoặc tương tự).
   - Nguyên nhân có thể: hai module có cùng namespace, hoặc namespace của module trùng tên một asmdef/DLL có sẵn
     (bình thường tab Issues đã chặn bằng `[AssemblyNameClash]`); hoặc có bản sao thư mục module (ví dụ `Combat 1`) do
     copy tay.
   - Gửi lại: dòng lỗi đầy đủ trong Console, đường dẫn của cả hai file `.asmdef` (tìm `t:AssemblyDefinitionAsset` trong
     cửa sổ Project), và `ModuleConfig.asset`.

9. **Hộp thoại cảnh báo git hiện liên tục** ("uncommitted git changes").
   - Nguyên nhân có thể: project còn thay đổi chưa commit (kể cả `ModuleConfig.asset` hoặc file Unity tự sửa như
     `ProjectSettings`); hoặc `git` báo thay đổi do line ending / file `.meta` mới. Hộp thoại chỉ hỏi một lần cho mỗi
     lần Confirm Apply.
   - Gửi lại: kết quả `git status --porcelain` chạy ở thư mục gốc project ngay lúc đó, và cho biết nó hiện bao nhiêu
     lần cho một lần bấm.

10. **Sync báo drift ngay sau Apply**.
    - Nguyên nhân có thể: Apply chưa xong hẳn (đợi Result báo compile OK rồi Scan lại); một bước Apply bị lỗi (xem
      tab Result); hoặc asmdef bị Unity/VS sửa (ví dụ thêm reference) nên khác với cấu hình tính ra (`ReferencesDiffer`).
    - Gửi lại: ảnh tab Sync (từng dòng drift), tab Result của lần Apply đó, và nội dung file `.asmdef` được nhắc tới.

11. **Bị chặn sai bởi `Cycle` hoặc `InternalAccess`** (bạn chắc là không có vòng phụ thuộc / không dùng `internal`).
    - Nguyên nhân có thể: tool đọc phụ thuộc từ DLL đã compile và thêm phụ thuộc theo text cho hằng số/enum (có thể
      dư); `InternalAccess` tính cả thành viên `protected internal` dùng từ lớp không kế thừa, và chỉ bỏ qua khi assembly
      khai báo có `[assembly: InternalsVisibleTo("<tên assembly sau Apply>")]`.
    - Gửi lại: toàn văn dòng Issue (có `causedBy` / tên file), và đoạn code ở các file được nhắc tới (dòng dùng kiểu đó,
      khai báo kiểu đó).
