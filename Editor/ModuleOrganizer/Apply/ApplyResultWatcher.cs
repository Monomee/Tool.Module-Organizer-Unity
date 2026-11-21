using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEditor.Compilation;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer
{
    [Serializable]
    public class ApplyResult
    {
        public bool editorCompileOk;
        public List<string> editorErrors = new List<string>();
        public bool playerCompileOk;
        public List<string> playerErrors = new List<string>();
        public string buildTarget;
        public List<string> reserialized = new List<string>(); // asset paths
        public List<string> warnings = new List<string>();
    }

    // Spec 5.5 steps 6-8. A successful compile reloads the domain, wiping window state and callbacks, so the pending
    // flag, the reserialize keys and the result live in SessionState. Compile errors mean no reload: the errors come
    // from assemblyCompilationFinished in the same domain. After a reload: reserialize assets still naming moved
    // [SerializeReference] types by their old location, compile player scripts for the active target, write the result.
    [InitializeOnLoad]
    public static class ApplyResultWatcher
    {
        const string PendingKey = "Monome.ModuleOrganizer.Pending";
        const string ResultKey = "Monome.ModuleOrganizer.Result";
        const string KeysKey = "Monome.ModuleOrganizer.ReserializeKeys";
        const string ReloadsKey = "Monome.ModuleOrganizer.PendingReloads";
        public const int MaxReloads = 3;
        const string PlayerOutput = "Temp/MonomePlayerCompile";

        static readonly List<string> EditorErrors = new List<string>(); // current domain's last compile
        static readonly Regex CompilerLine = new Regex(@"^\S+\.cs\(\d+");

        public static event Action ResultReady;

        public static bool Pending => SessionState.GetBool(PendingKey, false);

        // Domain reloads seen while Pending; the check gives up after MaxReloads (the Result tab shows both).
        public static int PendingReloads => SessionState.GetInt(ReloadsKey, 0);

        public static ApplyResult LastResult
        {
            get
            {
                var json = SessionState.GetString(ResultKey, "");
                return json.Length == 0 ? null : JsonUtility.FromJson<ApplyResult>(json);
            }
        }

        static ApplyResultWatcher()
        {
            CompilationPipeline.compilationStarted += _ => EditorErrors.Clear();
            CompilationPipeline.assemblyCompilationFinished += (_, messages) =>
                EditorErrors.AddRange(messages.Where(m => m.type == CompilerMessageType.Error).Select(m => m.message));
            CompilationPipeline.compilationFinished += _ => { if (Pending) EditorApplication.delayCall += Check; };
            if (!Pending)
            {
                // A reload is a good compile: settle what an earlier Apply still owes (its own compile failed, or the
                // Editor closed before the reload).
                EditorApplication.delayCall += ReserializeOwed;
                return;
            }
            var reloads = SessionState.GetInt(ReloadsKey, 0) + 1;
            SessionState.SetInt(ReloadsKey, reloads);
            if (reloads > MaxReloads)
            {
                var r = new ApplyResult { buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString() };
                r.warnings.Add($"The Apply result check did not finish within {MaxReloads} domain reloads and was dropped; check the Console.");
                Debug.LogWarning("[Module Organizer] " + r.warnings[0]);
                Finish(r);
                return;
            }
            EditorApplication.delayCall += Check;
        }

        // Call right before AssetDatabase.Refresh() (spec 5.5 step 7). Keys: ApplyPlan.reserializeTypeKeys.
        public static void BeginPending(IEnumerable<string> reserializeTypeKeys = null)
        {
            SessionState.EraseString(ResultKey);
            SessionState.SetString(KeysKey, string.Join("\n", reserializeTypeKeys ?? Enumerable.Empty<string>()));
            SessionState.SetInt(ReloadsKey, 0);
            SessionState.SetBool(PendingKey, true);
            EditorErrors.Clear();
        }

        // Undo BeginPending when the Apply it announced wrote nothing (no compile will come to resolve it).
        public static void Cancel()
        {
            SessionState.EraseBool(PendingKey);
            SessionState.EraseString(KeysKey);
            SessionState.EraseInt(ReloadsKey);
        }

        // Callers announce only plans that make Unity compile (ApplyPlan.TriggersCompile). ponytail: if one still compiles
        // nothing, Pending resolves at the next reload, or gives up after MaxReloads.
        static void Check()
        {
            if (!Pending) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Check;
                return;
            }
            var r = new ApplyResult { buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString() };
            if (EditorErrors.Count > 0)
            {
                r.editorErrors.AddRange(EditorErrors.Distinct());
                Finish(r);
                return;
            }
            if (SessionState.GetInt(ReloadsKey, 0) == 0) return; // old domain: the reload is still to come
            r.editorCompileOk = true;
            try
            {
                Guard(() => ReserializeWithOwed(SessionState.GetString(KeysKey, "").Split('\n'), r), "Re-serializing assets", r);
                Guard(() => CompilePlayer(r), "Player script compilation", r);
            }
            finally
            {
                Finish(r);
            }
        }

        static void Guard(Action step, string what, ApplyResult r)
        {
            try { step(); }
            catch (Exception e)
            {
                r.warnings.Add($"{what} failed: {e.Message}");
                Debug.LogException(e);
            }
        }

        static void Finish(ApplyResult r)
        {
            SessionState.SetString(ResultKey, JsonUtility.ToJson(r));
            SessionState.SetBool(PendingKey, false);
            SessionState.EraseString(KeysKey);
            SessionState.EraseInt(ReloadsKey);
            ResultReady?.Invoke();
        }

        // Owed keys of every ModuleConfig asset plus `keys`; only the keys Reserialize settled are cleared, so an asset it
        // could not read or re-serialize keeps blocking Apply until a later reload settles it.
        static void ReserializeWithOwed(IEnumerable<string> keys, ApplyResult r)
        {
            var configs = AssetDatabase.FindAssets("t:ModuleConfig").Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ModuleConfig>).Where(c => c != null).ToList();
            var owed = configs.SelectMany(c => c.owedReserializeKeys).Distinct().ToList();
            var unsettled = new HashSet<string>(Reserialize(owed.Concat(keys).Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList(), r));
            ClearOwed(configs, owed.Where(k => !unsettled.Contains(k)).ToList());
        }

        public static void ClearOwed(IEnumerable<ModuleConfig> configs, ICollection<string> keys)
        {
            foreach (var c in configs)
            {
                if (c.owedReserializeKeys.RemoveAll(keys.Contains) == 0) continue;
                EditorUtility.SetDirty(c);
                if (EditorUtility.IsPersistent(c)) AssetDatabase.SaveAssetIfDirty(c);
            }
        }

        // Outside an Apply check (after a reload, on window Scan, after an Apply that compiles nothing): only once compiled
        // code is loaded and no Apply is waiting.
        public static void ReserializeOwed()
        {
            if (Pending || EditorApplication.isCompiling || EditorUtility.scriptCompilationFailed) return;
            var r = new ApplyResult();
            try { ReserializeWithOwed(new List<string>(), r); }
            catch (Exception e)
            {
                Debug.LogException(e);
                return;
            }
            if (r.reserialized.Count > 0)
                Debug.Log("[Module Organizer] Re-serialized assets an earlier Apply still owed: " + string.Join(", ", r.reserialized));
            foreach (var w in r.warnings) Debug.LogWarning("[Module Organizer] " + w);
        }

        // MovedFrom makes Unity load the old names; re-serializing writes the new ns/asm so the YAML matches before the next move.
        // Every text-serialized asset under Assets/ (any extension: .prefab, .controller, .playable...), found by its header and
        // streamed line by line (ScanYaml). Returns the keys still owed (StillOwed).
        static List<string> Reserialize(List<string> keys, ApplyResult r)
        {
            if (keys.Count == 0) return new List<string>();
            var parsed = keys.Select(k => ParseKeys(k).DefaultIfEmpty((null, null, null)).First()).ToList();
            if (EditorSettings.serializationMode != SerializationMode.ForceText)
                r.warnings.Add("Project uses binary/mixed asset serialization; binary assets referencing moved [SerializeReference] types were not " +
                               "re-serialized. Switch Project Settings > Editor > Asset Serialization to Force Text, or re-save those assets " +
                               "before moving the types again.");
            var outcomes = new List<(bool read, bool failed, ICollection<string> candidates)>();
            var hits = new List<(string path, ICollection<string> candidates)>();
            var head = new byte[5];
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(p)) continue; // folders
                try
                {
                    using var s = File.OpenRead(p);
                    if (s.Read(head, 0, head.Length) < head.Length || !IsYamlHeader(head)) continue;
                    s.Position = 0;
                    using var reader = new StreamReader(s);
                    var (candidates, matched) = ScanYaml(reader, parsed);
                    var named = candidates.Select(i => keys[i]).ToList();
                    if (matched) hits.Add((p, named));
                    else outcomes.Add((true, false, named));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    outcomes.Add((false, false, keys));
                    r.warnings.Add($"Could not read {p} to check it for moved [SerializeReference] types ({e.Message}); the asset migration " +
                                   "stays owed and Apply stays blocked until it can be read.");
                }
            }
            var failed = false;
            if (hits.Count > 0)
                try
                {
                    AssetDatabase.ForceReserializeAssets(hits.Select(h => h.path).ToList());
                    r.reserialized.AddRange(hits.Select(h => h.path));
                }
                catch (Exception e)
                {
                    failed = true;
                    r.warnings.Add($"Re-serializing {string.Join(", ", hits.Select(h => h.path))} failed ({e.Message}); the asset migration stays owed.");
                }
            outcomes.AddRange(hits.Select(h => (true, failed, h.candidates)));
            return StillOwed(keys, outcomes);
        }

        // A key stays owed while some file that might hold it (its class name passed the pre-filter, or the file could not be
        // read at all) was not read or failed to re-serialize. Every other key is settled.
        public static List<string> StillOwed(IEnumerable<string> keys, IEnumerable<(bool read, bool failed, ICollection<string> candidates)> files)
        {
            var list = files.ToList();
            return keys.Where(k => list.Any(f => !f.read || (f.failed && f.candidates.Contains(k)))).ToList();
        }

        // Streams a YAML asset: indexes of keys whose class name appears on some line (candidates), and whether a managed-
        // reference type block names a key's old location (regex on the candidate line plus the next 2, so a wrapped block
        // still matches). Stops early once every key has matched.
        public static (List<int> candidates, bool matched) ScanYaml(TextReader reader, IList<(string ns, string asm, string cls)> keys)
        {
            var seen = new HashSet<int>();
            var matched = new HashSet<int>();
            var window = new List<string>(3);
            var eof = false;
            while (matched.Count < keys.Count)
            {
                while (!eof && window.Count < 3)
                {
                    var line = reader.ReadLine();
                    if (line == null) eof = true;
                    else window.Add(line);
                }
                if (window.Count == 0) break;
                string joined = null;
                for (var k = 0; k < keys.Count; k++)
                {
                    if (matched.Contains(k) || string.IsNullOrEmpty(keys[k].cls) || !window[0].Contains(keys[k].cls)) continue;
                    seen.Add(k);
                    joined ??= string.Join("\n", window);
                    if (ContainsManagedRefType(joined, keys[k].ns, keys[k].asm, keys[k].cls)) matched.Add(k);
                }
                window.RemoveAt(0);
            }
            return (seen.OrderBy(i => i).ToList(), matched.Count > 0);
        }

        // Unity text-serialized assets start with "%YAML".
        public static bool IsYamlHeader(byte[] head) =>
            head != null && head.Length >= 5 && head[0] == '%' && head[1] == 'Y' && head[2] == 'A' && head[3] == 'M' && head[4] == 'L';

        static void CompilePlayer(ApplyResult r)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var settings = new ScriptCompilationSettings { target = target, group = BuildPipeline.GetBuildTargetGroup(target) };
            Application.LogCallback capture = (msg, _, type) =>
            {
                if (type == LogType.Error && (msg.Contains("error CS") || CompilerLine.IsMatch(msg))) r.playerErrors.Add(msg);
            };
            Application.logMessageReceived += capture;
            try
            {
                var res = PlayerBuildInterface.CompilePlayerScripts(settings, PlayerOutput);
                if (r.playerErrors.Count == 0 && (res.assemblies == null || res.assemblies.Count == 0))
                    r.playerErrors.Add($"Player script compilation for {target} produced no assemblies; check the Console.");
                r.playerCompileOk = r.playerErrors.Count == 0;
            }
            finally
            {
                Application.logMessageReceived -= capture;
                if (Directory.Exists(PlayerOutput)) Directory.Delete(PlayerOutput, true);
            }
        }

        // Matches a managed-reference type block naming the OLD location exactly: type: {class: C, ns: N, asm: A} (ns empty = global).
        public static bool ContainsManagedRefType(string yaml, string oldNs, string oldAsm, string className) =>
            Regex.IsMatch(yaml, @"type:\s*\{\s*class:\s*" + Regex.Escape(className) + @"\s*,\s*ns:\s*" + Regex.Escape(oldNs ?? "") +
                                @"\s*,\s*asm:\s*" + Regex.Escape(oldAsm) + @"\s*\}");

        // "oldNs|oldAsm|className" per line; malformed lines are skipped.
        public static List<(string ns, string asm, string cls)> ParseKeys(string joined) =>
            (joined ?? "").Split('\n').Select(l => l.Split('|')).Where(p => p.Length == 3 && p[1].Length > 0 && p[2].Length > 0)
                .Select(p => (p[0], p[1], p[2])).ToList();
    }
}
