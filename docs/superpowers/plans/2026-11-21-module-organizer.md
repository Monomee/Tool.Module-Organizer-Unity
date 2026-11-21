# Module Organizer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** EditorWindow trong Unity 6 để kéo thả script vào module, kiểm tra phụ thuộc, rồi Apply: tạo thư mục, asmdef, sửa namespace và `using`.

**Architecture:** Logic thuần (classify, tìm vòng, rewrite text, lập kế hoạch Apply, kiểm tra lệch) tách khỏi Unity API để test bằng EditMode test không cần đụng ổ đĩa. Ba lớp mỏng mới chạm Unity: `DependencyScanner` (Mono.Cecil đọc `Library/ScriptAssemblies`), `ModuleApplier` (AssetDatabase), `ApplyResultWatcher` (SessionState + domain reload). UI Toolkit window chỉ gọi các lớp trên.

**Tech Stack:** Unity 6000.3.18f1, C# 9, UI Toolkit, `com.unity.nuget.mono-cecil`, Unity Test Framework (NUnit).

**Spec:** `docs/superpowers/specs/2026-11-21-module-organizer-design.md`

## Global Constraints

- Unity 6 (`"unity": "6000.0"` trong package.json), chỉ Editor, không có code runtime.
- Phụ thuộc ngoài Unity duy nhất: `com.unity.nuget.mono-cecil`.
- C# 9: **không** dùng file-scoped namespace, không dùng `record` (thiếu `IsExternalInit`).
- Package name `com.monome.utilitypack`; asmdef `Monome.UtilityPack.Editor`; menu `Tools/Monome/Module Organizer`.
- Di chuyển file **chỉ** bằng `AssetDatabase.MoveAsset` (giữ GUID).
- Không bao giờ sửa/di chuyển file ngoài `scopeFolders` hoặc trong thư mục có asmdef không do tool tạo.
- Reference trong asmdef ghi dạng `GUID:<guid>`; `overrideReferences: false`; `autoReferenced: true`.
- Namespace mặc định của module: `{rootNamespace}.{name}`; folder mặc định `{modulesRoot}/{name}`; file Editor nằm ở `{folder}/Editor/`, asmdef `{namespaceName}.Editor`, `includePlatforms: ["Editor"]`.
- Namespace của file = `namespaceName` + thư mục con tương đối trong module (`.`-nối), file Editor thêm `.Editor`.
- Phân loại Editor **chỉ** theo assembly hiện tại (`Assembly-CSharp-Editor` / `Assembly-CSharp-firstpass-Editor`), không theo việc tham chiếu `UnityEditor`.

## Review Focus

1. Đường dẫn scope so khớp theo ranh giới thư mục: `Assets/_Scripts2/X.cs` **không** thuộc scope `Assets/_Scripts`. (Test ở Task 3.)
2. File `.cs` có `namespace` trong comment/string hoặc `using` nằm trong `#if`: rewrite không được làm hỏng. (Test ở Task 5.)
3. File chứa nhiều type, một type `partial` trải nhiều file: phụ thuộc theo file đúng, file partial bị tách module thì báo lỗi. (Test ở Task 6.)
4. Bỏ gán file khi `originalPath` đã bị chiếm: khóa Apply, không ghi đè. (Test ở Task 7.)
5. Apply thành công kéo theo domain reload: kết quả và lần quét lại vẫn hiện ra sau reload. (Kiểm tra tay ở Task 9.)

## File Structure

```
package.json
Editor/Monome.UtilityPack.Editor.asmdef
Editor/ModuleOrganizer/
  Data/ModuleConfig.cs          ModuleConfig, Module, ScriptEntry + quy tắc scope/đường dẫn
  Data/ScanModel.cs             ScriptFile, TypeRecord, ScanResult, AssemblyKind
  Analysis/DependencyScanner.cs Cecil → ScanResult
  Analysis/DeclarationFinder.cs regex fallback type → file
  Analysis/CycleFinder.cs       Tarjan SCC
  Analysis/ReferenceClassifier.cs bảng 5.2
  Validation/Issue.cs           Issue, IssueCode, Severity
  Validation/ModuleValidator.cs ValidationReport (edges, cycles, issues)
  Apply/SourceRewriter.cs       thao tác text trên .cs
  Apply/ApplyPlan.cs            các op + ApplyPlanner (diff config ↔ đĩa)
  Apply/AsmdefJson.cs           sinh JSON asmdef
  Apply/ModuleApplier.cs        thực thi ApplyPlan bằng AssetDatabase
  Apply/ApplyResultWatcher.cs   [InitializeOnLoad], SessionState, compile player
  Sync/ModuleSync.cs            DiskSnapshot, DriftItem, Diff/AcceptDisk
  UI/ModuleOrganizerWindow.cs, .uxml, .uss
Tests/Editor/Monome.UtilityPack.Editor.Tests.asmdef
Tests/Editor/*Tests.cs
TestHost~/                      project Unity 6 dùng để chạy test (dấu ~ để Unity bỏ qua trong package)
```

**Chạy test (dùng ở mọi task):**
```bash
UNITY="/c/Program Files/Unity/Hub/Editor/6000.3.18f1/Editor/Unity.exe"
"$UNITY" -batchmode -nographics -projectPath TestHost~ -runTests -testPlatform EditMode \
  -testResults TestHost~/results.xml -logFile TestHost~/test.log [-testFilter <Name>]
```
Pass = exit code 0 và `results.xml` có `result="Passed"` ở `test-run`. Phải đóng Unity Editor đang mở TestHost~ trước khi chạy.

---

### Task 1: Scaffold package + test host

**Files:**
- Create: `package.json`, `Editor/Monome.UtilityPack.Editor.asmdef`, `Tests/Editor/Monome.UtilityPack.Editor.Tests.asmdef`, `Tests/Editor/SmokeTests.cs`, `.gitignore`
- Create: `TestHost~/Packages/manifest.json`, `TestHost~/ProjectSettings/ProjectVersion.txt`, `TestHost~/Assets/.keep`

