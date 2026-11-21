using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Monome.UtilityPack.ModuleOrganizer;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

// Headless end-to-end check of the Module Organizer (Task 13), host project only. Lives outside the scope folder
// (Assets/_E2E/Scripts) so the tool never moves it, and reaches the fixture types by name only: they change assembly.
//   Setup: build the prefab / asset / scene from the fixtures (run once, then commit).
//   Run:   A apply 2 modules -> (reload) B verify, C rename/move/unassign + apply -> (reload) D verify restore,
//          add a file by hand -> (reload) E Sync accepts it. Phases chain inside one batchmode process (no -quit) because
//          ApplyResultWatcher keeps its result in SessionState. Each phase logs "E2E <phase> PASS|FAIL <details>".
[InitializeOnLoad]
public static class E2EDriver
{
    const string Scope = "Assets/_E2E/Scripts";
    const string CfgPath = "Assets/_E2E/E2EConfig.asset";
    const string PrefabPath = "Assets/_E2E/Content/E2EShooter.prefab";
    const string StorePath = "Assets/_E2E/Content/E2EStore.asset";
    const string ScenePath = "Assets/_E2E/Content/E2EScene.unity";
    const string StateFile = "Library/E2EState.txt"; // "<next phase>|<generation of the domain that wrote it>"
    const double TimeoutSeconds = 600;

    static readonly string Generation = Guid.NewGuid().ToString("N");
    static readonly double Started = EditorApplication.timeSinceStartup;

    static E2EDriver()
    {
        if (Application.isBatchMode && File.Exists(StateFile)) EditorApplication.update += Tick;
    }

    static string P(string file) => Scope + "/" + file;
    static string ReadSource(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    static string GuidOf(string file) => AssetDatabase.AssetPathToGUID(P(file));

    // ---- entry points ----

    public static void Setup()
    {
        try
        {
            var shooter = new GameObject("E2EShooter");
            var c = shooter.AddComponent(T("E2EShooter"));
            var so = new SerializedObject(c);
            so.FindProperty("power").intValue = 42;
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(shooter, PrefabPath);
            UnityEngine.Object.DestroyImmediate(shooter);

            var store = ScriptableObject.CreateInstance(T("E2EStore"));
            var payload = Activator.CreateInstance(T("E2EPayload"));
            payload.GetType().GetField("value").SetValue(payload, 7);
            store.GetType().GetField("payload").SetValue(store, payload);
            AssetDatabase.CreateAsset(store, StorePath);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var emitter = new GameObject("Emitter").AddComponent(T("E2E.Events.E2EEmitter"));
            var target = new GameObject("Target").AddComponent(T("E2E.Events.E2ETarget"));
            var ev = (UnityEvent)emitter.GetType().GetField("onFire").GetValue(emitter);
            UnityEventTools.AddVoidPersistentListener(ev, (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), target, "Ping"));
            ev.SetPersistentListenerState(0, UnityEventCallState.EditorAndRuntime); // so Invoke() works in edit mode
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Log("Setup", true, "prefab, asset and scene created");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Log("Setup", false, e.ToString());
            EditorApplication.Exit(1);
        }
    }

    public static void Run()
    {
        EditorApplication.update -= Tick;
        Guarded("A", PhaseA);
    }

    // ---- phases ----

    // Step 2: scope _E2E/Scripts, Alpha (+ the Editor file) and Beta, Apply.
    static void PhaseA()
    {
        AssetDatabase.DeleteAsset(CfgPath);
        var cfg = ScriptableObject.CreateInstance<ModuleConfig>();
        cfg.rootNamespace = "Game";
        cfg.scopeFolders.Add(Scope);
        AssetDatabase.CreateAsset(cfg, CfgPath);
        var scan = DependencyScanner.Scan();
        var alpha = NewModule(cfg, "Alpha");
        var beta = NewModule(cfg, "Beta");
        var nA = ModuleEdits.Assign(cfg, scan, new[] { "E2EShooter.cs", "E2EPayload.cs", "E2EStore.cs", "Editor/E2EShooterEditor.cs" }.Select(GuidOf), alpha, ReadSource);
        var nB = ModuleEdits.Assign(cfg, scan, new[] { "E2EEmitter.cs", "E2ETarget.cs", "Misc/E2ELoose.cs" }.Select(GuidOf), beta, ReadSource);
        if (nA != 4 || nB != 3) throw new Exception($"assigned {nA}/4 to Alpha and {nB}/3 to Beta");
        Apply("A", cfg, scan, "B");
    }

