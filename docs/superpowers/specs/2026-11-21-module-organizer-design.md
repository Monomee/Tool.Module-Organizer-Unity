# Module Organizer — Thiết kế

Ngày: 2026-11-21 · Unity 6 · Package: Monome Utility Pack (tool đầu tiên trong pack)

## 1. Mục đích & lý do

Project Unity có nhiều script nằm chung trong `Assembly-CSharp`, khó nhìn, khó quản lý. Cách chuẩn là chia module bằng thư mục + namespace + `.asmdef`, nhưng làm tay thì mất công và dễ hỏng (vòng phụ thuộc, mất reference prefab, thiếu `using`).

Module Organizer là một EditorWindow trong Unity cho phép:
- định nghĩa module, **kéo thả** script có sẵn vào module;
- xem trước phụ thuộc giữa các module, phát hiện vòng, phát hiện tham chiếu ra ngoài phạm vi;
- **Apply**: tạo thư mục, di chuyển file, tạo asmdef, điền reference, sửa namespace + `using`.

Kết quả: mở Visual Studio 2026, mỗi module là một project riêng trong Solution Explorer (Unity sinh 1 `.csproj` cho mỗi asmdef), namespace khớp module. Đồng thời tách thật về compile: compile nhanh hơn, module không gọi lung tung sang nhau được.

## 2. Mục tiêu / Không làm / Chống mục tiêu

**Mục tiêu**
- Chia module thật bằng asmdef (Runtime + Editor tách riêng khi cần).
- UI kéo thả trực quan, có màu/mô tả module.
- Giới hạn phạm vi quét (scope folders), báo script tham chiếu ra ngoài phạm vi.
- Cấu hình lưu trong một asset, commit được lên git.
- Phát hiện lệch giữa config và ổ đĩa, cho đồng bộ hai chiều.

**Không làm (v1)**
- Tự động gỡ vòng phụ thuộc (chỉ báo, chỉ rõ file gây vòng).
- Đồ thị phụ thuộc dạng node kéo thả (v1 dùng danh sách cạnh + trạng thái).
- Asmdef cho Tests trong module.
- Tự viết hệ thống backup/undo riêng (dựa vào git).
- Tự sửa tên đầy đủ kiểu `OldNs.Weapon` trong code (chỉ cảnh báo).
- Hỗ trợ Unity < 6.

**Chống mục tiêu** (xảy ra là thất bại dù tool "chạy được")
- Mất reference script trong scene/prefab sau khi Apply.
- Apply xong project lỗi compile mà tool không báo trước trong bước Preview.
- Tool sửa/di chuyển file **ngoài scope** hoặc trong thư mục đã có asmdef của bên thứ ba.
- Mất dữ liệu `[SerializeReference]` do đổi namespace/assembly.

## 3. Ràng buộc

- Unity 6, Editor-only, không có code runtime.
- Phân phối dạng UPM package (`package.json` ở gốc thư mục này), cài bằng "Add package from disk" hoặc git URL.
- Phụ thuộc duy nhất ngoài Unity: `com.unity.nuget.mono-cecil` (package chính thức của Unity).
- Unity 6 dùng C# 9 → **không** có file-scoped namespace; khi thêm namespace phải dùng block `namespace X { }`.
- Asmdef tính theo thư mục → không gán từng file lẻ vào asmdef được; module bắt buộc là thư mục.
- Asmdef **không thể** reference `Assembly-CSharp`. Script trong module mà dùng type còn nằm ở `Assembly-CSharp` → lỗi compile chắc chắn.

## 4. Các phương án đã cân nhắc

| | Phương án | Đánh đổi |
|---|---|---|
| A | Thư mục + asmdef trên ổ đĩa là dữ liệu gốc, không lưu gì thêm | Không bao giờ lệch, gỡ package không ảnh hưởng. Nhưng không giữ được nháp, không có chỗ cho màu/mô tả. |
| **B (đã chọn)** | `ModuleConfig` ScriptableObject là dữ liệu gốc, Apply sinh ra asmdef | Giữ nháp lâu dài, có metadata, commit git để bàn với team. Phải làm thêm phần kiểm tra lệch + đồng bộ. |
| C | Gom nhóm ảo, không di chuyển file | Loại: asmdef tính theo thư mục, không tách compile được. |

