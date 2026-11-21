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
        const int MaxReloads = 3;
        const string PlayerOutput = "Temp/MonomePlayerCompile";

        static readonly List<string> EditorErrors = new List<string>(); // current domain's last compile
        static readonly Regex CompilerLine = new Regex(@"^\S+\.cs\(\d+");

        public static event Action ResultReady;

        public static bool Pending => SessionState.GetBool(PendingKey, false);

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
            if (!Pending) return;
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

        // ponytail: an Apply that triggers no compile (nothing script-related changed) resolves only at the next reload;
        // detect "no compile requested" after Refresh if that ever shows up in practice.
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
                Guard(() => Reserialize(ParseKeys(SessionState.GetString(KeysKey, "")), r), "Re-serializing assets", r);
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

        // MovedFrom makes Unity load the old names; re-serializing writes the new ns/asm so the YAML matches before the next move.
        // Every text-serialized asset under Assets/ (any extension: .prefab, .controller, .playable...), found by its header.
        static void Reserialize(List<(string ns, string asm, string cls)> keys, ApplyResult r)
        {
            if (keys.Count == 0) return;
            if (EditorSettings.serializationMode != SerializationMode.ForceText)
                r.warnings.Add("Project uses binary/mixed asset serialization; binary assets referencing moved [SerializeReference] types were not " +
                               "re-serialized. Switch Project Settings > Editor > Asset Serialization to Force Text, or re-save those assets " +
                               "before moving the types again.");
            var head = new byte[5];
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(p)) continue; // folders
                try
                {
                    using (var s = File.OpenRead(p))
                        if (s.Read(head, 0, head.Length) < head.Length || !IsYamlHeader(head)) continue;
                    var text = File.ReadAllText(p);
                    if (keys.Any(k => text.Contains(k.cls) && ContainsManagedRefType(text, k.ns, k.asm, k.cls))) r.reserialized.Add(p);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    r.warnings.Add($"Could not read {p} to check it for moved [SerializeReference] types: {e.Message}");
                }
            }
            if (r.reserialized.Count > 0) AssetDatabase.ForceReserializeAssets(r.reserialized);
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