    static void PhaseB()
    {
        var f = new List<string>();
        CheckResult(f, "Game.Alpha|Game.Alpha");
        ExpectType(f, "Game.Alpha.E2EShooter", "Game.Alpha");
        ExpectType(f, "Game.Alpha.E2EPayload", "Game.Alpha");
        ExpectType(f, "Game.Alpha.E2EStore", "Game.Alpha");
        ExpectType(f, "Game.Alpha.Editor.E2EShooterEditor", "Game.Alpha.Editor");
        ExpectType(f, "Game.Beta.E2EEmitter", "Game.Beta");
        ExpectType(f, "Game.Beta.E2ETarget", "Game.Beta");
        ExpectType(f, "Game.Beta.Misc.E2ELoose", "Game.Beta");
        foreach (var p in new[] { "Alpha/E2EShooter.cs", "Alpha/Editor/E2EShooterEditor.cs", "Beta/Misc/E2ELoose.cs",
                     "Alpha/Game.Alpha.asmdef", "Alpha/Editor/Game.Alpha.Editor.asmdef", "Beta/Game.Beta.asmdef" })
            Expect(f, File.Exists(P(p)), "missing " + P(p));
        CheckAssets(f, "Game.Alpha", "Game.Alpha", "Game.Beta");
        if (Report("B", f)) PhaseC();
    }

    // Step 4: rename Beta -> Gamma (namespace too), move E2EPayload Alpha -> Gamma, unassign E2ELoose, Apply.
    static void PhaseC()
    {
        var cfg = AssetDatabase.LoadAssetAtPath<ModuleConfig>(CfgPath);
        var scan = DependencyScanner.Scan();
        var beta = cfg.modules.Single(m => m.name == "Beta");
        if (!ModuleEdits.RenameModule(cfg, beta, "Gamma")) throw new Exception("rename refused");
        ModuleEdits.Change(cfg, "Set module namespace", () => beta.namespaceName = "Game.Gamma");
        if (ModuleEdits.Assign(cfg, scan, new[] { GuidOf("Alpha/E2EPayload.cs") }, beta, ReadSource) != 1) throw new Exception("payload not moved");
        var loose = GuidOf("Beta/Misc/E2ELoose.cs");
        ModuleEdits.Unassign(cfg, new[] { loose }, scan, ReadSource);
        if (cfg.pendingRemovals.All(e => e.guid != loose)) throw new Exception("E2ELoose not in pendingRemovals");
        Apply("C", cfg, scan, "D");
    }