Người dùng chọn **B**.

## 5. Thiết kế

### 5.1 Dữ liệu: `ModuleConfig.asset`

Một asset cho cả project (tool tìm bằng `AssetDatabase.FindAssets("t:ModuleConfig")`; nếu chưa có thì cửa sổ có nút tạo mới).

```
ModuleConfig
  rootNamespace: string          // vd "Game"
  scopeFolders: string[]         // vd ["Assets/_Scripts"]
  modulesRoot: string            // thư mục chứa các module, mặc định = scopeFolders[0]
  modules: Module[]

Module
  name: string                   // "Combat"
  namespaceName: string          // mặc định "{rootNamespace}.{name}"
  folder: string                 // mặc định "{modulesRoot}/{name}"
  color: Color
  description: string
  rewriteNamespace: bool         // mặc định true
  scripts: ScriptEntry[]         // script được gán
  extraReferences: string[]      // asmdef GUID / tên DLL thêm tay
  asmdefGuid / editorAsmdefGuid  // GUID asmdef do tool tạo (rỗng nếu chưa Apply)

ScriptEntry
  guid: string                   // GUID của file .cs
  originalPath: string           // đường dẫn trước lần Apply đầu tiên
  originalNamespace: string      // namespace trước lần Apply đầu tiên ("" = global)
  applied: bool                  // đã nằm trên ổ đĩa đúng như config chưa
```

Lưu script theo **GUID**, nên di chuyển file không làm mất liên kết. `originalPath/originalNamespace` để hoàn trả khi bỏ gán (xem 5.5).

### 5.2 Phạm vi quét (Restriction)

- Chỉ các file `.cs` nằm trong `scopeFolders` mới xuất hiện trong UI và được kéo thả.
- Thư mục con trong scope mà **đã có asmdef không do tool tạo** (tool đánh dấu asmdef của mình, xem 5.6) được coi là "ngoài": chỉ đọc, không bao giờ di chuyển hay sửa.
- Với mỗi file trong scope, mọi type nó tham chiếu được phân loại:

| Type được tham chiếu nằm ở | Mức | Hành động |
|---|---|---|
| Module khác trong config | — | Tự thêm reference asmdef |
| Script trong scope chưa gán module (vẫn ở Assembly-CSharp) | **Lỗi** nếu file nguồn đã gán module | Phải gán nốt file kia vào module |
| Script **ngoài scope** thuộc Assembly-CSharp(-Editor) hoặc **Assembly-CSharp-firstpass(-Editor)** (`Plugins/`, `Standard Assets/`) | **Lỗi** nếu file nguồn đã gán module; **Cảnh báo** nếu chưa gán | Báo "tham chiếu ra ngoài phạm vi" + đường dẫn file bị tham chiếu |
| Asmdef khác / package | Thông tin | Tự thêm reference |
| DLL precompiled auto-reference | Bỏ qua | Asmdef thấy sẵn (giữ `overrideReferences: false`) |
| DLL precompiled **không** auto-reference | **Lỗi** | Báo cho người dùng; v1 không bật `overrideReferences` (bật lên sẽ phải liệt kê mọi DLL) |

**Chiều ngược lại:** code **ngoài scope** (Assembly-CSharp) dùng type sắp bị đổi namespace → tool không được sửa file ngoài scope, nên sau Apply chắc chắn lỗi compile → **Lỗi, khóa Apply**, gợi ý mở rộng scope hoặc tắt `rewriteNamespace` cho module đó. (Quyết định của mình.) Bỏ namespace đổi thì không lỗi: Assembly-CSharp tự thấy mọi asmdef `autoReferenced: true`.
| UnityEngine / UnityEditor / System | Bỏ qua | — |

Panel "Issues" liệt kê theo file: file nào → tham chiếu type nào → nằm ở đâu.

### 5.3 Bộ quét phụ thuộc (Scanner)

Đọc DLL đã compile trong `Library/ScriptAssemblies` bằng Mono.Cecil (chính xác hơn đọc text `.cs`).

