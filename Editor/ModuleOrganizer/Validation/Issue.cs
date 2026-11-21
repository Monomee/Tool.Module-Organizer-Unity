using System.Collections.Generic;
using System.Linq;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public enum Severity { Info, Warning, Error }

    public enum IssueCode
    {
        Cycle, OutOfScopeRef, UnassignedInScopeRef, NonAutoDll, PartialSplit, RuntimeToEditor, NameClash,
        ReverseRefOutOfScope, RestoreConflict, QualifiedName, MultiNamespace, AssemblyQualifiedString, DuplicateAssignment, PathConflict,
    }

    public class Issue
    {
        public Severity severity;
        public IssueCode code;
        public string path;
        public string message;

        public override string ToString() => $"{severity} {code} {path}: {message}";
    }

    // Node = module name, or "{name}.Editor" for the module's Editor assembly.
    public class ModuleEdge
    {
        public string from;
        public string to;
        public List<string> causedBy = new List<string>();
    }

    public class ValidationReport
    {
        public List<Issue> issues = new List<Issue>();
        public List<ModuleEdge> edges = new List<ModuleEdge>();
        public List<List<string>> cycles = new List<List<string>>();
        public Dictionary<string, HashSet<string>> asmdefRefs = new Dictionary<string, HashSet<string>>(); // node -> outside asmdef GUIDs (assembly name when the GUID is unknown)
        public bool CanApply => !issues.Any(i => i.severity == Severity.Error);
    }
}