- [ ] **Step 1:** `git init` ở gốc repo. `.gitignore` bỏ qua `TestHost~/Library/`, `TestHost~/Temp/`, `TestHost~/Logs/`, `TestHost~/UserSettings/`, `TestHost~/results.xml`, `TestHost~/test.log`, `*.csproj`, `*.sln`.
- [ ] **Step 2:** `package.json`: name `com.monome.utilitypack`, displayName `Monome Utility Pack`, version `0.1.0`, unity `6000.0`, dependencies `{"com.unity.nuget.mono-cecil": "1.11.4"}`.
- [ ] **Step 3:** Editor asmdef: name `Monome.UtilityPack.Editor`, includePlatforms `["Editor"]`, overrideReferences `true`, precompiledReferences `["Mono.Cecil.dll","Mono.Cecil.Pdb.dll","Mono.Cecil.Rocks.dll"]` (Cecil package không auto-reference). Tests asmdef: name `Monome.UtilityPack.Editor.Tests`, references `Monome.UtilityPack.Editor`, `UnityEngine.TestRunner`, `UnityEditor.TestRunner`, includePlatforms `["Editor"]`, overrideReferences `true`, precompiledReferences gồm `nunit.framework.dll` + 3 DLL Cecil, defineConstraints `["UNITY_INCLUDE_TESTS"]`.
- [ ] **Step 4:** `TestHost~/Packages/manifest.json`: dependencies `com.monome.utilitypack: "file:../.."`, `com.unity.test-framework`, `com.unity.ugui`; `"testables": ["com.monome.utilitypack"]`. `ProjectVersion.txt`: `m_EditorVersion: 6000.3.18f1`.
- [ ] **Step 5:** `SmokeTests.CecilIsAvailable`: `Assert.IsNotNull(typeof(Mono.Cecil.AssemblyDefinition))`.
- [ ] **Step 6:** Chạy test. Lần đầu Unity import project (vài phút). Expected: PASS 1 test.
- [ ] **Step 7:** Commit `chore: scaffold package and test host`.

### Task 2: Spike — UnityEvent và SerializeReference khi đổi namespace/assembly

Throwaway, không giữ code. Kết quả quyết định Task 12 có làm hay không.

**Files:** tạm trong `TestHost~/Assets/_Spike/` (xóa sau khi xong); Modify: spec §8 (ghi kết quả).

- [ ] **Step 1:** Tạo `Assets/_Spike/Target.cs` (MonoBehaviour có `public void Ping()` và field `[SerializeReference] public object data` gán một `[Serializable] class Payload`), một prefab có `Button`/`UnityEvent` gọi `Target.Ping`, và `Payload` được gán vào `data`. Lưu prefab.
- [ ] **Step 2:** Mở Unity bằng tay, chuyển `Target.cs` + `Payload` vào thư mục có asmdef `Spike.Mod`, bọc `namespace Spike.Mod { }`. Thêm `[MovedFrom(false, sourceNamespace: "", sourceAssembly: "Assembly-CSharp")]` lên `Payload`.
- [ ] **Step 3:** Mở lại prefab. Ghi: listener UnityEvent còn không (còn = Inspector hiện `Target.Ping`, không có "Missing"), dữ liệu `data` còn không. Xem `m_TargetAssemblyTypeName` trong YAML prefab có tự cập nhật sau khi lưu lại không.
- [ ] **Step 4:** Ghi kết quả vào spec §8 (một đoạn). Nếu UnityEvent hỏng → Task 12 bắt buộc; nếu không → Task 12 bỏ. Xóa `_Spike`. Commit `docs: record UnityEvent/SerializeReference spike result`.

### Task 3: Data model + quy tắc scope và đường dẫn

**Files:**
- Create: `Editor/ModuleOrganizer/Data/ModuleConfig.cs`
- Test: `Tests/Editor/ModuleConfigTests.cs`

**Interfaces:**
- Produces:
  - `[Serializable] class ScriptEntry { string guid; string originalPath; string originalNamespace; bool applied; }`
  - `[Serializable] class Module { string name; string namespaceName; string folder; Color color; string description; bool rewriteNamespace = true; List<ScriptEntry> scripts; List<string> extraReferences; string asmdefGuid; string editorAsmdefGuid; }`
  - `class ModuleConfig : ScriptableObject { string rootNamespace = "Game"; List<string> scopeFolders; string modulesRoot; List<Module> modules; }`
  - `string ModuleConfig.EffectiveModulesRoot` (= `modulesRoot` hoặc `scopeFolders[0]`)
  - `Module ModuleConfig.AddModule(string name)` (điền namespace/folder mặc định)
  - `Module ModuleConfig.FindModuleOf(string scriptGuid)` (null nếu chưa gán)
  - `static bool PathRules.IsUnder(string path, string folder)`
  - `bool ModuleConfig.IsInScope(string assetPath)`
  - `static string PathRules.ScopeRelativeDir(string originalPath, IReadOnlyList<string> scopeFolders)` — thư mục của file so với scope folder chứa nó, bỏ mọi đoạn `Editor`, `""` nếu nằm ngay gốc
  - `static string PathRules.TargetPath(Module m, ScriptEntry e, bool isEditor, IReadOnlyList<string> scopeFolders)`
  - `static string PathRules.TargetNamespace(Module m, string relativeDir, bool isEditor)`

- [ ] **Step 1: Test thất bại**

```csharp
[Test] public void IsUnder_RespectsFolderBoundary() {
  Assert.IsTrue(PathRules.IsUnder("Assets/_Scripts/A.cs", "Assets/_Scripts"));
  Assert.IsTrue(PathRules.IsUnder("Assets/_Scripts/Sub/A.cs", "Assets/_Scripts/"));
  Assert.IsFalse(PathRules.IsUnder("Assets/_Scripts2/A.cs", "Assets/_Scripts"));
}
[Test] public void AddModule_FillsDefaults() {
  var c = ScriptableObject.CreateInstance<ModuleConfig>();
  c.scopeFolders = new List<string>{"Assets/_Scripts"};
  var m = c.AddModule("Combat");
  Assert.AreEqual("Game.Combat", m.namespaceName);
  Assert.AreEqual("Assets/_Scripts/Combat", m.folder);
}
[Test] public void TargetPathAndNamespace_KeepSubfolders_StripEditor() {
  var m = new Module{ name="Combat", namespaceName="Game.Combat", folder="Assets/_Scripts/Combat" };
  var scope = new[]{"Assets/_Scripts"};
  var e = new ScriptEntry{ originalPath="Assets/_Scripts/Weapons/Editor/GunEditor.cs" };
  Assert.AreEqual("Weapons", PathRules.ScopeRelativeDir(e.originalPath, scope));
  Assert.AreEqual("Assets/_Scripts/Combat/Editor/Weapons/GunEditor.cs", PathRules.TargetPath(m, e, true, scope));
  Assert.AreEqual("Game.Combat.Editor.Weapons", PathRules.TargetNamespace(m, "Weapons", true));
  Assert.AreEqual("Game.Combat", PathRules.TargetNamespace(m, "", false));
}
```