1. Load `Assembly-CSharp.dll`, `Assembly-CSharp-Editor.dll`, và DLL của các module do tool tạo (lần quét sau Apply).
2. Với mỗi type: gom type được tham chiếu từ base type, interface, field, property, chữ ký method, attribute, generic argument, và operand IL trong thân method.
3. **Ánh xạ type → file `.cs`**: dùng portable PDB (sequence points của method cho biết document path). Type không có method (enum, interface rỗng, struct chỉ có field) thì fallback bằng regex khai báo `class|struct|interface|enum|delegate <Name>` trong các file trong scope.
4. Phụ thuộc theo **file** = hợp phụ thuộc của các type khai báo trong file (vì đơn vị di chuyển là file).
5. `partial` type nằm ở nhiều file → các file đó **bắt buộc cùng module**; vi phạm thì báo lỗi.
6. Đánh dấu file là **Editor** **chỉ** khi nó đang thuộc `Assembly-CSharp-Editor` (tức nằm trong thư mục `Editor/`). **Không** dựa vào việc tham chiếu `UnityEditor`: trong Editor, Assembly-CSharp compile với `UNITY_EDITOR`, nên MonoBehaviour có khối `#if UNITY_EDITOR` cũng tham chiếu UnityEditor, và chuyển nó vào `Editor/` sẽ làm mất script trong bản build.
7. Load cả `Assembly-CSharp-firstpass(-Editor).dll` để phân loại tham chiếu vào đó.

Điều kiện: quét chỉ chạy khi project không có lỗi compile. Nếu đang lỗi thì cửa sổ báo và khóa Scan.

**Giới hạn đã biết:** scanner chỉ thấy code compile với define của Editor. Phụ thuộc nằm trong `#if UNITY_ANDROID`, `#if !UNITY_EDITOR`... không thấy được. Bù lại bằng bước compile player ở 5.5.

### 5.4 Kiểm tra (Validate / Preview)

Chạy trên bản nháp trong config (chưa đụng ổ đĩa), sinh ra:
- **Đồ thị module**: cạnh `A → B` kèm danh sách file gây ra cạnh đó.
- **Vòng phụ thuộc**: tìm chu trình (Tarjan SCC). Mỗi vòng hiện các cạnh và file gây ra. Còn vòng thì **khóa Apply**.
- Các lỗi/cảnh báo phạm vi ở 5.2, lỗi partial ở 5.3.
- Kế hoạch thay đổi: danh sách file sẽ di chuyển (từ → đến), asmdef sẽ tạo/sửa, file sẽ đổi namespace, file sẽ thêm `using`.
- Cảnh báo: chỗ dùng tên đầy đủ `OldNs.X` (tìm theo text), file có nhiều namespace hoặc namespace lồng nhau (bỏ qua việc rewrite file đó và báo).
- **Quy tắc namespace (quyết định của mình):** namespace của file = `namespaceName` + thư mục con tương đối trong module, giống quy ước của VS/Rider. Ví dụ `Combat/Weapons/Gun.cs` → `Game.Combat.Weapons`; file Editor → `Game.Combat.Editor`. Namespace con cũ của file bị thay, không giữ.
- **Trùng tên → Lỗi, khóa Apply:** hai type cùng tên rơi vào cùng namespace, hoặc một `using` thêm vào làm một tên bị mơ hồ trong file nhận nó.

Phụ thuộc Editor → Runtime hợp lệ. Runtime → Editor là **lỗi**.

### 5.5 Apply

Điều kiện: Validate không có lỗi. Nếu project là git repo và có thay đổi chưa commit → hộp thoại cảnh báo (cho phép tiếp tục).

Thứ tự:
1. `AssetDatabase.StartAssetEditing()`.
2. Tạo thư mục module: `{folder}/` cho runtime, `{folder}/Editor/` cho script Editor.
3. Di chuyển file bằng `AssetDatabase.MoveAsset` (giữ `.meta` + GUID → reference scene/prefab không mất). File Editor vào `{folder}/Editor/`, giữ cấu trúc thư mục con tương đối nếu có (builder quyết định có giữ hay làm phẳng; mặc định giữ).
4. Tạo/cập nhật asmdef:
   - Runtime: `{namespaceName}.asmdef`, `rootNamespace = namespaceName`.
   - Editor (chỉ khi có script Editor): `{namespaceName}.Editor.asmdef`, `includePlatforms: ["Editor"]`, reference asmdef runtime của chính module.
   - Reference ghi theo dạng `GUID:xxxx` (bền khi đổi tên).