    static void PhaseD()
    {
        var f = new List<string>();
        CheckResult(f, "Game.Gamma|Game.Gamma");
        ExpectType(f, "Game.Gamma.E2EPayload", "Game.Gamma");
        ExpectType(f, "Game.Gamma.E2EEmitter", "Game.Gamma");
        ExpectType(f, "Game.Gamma.E2ETarget", "Game.Gamma");
        ExpectType(f, "Game.Alpha.E2EShooter", "Game.Alpha");
        ExpectType(f, "E2E.Legacy.E2ELoose", "Assembly-CSharp");
        var loosePath = P("Misc/E2ELoose.cs");
        Expect(f, File.Exists(loosePath), "E2ELoose not restored to " + loosePath);
        var ns = File.Exists(loosePath) ? SourceRewriter.Parse(File.ReadAllText(loosePath)).namespaces.FirstOrDefault() : null;
        Expect(f, ns == "E2E.Legacy", $"E2ELoose namespace {ns}, expected E2E.Legacy");
        Expect(f, File.Exists(P("Gamma/Game.Gamma.asmdef")), "missing Gamma/Game.Gamma.asmdef");
        Expect(f, File.Exists(P("Gamma/E2EPayload.cs")), "missing Gamma/E2EPayload.cs");
        Expect(f, !Directory.Exists(P("Beta")), "old Beta folder still there");
        var cfg = AssetDatabase.LoadAssetAtPath<ModuleConfig>(CfgPath);
        Expect(f, cfg.pendingRemovals.Count == 0, "pendingRemovals not cleared");
        Expect(f, cfg.FindModuleOf(GuidOf("Misc/E2ELoose.cs")) == null, "E2ELoose still assigned");
        CheckAssets(f, "Game.Alpha", "Game.Gamma", "Game.Gamma");
        if (!Report("D", f)) return;

        // Step 5 setup: a file made by hand in the module folder; Sync runs after the compile it triggers.
        File.WriteAllText(P("Gamma/E2EAdded.cs"), "namespace Game.Gamma\n{\n    public class E2EAdded\n    {\n    }\n}\n");
        WriteState("E");
        AssetDatabase.Refresh();
    }

    static void PhaseE()
    {
        var f = new List<string>();
        var cfg = AssetDatabase.LoadAssetAtPath<ModuleConfig>(CfgPath);
        var scan = DependencyScanner.Scan();
        var report = ModuleValidator.Validate(cfg, scan, ReadSource);
        var disk = DiskSnapshot.Capture(cfg);
        var drift = ModuleSync.Diff(cfg, disk, report);
        var added = GuidOf("Gamma/E2EAdded.cs");
        Expect(f, !string.IsNullOrEmpty(added), "E2EAdded.cs not imported");
        foreach (var d in drift) Info("E", "drift: " + d);
        var item = drift.FirstOrDefault(d => d.kind == DriftKind.ScriptAddedInModuleFolder && d.guid == added && d.moduleName == "Gamma");
        Expect(f, item != null, "no ScriptAddedInModuleFolder for E2EAdded.cs");
        Expect(f, drift.Count == 1, $"expected exactly 1 drift item, got {drift.Count}");
        if (item != null)
        {
            ModuleSync.AcceptDisk(cfg, DiskSnapshot.Capture(cfg), new[] { item });
            AssetDatabase.SaveAssets();
            var e = cfg.modules.Single(m => m.name == "Gamma").scripts.FirstOrDefault(s => s.guid == added);
            Expect(f, e != null && e.applied, "E2EAdded not added to Gamma as applied");
            report = ModuleValidator.Validate(cfg, scan, ReadSource);
            var after = ModuleSync.Diff(cfg, DiskSnapshot.Capture(cfg), report);
            Expect(f, after.Count == 0, "drift left after accept: " + string.Join("; ", after));
            var plan = ApplyPlanner.Build(cfg, scan, report, DiskState.Capture(scan, ReadSource));
            Expect(f, plan.IsEmpty, "plan not empty after accept: moves " + string.Join(", ", plan.moves) + $", {plan.asmdefs.Count} asmdefs, {plan.edits.Count} edits");
        }
        if (Report("E", f)) Done(0);
    }

    // ---- shared steps ----

    static Module NewModule(ModuleConfig cfg, string name)
    {
        var m = ModuleEdits.AddModule(cfg);
        if (!ModuleEdits.RenameModule(cfg, m, name)) throw new Exception("rename refused: " + name);
        ModuleEdits.Change(cfg, "Set module namespace", () => m.namespaceName = "Game." + name);
        return m;
    }