- [ ] **Step 2:** Chạy `-testFilter ModuleConfigTests`. Expected: FAIL (compile error, type chưa có).
- [ ] **Step 3:** Implement. Chuẩn hóa `\` → `/` và bỏ `/` cuối trước khi so.
- [ ] **Step 4:** Chạy lại. Expected: PASS.
- [ ] **Step 5:** Commit `feat: module config data model and path rules`.

### Task 4: Scan model + DependencyScanner (Cecil)

**Files:**
- Create: `Editor/ModuleOrganizer/Data/ScanModel.cs`, `Editor/ModuleOrganizer/Analysis/DeclarationFinder.cs`, `Editor/ModuleOrganizer/Analysis/DependencyScanner.cs`
- Create fixtures: `TestHost~/Assets/_Fixtures/Scope/` (`FxA.cs` MonoBehaviour dùng `FxB`; `FxB.cs` plain class + `enum FxColor`; `FxPartial.Part1.cs` / `FxPartial.Part2.cs` partial class; `Editor/FxAEditor.cs` `CustomEditor(typeof(FxA))`; `FxGizmo.cs` MonoBehaviour có `#if UNITY_EDITOR UnityEditor.Handles...`), `TestHost~/Assets/_Fixtures/Outside/FxOut.cs` (được `FxA` dùng)
- Test: `Tests/Editor/DeclarationFinderTests.cs`, `Tests/Editor/DependencyScannerTests.cs`

**Interfaces:**
- Produces:
  - `enum AssemblyKind { Runtime, Editor, FirstPass, FirstPassEditor, AsmDef, Precompiled, Engine }`
  - `class TypeRecord { string fullName; string ns; string assemblyName; AssemblyKind kind; List<string> filePaths; string asmdefGuid; bool autoReferencedDll; bool serializableNonUnityObject; }`
  - `class ScriptFile { string guid; string path; AssemblyKind kind; bool IsEditor; List<string> declaredTypes; HashSet<string> referencedTypes; }` (`IsEditor` = kind là `Editor` hoặc `FirstPassEditor`; `referencedTypes` là full name, đã bỏ type do chính file khai báo)
  - `class ScanResult { Dictionary<string, ScriptFile> files /*key guid*/; Dictionary<string, TypeRecord> types /*key fullName*/; ScriptFile FileByPath(string path); }`
  - `static Dictionary<string,string> DeclarationFinder.Find(string source)` → `fullName → kind` cho `class|struct|interface|enum|delegate` (có namespace, kể cả nested type dạng `Outer/Inner` như Cecil)
  - `static ScanResult DependencyScanner.Scan(string scriptAssembliesDir = "Library/ScriptAssemblies")`

- [ ] **Step 1: Test thất bại — DeclarationFinder**

```csharp
[Test] public void Find_HandlesNamespacesEnumsAndComments() {
  var src = "// class Fake {}\nnamespace A.B {\n public enum Color { R }\n class Gun { class Inner {} }\n}\nstruct Top {}";
  var r = DeclarationFinder.Find(src);
  CollectionAssert.AreEquivalent(new[]{"A.B.Color","A.B.Gun","A.B.Gun/Inner","Top"}, r.Keys);
}
```

- [ ] **Step 2: Test thất bại — Scanner trên fixtures**

```csharp
static ScanResult S => DependencyScanner.Scan();
static ScriptFile F(string name) => S.FileByPath("Assets/_Fixtures/" + name);
[Test] public void FxA_ReferencesFxB_AndOutside() {
  CollectionAssert.IsSupersetOf(F("Scope/FxA.cs").referencedTypes, new[]{"FxB","FxOut"});
}
[Test] public void EnumWithoutMethods_MapsToFile() {
  CollectionAssert.Contains(S.types["FxColor"].filePaths, "Assets/_Fixtures/Scope/FxB.cs");
}
[Test] public void Partial_HasTwoFiles() { Assert.AreEqual(2, S.types["FxPartial"].filePaths.Count); }
[Test] public void EditorKind_OnlyByAssembly() {
  Assert.IsTrue(F("Scope/Editor/FxAEditor.cs").IsEditor);
  Assert.IsFalse(F("Scope/FxGizmo.cs").IsEditor);
}
```

- [ ] **Step 3:** Chạy `-testFilter DeclarationFinderTests;DependencyScannerTests`. Expected: FAIL.
- [ ] **Step 4:** Implement `DeclarationFinder.Find`. Strip comment và string literal trước khi regex; theo dõi `{}` để biết namespace/type cha.
- [ ] **Step 5:** Implement `DependencyScanner.Scan`:
  - Danh sách assembly + kind: `CompilationPipeline.GetAssemblies(AssembliesType.Editor)` cho tên/đường dẫn file nguồn; tên `Assembly-CSharp` → Runtime, `-Editor` → Editor, `-firstpass` → FirstPass, `-firstpass-Editor` → FirstPassEditor; asmdef khác → AsmDef (`CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName` → GUID); DLL trong `Assets/` → Precompiled (`PluginImporter`, đọc auto-reference qua `PluginImporter.GetIsOverridable`/`isPreloaded` — builder xác nhận API trong 6000.3); còn lại → Engine.
  - Đọc DLL bằng `AssemblyDefinition.ReadAssembly(path, new ReaderParameters{ ReadSymbols = true, AssemblyResolver = resolver })` với resolver thêm `scriptAssembliesDir` và thư mục DLL của Unity.
  - Tham chiếu của type: base type, interface, field, property, event, tham số/return method, custom attribute (kể cả `typeof` trong argument), generic argument, operand IL (`TypeReference`, `MemberReference.DeclaringType`). Bỏ type compiler sinh ra (`<` trong tên), quy nested về type ngoài cùng khi gán vào file.
  - Map type → file: document của sequence point trong PDB; type không có method → `DeclarationFinder` trên các `.cs` của assembly đó.
  - Map file → GUID: `AssetDatabase.AssetPathToGUID`.
  - `serializableNonUnityObject` = có `SerializableAttribute` và không kế thừa `UnityEngine.Object`.
