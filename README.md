# Monome Utility Pack

> **English summary.** Editor-only Unity 6 package (tested on 6000.3). Its main tool, **Module Organizer**
> (`Tools > Monome > Module Organizer`), lets you drag existing scripts into modules; **Apply** then creates the module
> folders and `.asmdef` files, moves the scripts (GUIDs kept), rewrites their namespaces and adds `[MovedFrom]` so
> serialized data survives. A **Sync** tab detects changes you made by hand on disk and can take them into the config.
> Commit before Apply: git is the undo. Only the active build target is compile-checked.

Gói chỉ dùng trong Editor cho Unity 6 (6000.0 trở lên; đã kiểm thử trên 6000.3). Công cụ chính là **Module Organizer**: kéo các script có sẵn
vào từng module, bấm **Apply**, tool sẽ tạo thư mục + `.asmdef`, chuyển file (giữ GUID), sửa namespace và thêm
`[MovedFrom]` để dữ liệu đã serialize không bị mất. Tab **Sync** phát hiện những thay đổi bạn làm tay trên ổ đĩa.

## Cài đặt

**Cách 1: từ ổ đĩa**

1. Unity: **Window > Package Manager**.
2. Nút **+** > **Add package from disk...**
3. Chọn file `package.json` ở thư mục gốc của gói này.

**Cách 2: từ git URL**

**Window > Package Manager** > **+** > **Add package from git URL...**, nhập git URL của repo bạn, ví dụ:

```
https://<host>/<you>/MonomeUltilityPack.git
```

Nếu gói nằm trong thư mục con của repo, thêm `?path=<thư mục con>` vào cuối URL. Có thể thêm `#<tag hoặc branch>` để
cố định phiên bản.

Gói tự kéo theo `com.unity.nuget.mono-cecil` (dùng để đọc các assembly đã compile).

## Cách dùng (5 bước)

1. **Commit trước.** Apply chuyển và sửa file script; git là cách hoàn tác duy nhất. Nếu còn thay đổi chưa commit, tool
   sẽ hỏi lại trước khi Apply.
2. Mở **Tools > Monome > Module Organizer**, bấm **Create ModuleConfig** (tạo `Assets/ModuleConfig.asset`), rồi bấm
   **+** cạnh *Scope* để chọn thư mục chứa script cần sắp xếp (phải nằm trong `Assets/`).
3. Bấm **+ Module**, đặt tên và namespace (ví dụ `Combat` / `Game.Combat`). Kéo file hoặc cả thư mục từ cây bên trái
   thả vào thẻ module. Script Editor (nằm trong thư mục `Editor/`) tự vào asmdef `<namespace>.Editor`.
4. Xem tab **Issues** (lỗi ✗ phải sửa trước, cảnh báo ⚠ thì nên đọc) và tab **Plan** (danh sách file sẽ chuyển,
   asmdef sẽ tạo, namespace sẽ sửa). Bấm **Apply**, rồi **Confirm Apply**.
5. Đợi Unity compile và reload. Tab **Result** báo kết quả compile Editor và compile player cho build target đang chọn.
   Về sau, nếu bạn tự đổi tên/chuyển/thêm file trong thư mục module, tab **Sync** sẽ liệt kê; bấm **Take from disk** để
   cập nhật config theo ổ đĩa, hoặc Apply lại để đưa ổ đĩa về theo config.

Có thể kéo file `.cs` hoặc cả thư mục từ cửa sổ **Project** của Unity thả vào thẻ module (hoặc thả vào cây bên trái để
bỏ gán); chỉ các file trong scope gán được mới được nhận.

Bỏ gán một file đã Apply (kéo nó từ thẻ module về cây) rồi Apply: file về lại đúng thư mục cũ với namespace cũ, và trở
lại `Assembly-CSharp(-Editor)`, miễn là thư mục cũ không nằm trong thư mục của một module (nếu có, Apply bị chặn với lỗi
`RestoreConflict`). Đổi tên module rồi Apply: thư mục và asmdef được đổi theo.

## An toàn

- **Commit trước khi Apply.** Apply không hoàn tác được bằng Ctrl+Z; muốn quay lại thì dùng git.
- Tool chỉ đụng tới các thư mục scope và thư mục gốc của module (`modulesRoot`, mặc định là thư mục scope đầu tiên),
  cùng các asset text (`.prefab`, `.unity`, `.asset`...) cần serialize lại sau khi chuyển kiểu `[SerializeReference]`.