    // What the window's second Apply click does, minus the dialogs.
    static void Apply(string phase, ModuleConfig cfg, ScanResult scan, string next)
    {
        var report = ModuleValidator.Validate(cfg, scan, ReadSource);
        var plan = ApplyPlanner.Build(cfg, scan, report, DiskState.Capture(scan, ReadSource));
        foreach (var i in report.issues.Concat(plan.issues)) Info(phase, "issue: " + i);
        foreach (var m in plan.moves) Info(phase, "move: " + m);
        foreach (var a in plan.asmdefs) Info(phase, $"asmdef: {(a.delete ? "delete" : "write")} {a.path}");
        if (!report.CanApply || plan.issues.Any(i => i.severity == Severity.Error) || plan.IsEmpty)
            throw new Exception($"cannot apply (canApply {report.CanApply}, empty plan {plan.IsEmpty}, cycles {report.cycles.Count})");
        WriteState(next);
        var pending = plan.TriggersCompile;
        if (pending) ApplyResultWatcher.BeginPending(plan.reserializeTypeKeys);
        var errors = ModuleApplier.Execute(plan, cfg, out var touched);
        Undo.ClearUndo(cfg);
        if (errors.Count > 0)
        {
            if (pending && !touched) ApplyResultWatcher.Cancel();
            throw new Exception("apply errors: " + string.Join("; ", errors));
        }
        AssetDatabase.SaveAssets();
        Log(phase, true, $"applied {plan.moves.Count} moves, {plan.asmdefs.Count} asmdef ops, {plan.edits.Count} edits, " +
                         $"reserialize keys [{string.Join(", ", plan.reserializeTypeKeys)}]");
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    static void CheckResult(List<string> f, string payloadNsAsm)
    {
        var r = ApplyResultWatcher.LastResult;
        if (r == null)
        {
            f.Add("no ApplyResultWatcher.LastResult");
            return;
        }
        Info("result", JsonUtility.ToJson(r));
        Expect(f, r.editorCompileOk, "Editor compile failed: " + string.Join(" | ", r.editorErrors));
        Expect(f, r.playerCompileOk, $"player compile ({r.buildTarget}) failed: " + string.Join(" | ", r.playerErrors));
        Expect(f, r.warnings.Count == 0, "warnings: " + string.Join(" | ", r.warnings));
        Expect(f, r.reserialized.Contains(StorePath), "store asset not re-serialized");
        var owed = AssetDatabase.LoadAssetAtPath<ModuleConfig>(CfgPath).owedReserializeKeys;
        Expect(f, owed.Count == 0, "config still owes reserialize keys: " + string.Join(", ", owed));
        var parts = payloadNsAsm.Split('|');
        var yaml = File.ReadAllText(StorePath);
        Expect(f, ApplyResultWatcher.ContainsManagedRefType(yaml, parts[0], parts[1], "E2EPayload"),
            $"store YAML does not name E2EPayload in {parts[0]}/{parts[1]}");
        Expect(f, !EditorUtility.scriptCompilationFailed, "scriptCompilationFailed");
    }

    // Prefab keeps its script and data, [SerializeReference] payload keeps its data, the scene's UnityEvent keeps its listener.
    static void CheckAssets(List<string> f, string shooterNs, string payloadNs, string eventsNs)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Expect(f, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(prefab) == 0, "prefab has a missing script");
        var shooter = prefab.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().Name == "E2EShooter");
        Expect(f, shooter != null && shooter.GetType().FullName == shooterNs + ".E2EShooter", "prefab shooter type " + shooter?.GetType().FullName);
        if (shooter != null) Expect(f, new SerializedObject(shooter).FindProperty("power").intValue == 42, "prefab power != 42");