- [ ] **Step 6:** Chạy lại. Expected: PASS.
- [ ] **Step 7:** Commit `feat: Cecil dependency scanner`.

### Task 5: SourceRewriter

**Files:**
- Create: `Editor/ModuleOrganizer/Apply/SourceRewriter.cs`
- Test: `Tests/Editor/SourceRewriterTests.cs`

**Interfaces:**
- Produces:
  - `class NamespaceInfo { List<string> namespaces; bool nested; bool IsGlobal => namespaces.Count == 0; bool Rewritable => !nested && namespaces.Count <= 1; }`
  - `static NamespaceInfo SourceRewriter.Parse(string src)`
  - `static string SourceRewriter.SetNamespace(string src, string newNs)` — yêu cầu `Rewritable`; `newNs == ""` gỡ block (dùng cho khôi phục global namespace)
  - `static string SourceRewriter.AddUsing(string src, string ns)` / `RemoveUsing(string src, string ns)` — idempotent, chỉ đụng các `using` ở đầu file
  - `static string SourceRewriter.AddMovedFrom(string src, string typeSimpleName, string oldNs, string oldAssembly)`
  - `static List<int> SourceRewriter.FindQualifiedUses(string src, string ns, IEnumerable<string> typeSimpleNames)` → số dòng (1-based)
  - `static List<int> SourceRewriter.FindAssemblyQualifiedStrings(string src)` → dòng chứa `", Assembly-CSharp"` trong string literal

Line ending: giữ nguyên `\r\n` hoặc `\n` theo file gốc; thụt lề khi bọc = 4 dấu cách.

- [ ] **Step 1: Test thất bại**

```csharp
[Test] public void SetNamespace_ReplacesBlock() =>
  StringAssert.Contains("namespace Game.Combat\n{", SourceRewriter.SetNamespace("namespace Old\n{\n    class A {}\n}\n", "Game.Combat"));
[Test] public void SetNamespace_WrapsGlobal_KeepsUsingsOutside() {
  var r = SourceRewriter.SetNamespace("using UnityEngine;\n\npublic class A {}\n", "Game.Combat");
  Assert.AreEqual("using UnityEngine;\n\nnamespace Game.Combat\n{\n    public class A {}\n}\n", r);
}
[Test] public void SetNamespace_Empty_Unwraps() =>
  Assert.AreEqual("using X;\n\npublic class A {}\n", SourceRewriter.SetNamespace("using X;\n\nnamespace N\n{\n    public class A {}\n}\n", ""));
[Test] public void Parse_IgnoresCommentsAndStrings() {
  var i = SourceRewriter.Parse("// namespace Fake\nclass A { string s = \"namespace X\"; }");
  Assert.IsTrue(i.IsGlobal);
}
[Test] public void Parse_DetectsNestedAndMultiple() {
  Assert.IsFalse(SourceRewriter.Parse("namespace A { namespace B { } }").Rewritable);
  Assert.IsFalse(SourceRewriter.Parse("namespace A { } namespace B { }").Rewritable);
}
[Test] public void AddUsing_AfterLastTopUsing_Idempotent_IgnoresIfBlocks() {
  var src = "using A;\n#if UNITY_EDITOR\nusing UnityEditor;\n#endif\nclass X {}\n";
  var r = SourceRewriter.AddUsing(SourceRewriter.AddUsing(src, "Game.Combat"), "Game.Combat");
  Assert.AreEqual("using A;\n#if UNITY_EDITOR\nusing UnityEditor;\n#endif\nusing Game.Combat;\nclass X {}\n", r);
}
[Test] public void RemoveUsing_RemovesOnlyExact() =>
  Assert.AreEqual("using Game.CombatX;\nclass X {}\n", SourceRewriter.RemoveUsing("using Game.Combat;\nusing Game.CombatX;\nclass X {}\n", "Game.Combat"));
[Test] public void PreservesCrlf() =>
  StringAssert.Contains("\r\n    class A", SourceRewriter.SetNamespace("class A {}\r\n", "N"));
[Test] public void AddMovedFrom_OnTypeDeclaration() =>
  StringAssert.Contains("[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"\", sourceAssembly: \"Assembly-CSharp\")]\n    [System.Serializable] public class P",
    SourceRewriter.AddMovedFrom("namespace N\n{\n    [System.Serializable] public class P {}\n}\n", "P", "", "Assembly-CSharp"));
[Test] public void FindQualifiedUses_ReportsLine() =>
  CollectionAssert.AreEqual(new[]{2}, SourceRewriter.FindQualifiedUses("class X {\n  Old.Gun g;\n}", "Old", new[]{"Gun"}));
```

