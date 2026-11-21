using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using UnityEditor;
using UnityEditor.Compilation;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public static class DependencyScanner
    {
        // Reads the compiled script assemblies with Cecil and maps types <-> .cs files and file -> referenced types.
        // File-level analysis only for assemblies with sources under Assets/; other script assemblies only register their types.
        public static ScanResult Scan(string scriptAssembliesDir = "Library/ScriptAssemblies")
        {
            var result = new ScanResult();
            var root = PathRules.Norm(Path.GetFullPath(".")) + "/";
            var asms = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            var kinds = asms.ToDictionary(a => a.name, a => KindOf(a.name));
            var precompiled = CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly)
                .GroupBy(p => Path.GetFileNameWithoutExtension(p)).ToDictionary(g => g.Key, g => AutoReferenced(g.First()));

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(scriptAssembliesDir);
            foreach (var dir in asms.SelectMany(a => a.compiledAssemblyReferences)
                         .Concat(CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.All))
                         .Select(p => Path.GetDirectoryName(p)).Where(d => !string.IsNullOrEmpty(d)).Distinct())
                resolver.AddSearchDirectory(dir);

            var loaded = new List<(Assembly asm, AssemblyDefinition def)>();
            try
            {
                foreach (var a in asms)
                {
                    var dll = Path.Combine(scriptAssembliesDir, a.name + ".dll");
                    if (File.Exists(dll)) loaded.Add((a, Read(dll, resolver)));
                    else UnityEngine.Debug.LogWarning($"[ModuleOrganizer] Scan skipped {a.name}: {dll} not found.");
                }

                // Pass 1: every type declared by a script assembly.
                foreach (var (a, def) in loaded)
                {
                    var kind = kinds[a.name];
                    var asmdefGuid = kind == AssemblyKind.AsmDef
                        ? AssetDatabase.AssetPathToGUID(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(a.name))
                        : null;
                    foreach (var t in def.MainModule.GetTypes())
                    {
                        if (t.FullName.Contains('<')) continue;
                        result.types[t.FullName] = new TypeRecord
                        {
                            fullName = t.FullName, ns = Outermost(t).Namespace, assemblyName = a.name, kind = kind,
                            asmdefGuid = asmdefGuid, serializableNonUnityObject = t.IsSerializable && !IsUnityObject(t),
                        };
                    }
                }

                // Pass 2: files, type -> file mapping, references.
                foreach (var (a, def) in loaded)
                {
                    if (!a.sourceFiles.Any(p => p.StartsWith("Assets/", StringComparison.Ordinal))) continue;
                    var files = new Dictionary<string, ScriptFile>();
                    var declared = new List<(TypeRecord rec, string src, bool partial)>();
                    foreach (var src in a.sourceFiles.Select(PathRules.Norm))
                    {
                        if (!File.Exists(src)) continue; // deleted since the last compile
                        var f = files[src] = new ScriptFile { guid = AssetDatabase.AssetPathToGUID(src), path = src, kind = kinds[a.name] };
                        result.files[f.guid] = f;
                        foreach (var d in DeclarationFinder.Find(File.ReadAllText(src)))
                            if (Own(d.Key, out var rec)) declared.Add((rec, src, d.Value.StartsWith("partial ", StringComparison.Ordinal)));
                    }

                    var types = def.MainModule.GetTypes().ToList();
                    foreach (var t in types)
                    foreach (var m in t.Methods)
                    foreach (var sp in m.DebugInformation.SequencePoints)
                    {
                        var path = PathRules.Norm(sp.Document.Url);
                        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) path = path.Substring(root.Length);
                        if (!files.ContainsKey(path)) continue;
                        if (Own(Outermost(t).FullName, out var outer)) AddPath(outer, path);
                        if (Own(t.FullName, out var rec)) AddPath(rec, path);
                    }

                    // The PDB is the authority. Source declarations only place types without sequence points
                    // (enums, interfaces, delegates, field-only types) and partial parts (a field-only part has no
                    // sequence points), so a non-partial copy inside an inactive #if adds no file.
                    foreach (var (rec, src, _) in declared.Where(d => d.partial || d.rec.filePaths.Count == 0).ToList()) AddPath(rec, src);

                    foreach (var rec in result.types.Values.Where(r => r.assemblyName == a.name))
                    foreach (var p in rec.filePaths)
                        files[p].declaredTypes.Add(rec.fullName);

                    // References of nested/compiler-generated types fold into the outermost type's files.
                    foreach (var t in types)
                    {
                        if (!Own(Outermost(t).FullName, out var outer) || outer.filePaths.Count == 0) continue;
                        var refs = new HashSet<string>();
                        Collect(t, refs);
                        foreach (var p in outer.filePaths) files[p].referencedTypes.UnionWith(refs);
                    }
                    foreach (var f in files.Values) f.referencedTypes.ExceptWith(f.declaredTypes);

                    bool Own(string name, out TypeRecord rec) =>
                        result.types.TryGetValue(name, out rec) && rec.assemblyName == a.name;
                }
            }
            finally
            {
                foreach (var (_, def) in loaded) def.Dispose();
                resolver.Dispose();
            }
            return result;

            void Collect(TypeDefinition t, HashSet<string> refs)
            {
                Add(t.BaseType);
                foreach (var i in t.Interfaces) Add(i.InterfaceType);
                Attrs(t);
                GenericParams(t);
                foreach (var f in t.Fields) { Add(f.FieldType); Attrs(f); }
                foreach (var p in t.Properties) { Add(p.PropertyType); Attrs(p); }
                foreach (var e in t.Events) { Add(e.EventType); Attrs(e); }
                foreach (var m in t.Methods)
                {
                    Signature(m);
                    Attrs(m);
                    Attrs(m.MethodReturnType);
                    foreach (var p in m.Parameters) Attrs(p);
                    GenericParams(m);
                    if (!m.HasBody) continue;
                    foreach (var v in m.Body.Variables) Add(v.VariableType);
                    foreach (var h in m.Body.ExceptionHandlers) Add(h.CatchType);
                    foreach (var ins in m.Body.Instructions)
                    {
                        switch (ins.Operand)
                        {
                            case TypeReference tr: Add(tr); break;
                            case MethodReference mr: Signature(mr); break;
                            case FieldReference fr: Add(fr.DeclaringType); Add(fr.FieldType); break;
                        }
                    }
                }

                void Signature(MethodReference mr)
                {
                    Add(mr.DeclaringType);
                    Add(mr.ReturnType);
                    foreach (var p in mr.Parameters) Add(p.ParameterType);
                    if (mr is GenericInstanceMethod gim) foreach (var g in gim.GenericArguments) Add(g);
                }

                void GenericParams(IGenericParameterProvider g)
                {
                    foreach (var gp in g.GenericParameters)
                    {
                        Attrs(gp);
                        foreach (var c in gp.Constraints) Add(c.ConstraintType);
                    }
                }

                void Attrs(ICustomAttributeProvider p)
                {
                    if (!p.HasCustomAttributes) return;
                    foreach (var ca in p.CustomAttributes)
                    {
                        Add(ca.AttributeType);
                        try
                        {
                            // Decoding the blob may need to resolve enum types from other assemblies.
                            foreach (var arg in ca.ConstructorArguments) Arg(arg);
                            foreach (var na in ca.Properties.Concat(ca.Fields)) Arg(na.Argument);
                        }
                        catch (AssemblyResolutionException) { }
                    }
                }

                void Arg(CustomAttributeArgument arg)
                {
                    Add(arg.Type);
                    if (arg.Value is TypeReference tr) Add(tr); // typeof(X)
                    else if (arg.Value is CustomAttributeArgument boxed) Arg(boxed);
                    else if (arg.Value is CustomAttributeArgument[] arr) foreach (var x in arr) Arg(x);
                }

                void Add(TypeReference tr)
                {
                    while (tr is TypeSpecification spec) // arrays, by-ref, pointers, modifiers, generic instances
                    {
                        if (tr is GenericInstanceType git) foreach (var g in git.GenericArguments) Add(g);
                        if (tr is IModifierType mod) Add(mod.ModifierType);
                        tr = spec.ElementType;
                    }
                    if (tr == null || tr is GenericParameter) return;
                    var name = tr.FullName;
                    if (name.Contains('<') || !refs.Add(name) || result.types.ContainsKey(name)) return;
                    var outer = Outermost(tr);
                    var asmName = outer.Scope is ModuleDefinition md ? md.Assembly.Name.Name : outer.Scope?.Name;
                    var rec = result.types[name] = new TypeRecord
                    {
                        fullName = name, ns = outer.Namespace, assemblyName = asmName, kind = AssemblyKind.Engine,
                    };
                    if (asmName == null) return;
                    if (kinds.TryGetValue(asmName, out var k)) rec.kind = k;
                    else if (precompiled.TryGetValue(asmName, out var auto))
                    {
                        rec.kind = AssemblyKind.Precompiled;
                        rec.autoReferencedDll = auto;
                    }
                }
            }
        }

        static AssemblyKind KindOf(string assemblyName)
        {
            switch (assemblyName)
            {
                case "Assembly-CSharp": return AssemblyKind.Runtime;
                case "Assembly-CSharp-Editor": return AssemblyKind.Editor;
                case "Assembly-CSharp-firstpass": return AssemblyKind.FirstPass;
                case "Assembly-CSharp-Editor-firstpass": return AssemblyKind.FirstPassEditor;
                default: return AssemblyKind.AsmDef;
            }
        }

        // PluginImporter.IsExplicitlyReferenced is internal in 6000.3, so read the importer's .meta instead.
        static bool AutoReferenced(string dllPath)
        {
            var meta = dllPath + ".meta";
            return !File.Exists(meta) || !File.ReadAllText(meta).Contains("isExplicitlyReferenced: 1");
        }

        // Loads DLL and PDB into memory so no file handle stays open on files Unity rewrites.
        static AssemblyDefinition Read(string dll, IAssemblyResolver resolver)
        {
            var bytes = File.ReadAllBytes(dll);
            var pdb = Path.ChangeExtension(dll, ".pdb");
            var why = "no .pdb";
            if (File.Exists(pdb))
            {
                try
                {
                    return AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters
                    {
                        AssemblyResolver = resolver, ReadSymbols = true, SymbolStream = new MemoryStream(File.ReadAllBytes(pdb)),
                    });
                }
                catch (Exception e) when (e is Mono.Cecil.Cil.SymbolsNotMatchingException || e is Mono.Cecil.Cil.SymbolsNotFoundException)
                {
                    why = e.Message;
                }
            }
            UnityEngine.Debug.LogWarning($"[ModuleOrganizer] No symbols for {Path.GetFileName(dll)} ({why}); file mapping falls back to source declarations.");
            return AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver });
        }

        static TypeReference Outermost(TypeReference t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t;
        }

        static TypeDefinition Outermost(TypeDefinition t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t;
        }

        static bool IsUnityObject(TypeDefinition t)
        {
            for (var b = t.BaseType; b != null;)
            {
                if (b.FullName == "UnityEngine.Object") return true;
                try { b = b.Resolve()?.BaseType; }
                catch (AssemblyResolutionException) { return false; }
            }
            return false;
        }

        static void AddPath(TypeRecord rec, string path)
        {
            if (!rec.filePaths.Contains(path)) rec.filePaths.Add(path);
        }
    }
}
