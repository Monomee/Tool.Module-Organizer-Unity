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
                Reserialize(ParseKeys(SessionState.GetString(KeysKey, "")), r);
                CompilePlayer(r);
            }
            catch (Exception e)
            {
                r.warnings.Add("Post-Apply check failed: " + e.Message);
                Debug.LogException(e);
            }
            finally
            {
                Finish(r);
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
        static void Reserialize(List<(string ns, string asm, string cls)> keys, ApplyResult r)
        {
            if (keys.Count == 0) return;
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext != ".prefab" && ext != ".unity" && ext != ".asset") continue;
                // ponytail: reads whole files to check the header; peek the first bytes if big binary assets make this slow.
                var text = File.ReadAllText(p);
                if (!text.StartsWith("%YAML", StringComparison.Ordinal)) continue;
                if (keys.Any(k => text.Contains(k.cls) && ContainsManagedRefType(text, k.ns, k.asm, k.cls))) r.reserialized.Add(p);
            }
            if (r.reserialized.Count > 0) AssetDatabase.ForceReserializeAssets(r.reserialized);
        }

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