- [ ] **Step 2:** Chạy `-testFilter SourceRewriterTests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Dùng một hàm `Mask(src)` thay comment/string bằng khoảng trắng cùng độ dài để mọi tìm kiếm chạy trên bản mask, rồi sửa trên bản gốc theo cùng offset.
- [ ] **Step 4:** Chạy lại. Expected: PASS.
- [ ] **Step 5:** Commit `feat: source rewriter for namespaces and usings`.

### Task 6: Classifier, CycleFinder, ModuleValidator

**Files:**
- Create: `Editor/ModuleOrganizer/Analysis/ReferenceClassifier.cs`, `Editor/ModuleOrganizer/Analysis/CycleFinder.cs`, `Editor/ModuleOrganizer/Validation/Issue.cs`, `Editor/ModuleOrganizer/Validation/ModuleValidator.cs`
- Test: `Tests/Editor/CycleFinderTests.cs`, `Tests/Editor/ModuleValidatorTests.cs` (dựng `ScanResult` bằng tay, không gọi Cecil)

**Interfaces:**
- Consumes: Task 3 (`ModuleConfig`, `PathRules`), Task 4 (`ScanResult`, `TypeRecord`, `ScriptFile`), Task 5 (`FindQualifiedUses`, `FindAssemblyQualifiedStrings`, `Parse`)
- Produces:
  - `enum RefClass { SameModule, OtherModule, UnassignedInScope, OutOfScope, OtherAsmdef, AutoDll, NonAutoDll, Ignored }`
  - `static RefClass ReferenceClassifier.Classify(ScriptFile src, TypeRecord target, ModuleConfig cfg, ScanResult scan)`
  - `static List<List<string>> CycleFinder.Find(IReadOnlyDictionary<string, HashSet<string>> edges)` — mỗi SCC có > 1 node, node sắp xếp ổn định
  - `enum Severity { Info, Warning, Error }`
  - `enum IssueCode { Cycle, OutOfScopeRef, UnassignedInScopeRef, NonAutoDll, PartialSplit, RuntimeToEditor, NameClash, ReverseRefOutOfScope, RestoreConflict, QualifiedName, MultiNamespace, AssemblyQualifiedString }`
  - `class Issue { Severity severity; IssueCode code; string path; string message; }`
  - `class ModuleEdge { string from; string to; List<string> causedBy /*paths*/; }` — node = tên module, node Editor = `"{name}.Editor"`
  - `class ValidationReport { List<Issue> issues; List<ModuleEdge> edges; List<List<string>> cycles; Dictionary<string, HashSet<string>> asmdefRefs /*node → asmdef GUID ngoài*/; bool CanApply => !issues.Any(i => i.severity == Severity.Error); }`
  - `static ValidationReport ModuleValidator.Validate(ModuleConfig cfg, ScanResult scan, Func<string,string> readSource)`

Mức độ (bảng 5.2 spec): `UnassignedInScope`/`OutOfScope` → Error nếu file nguồn đã gán module, Warning nếu chưa; `NonAutoDll` → Error nếu đã gán; `OtherAsmdef` → ghi vào `asmdefRefs`; `AutoDll`/`Ignored`/`SameModule` → không issue. `OutOfScope` gồm cả FirstPass/FirstPassEditor. `ReverseRefOutOfScope`: file ngoài scope (kind Runtime/Editor/FirstPass*) tham chiếu type trong file đã gán module mà namespace đích ≠ namespace hiện tại → Error. `NameClash`: hai type cùng simple name có cùng namespace đích → Error. `PartialSplit`: các `filePaths` của một type nằm ở khác module (hoặc gán/chưa gán lẫn nhau) → Error. `RuntimeToEditor`: file runtime đã gán tham chiếu type ở file Editor → Error. Mỗi cycle → một Issue Error. File trong scope thuộc asmdef **không do tool tạo** (GUID asmdef không nằm trong `asmdefGuid/editorAsmdefGuid` của module nào) → read-only: `static bool ModuleValidator.IsAssignable(ScriptFile f, ModuleConfig cfg)` trả false, UI không cho kéo; nếu config lỡ gán → Error `OutOfScopeRef` trên chính file đó. Test bổ sung: `ThirdPartyAsmdefFileInScope_IsNotAssignable()`.

- [ ] **Step 1: Test thất bại — CycleFinder**

```csharp
[Test] public void Finds_TwoAndThreeCycles_IgnoresDag() {
  var e = new Dictionary<string,HashSet<string>>{
    ["A"]=new(){"B"}, ["B"]=new(){"A"}, ["C"]=new(){"D"}, ["D"]=new(){"E"}, ["E"]=new(){"C"}, ["F"]=new(){"A"} };
  var r = CycleFinder.Find(e);
  Assert.AreEqual(2, r.Count);
  CollectionAssert.AreEquivalent(new[]{"A","B"}, r.Single(c => c.Contains("A")));
}
```

- [ ] **Step 2: Test thất bại — Validator** (helper `Scan(params (string path, string guid, AssemblyKind kind, string[] declares, string[] refs)[] files)` trong file test)

```csharp
[Test] public void CrossModuleCycle_BlocksApply() { /* Combat:Weapon.cs→HUD; UI:HUD.cs→Weapon */
  Assert.IsFalse(r.CanApply); Assert.AreEqual(1, r.cycles.Count);
  StringAssert.Contains("HUD.cs", r.issues.Single(i => i.code == IssueCode.Cycle).message); }
