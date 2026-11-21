using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer
{
    [Serializable]
    public class ScriptEntry
    {
        public string guid;
        public string originalPath;
        public string originalNamespace;
        public bool applied;
    }

    [Serializable]
    public class Module
    {
        public string name;
        public string namespaceName;
        public string folder;
        public Color color = Color.white;
        public string description;
        public bool rewriteNamespace = true;
        public List<ScriptEntry> scripts = new List<ScriptEntry>();
        public List<string> extraReferences = new List<string>();
        public string asmdefGuid;
        public string editorAsmdefGuid;
    }

    public class ModuleConfig : ScriptableObject
    {
        public string rootNamespace = "Game";
        public List<string> scopeFolders = new List<string>();
        public string modulesRoot;
        public List<Module> modules = new List<Module>();
        // Applied entries the user unassigned, or that belonged to a deleted module: Apply restores them to
        // originalPath / originalNamespace (back in Assembly-CSharp(-Editor)).
        public List<ScriptEntry> pendingRemovals = new List<ScriptEntry>();
        // Runtime and Editor asmdef GUIDs of deleted modules; still tool-owned until Apply removes them.
        public List<string> retiredAsmdefGuids = new List<string>();

        public string EffectiveModulesRoot =>
            !string.IsNullOrEmpty(modulesRoot) ? modulesRoot : (scopeFolders.Count > 0 ? scopeFolders[0] : "Assets");

        public Module AddModule(string name)
        {
            var m = new Module
            {
                name = name,
                namespaceName = rootNamespace + "." + name,
                folder = PathRules.Norm(EffectiveModulesRoot) + "/" + name,
            };
            modules.Add(m);
            return m;
        }

        public Module FindModuleOf(string scriptGuid) =>
            modules.FirstOrDefault(m => m.scripts.Any(s => s.guid == scriptGuid));

        public bool IsInScope(string assetPath) => scopeFolders.Any(f => PathRules.IsUnder(assetPath, f));
    }

    public static class PathRules
    {
        internal static string Norm(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/');

        public static bool IsUnder(string path, string folder)
        {
            path = Norm(path);
            folder = Norm(folder);
            return path.StartsWith(folder + "/", StringComparison.Ordinal);
        }

        public static string ScopeRelativeDir(string originalPath, IReadOnlyList<string> scopeFolders)
        {
            var path = Norm(originalPath);
            var scope = scopeFolders.Where(f => IsUnder(path, f)).OrderByDescending(f => Norm(f).Length).FirstOrDefault();
            if (scope == null) return "";
            var dir = path.Substring(0, Math.Max(path.LastIndexOf('/'), 0));
            var rel = dir.Length > Norm(scope).Length ? dir.Substring(Norm(scope).Length + 1) : "";
            return string.Join("/", rel.Split('/').Where(s => s.Length > 0 && s != "Editor"));
        }

        public static string TargetPath(Module m, ScriptEntry e, bool isEditor, IReadOnlyList<string> scopeFolders)
        {
            var parts = new List<string> { Norm(m.folder) };
            if (isEditor) parts.Add("Editor");
            parts.Add(ScopeRelativeDir(e.originalPath, scopeFolders));
            parts.Add(Norm(e.originalPath).Split('/').Last());
            return string.Join("/", parts.Where(s => s.Length > 0));
        }

        // Assembly (and asmdef file) name of a module's runtime or Editor asmdef.
        public static string AsmdefName(Module m, bool editor) => m.namespaceName + (editor ? ".Editor" : "");

        public static string TargetNamespace(Module m, string relativeDir, bool isEditor)
        {
            var ns = m.namespaceName + (isEditor ? ".Editor" : "");
            return string.IsNullOrEmpty(relativeDir) ? ns : ns + "." + relativeDir.Replace('/', '.');
        }
    }
}