5. Sửa code:
   - File có namespace cũ → thay bằng `namespaceName` (nếu `rewriteNamespace`).
   - File ở global namespace → bọc `namespace X { ... }` + thụt lề.
   - File (ở bất kỳ đâu trong scope) dùng type vừa đổi namespace → thêm `using NewNs;` nếu chưa có.
   - Class `[Serializable]` không phải `UnityEngine.Object` mà bị đổi namespace/assembly → thêm `[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: "...", sourceAssembly: "Assembly-CSharp")]` để giữ dữ liệu `[SerializeReference]`.
6. `StopAssetEditing()`, `AssetDatabase.Refresh()`, chờ compile.
7. Sau compile: compile thành công sẽ gây **domain reload**, xóa trạng thái cửa sổ và các callback đã đăng ký. Vì vậy trước bước 6, ghi "đang chờ kết quả Apply" vào `SessionState`. Một class `[InitializeOnLoad]` đọc cờ này sau reload, rồi quét lại và hiện kết quả. Khi compile lỗi thì không có reload: đọc lỗi qua `CompilationPipeline.assemblyCompilationFinished` và hiện trong panel.
8. Sau khi Editor compile xong: chạy `PlayerBuildInterface.CompilePlayerScripts` cho build target đang chọn, để bắt lỗi chỉ xuất hiện ở bản build (code trong `#if`). Có lỗi thì báo, không tự hoàn tác. Chỉ kiểm tra target đang chọn; thêm target khác là việc của v2.

**Apply theo phần khác biệt.** Apply so config với trạng thái hiện tại và chỉ làm phần khác nhau, nên chạy lần đầu hay chạy lại đều dùng chung một đường:
- **File mới gán:** làm như các bước trên.
- **File chuyển sang module khác:** di chuyển file, đổi namespace, cập nhật `using` ở nơi dùng.
- **Đổi tên module hoặc namespace:** đổi tên thư mục và asmdef (dùng `AssetDatabase.MoveAsset`, GUID giữ nguyên), rewrite namespace của các file trong module, cập nhật `using` ở nơi dùng.
- **Bỏ gán file / xóa module (người dùng chọn):** trả file về `originalPath` và khôi phục `originalNamespace`, coi như undo đúng nghĩa.
  - Nơi dùng type đó được thêm lại `using <originalNamespace>` khi cần.
  - `using` trỏ tới namespace không còn type nào thì phải xóa, nếu không sẽ lỗi CS0246.
  - Nếu `originalPath` đã có file khác chiếm chỗ hoặc thư mục cũ không còn: tạo lại thư mục; nếu trùng tên file thì **Lỗi, khóa Apply** và báo người dùng.
  - Xóa module: trả hết file về chỗ cũ, xóa asmdef, xóa thư mục module nếu đã trống.
  - Sau khi trả về chỗ cũ, entry bị gỡ khỏi config.

### 5.6 Kiểm tra lệch (Sync)

Asmdef do tool tạo được đánh dấu (vd file `.asmdef` có tên khớp config và nằm ở `module.folder`, hoặc lưu GUID asmdef trong `Module`; builder chọn cách).

Mỗi lần mở cửa sổ / sau compile, so sánh:
- module trong config ↔ asmdef có tồn tại đúng chỗ, đúng tên;
- `scriptGuids` ↔ file `.cs` thật trong thư mục module (file mới tạo trong thư mục module = lệch, file bị xóa = GUID không còn);
- reference trong asmdef ↔ reference tính được.

Hiện danh sách khác biệt, mỗi mục (hoặc tất cả) chọn:
- **Lấy theo ổ đĩa**: cập nhật config (thêm file mới vào module, xóa GUID chết, nhận reference tay).
- **Áp lại config**: chạy Apply cho phần khác biệt.