[Test] public void OutOfScopeRef_ErrorWhenAssigned_WarningWhenNot() { /* Assets/Old/Legacy.cs ngoài scope */ }
[Test] public void FirstPassRef_IsOutOfScope() { }
[Test] public void ReverseRefFromOutside_WithNamespaceChange_IsError_WithoutChange_IsFine() { }
[Test] public void PartialSplitAcrossModules_IsError() { }
[Test] public void RuntimeReferencingEditor_IsError_EditorReferencingRuntime_IsFine() { }
[Test] public void SameSimpleNameSameTargetNamespace_IsNameClash() { }
[Test] public void OtherAsmdefRef_GoesToAsmdefRefs() { }
[Test] public void QualifiedOldName_IsWarning_NotBlocking() { }
```
Mỗi test assert đúng `IssueCode` + `Severity` + `CanApply`.

- [ ] **Step 3:** Chạy `-testFilter CycleFinderTests;ModuleValidatorTests`. Expected: FAIL.
- [ ] **Step 4:** Implement (Tarjan SCC lặp, không đệ quy).
- [ ] **Step 5:** Chạy lại. Expected: PASS.
- [ ] **Step 6:** Commit `feat: module validator with cycle and scope checks`.

### Task 7: ApplyPlanner (diff config ↔ đĩa) + AsmdefJson

**Files:**
- Create: `Editor/ModuleOrganizer/Apply/ApplyPlan.cs`, `Editor/ModuleOrganizer/Apply/AsmdefJson.cs`
- Test: `Tests/Editor/ApplyPlannerTests.cs`, `Tests/Editor/AsmdefJsonTests.cs`

**Interfaces:**
- Consumes: Task 3, 4, 5 (`Parse`), 6 (`ValidationReport.edges`, `asmdefRefs`)
- Produces:
  - `class MoveOp { string guid; string from; string to; }`
  - `class AsmdefOp { string path; string name; string rootNamespace; bool editorOnly; List<string> references /*"GUID:..." hoặc tên asmdef của node chưa có GUID*/; bool delete; string moduleName; }`
  - `class TextEditOp { string guid; string setNamespace /*null = không đổi*/; List<string> addUsings; List<string> removeUsings; List<(string type, string oldNs, string oldAsm)> movedFrom; }`
  - `class ApplyPlan { List<string> createFolders; List<MoveOp> moves; List<AsmdefOp> asmdefs; List<TextEditOp> edits; List<string> deleteFoldersIfEmpty; List<Issue> issues; List<string> removeEntries /*guid gỡ khỏi config sau Apply*/; bool IsEmpty; }`
  - `class DiskState { Dictionary<string,string> pathByGuid; Dictionary<string,string> namespaceByGuid; HashSet<string> existingPaths; }`
  - `static ApplyPlan ApplyPlanner.Build(ModuleConfig cfg, ScanResult scan, ValidationReport report, DiskState disk, IReadOnlyCollection<string> pendingRemovals)` — `pendingRemovals` = GUID vừa bỏ gán (UI giữ, xem Task 10)
  - `static string AsmdefJson.Write(AsmdefOp op)`

Quy tắc diff:
- Entry đích (`TargetPath`) ≠ vị trí hiện tại → `MoveOp`; `TargetNamespace` ≠ namespace hiện tại và `rewriteNamespace` → `TextEditOp.setNamespace`.
- Mọi file trong scope dùng type có namespace thay đổi → `addUsings` namespace mới; nếu namespace cũ không còn type nào sau Apply → `removeUsings` namespace cũ.
- Type `serializableNonUnityObject` đổi namespace hoặc assembly → `movedFrom` (oldAsm = assembly hiện tại).
- Đổi tên/folder module đã có `asmdefGuid` → move asmdef cũ (giữ GUID) + ghi lại nội dung.
- `pendingRemovals` và module bị xóa → `MoveOp` về `originalPath`, `setNamespace = originalNamespace`; nếu `originalPath` ∈ `existingPaths` (bởi file khác) → `Issue RestoreConflict` Error. Module bị xóa → `AsmdefOp.delete`, `deleteFoldersIfEmpty`.
- Node có file Editor → asmdef Editor, reference asmdef runtime cùng module.
- `report.edges` + `asmdefRefs` + `extraReferences` → `references` (sort, distinct).

- [ ] **Step 1: Test thất bại**

```csharp
[Test] public void FirstApply_MovesWrapsAndCreatesAsmdef() { /* Assets/_Scripts/Weapon.cs global ns → Combat */
  Assert.AreEqual("Assets/_Scripts/Combat/Weapon.cs", p.moves.Single().to);
  Assert.AreEqual("Game.Combat", p.edits.Single(e => e.guid == "w").setNamespace);
  Assert.AreEqual("Assets/_Scripts/Combat/Game.Combat.asmdef", p.asmdefs.Single().path); }
[Test] public void Dependent_GetsUsing() { }
[Test] public void ReapplyWithNoChanges_IsEmpty() { }
[Test] public void MoveBetweenModules_MovesAndRenamespaces() { }
[Test] public void RenameModule_MovesAsmdefKeepsGuid() { }
[Test] public void Unassign_RestoresOriginalPathAndNamespace_RemovesStaleUsing() { }
[Test] public void Unassign_OriginalPathTaken_IsRestoreConflict() { }
[Test] public void DeleteModule_DeletesAsmdefAndRestoresAll() { }
[Test] public void EditorFiles_GetEditorAsmdefReferencingRuntime() { }
[Test] public void SerializableMoved_GetsMovedFrom() { }
```
```csharp
[Test] public void Write_EditorAsmdef() {
  var json = AsmdefJson.Write(new AsmdefOp{ name="Game.Combat.Editor", rootNamespace="Game.Combat.Editor", editorOnly=true, references=new(){"GUID:abc"} });
  StringAssert.Contains("\"includePlatforms\": [\n        \"Editor\"\n    ]", json);
  StringAssert.Contains("\"autoReferenced\": true", json);
  StringAssert.Contains("\"overrideReferences\": false", json); }
```

- [ ] **Step 2:** Chạy `-testFilter ApplyPlannerTests;AsmdefJsonTests`. Expected: FAIL.
- [ ] **Step 3:** Implement. AsmdefJson theo đúng format Unity tự ghi (4 dấu cách, field `name, rootNamespace, references, includePlatforms, excludePlatforms, allowUnsafeCode, overrideReferences, precompiledReferences, autoReferenced, defineConstraints, versionDefines, noEngineReferences`), dùng `EditorJsonUtility` hoặc viết tay.
- [ ] **Step 4:** Chạy lại. Expected: PASS.
- [ ] **Step 5:** Commit `feat: diff-based apply planner and asmdef writer`.

### Task 8: ModuleApplier (thực thi plan)

**Files:**
- Create: `Editor/ModuleOrganizer/Apply/ModuleApplier.cs`
- Test: `Tests/Editor/ModuleApplierTests.cs`

**Interfaces:**
- Consumes: Task 7 (`ApplyPlan`, `AsmdefJson`), Task 5
- Produces:
  - `static List<string> ModuleApplier.Execute(ApplyPlan plan, ModuleConfig cfg, bool refresh = true)` → danh sách lỗi (rỗng = OK). Cập nhật `cfg`: `asmdefGuid/editorAsmdefGuid`, `applied = true`, gỡ `removeEntries`; `EditorUtility.SetDirty` + `AssetDatabase.SaveAssetIfDirty`.
  - `static bool ModuleApplier.HasUncommittedGitChanges(string projectRoot)` — chạy `git status --porcelain`, false nếu không có git.

Thứ tự trong `Execute`: (1) `StartAssetEditing` (2) tạo folder (`AssetDatabase.CreateFolder` từng cấp) (3) `MoveAsset` asmdef rồi file (lỗi trả về chuỗi rỗng = OK) (4) ghi asmdef JSON (5) text edits trên đường dẫn **mới** (đọc/ghi `File`, giữ BOM/encoding gốc) (6) xóa asmdef `delete`, xóa folder trống (7) `finally StopAssetEditing` (8) `Refresh` (9) đọc GUID asmdef mới vào cfg. Không gọi Refresh trong test (tham số `bool refresh = true`).

- [ ] **Step 1: Test thất bại** — dùng file `.txt` trong `Assets/_ApplierTmp/` để không kích hoạt compile; `[TearDown]` xóa folder.

```csharp
[Test] public void Move_PreservesGuid() {
  var guid = AssetDatabase.AssetPathToGUID("Assets/_ApplierTmp/a.txt");
  ModuleApplier.Execute(new ApplyPlan{ createFolders={"Assets/_ApplierTmp/M"}, moves={ new MoveOp{guid=guid, from="Assets/_ApplierTmp/a.txt", to="Assets/_ApplierTmp/M/a.txt"} } }, cfg, refresh:false);
  Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID("Assets/_ApplierTmp/M/a.txt")); }
