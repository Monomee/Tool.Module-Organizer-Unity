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
        public bool serializableNonUnityObject;
    }

    public class ScriptFile
    {
        public string guid;
        public string path;
        public AssemblyKind kind;
        public string asmdefGuid; // kind == AsmDef only
        public bool editorOnlyAssembly; // AsmDef compiled only for the Editor (e.g. a module's {ns}.Editor asmdef)
        public bool IsEditor => kind == AssemblyKind.Editor || kind == AssemblyKind.FirstPassEditor || editorOnlyAssembly;
        public List<string> declaredTypes = new List<string>();
        public HashSet<string> referencedTypes = new HashSet<string>();
    }

    public class ScanResult
    {
        public Dictionary<string, ScriptFile> files = new Dictionary<string, ScriptFile>();
        public Dictionary<string, TypeRecord> types = new Dictionary<string, TypeRecord>();

        public ScriptFile FileByPath(string path)
        {
            path = PathRules.Norm(path);
            return files.Values.FirstOrDefault(f => f.path == path);
        }
    }
}