### 5.7 Giao diện

`Tools > Monome > Module Organizer`, UI Toolkit (UXML + USS).

```
┌ Toolbar: [ModuleConfig ▾] [Scan] [Validate] [Apply]   ⚠ 2 lệch → [Sync] ┐
├ Scope ───────────────┬ Modules ──────────────────────────────────────────┤
│ Assets/_Scripts  [+] │ ┌ Combat ●──────┐ ┌ UI ●─────────┐ [+ Module]     │
│ 🔍 filter            │ │ Weapon.cs     │ │ HUD.cs     ⚠ │                │
│ Chưa gán (12)        │ │ Damage.cs  ed │ │ Menu.cs      │                │
│  ▸ Player/           │ └───────────────┘ └──────────────┘                │
│     Player.cs        │                                                    │
├──────────────────────┴────────────────────────────────────────────────────┤
│ Issues | Dependencies | Plan                                              │
│ ✗ Vòng: Combat → UI → Combat (HUD.cs dùng Weapon, Weapon.cs dùng HUD)     │
│ ⚠ Player.cs dùng Assets/Old/Legacy.cs (ngoài phạm vi)                    │
└───────────────────────────────────────────────────────────────────────────┘
```

- Kéo thả nhiều file cùng lúc; kéo cả thư mục ở cột trái = kéo mọi file bên trong.
- Kéo từ module về "Chưa gán" để bỏ gán.
- Click một module → sửa tên, namespace, màu, mô tả, extra references (inspector của module ngay trong cửa sổ).
- File Editor có nhãn `ed`; file có vấn đề có icon ⚠/✗, hover xem lý do.
- Tab **Dependencies**: danh sách cạnh module → module, mở rộng ra thấy file gây cạnh.
- Tab **Plan**: kế hoạch Apply ở 5.4.
- Mọi thay đổi trên config dùng `Undo.RecordObject` (Ctrl+Z trong lúc xếp nháp).

## 6. Cấu trúc package

```
MonomeUltilityPack/
  package.json                 // name: com.monome.utilitypack, unity: 6000.0, dep mono-cecil
  Editor/
    Monome.UtilityPack.Editor.asmdef
    ModuleOrganizer/
      ModuleConfig.cs
      DependencyScanner.cs
      ModuleValidator.cs
      ModuleApplier.cs
      ModuleSync.cs
      ModuleOrganizerWindow.cs (+ .uxml, .uss)
  Tests/Editor/                // EditMode tests cho logic thuần
  docs/
```

Tên package `com.monome.utilitypack` là mình tự đặt, đổi được.

## 7. Kiểm thử

- EditMode tests (Unity Test Framework) cho logic thuần: tìm vòng, phân loại tham chiếu theo scope, rewrite namespace/using trên chuỗi mẫu (block namespace, global namespace, nhiều namespace).
- Một project Unity 6 mẫu có vòng, script ngoài scope, script Editor, `[SerializeReference]`, prefab dùng script. Apply xong, kiểm tra tay: không lỗi compile, prefab không mất script, VS hiện đúng project theo module.

## 8. Rủi ro cần kiểm chứng sớm

- **UnityEvent gán trong Inspector** lưu `m_TargetAssemblyTypeName` ("Ns.Type, Assembly-CSharp") trong scene/prefab. Đổi namespace hoặc assembly có thể làm listener bị báo missing, và `MovedFrom` có thể không cứu được trường hợp này. **Bước đầu tiên của plan:** dựng spike trong Unity 6 để kiểm tra. Nếu đúng là hỏng, thêm bước Apply sửa chuỗi này trong các file `.unity/.prefab/.asset` có chứa nó (YAML text), và đưa danh sách file sẽ sửa vào Plan.
- `Type.GetType("..., Assembly-CSharp")` trong code: tìm theo text và báo cảnh báo.

## 9. Để builder quyết định

- Cách đánh dấu asmdef do tool tạo (5.6).
- Giữ hay làm phẳng cấu trúc thư mục con khi di chuyển (mặc định giữ).
- Chi tiết layout USS, màu mặc định module.
- Tổ chức class nội bộ, miễn đúng các phần trên.
