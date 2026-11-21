using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // Spec 5.7. Drives the engine: Scan -> Validate (report, plan, drift) -> Apply. Config changes go through ModuleEdits.
    public class ModuleOrganizerWindow : EditorWindow
    {
        const string UxmlPath = "Packages/com.monome.utilitypack/Editor/ModuleOrganizer/UI/ModuleOrganizerWindow.uxml";
        const string DragKey = "MonomeScripts"; // generic drag data: List<string> of script GUIDs
        const string ShownResultKey = "Monome.ModuleOrganizer.ShownResult";

        [SerializeField] ModuleConfig cfg;
        [SerializeField] int selected = -1;

        ScanResult scan;
        ValidationReport report;
        ApplyPlan plan;
        DiskSnapshot disk;
        List<DriftItem> drift = new List<DriftItem>();
        List<string> applyErrors;
        ApplyResult result;
        string error;
        readonly Dictionary<string, (Severity sev, string tip)> worst = new Dictionary<string, (Severity, string)>();
        List<ModuleConfig> configs = new List<ModuleConfig>();
        int nextId;
        bool armed; // Apply clicked once: the next click executes

        DropdownField picker;
        ToolbarButton createBtn, scanBtn, validateBtn, applyBtn, syncBtn;
        HelpBox status;
        VisualElement scopeList, cards, panel, issuesView, depsView, planView, driftView, resultView;
        Label unassignedHeader;
        ToolbarSearchField filter;
        TreeView tree;
        TabView tabs;
        Tab issuesTab, planTab, syncTab, resultTab;
        TextField nameF, nsF, descF, extraF, editorRefsF;
        ColorField colorF;
        Toggle rewriteT;

        Module SelectedModule => cfg != null && selected >= 0 && selected < cfg.modules.Count ? cfg.modules[selected] : null;
        static bool CanScan => !EditorApplication.isCompiling && !EditorUtility.scriptCompilationFailed;

        [MenuItem("Tools/Monome/Module Organizer")]
        public static void Open() => GetWindow<ModuleOrganizerWindow>("Module Organizer");

        void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            ApplyResultWatcher.ResultReady += OnResultReady;
            result = ApplyResultWatcher.LastResult;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            ApplyResultWatcher.ResultReady -= OnResultReady;
        }

        // ponytail: re-validates on any undo in the Editor, not just config ones; filter by Undo group if it lags.
        void OnUndoRedo()
        {
            if (cfg != null) Validate();
        }

        void OnResultReady()
        {
            result = ApplyResultWatcher.LastResult;
            if (tabs == null) return;
            RefreshResult();
            ShowResultIfNew();
        }

        void CreateGUI()
        {
            var r = rootVisualElement;
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                r.Add(new HelpBox("Missing " + UxmlPath, HelpBoxMessageType.Error));
                return;
            }
            uxml.CloneTree(r);

            picker = r.Q<DropdownField>("config-picker");
            createBtn = r.Q<ToolbarButton>("create-config");
            scanBtn = r.Q<ToolbarButton>("scan");
            validateBtn = r.Q<ToolbarButton>("validate");
            applyBtn = r.Q<ToolbarButton>("apply");
            syncBtn = r.Q<ToolbarButton>("sync");
            status = r.Q<HelpBox>("status");
            scopeList = r.Q("scope-list");
            filter = r.Q<ToolbarSearchField>("filter");
            unassignedHeader = r.Q<Label>("unassigned-header");
            tree = r.Q<TreeView>("unassigned");
            cards = r.Q("cards");
            panel = r.Q("module-panel");
            tabs = r.Q<TabView>("tabs");
            issuesTab = r.Q<Tab>("tab-issues");
            planTab = r.Q<Tab>("tab-plan");
            syncTab = r.Q<Tab>("tab-sync");
            resultTab = r.Q<Tab>("tab-result");
            issuesView = r.Q("issues");
            depsView = r.Q("deps");
            planView = r.Q("plan");
            driftView = r.Q("drift");
            resultView = r.Q("result");

            picker.RegisterValueChangedCallback(_ =>
            {
                if (picker.index < 0 || picker.index >= configs.Count) return;
                cfg = configs[picker.index];
                selected = -1;
                Validate();
            });
            createBtn.clicked += CreateConfig;
            scanBtn.clicked += Scan;
            validateBtn.clicked += Validate;
            applyBtn.clicked += Apply;
            syncBtn.clicked += () => tabs.activeTab = syncTab;
            r.Q<Button>("add-scope").clicked += AddScope;
            r.Q<Button>("add-module").clicked += () =>
            {
                if (cfg == null) return;
                ModuleEdits.AddModule(cfg);
                selected = cfg.modules.Count - 1;
                Validate();
            };
            filter.RegisterValueChangedCallback(_ => RefreshTree());
            SetupTree();
            SetupPanel();

            r.schedule.Execute(RefreshToolbar).Every(500);
            RefreshConfigs();
            if (cfg != null && CanScan) Scan();
            else RefreshAll();
            ShowResultIfNew();
        }

        // ---- engine calls ----

        static string ReadSource(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        void Scan()
        {
            if (cfg == null || !CanScan)
            {
                RefreshAll();
                return;
            }
            ApplyResultWatcher.ReserializeOwed(); // a compiled project: settle an earlier Apply's asset migration first
            try
            {
                scan = DependencyScanner.Scan();
            }
            catch (Exception e)
            {
                scan = null;
                Debug.LogException(e);
                error = "Scan failed: " + e.Message;
                RefreshAll();
                return;
            }
            Validate();
        }

        // Report, plan and drift together: Diff needs a fresh report, and the Plan tab shows what Apply would do.
        void Validate()
        {
            armed = false;
            report = null;
            plan = null;
            drift = new List<DriftItem>();
            worst.Clear();
            error = null;
            if (cfg != null && scan != null)
                try
                {
                    report = ModuleValidator.Validate(cfg, scan, ReadSource);
                    // ponytail: re-reads every in-scope source and asmdef per edit; cache DiskState per scan if big projects lag.
                    plan = ApplyPlanner.Build(cfg, scan, report, DiskState.Capture(scan, ReadSource));
                    disk = DiskSnapshot.Capture(cfg);
                    drift = ModuleSync.Diff(cfg, disk, report);
                    foreach (var i in report.issues.Concat(plan.issues).Where(i => i.severity != Severity.Info && i.path != null))
                        worst[i.path] = worst.TryGetValue(i.path, out var w)
                            ? ((Severity)Math.Max((int)w.sev, (int)i.severity), w.tip + "\n" + i.message)
                            : (i.severity, i.message);
                }
                catch (Exception e)
                {
                    report = null;
                    plan = null;
                    Debug.LogException(e);
                    error = "Validate failed: " + e.Message;
                }
            RefreshAll();
        }

        string ApplyBlocker()
        {
            if (cfg == null || scan == null || report == null || plan == null) return "Scan first.";
            if (!CanScan) return "Wait for compilation to finish (and fix compile errors).";
            if (configs.Any(c => c != null && c.owedReserializeKeys.Count > 0)) return "Pending asset migration — fix compile errors first.";
            if (!report.CanApply) return "Fix the Error issues first.";
            if (plan.issues.Any(i => i.severity == Severity.Error)) return "The plan has errors (see the Plan tab).";
            if (plan.IsEmpty) return "Nothing to apply: the project already matches the config.";
            return null;
        }

        // Two clicks: the first shows the Plan tab and arms the button; the second (git warning if needed) executes.
        // Any config edit or rescan goes through Validate/Edit, which disarm it.
        void Apply()
        {
            if (!armed)
            {
                Validate();
                if (ApplyBlocker() != null) return;
                armed = true;
                tabs.activeTab = planTab;
                RefreshToolbar();
                return;
            }
            armed = false;
            if (ApplyBlocker() != null) return;
            if (ModuleApplier.HasUncommittedGitChanges(Directory.GetParent(Application.dataPath).FullName) &&
                !EditorUtility.DisplayDialog("Module Organizer",
                    "The project has uncommitted git changes. Apply moves and rewrites scripts; commit first so it can be reverted.\n\nApply anyway?",
                    "Apply", "Cancel"))
            {
                RefreshToolbar();
                return;
            }
            // Without a compile the watcher would wait for some unrelated later one.
            var pending = plan.TriggersCompile;
            if (pending) ApplyResultWatcher.BeginPending(plan.reserializeTypeKeys);
            result = null;
            applyErrors = ModuleApplier.Execute(plan, cfg, out var touchedDisk);
            // Execute records GUIDs/applied flags outside Undo: undoing past it would desync config and disk.
            Undo.ClearUndo(cfg);
            if (!pending && plan.reserializeTypeKeys.Count > 0) ApplyResultWatcher.ReserializeOwed(); // no reload will come to do it
            if (applyErrors.Count > 0 && !touchedDisk)
            {
                if (pending) ApplyResultWatcher.Cancel();
                Validate(); // nothing changed: the scan still holds
                tabs.activeTab = resultTab;
                return;
            }
            // The scan describes the old assemblies; the window rescans after the reload (or press Scan).
            scan = null;
            report = null;
            plan = null;
            drift = new List<DriftItem>();
            worst.Clear();
            RefreshAll();
            tabs.activeTab = resultTab;
        }

        void CreateConfig()
        {
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/ModuleConfig.asset");
            AssetDatabase.CreateAsset(CreateInstance<ModuleConfig>(), path);
            AssetDatabase.SaveAssets();
            cfg = AssetDatabase.LoadAssetAtPath<ModuleConfig>(path);
            RefreshConfigs();
            Scan();
        }

        void AddScope()
        {
            if (cfg == null) return;
            var abs = EditorUtility.OpenFolderPanel("Add scope folder", Application.dataPath, "");
            if (string.IsNullOrEmpty(abs)) return;
            var full = Path.GetFullPath(abs).Replace('\\', '/').TrimEnd('/');
            var data = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            string rel = null;
            if (string.Equals(full, data, StringComparison.OrdinalIgnoreCase)) rel = "Assets";
            else if (full.StartsWith(data + "/", StringComparison.OrdinalIgnoreCase)) rel = "Assets" + full.Substring(data.Length);
            if (rel == null)
            {
                EditorUtility.DisplayDialog("Module Organizer", "Scope folders must be inside Assets/.", "OK");
                return;
            }
            AddScopeFolder(rel);
        }

        void AddScopeFolder(string rel)
        {
            if (cfg == null) return;
            if (!cfg.scopeFolders.Contains(rel)) ModuleEdits.Change(cfg, "Add scope folder", () => cfg.scopeFolders.Add(rel));
            Validate();
        }

        // ---- drag and drop (collection views on both ends) ----

        static StartDragArgs Drag(IEnumerable<string> guids)
        {
            var list = guids.Distinct().ToList();
            var args = new StartDragArgs(list.Count == 1 ? "1 script" : list.Count + " scripts", DragVisualMode.Move);
            args.SetGenericData(DragKey, list);
            return args;
        }

        // Our own drags carry GUIDs; a drag from the Project window carries paths (.cs files or folders).
        List<string> Payload(HandleDragAndDropArgs a)
        {
            if (cfg == null || scan == null) return new List<string>();
            if (a.dragAndDropData.GetGenericData(DragKey) is List<string> g) return g;
            var paths = a.dragAndDropData.paths?.Select(PathRules.Norm).ToList();
            if (paths == null || paths.Count == 0) return new List<string>();
            return scan.files.Values.Where(f => paths.Any(p => f.path == p || PathRules.IsUnder(f.path, p))).Select(f => f.guid).ToList();
        }

        DragVisualMode Accept(HandleDragAndDropArgs a) => Payload(a).Count > 0 ? DragVisualMode.Move : DragVisualMode.Rejected;

        DragVisualMode Drop(HandleDragAndDropArgs a, Action<List<string>> edit)
        {
            var guids = Payload(a);
            if (guids.Count == 0) return DragVisualMode.Rejected;
            edit(guids);
            rootVisualElement.schedule.Execute(Validate); // not inside the view's own drop handling
            return DragVisualMode.Move;
        }

        // ---- left: scope + unassigned tree ----

        class Node
        {
            public string label, path;
            public List<string> guids; // a folder drags every file below it
            public bool isFile, editor, pending;
        }

        void SetupTree()
        {
            tree.makeItem = Row;
            tree.bindItem = (e, i) =>
            {
                var n = tree.GetItemDataForIndex<Node>(i);
                BindRow(e, n.label, n.isFile ? n.path : null, n.editor, n.pending);
            };
            tree.reorderable = true; // makes the dragger start drags; handleDrop below takes every drop
            tree.canStartDrag += _ => true;
            tree.setupDragAndDrop += a => Drag(a.selectedIds.SelectMany(id => tree.GetItemDataForId<Node>(id)?.guids ?? new List<string>()));
            tree.dragAndDropUpdate += Accept;
            tree.handleDrop += a => Drop(a, g => ModuleEdits.Unassign(cfg, g, scan, ReadSource));
        }

        void RefreshTree()
        {
            var files = new List<ScriptFile>();
            if (cfg != null && scan != null)
                files = scan.files.Values
                    .Where(f => ModuleValidator.IsAssignable(f, cfg) && cfg.IsInScope(f.path) && cfg.FindModuleOf(f.guid) == null &&
                                (string.IsNullOrEmpty(filter.value) || f.path.IndexOf(filter.value, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(f => f.path, StringComparer.Ordinal).ToList();
            unassignedHeader.text = $"Unassigned ({files.Count})";
            nextId = 0;
            var roots = new List<TreeViewItemData<Node>>();
            if (cfg != null)
            {
                var scopes = cfg.scopeFolders.Select(PathRules.Norm).Distinct().ToList();
                foreach (var s in scopes)
                {
                    // Each file under its innermost scope folder.
                    var mine = files.Where(f => scopes.Where(x => PathRules.IsUnder(f.path, x)).OrderByDescending(x => x.Length).First() == s).ToList();
                    if (mine.Count == 0) continue;
                    roots.Add(new TreeViewItemData<Node>(nextId++, new Node { label = s + "/", guids = mine.Select(f => f.guid).ToList() }, Build(s, mine)));
                }
            }
            tree.SetRootItems(roots);
            tree.Rebuild();
            tree.ExpandAll();
        }

        List<TreeViewItemData<Node>> Build(string dir, List<ScriptFile> files)
        {
            var items = new List<TreeViewItemData<Node>>();
            foreach (var g in files.Where(f => Dir(f.path) != dir).GroupBy(f => f.path.Substring(dir.Length + 1).Split('/')[0]).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var sub = g.ToList();
                var node = new Node { label = g.Key + "/", guids = sub.Select(f => f.guid).ToList() };
                items.Add(new TreeViewItemData<Node>(nextId++, node, Build(dir + "/" + g.Key, sub)));
            }
            foreach (var f in files.Where(f => Dir(f.path) == dir))
                items.Add(new TreeViewItemData<Node>(nextId++, new Node
                {
                    label = FileName(f.path), path = f.path, guids = new List<string> { f.guid }, isFile = true, editor = f.IsEditor,
                    pending = cfg.pendingRemovals.Any(e => e.guid == f.guid),
                }));
            return items;
        }

        void RefreshScope()
        {
            scopeList.Clear();
            if (cfg == null) return;
            foreach (var s in cfg.scopeFolders.ToList())
            {
                var row = new VisualElement();
                row.AddToClassList("scope-row");
                row.Add(new Label(s));
                row.Add(new Button(() =>
                {
                    ModuleEdits.Change(cfg, "Remove scope folder", () => cfg.scopeFolders.Remove(s));
                    Validate();
                }) { text = "×", tooltip = "Remove scope folder" });
                scopeList.Add(row);
            }
        }

        // ---- middle: module cards + edit panel ----

        void RefreshCards()
        {
            cards.Clear();
            if (cfg == null) return;
            for (var i = 0; i < cfg.modules.Count; i++) cards.Add(Card(cfg.modules[i], i));
        }

        VisualElement Card(Module m, int index)
        {
            var card = new VisualElement();
            card.AddToClassList("card");
            card.EnableInClassList("card--selected", index == selected);
            card.style.borderTopColor = card.style.borderBottomColor = card.style.borderLeftColor = card.style.borderRightColor = m.color;
            card.Add(new Label(m.name) { tooltip = m.description }.With("card-title"));
            card.Add(new Label(m.namespaceName).With("card-ns"));
            var guids = m.scripts.Select(e => e.guid).ToList();
            var list = new ListView(guids, 18, Row, (e, i) =>
            {
                var f = scan != null && scan.files.TryGetValue(guids[i], out var sf) ? sf : null;
                var path = f?.path ?? m.scripts.FirstOrDefault(x => x.guid == guids[i])?.originalPath ?? guids[i];
                BindRow(e, FileName(path) + (f == null ? " (not scanned)" : ""), path, f != null && f.IsEditor, false);
            })
            {
                selectionType = SelectionType.Multiple,
                reorderable = true, // makes the dragger start drags; handleDrop takes every drop (same module = no-op)
            };
            list.canStartDrag += _ => true;
            list.dragAndDropUpdate += Accept;
            list.style.height = Mathf.Clamp(guids.Count * 18 + 6, 40, 320);
            list.setupDragAndDrop += a => Drag(a.selectedIds.Select(id => list.viewController.GetIndexForId(id))
                .Where(i => i >= 0 && i < guids.Count).Select(i => guids[i]));
            list.handleDrop += a => Drop(a, g => ModuleEdits.Assign(cfg, scan, g, m, ReadSource));
            card.Add(list);
            // Scheduled: a delayed field being edited commits to the old selection first.
            card.RegisterCallback<PointerDownEvent>(_ => rootVisualElement.schedule.Execute(() => Select(index)), TrickleDown.TrickleDown);
            return card;
        }

        void Select(int index)
        {
            if (selected == index) return;
            selected = index;
            for (var i = 0; i < cards.childCount; i++) cards[i].EnableInClassList("card--selected", i == selected);
            RefreshPanel();
        }

        void SetupPanel()
        {
            var r = rootVisualElement;
            nameF = r.Q<TextField>("m-name");
            nsF = r.Q<TextField>("m-namespace");
            colorF = r.Q<ColorField>("m-color");
            descF = r.Q<TextField>("m-description");
            rewriteT = r.Q<Toggle>("m-rewrite");
            extraF = r.Q<TextField>("m-extra-refs");
            editorRefsF = r.Q<TextField>("m-editor-refs");

            nameF.RegisterValueChangedCallback(e =>
            {
                var m = SelectedModule;
                if (m == null) return;
                if (!ModuleEdits.RenameModule(cfg, m, e.newValue))
                    ShowNotification(new GUIContent("Module names must be unique, non-empty and without '/'."));
                Validate();
            });
            nsF.RegisterValueChangedCallback(e =>
            {
                var ns = e.newValue.Trim();
                if (ns.Length > 0) Edit("Set module namespace", m => m.namespaceName = ns);
                else Validate();
            });
            colorF.RegisterValueChangedCallback(e => Edit("Set module color", m => m.color = e.newValue, false));
            descF.RegisterValueChangedCallback(e => Edit("Set module description", m => m.description = e.newValue, false));
            rewriteT.RegisterValueChangedCallback(e => Edit("Toggle rewrite namespace", m => m.rewriteNamespace = e.newValue));
            extraF.RegisterValueChangedCallback(e => Edit("Set extra references", m => m.extraReferences = Lines(e.newValue)));
            editorRefsF.RegisterValueChangedCallback(e => Edit("Set Editor extra references", m => m.editorExtraReferences = Lines(e.newValue)));
            r.Q<Button>("m-delete").clicked += () =>
            {
                var m = SelectedModule;
                if (m == null || !EditorUtility.DisplayDialog("Delete module",
                        $"Delete {m.name}? Its applied scripts go back to where they were, and its asmdefs are removed, on the next Apply.",
                        "Delete", "Cancel")) return;
                ModuleEdits.DeleteModule(cfg, m, scan, ReadSource);
                selected = -1;
                Validate();
            };
        }

        void Edit(string label, Action<Module> change, bool validate = true)
        {
            var m = SelectedModule;
            if (m == null) return;
            armed = false;
            ModuleEdits.Change(cfg, label, () => change(m));
            if (validate) Validate();
            else RefreshCards();
        }

        static List<string> Lines(string s) =>
            (s ?? "").Split('\n', ',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

        void RefreshPanel()
        {
            var m = SelectedModule;
            panel.style.display = m == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (m == null) return;
            nameF.SetValueWithoutNotify(m.name);
            nsF.SetValueWithoutNotify(m.namespaceName);
            colorF.SetValueWithoutNotify(m.color);
            descF.SetValueWithoutNotify(m.description ?? "");
            rewriteT.SetValueWithoutNotify(m.rewriteNamespace);
            extraF.SetValueWithoutNotify(string.Join("\n", m.extraReferences));
            editorRefsF.SetValueWithoutNotify(string.Join("\n", m.editorExtraReferences));
        }

        // ---- rows ----

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.AddToClassList("script-row");
            row.Add(new Label { name = "name" }.With("name"));
            row.Add(new Label("ed") { name = "ed", tooltip = "Editor script" }.With("badge"));
            row.Add(new Label { name = "icon" });
            return row;
        }

        // Worst issue of the file: ✗ error, ⚠ warning; tooltip = the messages.
        void BindRow(VisualElement row, string text, string path, bool editor, bool pending)
        {
            row.Q<Label>("name").text = text;
            row.Q("ed").style.display = editor ? DisplayStyle.Flex : DisplayStyle.None;
            var icon = row.Q<Label>("icon");
            (Severity sev, string tip) w = default;
            var has = path != null && worst.TryGetValue(path, out w);
            icon.text = !has ? "" : w.sev == Severity.Error ? "✗" : "⚠";
            icon.RemoveFromClassList("sev-Error");
            icon.RemoveFromClassList("sev-Warning");
            if (has) icon.AddToClassList("sev-" + w.sev);
            row.EnableInClassList("pending", pending);
            row.tooltip = has ? w.tip : pending ? path + "\nUnassigned: Apply moves it back to where it was." : path;
        }

        // ---- bottom tabs ----

        void RefreshAll()
        {
            if (tabs == null) return;
            RefreshConfigs();
            RefreshScope();
            RefreshTree();
            RefreshCards();
            RefreshPanel();
            RefreshIssues();
            RefreshDeps();
            RefreshPlan();
            RefreshDrift();
            RefreshResult();
            RefreshToolbar();
        }

        void RefreshConfigs()
        {
            configs = AssetDatabase.FindAssets("t:ModuleConfig").Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ModuleConfig>).Where(c => c != null).ToList();
            if (cfg == null || !configs.Contains(cfg)) cfg = configs.FirstOrDefault();
            // '/' would make submenus in the dropdown.
            picker.choices = configs.Select(c => AssetDatabase.GetAssetPath(c).Replace('/', '∕')).ToList();
            picker.SetValueWithoutNotify(cfg != null ? AssetDatabase.GetAssetPath(cfg).Replace('/', '∕') : "");
            picker.style.display = configs.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            createBtn.style.display = configs.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void RefreshToolbar()
        {
            if (tabs == null) return;
            var msg = EditorApplication.isCompiling ? "Scripts are compiling: Scan is locked until compilation finishes."
                : EditorUtility.scriptCompilationFailed ? "The project has compile errors: fix them, then Scan."
                : error ?? (cfg == null ? "No ModuleConfig yet: create one to start."
                    : scan == null ? "Press Scan to read the compiled assemblies." : null);
            status.text = msg ?? "";
            status.messageType = EditorUtility.scriptCompilationFailed || error != null ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
            status.style.display = msg == null ? DisplayStyle.None : DisplayStyle.Flex;
            scanBtn.SetEnabled(cfg != null && CanScan);
            validateBtn.SetEnabled(cfg != null && scan != null);
            var blocker = ApplyBlocker();
            if (blocker != null) armed = false;
            applyBtn.SetEnabled(blocker == null);
            applyBtn.text = armed ? $"Confirm Apply ({plan.moves.Count} moves, {plan.asmdefs.Count} asmdefs)" : "Apply";
            applyBtn.tooltip = blocker ?? "Move files, write asmdefs and rewrite namespaces as planned.";
            syncBtn.text = $"⚠ {drift.Count} drift → Sync";
            syncBtn.style.display = drift.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void RefreshIssues()
        {
            issuesView.Clear();
            var issues = report?.issues ?? new List<Issue>();
            // A module outside the scope and modulesRoot blocks only the plan; shown here too, once per folder, with the fix.
            var boundary = (plan?.issues ?? new List<Issue>()).Where(i => i.scopeFix != null).GroupBy(i => i.scopeFix).Select(g => g.First()).ToList();
            issuesTab.label = $"Issues ({issues.Count(i => i.severity == Severity.Error) + boundary.Count} ✗, {issues.Count(i => i.severity == Severity.Warning)} ⚠)";
            if (report == null)
            {
                issuesView.Add(Line("Not validated yet."));
                return;
            }
            if (issues.Count + boundary.Count == 0) issuesView.Add(Line("No issues."));
            foreach (var i in boundary)
            {
                var row = new VisualElement().With("row");
                row.Add(IssueLine(i));
                row.Add(new Button(() => AddScopeFolder(i.scopeFix)) { name = "add-as-scope", text = "Add as scope", tooltip = "Add " + i.scopeFix + " to the scope folders." });
                issuesView.Add(row);
            }
            foreach (var i in issues.OrderByDescending(i => i.severity).ThenBy(i => i.path, StringComparer.Ordinal))
                issuesView.Add(IssueLine(i));
        }

        void RefreshDeps()
        {
            depsView.Clear();
            if (report == null) return;
            foreach (var c in report.cycles)
                depsView.Add(Line("✗ Cycle: " + string.Join(" → ", c.Concat(c.Take(1))), "sev-Error"));
            if (report.edges.Count == 0) depsView.Add(Line("No module depends on another."));
            foreach (var e in report.edges.OrderBy(e => e.from, StringComparer.Ordinal).ThenBy(e => e.to, StringComparer.Ordinal))
            {
                var fo = new Foldout { text = $"{e.from} → {e.to} ({e.causedBy.Count} file(s))", value = false };
                foreach (var c in e.causedBy) fo.Add(Line(c));
                depsView.Add(fo);
            }
        }

        void RefreshPlan()
        {
            planView.Clear();
            if (plan == null)
            {
                planView.Add(Line("Not validated yet."));
                return;
            }
            if (plan.IsEmpty) planView.Add(Line("Nothing to apply: the project matches the config."));
            Section(planView, "Plan issues", plan.issues, IssueLine);
            Section(planView, "Create folders", plan.createFolders, f => Line(f));
            Section(planView, "Moves", plan.moves, m => Line($"{m.from} → {m.to}"));
            Section(planView, "Asmdefs", plan.asmdefs, a => Line(a.delete ? "delete " + a.path : $"{a.path}  [{string.Join(", ", a.references)}]"));
            Section(planView, "Source edits", plan.edits, e =>
            {
                var parts = new List<string>();
                if (e.setNamespace != null) parts.Add("namespace → " + (e.setNamespace.Length == 0 ? "(global)" : e.setNamespace));
                parts.AddRange(e.addUsings.Select(u => "+using " + u));
                parts.AddRange(e.removeUsings.Select(u => "-using " + u));
                parts.AddRange(e.removeMarkedUsings.Select(u => "-using " + u + " (added by Apply)"));
                parts.AddRange(e.aliases.Select(x => $"+using {x.alias} = {x.target}"));
                parts.AddRange(e.movedFrom.Select(x => $"[MovedFrom {x.oldNs}] on {x.type}"));
                return Line($"{PathOf(e.guid)}: {string.Join(", ", parts)}");
            });
            Section(planView, "Delete folders if empty", plan.deleteFoldersIfEmpty, f => Line(f));
        }

        void RefreshDrift()
        {
            driftView.Clear();
            syncTab.label = drift.Count > 0 ? $"Sync ({drift.Count})" : "Sync";
            if (drift.Count == 0)
            {
                driftView.Add(Line(report == null ? "Not validated yet." : "Config and disk agree."));
                return;
            }
            var bar = new VisualElement().With("row");
            bar.Add(new Button(() => TakeFromDisk(drift.ToList())) { text = "Take all from disk", tooltip = "Update the config to match the files on disk." });
            bar.Add(new Button(Apply) { text = "Re-apply config", tooltip = "Runs Apply: puts the disk back to what the config says." });
            driftView.Add(bar);
            driftView.Add(Line("Re-apply applies the whole config (there is no per-item re-apply)."));
            foreach (var d in drift)
            {
                var row = new VisualElement().With("row");
                row.Add(Line($"{d.kind} · {d.moduleName}: {d.detail}").With("grow"));
                row.Add(new Button(() => TakeFromDisk(new List<DriftItem> { d })) { text = "Take from disk" });
                driftView.Add(row);
            }
        }

        // Against a fresh snapshot: the disk may have changed since the list was drawn.
        void TakeFromDisk(List<DriftItem> items)
        {
            disk = DiskSnapshot.Capture(cfg);
            var wanted = new HashSet<(DriftKind, string, string)>(items.Select(i => (i.kind, i.moduleName, i.guid)));
            var refused = ModuleSync.AcceptDisk(cfg, disk, ModuleSync.Diff(cfg, disk, report).Where(i => wanted.Contains((i.kind, i.moduleName, i.guid))).ToList());
            Validate();
            if (refused.Count == 0) return;
            error = string.Join("\n", refused);
            RefreshToolbar();
        }

        void RefreshResult()
        {
            resultView.Clear();
            if (applyErrors != null)
            {
                resultView.Add(Line(applyErrors.Count == 0 ? "Apply: files moved and written." : "Apply failed:", "section"));
                foreach (var e in applyErrors) resultView.Add(Line(e, "sev-Error"));
            }
            if (ApplyResultWatcher.Pending)
                resultView.Add(Line($"Waiting for the compile result… (domain reload {ApplyResultWatcher.PendingReloads} of at most " +
                                    $"{ApplyResultWatcher.MaxReloads}; on reload {ApplyResultWatcher.MaxReloads} the check gives up with a warning)."));
            if (result != null)
            {
                // Editor first: playerCompileOk is false whenever the Editor compile failed.
                resultView.Add(Line("Editor compile: " + (result.editorCompileOk ? "OK" : "failed"), "section"));
                foreach (var e in result.editorErrors) resultView.Add(Line(e, "sev-Error"));
                resultView.Add(Line($"Player compile ({result.buildTarget}): " +
                                    (!result.editorCompileOk ? "skipped" : result.playerCompileOk ? "OK" : "failed"), "section"));
                foreach (var e in result.playerErrors) resultView.Add(Line(e, "sev-Error"));
                foreach (var w in result.warnings) resultView.Add(Line("⚠ " + w, "sev-Warning"));
                foreach (var p in result.reserialized) resultView.Add(Line("Re-serialized " + p));
            }
            if (resultView.childCount == 0) resultView.Add(Line("No Apply yet in this session."));
        }

        void ShowResultIfNew()
        {
            if (result == null || tabs == null) return;
            var json = JsonUtility.ToJson(result);
            if (SessionState.GetString(ShownResultKey, "") == json) return;
            SessionState.SetString(ShownResultKey, json);
            tabs.activeTab = resultTab;
        }

        // ---- helpers ----

        static Label Line(string text, string cls = null)
        {
            var l = new Label(text).With("line");
            if (cls != null) l.AddToClassList(cls);
            return l;
        }

        static Label IssueLine(Issue i)
        {
            var l = Line($"{(i.severity == Severity.Error ? "✗" : i.severity == Severity.Warning ? "⚠" : "i")} [{i.code}] {i.message}", "sev-" + i.severity);
            l.RegisterCallback<ClickEvent>(_ =>
            {
                var o = string.IsNullOrEmpty(i.path) ? null : AssetDatabase.LoadAssetAtPath<Object>(i.path);
                if (o != null) EditorGUIUtility.PingObject(o);
            });
            return l;
        }

        static void Section<T>(VisualElement parent, string title, List<T> items, Func<T, VisualElement> line)
        {
            if (items.Count == 0) return;
            parent.Add(Line($"{title} ({items.Count})", "section"));
            foreach (var x in items) parent.Add(line(x));
        }

        string PathOf(string guid) => scan != null && scan.files.TryGetValue(guid, out var f) ? f.path : AssetDatabase.GUIDToAssetPath(guid);

        static string Dir(string path) => path.Contains('/') ? path.Substring(0, path.LastIndexOf('/')) : "";

        static string FileName(string path) => path.Substring(path.LastIndexOf('/') + 1);
    }

    static class VisualElementExt
    {
        public static T With<T>(this T e, string cls) where T : VisualElement
        {
            e.AddToClassList(cls);
            return e;
        }
    }
}