[Test] public void TextEdit_PreservesBomAndCrlf() { }
[Test] public void DeleteFoldersIfEmpty_KeepsNonEmpty() { }
```

- [ ] **Step 2:** Chạy `-testFilter ModuleApplierTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Chạy lại. Expected: PASS.
- [ ] **Step 5:** Commit `feat: module applier`.

### Task 9: ApplyResultWatcher (sống sót qua domain reload + compile player)

**Files:**
- Create: `Editor/ModuleOrganizer/Apply/ApplyResultWatcher.cs`

**Interfaces:**
- Produces:
  - `class ApplyResult { bool editorCompileOk; List<string> editorErrors; bool playerCompileOk; List<string> playerErrors; string buildTarget; }` (`[Serializable]`, JSON qua `JsonUtility`)
  - `static void ApplyResultWatcher.BeginPending()` — `SessionState.SetBool("Monome.ModuleOrganizer.Pending", true)`
  - `static ApplyResult ApplyResultWatcher.LastResult` (đọc `SessionState.GetString("Monome.ModuleOrganizer.Result")`)
  - `static event Action ApplyResultWatcher.ResultReady`

Hành vi: `[InitializeOnLoad]` static ctor đăng ký `CompilationPipeline.assemblyCompilationFinished` (gom `CompilerMessageType.Error`) và `EditorApplication.delayCall`. Khi Pending và Editor không còn compile: nếu có lỗi Editor → ghi result, tắt Pending (không có reload). Nếu không lỗi (sau reload) → `PlayerBuildInterface.CompilePlayerScripts(new ScriptCompilationSettings{ target = EditorUserBuildSettings.activeBuildTarget, group = BuildPipeline.GetBuildTargetGroup(...) }, "Temp/MonomePlayerCompile")`, gom lỗi từ `ScriptCompilationResult`/log, ghi result, tắt Pending, raise `ResultReady`.

- [ ] **Step 1:** Implement.
- [ ] **Step 2: Kiểm tra tay** trên TestHost~: gọi `BeginPending()` + sửa một script hợp lệ qua menu debug tạm → sau reload `LastResult.editorCompileOk == true`, có `playerCompileOk`. Lặp với script lỗi → `editorErrors` có lỗi, không treo Pending. Gỡ menu debug.
- [ ] **Step 3:** Chạy toàn bộ test. Expected: PASS (không vỡ test cũ).
- [ ] **Step 4:** Commit `feat: apply result watcher across domain reload`.

### Task 10: ModuleSync (kiểm tra lệch)

**Files:**
- Create: `Editor/ModuleOrganizer/Sync/ModuleSync.cs`
- Test: `Tests/Editor/ModuleSyncTests.cs`

**Interfaces:**
- Consumes: Task 3, Task 6 (`ValidationReport.edges/asmdefRefs` để so reference)
- Produces:
  - `class AsmdefSnapshot { string guid; string path; string name; List<string> references; }`
  - `class DiskSnapshot { Dictionary<string,string> scriptPathByGuid; Dictionary<string, AsmdefSnapshot> asmdefsByGuid; static DiskSnapshot Capture(ModuleConfig cfg); }` (Capture dùng AssetDatabase, không test)
  - `enum DriftKind { AsmdefMissing, AsmdefMovedOrRenamed, ScriptDeleted, ScriptAddedInModuleFolder, ScriptMovedOut, ReferencesDiffer }`
  - `class DriftItem { DriftKind kind; string moduleName; string guid; string detail; }`
  - `static List<DriftItem> ModuleSync.Diff(ModuleConfig cfg, DiskSnapshot disk, ValidationReport report)`
  - `static void ModuleSync.AcceptDisk(ModuleConfig cfg, DiskSnapshot disk, IEnumerable<DriftItem> items)` — thêm file mới (originalPath = path hiện tại, originalNamespace = namespace hiện tại), gỡ GUID chết, cập nhật folder/tên theo asmdef, nhận reference tay vào `extraReferences`
  - "Áp lại config" = gọi `ApplyPlanner.Build` + `ModuleApplier.Execute`, không có hàm riêng.

Chỉ xét entry `applied == true` và module có `asmdefGuid`; nháp chưa Apply không bị coi là lệch.

- [ ] **Step 1: Test thất bại**

```csharp
[Test] public void NoDrift_WhenDiskMatches() { }
[Test] public void NewFileInModuleFolder_IsAdded_AndAcceptDiskAddsEntry() { }
[Test] public void DeletedScript_IsReported_AndAcceptDiskRemovesEntry() { }
[Test] public void AsmdefRenamedOnDisk_AcceptDiskUpdatesModule() { }
[Test] public void ManualReference_AcceptDiskAddsExtraReference() { }
[Test] public void DraftEntries_AreNotDrift() { }
```

