using System;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public enum RefClass { SameModule, OtherModule, UnassignedInScope, OutOfScope, OtherAsmdef, AutoDll, NonAutoDll, Ignored }

    public static class ReferenceClassifier
    {
        // Where `target` will live relative to `src` under the draft config (spec 5.2).
        public static RefClass Classify(ScriptFile src, TypeRecord target, ModuleConfig cfg, ScanResult scan) =>
            Classify(target, cfg, scan.FileByPath, cfg.FindModuleOf, cfg.FindModuleOf(src.guid));

        // Same, with indexed lookups (path -> file, guid -> module) supplied by the caller.
        internal static RefClass Classify(TypeRecord target, ModuleConfig cfg, Func<string, ScriptFile> fileAt,
            Func<string, Module> moduleOf, Module srcModule)
        {
            if (target.kind == AssemblyKind.Engine) return RefClass.Ignored;
            if (target.kind == AssemblyKind.Precompiled) return target.autoReferencedDll ? RefClass.AutoDll : RefClass.NonAutoDll;

            var file = target.filePaths.Count > 0 ? fileAt(target.filePaths[0]) : null;
            var module = file != null ? moduleOf(file.guid) : null;
            if (module != null) return module == srcModule ? RefClass.SameModule : RefClass.OtherModule;

            var toolAsmdef = ModuleValidator.OwnsAsmdef(cfg, target.asmdefGuid);
            if (target.kind == AssemblyKind.AsmDef && !toolAsmdef) return RefClass.OtherAsmdef;
            // A tool asmdef file that is no longer assigned (pending removal) goes back to Assembly-CSharp on Apply.
            if (file != null && (toolAsmdef || ((target.kind is AssemblyKind.Runtime or AssemblyKind.Editor) && cfg.IsInScope(file.path))))
                return RefClass.UnassignedInScope;
            return RefClass.OutOfScope; // incl. FirstPass / FirstPassEditor
        }
    }
}