- Ctrl+Z chỉ dùng cho lúc soạn (gán, bỏ gán, đổi tên, xóa module). Sau Apply lịch sử undo của config bị xóa.
- File `.meta` (GUID) luôn được giữ, nên prefab, scene và asset vẫn trỏ đúng script.
- Asmdef của module compile **mọi** file `.cs` trong thư mục module. Một file nằm (hoặc sẽ nằm sau Apply) trong thư mục
  module mà chưa gán vào module đó sẽ chặn Apply với lỗi `CapturedByModule`: gán nó vào module, hoặc chuyển nó ra ngoài.
- Nếu Apply đã ghi `[MovedFrom]` mà chưa serialize lại được asset (compile lỗi, hoặc Editor bị đóng trước khi reload),
  tool nhớ việc còn nợ trong config và làm nốt sau lần compile thành công kế tiếp; trong lúc đó nút Apply bị khóa
  ("pending asset migration").
- Mọi thứ kiểm tra được đều kiểm trước khi đổi file đầu tiên; nếu một bước giữa chừng lỗi, Apply dừng lại, và lần Apply
  sau sẽ tính kế hoạch từ trạng thái thật trên ổ đĩa.

## Giới hạn

- **Chỉ compile thử build target đang chọn.** Code trong `#if UNITY_ANDROID`, `#if UNITY_IOS`... của target khác
  không được kiểm tra; hãy chuyển target rồi compile lại nếu cần.
- **Vòng phụ thuộc chỉ được báo, không tự gỡ.** Nếu module A dùng B và B dùng A, tab Issues báo `Cycle` và chỉ ra file
  gây ra; bạn tự chuyển file hoặc tách code.
- **Tên kiểu viết đầy đủ** (`OldNamespace.Type` trong code) không được sửa tự động: cảnh báo `QualifiedName`.
- **Chuỗi tên kiểu kèm assembly** như `"Ns.Type, Assembly-CSharp"` (dùng với `Type.GetType`) không được sửa: cảnh báo
  `AssemblyQualifiedString`.
- **Asset lưu dạng binary** không được serialize lại. Nên đặt *Project Settings > Editor > Asset Serialization* là
  **Force Text**; nếu không, Result sẽ có cảnh báo.
- File có nhiều namespace hoặc namespace lồng nhau không được tự sửa namespace (cảnh báo `MultiNamespace`).
- `m_TargetAssemblyTypeName` của UnityEvent trong scene/prefab vẫn giữ tên cũ; listener vẫn chạy vì Unity tìm target
  qua fileID, chuỗi này chỉ dùng để hiển thị khi target đã mất.
- **Asset binary hoặc Mixed: lần chuyển thứ hai làm mất dữ liệu.** Asset binary không được serialize lại, nên vẫn ghi tên
  cũ của kiểu `[SerializeReference]`; nếu kiểu đó bị chuyển thêm lần nữa, `[MovedFrom]` chỉ trỏ về chỗ ngay trước đó và
  dữ liệu trong các asset binary đó bị mất. Hãy dùng Force Text, hoặc tự re-save các asset đó giữa hai lần chuyển.
- **Kiểu partial chỉ nằm trong các nhánh `#if` khác nhau** (ví dụ một phần trong `#if A`, một phần trong `#if B`, không có
  phần nào ngoài `#if`): `[MovedFrom]` có thể nằm ở nhánh không được compile, và dữ liệu `[SerializeReference]` của kiểu
  đó có thể mất. Hãy để ít nhất một phần của kiểu nằm ngoài `#if`.
- **`unsafe` tính theo từng module.** Nếu một file của module có từ khóa `unsafe`, asmdef của module đó (runtime hoặc
  Editor, theo phía của file) được bật `allowUnsafeCode`.
- **Kiểu hoặc thành viên `internal`** dùng qua ranh giới module mới sẽ chặn Apply (`InternalAccess`): đổi sang `public`
  hoặc để hai file chung một module. `InternalsVisibleTo` không được hỗ trợ.
- **Apply lỗi giữa chừng khi đang chuyển file** có thể để lại một file đã chuyển mà chưa có `[MovedFrom]`. Vì vậy hãy
  commit trước khi Apply, và khi có lỗi thì quay lại bằng git (`git checkout` / `git clean` trong thư mục bị ảnh hưởng).

## Kiểm tra

- EditMode tests: chạy trong project `TestHost~` (Test Runner, hoặc lệnh trong `docs/manual-checks.md`).
- Kiểm tra end-to-end không cần giao diện và các bước kiểm tra tay: `docs/manual-checks.md`.