- [ ] **Step 2:** Chạy `-testFilter ModuleSyncTests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Chạy lại. Expected: PASS.
- [ ] **Step 5:** Commit `feat: drift detection and accept-from-disk`.

### Task 11: ModuleOrganizerWindow (UI Toolkit)

**Files:**
- Create: `Editor/ModuleOrganizer/UI/ModuleOrganizerWindow.cs`, `ModuleOrganizerWindow.uxml`, `ModuleOrganizerWindow.uss`

**Interfaces:**
- Consumes: tất cả task trước.

Hành vi (spec §5.7):
- Menu `Tools/Monome/Module Organizer`. Tìm `ModuleConfig` bằng `AssetDatabase.FindAssets("t:ModuleConfig")`; không có → nút "Create ModuleConfig" (`Assets/ModuleConfig.asset`); nhiều → dropdown.
- Toolbar: config picker, Scan, Validate, Apply, badge lệch + Sync.
- Trái: danh sách scope folders (thêm bằng `EditorUtility.OpenFolderPanel`, phải nằm trong `Assets/`), ô lọc, cây "Chưa gán" theo thư mục.
- Giữa: một card mỗi module (màu viền = `color`), `ListView` multi-select. Kéo thả bằng `DragAndDrop` (genericData `"MonomeScripts"` = list GUID): chưa gán → module, module → module, module → chưa gán (thêm GUID vào `pendingRemovals` của window nếu entry đã `applied`, ngược lại xóa entry). Kéo thư mục ở cột trái = mọi file trong đó. Mỗi thay đổi `Undo.RecordObject(cfg, ...)` + `SetDirty`.
- Click card → panel sửa name, namespace, color, description, rewriteNamespace, extraReferences, nút Delete module.
- Item: nhãn `ed` nếu `IsEditor`; icon ⚠/✗ theo issue nặng nhất của file, tooltip = message.
- Dưới: tab Issues / Dependencies (edge + mở rộng `causedBy`) / Plan (moves, asmdefs, edits, deleteFolders).
- Apply: disable khi `!CanApply` hoặc plan có Error. Nếu `HasUncommittedGitChanges` → `EditorUtility.DisplayDialog` cảnh báo (cho tiếp tục). Rồi `BeginPending()` → `Execute` → xóa `pendingRemovals`.
- Đang compile hoặc có lỗi compile (`EditorUtility.scriptCompilationFailed`) → khóa Scan, hiện thông báo.
- `OnEnable`: nếu có `LastResult` mới → hiện panel kết quả; tự Scan + Diff sync.
- Badge Sync mở danh sách `DriftItem` với nút "Lấy theo ổ đĩa" / "Áp lại config" (từng mục và tất cả).

- [ ] **Step 1:** Implement window + UXML/USS.
- [ ] **Step 2: Kiểm tra tay** trên TestHost~ với fixtures Task 4: mở window, tạo config, scope `Assets/_Fixtures/Scope`, kéo `FxA.cs` vào module "Alpha" → Issues hiện `FxOut` ngoài phạm vi (Error) và `FxB` chưa gán (Error), Apply bị khóa. Kéo `FxB.cs` vào "Alpha", thêm scope `Assets/_Fixtures/Outside` + kéo `FxOut.cs` vào "Alpha" → Apply mở. Ctrl+Z hoàn tác được thao tác kéo.
- [ ] **Step 3:** Commit `feat: module organizer window`.

### Task 12 (chỉ làm nếu Task 2 cho thấy UnityEvent hỏng): sửa `m_TargetAssemblyTypeName`

**Files:**
- Modify: `Editor/ModuleOrganizer/Apply/ApplyPlan.cs` (thêm `List<YamlEditOp> yamlEdits`), `ModuleApplier.cs`
- Create: `Editor/ModuleOrganizer/Apply/YamlTypeNameRewriter.cs`
- Test: `Tests/Editor/YamlTypeNameRewriterTests.cs`

**Interfaces:**
- Produces: `class YamlEditOp { string path; string oldTypeName; string newTypeName; }` (dạng `"Ns.Type, Assembly"`); `static string YamlTypeNameRewriter.Rewrite(string yaml, string oldName, string newName)` chỉ thay sau khóa `m_TargetAssemblyTypeName:`.
- Planner: với mỗi type `UnityEngine.Object` đổi namespace/assembly, tìm file `.unity/.prefab/.asset` (text-serialized) chứa `m_TargetAssemblyTypeName: <old>` → `YamlEditOp`; tab Plan liệt kê.

- [ ] **Step 1:** Test: `Rewrite("    m_TargetAssemblyTypeName: Target, Assembly-CSharp\n    m_Other: Target, Assembly-CSharp", "Target, Assembly-CSharp", "Spike.Mod.Target, Spike.Mod")` chỉ đổi dòng đầu.
- [ ] **Step 2:** Chạy test → FAIL. **Step 3:** Implement. **Step 4:** Chạy → PASS.
- [ ] **Step 5:** Lặp lại kịch bản Task 2 bằng tool → listener còn nguyên.
- [ ] **Step 6:** Commit `feat: rewrite UnityEvent type names on apply`.

### Task 13: Kiểm tra end-to-end

**Files:**
- Create: `TestHost~/Assets/_E2E/` (copy fixtures + một prefab dùng `FxA`, một `[SerializeReference]`, một scene có UnityEvent)
- Create: `README.md` (cài đặt: Add package from disk / git URL; cách dùng 5 bước; giới hạn: chỉ kiểm tra build target đang chọn, không tự gỡ vòng, cảnh báo tên đầy đủ)

- [ ] **Step 1:** Commit TestHost~ trước (để thử cảnh báo git và hoàn tác bằng `git checkout`).
- [ ] **Step 2:** Scope `Assets/_E2E`, chia 2 module + 1 file Editor, Apply. Expected: không lỗi compile Editor và player; prefab không mất script; `[SerializeReference]` còn dữ liệu; UnityEvent còn listener; `LastResult` hiện sau reload.
- [ ] **Step 3:** Mở `TestHost~.sln` trong Visual Studio 2026 (Unity: Preferences > External Tools > Regenerate project files). Expected: có project `Game.Alpha`, `Game.Beta`, `Game.Alpha.Editor`; namespace khớp.
- [ ] **Step 4:** Đổi tên module, chuyển 1 file sang module khác, bỏ gán 1 file, Apply lại. Expected: file bỏ gán về đúng `originalPath` + namespace gốc; không lỗi compile.
- [ ] **Step 5:** Tạo file mới bằng tay trong thư mục module → Sync báo `ScriptAddedInModuleFolder` → "Lấy theo ổ đĩa" thêm vào config.
- [ ] **Step 6:** Viết README. Commit `docs: readme and e2e fixtures`.
