using System.Collections.Generic;
using System.Linq;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public enum AssemblyKind { Runtime, Editor, FirstPass, FirstPassEditor, AsmDef, Precompiled, Engine }

    public class TypeRecord
    {
        public string fullName;
        public string ns;
        public string assemblyName;
        public AssemblyKind kind;
        public List<string> filePaths = new List<string>();
        public string asmdefGuid;
        public bool autoReferencedDll;
        public bool notAutoReferenced; // AsmDef with "autoReferenced": false: Assembly-CSharp(-Editor) does not see it
        public bool editorOnly; // AsmDef compiled only for the Editor
        public bool serializableNonUnityObject;
    }

    public class ScriptFile
    {
        public string guid;
        public string path;
        public AssemblyKind kind;
        public string asmdefGuid; // kind == AsmDef only
        public string assemblyName; // set by the scanner; null in hand-made scans (FileFates.CurrentAssembly falls back)
        public bool editorOnlyAssembly; // AsmDef compiled only for the Editor (e.g. a module's {ns}.Editor asmdef)
        public bool IsEditor => kind == AssemblyKind.Editor || kind == AssemblyKind.FirstPassEditor || editorOnlyAssembly;
        public List<string> declaredTypes = new List<string>();
        public HashSet<string> referencedTypes = new HashSet<string>();
        // Script-assembly types (outermost full names) this file reaches through something not public: an internal type,
        // or an internal / private protected member. Fine inside one assembly only.
        public HashSet<string> internalRefs = new HashSet<string>();
    }

    public class ScanResult
    {
        public Dictionary<string, ScriptFile> files = new Dictionary<string, ScriptFile>();
        public Dictionary<string, TypeRecord> types = new Dictionary<string, TypeRecord>();
        // Top-level full names declared by more than one script assembly -> every file declaring them. types keeps one record.
        public Dictionary<string, List<string>> duplicateTypes = new Dictionary<string, List<string>>();
        // Every script and precompiled assembly name -> its asmdef GUID ("" when it has none).
        public Dictionary<string, string> assemblies = new Dictionary<string, string>();
        // Assembly name -> the assembly names its [InternalsVisibleTo] grants: every script assembly, plus each other
        // assembly a file reaches something non-public in.
        public Dictionary<string, HashSet<string>> internalsVisibleTo = new Dictionary<string, HashSet<string>>();
        // Public top-level full names declared by two or more assemblies (script assemblies and user DLLs) -> one record per
        // declaration. The record types keeps is among them. A file seeing two of them gets CS0433.
        public Dictionary<string, List<TypeRecord>> publicDeclarers = new Dictionary<string, List<TypeRecord>>();

        public ScriptFile FileByPath(string path)
        {
            path = PathRules.Norm(path);
            return files.Values.FirstOrDefault(f => f.path == path);
        }
    }
}
