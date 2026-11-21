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
        // Subfolder inside the module, fixed when the entry is created (PathRules.NewEntry) so later config folder
        // renames keep it. Null on older entries: derived from originalPath instead.
        public string relativeDir;
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
        public List<string> extraReferences = new List<string>(); // runtime and Editor asmdefs
        public List<string> editorExtraReferences = new List<string>(); // Editor asmdef only
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
        // originalPath's directory under their current file name / originalNamespace (back in Assembly-CSharp(-Editor)).
        public List<ScriptEntry> pendingRemovals = new List<ScriptEntry>();
        // Runtime and Editor asmdef GUIDs of deleted modules; still tool-owned until Apply removes them.
        public List<string> retiredAsmdefGuids = new List<string>();
        // "oldNs|oldAsm|className" of [MovedFrom]s an Apply wrote whose assets are not re-serialized yet (the compile failed,
        // or the Editor closed before the reload). ApplyResultWatcher clears them; Apply is blocked while any are owed.
        public List<string> owedReserializeKeys = new List<string>();

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

        // Strictly below folder. Ignoring case: Unity asset paths are case-insensitive on Windows and macOS.
        public static bool IsUnder(string path, string folder) =>
            Norm(path).StartsWith(Norm(folder) + "/", StringComparison.OrdinalIgnoreCase);

        // The folder itself or below it: the write boundary (planner, Sync) and module-folder checks.
        public static bool IsInside(string path, string folder) =>
            string.Equals(Norm(path), Norm(folder), StringComparison.OrdinalIgnoreCase) || IsUnder(path, folder);

        public static string ScopeRelativeDir(string originalPath, IReadOnlyList<string> scopeFolders)
        {
            var path = Norm(originalPath);
            var scope = scopeFolders.Where(f => IsUnder(path, f)).OrderByDescending(f => Norm(f).Length).FirstOrDefault();
            return scope == null ? "" : DirUnder(path, Norm(scope));
        }

        // Subfolder a file keeps inside its module: relative to the module folder when it already lives there
        // (no doubled "Combat/Combat"), else relative to its scope folder. Editor segments stripped.
        public static string RelativeDir(Module m, string originalPath, IReadOnlyList<string> scopeFolders)
        {
            var path = Norm(originalPath);
            return IsUnder(path, m.folder) ? DirUnder(path, Norm(m.folder)) : ScopeRelativeDir(path, scopeFolders);
        }

        public static string EntryRelativeDir(Module m, ScriptEntry e, IReadOnlyList<string> scopeFolders) =>
            e.relativeDir ?? RelativeDir(m, e.originalPath, scopeFolders);

        // The one way to assign a file: records where it is now and its subfolder inside m.
        public static ScriptEntry NewEntry(Module m, string guid, string currentPath, string currentNamespace, IReadOnlyList<string> scopeFolders) =>
            new ScriptEntry
            {
                guid = guid, originalPath = Norm(currentPath), originalNamespace = currentNamespace,
                relativeDir = RelativeDir(m, currentPath, scopeFolders),
            };

        static string DirUnder(string path, string root)
        {
            var dir = path.Substring(0, Math.Max(path.LastIndexOf('/'), 0));
            var rel = dir.Length > root.Length ? dir.Substring(root.Length + 1) : "";
            return string.Join("/", rel.Split('/').Where(s => s.Length > 0 && s != "Editor"));
        }

        // The file name always comes from the file's current path: a rename by the user is never undone.
        // originalPath only decides the directory (via relativeDir) and the restore target.
        public static string TargetPath(Module m, ScriptEntry e, string currentPath, bool isEditor, IReadOnlyList<string> scopeFolders)
        {
            var parts = new List<string> { Norm(m.folder) };
            if (isEditor) parts.Add("Editor");
            parts.Add(EntryRelativeDir(m, e, scopeFolders));
            parts.Add(FileName(currentPath ?? e.originalPath));
            return string.Join("/", parts.Where(s => s.Length > 0));
        }

        // Where an unassigned file goes back to: its original directory, under its current name.
        public static string RestorePath(ScriptEntry e, string currentPath)
        {
            var orig = Norm(e.originalPath);
            var dir = orig.Contains('/') ? orig.Substring(0, orig.LastIndexOf('/')) : "";
            var name = FileName(currentPath ?? orig);
            return dir.Length == 0 ? name : dir + "/" + name;
        }

        static string FileName(string path) => Norm(path).Split('/').Last();

        // Assembly (and asmdef file) name of a module's runtime or Editor asmdef.
        public static string AsmdefName(Module m, bool editor) => m.namespaceName + (editor ? ".Editor" : "");

        // Tool layout: {folder}/X.asmdef or {folder}/Editor/X.Editor.asmdef.
        public static string AsmdefPath(Module m, bool editor) => Norm(m.folder) + (editor ? "/Editor/" : "/") + AsmdefName(m, editor) + ".asmdef";

        public static string TargetNamespace(Module m, string relativeDir, bool isEditor)
        {
            var ns = m.namespaceName + (isEditor ? ".Editor" : "");
            return string.IsNullOrEmpty(relativeDir) ? ns : ns + "." + string.Join(".", relativeDir.Split('/').Select(Identifier));
        }

        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
            "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
            "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while",
        };

        // A folder name as a namespace segment, the way VS/Rider do it: invalid chars -> '_', and a '_' prefix when it
        // does not start with a letter or '_' (a digit) or is a C# keyword.
        public static string Identifier(string segment)
        {
            var s = new string((segment ?? "").Select(c => IsIdentChar(c) ? c : '_').ToArray());
            return s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_') || Keywords.Contains(s) ? "_" + s : s;
        }

        // Dot-separated identifiers, none a keyword.
        public static bool IsValidNamespace(string ns) => !string.IsNullOrEmpty(ns) && ns.Split('.').All(s => s.Length > 0 && Identifier(s) == s);

        static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || char.GetUnicodeCategory(c) is
            System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or
            System.Globalization.UnicodeCategory.ConnectorPunctuation or System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.LetterNumber;
    }
}
