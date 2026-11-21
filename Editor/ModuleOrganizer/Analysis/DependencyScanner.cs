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
            var userDlls = CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly)
                .GroupBy(p => Path.GetFileNameWithoutExtension(p)).ToDictionary(g => g.Key, g => g.First());
            var precompiled = userDlls.ToDictionary(kv => kv.Key, kv => DllInfo(kv.Value));
            var declarers = new Dictionary<string, List<TypeRecord>>();

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(scriptAssembliesDir);
            foreach (var dir in asms.SelectMany(a => a.compiledAssemblyReferences)
                         .Concat(CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.All))
                         .Select(p => Path.GetDirectoryName(p)).Where(d => !string.IsNullOrEmpty(d)).Distinct())
                resolver.AddSearchDirectory(dir);

            var loaded = new List<(Assembly asm, AssemblyDefinition def)>();
            var assetsAsms = new HashSet<string>(asms.Where(x => x.sourceFiles.Any(p => p.StartsWith("Assets/", StringComparison.Ordinal))).Select(x => x.name));
            try
            {
                foreach (var a in asms)
                {
                    var dll = Path.Combine(scriptAssembliesDir, a.name + ".dll");
                    if (File.Exists(dll)) loaded.Add((a, Read(dll, resolver)));
                    else UnityEngine.Debug.LogWarning($"[ModuleOrganizer] Scan skipped {a.name}: {dll} not found.");
                }

                // Pass 1: every type declared by a script assembly. A full name two script assemblies declare keeps the
                // record of one with sources under Assets/ (the scan's file analysis covers only those).
                var asmdefGuids = new Dictionary<string, string>();
                var duplicates = new HashSet<string>();
                foreach (var (a, def) in loaded)
                {
                    var kind = kinds[a.name];
                    var asmdefPath = kind == AssemblyKind.AsmDef ? CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(a.name) : null;
                    var asmdefGuid = asmdefGuids[a.name] = asmdefPath != null ? AssetDatabase.AssetPathToGUID(asmdefPath) : null;
                    result.assemblies[a.name] = asmdefGuid ?? "";
                    var notAuto = asmdefPath != null && File.Exists(asmdefPath) &&
                                  System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(asmdefPath), @"""autoReferenced""\s*:\s*false");
                    var editorOnly = kind == AssemblyKind.AsmDef && a.flags.HasFlag(AssemblyFlags.EditorAssembly);
                    Friends(def);
                    foreach (var t in def.MainModule.GetTypes())
                    {
                        if (t.FullName.Contains('<')) continue;
                        var rec = new TypeRecord
                        {
                            fullName = t.FullName, ns = Outermost(t).Namespace, assemblyName = a.name, kind = kind,
                            asmdefGuid = asmdefGuid, serializableNonUnityObject = t.IsSerializable && !IsUnityObject(t),
                            notAutoReferenced = notAuto, editorOnly = editorOnly,
                        };
                        if (t.IsPublic) Declarer(rec);
                        if (result.types.TryGetValue(t.FullName, out var prev))
                        {
                            // Only both under Assets/ is ambiguous; an internal polyfill also in a package is legal and harmless.
                            // The record kept is the Assets/ one (the scan's file analysis covers only those).
                            if (!t.IsNested && InAssets(prev.assemblyName) && InAssets(a.name)) duplicates.Add(t.FullName);
                            if (InAssets(prev.assemblyName) || !InAssets(a.name)) continue;
                        }
                        result.types[t.FullName] = rec;
                    }
                }

                // Public top-level types of user DLLs, only to spot a full name two assemblies a file sees both declare.
                // ponytail: reads every user DLL's metadata on each scan; cache by file time if scans get slow.
                foreach (var kv in userDlls.Where(kv => !kinds.ContainsKey(kv.Key)))
                    try
                    {
                        using var dll = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(kv.Value)));
                        foreach (var t in dll.MainModule.Types.Where(x => x.IsPublic && !x.FullName.Contains('<')))
                            Declarer(new TypeRecord
                            {
                                fullName = t.FullName, ns = t.Namespace, assemblyName = kv.Key, kind = AssemblyKind.Precompiled,
                                autoReferencedDll = precompiled[kv.Key].auto, editorOnly = precompiled[kv.Key].editorOnly,
                            });
                    }
                    catch (Exception e) when (e is BadImageFormatException || e is IOException) { } // not a .NET assembly
                foreach (var kv in declarers.Where(kv => kv.Value.Select(r => r.assemblyName).Distinct().Count() > 1))
                    result.publicDeclarers[kv.Key] = kv.Value;

                foreach (var a in asms) if (!result.assemblies.ContainsKey(a.name)) result.assemblies[a.name] = "";
                foreach (var p in CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.All))
                    if (!result.assemblies.ContainsKey(Path.GetFileNameWithoutExtension(p))) result.assemblies[Path.GetFileNameWithoutExtension(p)] = "";

                // Pass 2: files, type -> file mapping, references.
                var scanned = new List<(Assembly a, Dictionary<string, ScriptFile> files, Dictionary<string, string> texts)>();
                foreach (var (a, def) in loaded)
                {
                    if (!InAssets(a.name)) continue;
                    var files = new Dictionary<string, ScriptFile>();
                    var texts = new Dictionary<string, string>();
                    foreach (var src in a.sourceFiles.Select(PathRules.Norm))
                    {
                        if (!File.Exists(src)) continue; // deleted since the last compile
                        var f = files[src] = new ScriptFile
                        {
                            guid = AssetDatabase.AssetPathToGUID(src), path = src, kind = kinds[a.name], asmdefGuid = asmdefGuids[a.name],
                            assemblyName = a.name, editorOnlyAssembly = kinds[a.name] == AssemblyKind.AsmDef && a.flags.HasFlag(AssemblyFlags.EditorAssembly),
                        };
                        result.files[f.guid] = f;
                        // Evaluated with the assembly's defines, so declarations in inactive #if branches add no file.
                        var text = texts[src] = File.ReadAllText(src);
                        foreach (var name in DeclarationFinder.Find(text, a.defines).Keys)
                        {
                            if (Own(name, out var rec)) AddPath(rec, src);
                            if (!duplicates.Contains(name)) continue;
                            if (!result.duplicateTypes.TryGetValue(name, out var where)) where = result.duplicateTypes[name] = new List<string>();
                            if (!where.Contains(src)) where.Add(src);
                        }
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

                    foreach (var rec in result.types.Values.Where(r => r.assemblyName == a.name))
                    foreach (var p in rec.filePaths)
                        files[p].declaredTypes.Add(rec.fullName);

                    // References of nested/compiler-generated types fold into the outermost type's files.
                    foreach (var t in types)
                    {
                        if (!Own(Outermost(t).FullName, out var outer) || outer.filePaths.Count == 0) continue;
                        var refs = new HashSet<string>();
                        var internals = new HashSet<string>();
                        Collect(t, refs, internals);
                        foreach (var p in outer.filePaths)
                        {
                            files[p].referencedTypes.UnionWith(refs);
                            files[p].internalRefs.UnionWith(internals);
                        }
                    }
                    scanned.Add((a, files, texts));

                    bool Own(string name, out TypeRecord rec) =>
                        result.types.TryGetValue(name, out rec) && rec.assemblyName == a.name;
                }
                // Once every type knows its files.
                foreach (var (a, files, texts) in scanned)
                {
                    InlinedTypes(a, files, texts);
                    foreach (var f in files.Values) f.referencedTypes.ExceptWith(f.declaredTypes);
                }
            }
            finally
            {
                foreach (var (_, def) in loaded) def.Dispose();
                resolver.Dispose();
            }
            return result;

            bool InAssets(string asmName) => assetsAsms.Contains(asmName);

            void Declarer(TypeRecord rec)
            {
                if (!declarers.TryGetValue(rec.fullName, out var list)) list = declarers[rec.fullName] = new List<TypeRecord>();
                list.Add(rec);
            }

            // The names an assembly's [InternalsVisibleTo] grants, read once per assembly.
            void Friends(AssemblyDefinition asm)
            {
                if (asm == null || result.internalsVisibleTo.ContainsKey(asm.Name.Name)) return;
                result.internalsVisibleTo[asm.Name.Name] = new HashSet<string>(asm.CustomAttributes
                    .Where(ca => ca.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute" && ca.ConstructorArguments.Count == 1)
                    .Select(ca => ((ca.ConstructorArguments[0].Value as string) ?? "").Split(',')[0].Trim()).Where(n => n.Length > 0));
            }

            // Constants and enum values are inlined: `B.Value` leaves no trace in IL. From source (comments and strings
            // masked), each member-access chain `A.B<..>.C.` names at most one script type of an assembly a.name sees: its
            // first segment as a simple name this file resolves (own namespace or an enclosing one, or a using), or a
            // namespace prefix (relative to an enclosing namespace or global) followed by a type segment. Generic segments
            // count their arity (B<int> is B`1). Only exactly one match adds the type: precision over recall.
            void InlinedTypes(Assembly a, Dictionary<string, ScriptFile> files, Dictionary<string, string> texts)
            {
                var visible = new HashSet<string>(a.assemblyReferences.Select(r => r.name)) { a.name };
                var candidates = result.types.Values.Where(r => !r.fullName.Contains('/') && r.filePaths.Count > 0 && visible.Contains(r.assemblyName)).ToList();
                var byName = candidates.ToLookup(r => r.fullName.Substring(r.fullName.LastIndexOf('.') + 1));
                var byFull = new HashSet<string>(candidates.Select(r => r.fullName));
                foreach (var f in files.Values)
                {
                    var text = texts[f.path];
                    var enclosing = new HashSet<string>(SourceRewriter.Parse(text).namespaces.SelectMany(ModuleValidator.Prefixes)) { "" };
                    var resolvable = new HashSet<string>(enclosing);
                    resolvable.UnionWith(SourceRewriter.FindUsings(text));
                    foreach (var chain in MemberChains(DeclarationFinder.Strip(text, a.defines)))
                    {
                        if (chain[0].dotted) One(byName[Key(chain[0])].Where(r => resolvable.Contains(r.ns ?? "")).Select(r => r.fullName));
                        for (var i = 1; i < chain.Count && chain[i].dotted && chain[i - 1].arity == 0; i++)
                        {
                            var qualified = string.Join(".", chain.Take(i).Select(s => s.name)) + "." + Key(chain[i]);
                            One(enclosing.Select(e => e.Length == 0 ? qualified : e + "." + qualified).Where(byFull.Contains));
                        }
                    }

                    void One(IEnumerable<string> hits)
                    {
                        var two = hits.Distinct().Take(2).ToList();
                        if (two.Count == 1) f.referencedTypes.Add(two[0]);
                    }
                }

                static string Key((string name, int arity, bool dotted) s) => s.arity > 0 ? s.name + "`" + s.arity : s.name;
            }

            void Collect(TypeDefinition t, HashSet<string> refs, HashSet<string> internals)
            {
                var members = new HashSet<string>();
                var self = Outermost(t).FullName; // its own internals are always in reach
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
                            case MethodReference mr: Signature(mr); Member(mr); break;
                            case FieldReference fr: Add(fr.DeclaringType); Add(fr.FieldType); Member(fr); break;
                        }
                    }
                }

                // Pseudo-attributes compile to metadata flags, not attributes: keep their namespaces in view (a `using`
                // of them must not look unused). After the IL pass, so a real reference to one still gets its record.
                if (t.IsSerializable && !t.Name.Contains('<')) refs.Add("System.SerializableAttribute"); // closures are [Serializable] too
                // A struct is Sequential with no pack/size (or size 1 when empty) by default: no [StructLayout] there.
                if (t.IsExplicitLayout || (t.HasLayoutInfo && !(t.IsValueType && t.IsSequentialLayout && t.PackingSize <= 0 && t.ClassSize <= 1)))
                    refs.Add("System.Runtime.InteropServices.StructLayoutAttribute");
                foreach (var f in t.Fields)
                {
                    if (f.IsNotSerialized) refs.Add("System.NonSerializedAttribute");
                    if (f.HasLayoutInfo) refs.Add("System.Runtime.InteropServices.FieldOffsetAttribute");
                }
                foreach (var m in t.Methods)
                {
                    // 0x0200 = AggressiveOptimization (not in Cecil's enum)
                    if ((m.ImplAttributes & (MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveInlining | MethodImplAttributes.NoOptimization |
                                             MethodImplAttributes.Synchronized | (MethodImplAttributes)0x0200)) != 0)
                        refs.Add("System.Runtime.CompilerServices.MethodImplAttribute");
                    if (m.IsPInvokeImpl) refs.Add("System.Runtime.InteropServices.DllImportAttribute");
                }

                void Signature(MethodReference mr)
                {
                    Add(mr.DeclaringType);
                    Add(mr.ReturnType);
                    foreach (var p in mr.Parameters) Add(p.ParameterType);
                    if (mr is GenericInstanceMethod gim) foreach (var g in gim.GenericArguments) Add(g);
                }

                // An internal / private protected member (property and event accessors are methods) of a type in a script
                // assembly or a DLL, or a protected internal one used from a type that does not derive from its declarer.
                void Member(MemberReference mr)
                {
                    if (mr.DeclaringType == null || mr.Name.Contains('<')) return; // compiler-generated (lambdas, iterators)
                    var outerRef = Outermost(mr.DeclaringType.GetElementType());
                    var outer = outerRef.FullName;
                    if (outer == self || outer.Contains('<') || !members.Add(mr.FullName) || !Checked(outerRef)) return;
                    IMemberDefinition d;
                    try { d = mr.Resolve(); }
                    catch (AssemblyResolutionException) { return; }
                    var (assembly, either) = d is MethodDefinition md ? (md.IsAssembly || md.IsFamilyAndAssembly, md.IsFamilyOrAssembly)
                        : d is FieldDefinition fd ? (fd.IsAssembly || fd.IsFamilyAndAssembly, fd.IsFamilyOrAssembly) : (false, false);
                    if (!assembly && !(either && !Derives(t, d.DeclaringType))) return;
                    internals.Add(outer);
                    Friends(d.DeclaringType.Module.Assembly);
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
                    if (name.Contains('<') || !refs.Add(name)) return;
                    if (!result.types.ContainsKey(name)) Register(tr, name);
                    var top = Outermost(tr);
                    if (top.FullName != self && Checked(top) && !Reachable(tr)) internals.Add(top.FullName);
                }

                // Reachable from t in another assembly: each type of the chain public, or nested protected (it compiles
                // today, so t derives), or nested protected internal with t deriving from its declaring type.
                bool Reachable(TypeReference tr)
                {
                    TypeDefinition d;
                    try { d = tr.Resolve(); }
                    catch (AssemblyResolutionException) { return true; }
                    var asm = d?.Module.Assembly;
                    for (; d != null; d = d.DeclaringType)
                    {
                        if (d.IsNested ? d.IsNestedPublic || d.IsNestedFamily || (d.IsNestedFamilyOrAssembly && Derives(t, d.DeclaringType)) : d.IsPublic) continue;
                        Friends(asm);
                        return false;
                    }
                    return true;
                }
            }

            // Script assemblies and DLLs only: an engine/BCL internal is not something user code reaches.
            bool Checked(TypeReference outer)
            {
                if (!result.types.TryGetValue(outer.FullName, out var rec)) rec = Register(outer, outer.FullName);
                return rec.kind != AssemblyKind.Engine;
            }

            TypeRecord Register(TypeReference tr, string name)
            {
                var outer = Outermost(tr);
                var asmName = outer.Scope is ModuleDefinition md ? md.Assembly.Name.Name : outer.Scope?.Name;
                var rec = result.types[name] = new TypeRecord
                {
                    fullName = name, ns = outer.Namespace, assemblyName = asmName, kind = AssemblyKind.Engine,
                };
                if (asmName == null) return rec;
                if (kinds.TryGetValue(asmName, out var k)) rec.kind = k;
                else if (precompiled.TryGetValue(asmName, out var dll))
                {
                    rec.kind = AssemblyKind.Precompiled;
                    rec.autoReferencedDll = dll.auto;
                    rec.editorOnly = dll.editorOnly;
                }
                return rec;
            }
        }

        // `caller` (or a type it is nested in) derives from `declaring`, through its base chain. Unresolvable = not derived.
        static bool Derives(TypeDefinition caller, TypeReference declaring)
        {
            var target = declaring.GetElementType().FullName;
            for (var c = caller; c != null; c = c.DeclaringType)
                for (var b = c.BaseType; b != null;)
                {
                    if (b.GetElementType().FullName == target) return true;
                    try { b = b.Resolve()?.BaseType; }
                    catch (AssemblyResolutionException) { break; }
                }
            return false;
        }

        // Member-access chains in masked code: `A.B<int>.C` -> (A, 0, dotted), (B, 1, dotted), (C, 0, not dotted). A chain
        // starts at an identifier not right after `.`; dotted = followed by `.` (not `..`, not a number). A `<...>` after a
        // segment counts as type arguments only when balanced and made of type-name characters.
        internal static List<List<(string name, int arity, bool dotted)>> MemberChains(string mask)
        {
            var chains = new List<List<(string, int, bool)>>();
            var i = 0;
            while (i < mask.Length)
            {
                if (!IdentStart(mask[i]))
                {
                    i++;
                    continue;
                }
                var b = i - 1;
                var glued = b >= 0 && (char.IsLetterOrDigit(mask[b]) || mask[b] == '_'); // 0x1F, 2f
                while (b >= 0 && char.IsWhiteSpace(mask[b])) b--;
                var member = b >= 0 && mask[b] == '.';
                var chain = new List<(string, int, bool)>();
                var p = i;
                while (true)
                {
                    if (p < mask.Length && mask[p] == '@') p++;
                    var s = p;
                    while (p < mask.Length && (char.IsLetterOrDigit(mask[p]) || mask[p] == '_')) p++;
                    if (p == s) break;
                    var name = mask.Substring(s, p - s);
                    var q = SkipWs(mask, p);
                    var arity = 0;
                    if (q < mask.Length && mask[q] == '<')
                    {
                        var close = TypeArgs(mask, q, out var n);
                        if (close > 0)
                        {
                            arity = n;
                            q = SkipWs(mask, close + 1);
                        }
                    }
                    var dotted = q < mask.Length && mask[q] == '.' && (q + 1 >= mask.Length || (mask[q + 1] != '.' && !char.IsDigit(mask[q + 1])));
                    chain.Add((name, arity, dotted));
                    if (!dotted)
                    {
                        if (arity > 0) p = q;
                        break;
                    }
                    p = SkipWs(mask, q + 1);
                    if (p >= mask.Length || !IdentStart(mask[p])) break;
                }
                if (!glued && !member && chain.Count > 0) chains.Add(chain);
                i = Math.Max(p, i + 1);
            }
            return chains;

            static bool IdentStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
        }

        static int SkipWs(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        // Index of the `>` closing the `<` at open, with the number of top-level type arguments; -1 when it is not a type
        // argument list (unbalanced, or a character no type name has: a comparison `a < b && c > d`).
        static int TypeArgs(string s, int open, out int arity)
        {
            arity = 0;
            int depth = 0, parens = 0, commas = 0;
            for (var i = open; i < s.Length && i < open + 512; i++)
            {
                var c = s[i];
                if (c == '<') depth++;
                else if (c == '>')
                {
                    if (--depth == 0)
                    {
                        arity = commas + 1;
                        return i;
                    }
                }
                else if (c == '(') parens++;
                else if (c == ')') parens--;
                else if (c == ',' && depth == 1 && parens == 0) commas++;
                else if (!(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '_' || c == '@' || c == '.' || c == ',' || c == '[' || c == ']' || c == '?' || c == ':'))
                    return -1;
                if (parens < 0) return -1;
            }
            return -1;
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
        static (bool auto, bool editorOnly) DllInfo(string dllPath)
        {
            var meta = dllPath + ".meta";
            var text = File.Exists(meta) ? File.ReadAllText(meta) : "";
            return (!text.Contains("isExplicitlyReferenced: 1"), IsEditorOnlyDll(dllPath, text));
        }

        // A DLL only the Editor loads: under an `Editor` folder (Unity's special-folder rule), or a PluginImporter .meta whose
        // platformData enables Editor and nothing else (Any off). Entries read line by line: `- first:`, then
        // `<Group>: <Name>` (`: Any` for Any), then the entry's first `enabled: 0|1`.
        public static bool IsEditorOnlyDll(string dllPath, string metaText)
        {
            var dirs = PathRules.Norm(dllPath).Split('/');
            if (dirs.Take(dirs.Length - 1).Contains("Editor")) return true;
            bool any = false, editor = false, other = false;
            var lines = (metaText ?? "").Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var head = lines[i].Trim();
                if (!head.StartsWith("- first:", StringComparison.Ordinal)) continue;
                var key = head.Substring("- first:".Length).Trim();
                if (key.Length == 0 && i + 1 < lines.Length) key = lines[++i].Trim();
                key = key.Trim('{', '}', ' ');
                var cut = key.LastIndexOf(':');
                var group = cut < 0 ? "" : key.Substring(0, cut).Trim();
                var name = cut < 0 ? key : key.Substring(cut + 1).Trim();
                var on = false;
                for (var j = i + 1; j < lines.Length && !lines[j].Trim().StartsWith("- first:", StringComparison.Ordinal); j++)
                {
                    var l = lines[j].Trim();
                    if (!l.StartsWith("enabled:", StringComparison.Ordinal)) continue;
                    on = l.Substring("enabled:".Length).Trim() == "1";
                    break;
                }
                if (!on) continue;
                if (name == "Any") any = true;
                else if (group == "Editor" && name == "Editor") editor = true;
                else other = true;
            }
            return !any && editor && !other;
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
