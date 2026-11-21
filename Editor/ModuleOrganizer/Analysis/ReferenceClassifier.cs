namespace Monome.UtilityPack.ModuleOrganizer
{
    public enum RefClass { SameModule, OtherModule, UnassignedInScope, OutOfScope, OtherAsmdef, AutoDll, NonAutoDll, Ignored }

    public static class ReferenceClassifier
    {
        // Where `target` will live relative to `src` under the draft config (spec 5.2).
        public static RefClass Classify(ScriptFile src, TypeRecord target, ModuleConfig cfg, ScanResult scan)
        {
            if (target.kind == AssemblyKind.Engine) return RefClass.Ignored;
            if (target.kind == AssemblyKind.Precompiled) return target.autoReferencedDll ? RefClass.AutoDll : RefClass.NonAutoDll;

            // ponytail: FileByPath and FindModuleOf are linear scans; index them if Validate gets slow on big projects.
            var file = target.filePaths.Count > 0 ? scan.FileByPath(target.filePaths[0]) : null;
            var module = file != null ? cfg.FindModuleOf(file.guid) : null;
            if (module != null) return module == cfg.FindModuleOf(src.guid) ? RefClass.SameModule : RefClass.OtherModule;

            var toolAsmdef = ModuleValidator.OwnsAsmdef(cfg, target.asmdefGuid);
            if (target.kind == AssemblyKind.AsmDef && !toolAsmdef) return RefClass.OtherAsmdef;
            // A tool asmdef file that is no longer assigned goes back to Assembly-CSharp on Apply.
            if (file != null && (toolAsmdef || ((target.kind is AssemblyKind.Runtime or AssemblyKind.Editor) && cfg.IsInScope(file.path))))
                return RefClass.UnassignedInScope;
            return RefClass.OutOfScope; // incl. FirstPass / FirstPassEditor
        }
    }
}