        var store = AssetDatabase.LoadMainAssetAtPath(StorePath);
        Expect(f, store != null && store.GetType().FullName == shooterNs + ".E2EStore", "store type " + store?.GetType().FullName);
        if (store != null)
        {
            Expect(f, !SerializationUtility.HasManagedReferencesWithMissingTypes(store), "store has managed references with missing types");
            var payload = new SerializedObject(store).FindProperty("payload");
            var value = payload.managedReferenceValue;
            Expect(f, value != null && value.GetType().FullName == payloadNs + ".E2EPayload", "payload type " + value?.GetType().FullName);
            Expect(f, new SerializedObject(store).FindProperty("payload.value")?.intValue == 7, "payload.value != 7");
        }

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var emitterGo = GameObject.Find("Emitter");
        var targetGo = GameObject.Find("Target");
        Expect(f, emitterGo != null && targetGo != null, "scene objects missing");
        if (emitterGo == null || targetGo == null) return;
        Expect(f, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(emitterGo) + GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(targetGo) == 0,
            "scene has a missing script");
        var emitter = emitterGo.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().Name == "E2EEmitter");
        var target = targetGo.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().Name == "E2ETarget");
        Expect(f, emitter != null && emitter.GetType().FullName == eventsNs + ".E2EEmitter", "emitter type " + emitter?.GetType().FullName);
        Expect(f, target != null && target.GetType().FullName == eventsNs + ".E2ETarget", "target type " + target?.GetType().FullName);
        if (emitter == null || target == null) return;
        var ev = emitter.GetType().GetField("onFire").GetValue(emitter) as UnityEvent;
        Expect(f, ev != null && ev.GetPersistentEventCount() == 1, "listener count " + ev?.GetPersistentEventCount());
        if (ev == null || ev.GetPersistentEventCount() != 1) return;
        Expect(f, ev.GetPersistentTarget(0) == target && ev.GetPersistentMethodName(0) == "Ping",
            $"listener -> {ev.GetPersistentTarget(0)}.{ev.GetPersistentMethodName(0)}");
        var hits = target.GetType().GetField("hits");
        var before = (int)hits.GetValue(target);
        ev.Invoke();
        Expect(f, (int)hits.GetValue(target) == before + 1, "Invoke() did not call Ping");
    }

    static void ExpectType(List<string> f, string fullName, string assembly)
    {
        var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(x => x != null);
        Expect(f, t != null && t.Assembly.GetName().Name == assembly, $"type {fullName} in {t?.Assembly.GetName().Name ?? "nowhere"}, expected {assembly}");
    }

    static Type T(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(x => x != null)
        ?? throw new Exception("type not found: " + fullName);

    // ---- phase plumbing ----

    static void Tick()
    {
        if (EditorApplication.timeSinceStartup - Started > TimeoutSeconds)
        {
            EditorApplication.update -= Tick;
            Log(ReadState().phase ?? "?", false, $"timeout after {TimeoutSeconds}s (pending {ApplyResultWatcher.Pending}, result {ApplyResultWatcher.LastResult != null})");
            Done(1);
            return;
        }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || ApplyResultWatcher.Pending) return;
        var (phase, gen) = ReadState();
        Action run = phase switch { "B" => PhaseB, "D" => PhaseD, "E" => PhaseE, _ => null };
        if (run == null) return;
        if (phase == "E" ? gen == Generation : ApplyResultWatcher.LastResult == null) return; // E: wait for the reload
        EditorApplication.update -= Tick;
        File.Delete(StateFile);
        Guarded(phase, run);
    }

    static void Guarded(string phase, Action run)
    {
        try { run(); }
        catch (Exception e)
        {
            Log(phase, false, e.ToString());
            Done(1);
        }
    }

    static void WriteState(string next) => File.WriteAllText(StateFile, next + "|" + Generation);

    static (string phase, string gen) ReadState()
    {
        if (!File.Exists(StateFile)) return (null, null);
        var p = File.ReadAllText(StateFile).Split('|');
        return (p[0], p.Length > 1 ? p[1] : "");
    }

    static void Expect(List<string> failures, bool ok, string message)
    {
        if (!ok) failures.Add(message);
    }

    static bool Report(string phase, List<string> failures)
    {
        Log(phase, failures.Count == 0, failures.Count == 0 ? "all checks passed" : string.Join(" || ", failures));
        if (failures.Count > 0) Done(1);
        return failures.Count == 0;
    }

    static void Done(int code)
    {
        if (File.Exists(StateFile)) File.Delete(StateFile);
        EditorApplication.Exit(code);
    }

    static void Log(string phase, bool ok, string details) =>
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "E2E {0} {1} {2}", phase, ok ? "PASS" : "FAIL", details);

    static void Info(string phase, string details) =>
        Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "E2E-info {0} {1}", phase, details);
}
