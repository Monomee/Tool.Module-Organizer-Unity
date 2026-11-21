using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class ModuleOrganizerWindowTests
    {
        static readonly string[] Names =
        {
            "config-picker", "create-config", "scan", "validate", "apply", "sync", "status", "scope-list", "add-scope", "filter",
            "unassigned", "cards", "add-module", "module-panel", "m-name", "m-namespace", "m-color", "m-delete", "tabs", "issues",
            "deps", "plan", "drift", "result",
        };

        // -nographics cannot show windows ("No graphic device is available"), so build the UI the way showing would:
        // CreateInstance runs OnEnable, then CreateGUI.
        [Test]
        public void Window_BuildsUi_WithNamedElements()
        {
            var w = ScriptableObject.CreateInstance<ModuleOrganizerWindow>();
            try
            {
                typeof(ModuleOrganizerWindow).GetMethod("CreateGUI", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w, null);
                foreach (var n in Names) Assert.That(w.rootVisualElement.Q(n), Is.Not.Null, n);
                Assert.That(w.rootVisualElement.Q<TreeView>("unassigned"), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(w);
            }
        }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic;

        static void Call(ModuleOrganizerWindow w, string method) => typeof(ModuleOrganizerWindow).GetMethod(method, Any).Invoke(w, null);

        static string Guid(string file) => AssetDatabase.AssetPathToGUID("Assets/_Fixtures/" + file);

        static string Issues(ModuleOrganizerWindow w) => string.Join("\n", w.rootVisualElement.Q("issues").Query<Label>().ToList().Select(l => l.text));

        // Manual check Step 2 without the mouse: the drops call the same ModuleEdits the window's drop handlers call.
        [Test]
        public void Scenario_IssuesGateApply_UndoRevertsAssign()
        {
            const string path = "Assets/ModuleConfigWindowTest.asset";
            var cfg = ScriptableObject.CreateInstance<ModuleConfig>();
            cfg.scopeFolders.Add("Assets/_Fixtures/Scope");
            AssetDatabase.CreateAsset(cfg, path);
            var w = ScriptableObject.CreateInstance<ModuleOrganizerWindow>();
            try
            {
                Call(w, "CreateGUI"); // finds the config and scans
                var scan = (ScanResult)typeof(ModuleOrganizerWindow).GetField("scan", Any).GetValue(w);
                Assert.That(scan, Is.Not.Null, "auto-scan");
                var alpha = ModuleEdits.AddModule(cfg);
                ModuleEdits.RenameModule(cfg, alpha, "Alpha");
                var apply = w.rootVisualElement.Q("apply");

                ModuleEdits.Assign(cfg, scan, new[] { Guid("Scope/FxA.cs") }, alpha, File.ReadAllText);
                Call(w, "Validate");
                var issues = Issues(w);
                StringAssert.Contains("[OutOfScopeRef]", issues);
                StringAssert.Contains("FxOut", issues);
                StringAssert.Contains("[UnassignedInScopeRef]", issues);
                StringAssert.Contains("FxB", issues);
                Assert.That(apply.enabledSelf, Is.False);

                ModuleEdits.Change(cfg, "Add scope folder", () => cfg.scopeFolders.Add("Assets/_Fixtures/Outside"));
                Undo.IncrementCurrentGroup();
                ModuleEdits.Assign(cfg, scan, new[] { "Scope/FxB.cs", "Scope/FxLambdaOnly.cs", "Outside/FxOut.cs" }.Select(Guid), alpha, File.ReadAllText);
                Call(w, "Validate");
                Assert.That(apply.enabledSelf, Is.True, Issues(w));

                // First click only arms (Plan tab shown); any re-validate (edit, rescan, undo) disarms.
                Call(w, "Apply");
                StringAssert.StartsWith("Confirm Apply (", ((Button)apply).text);
                Assert.That(w.rootVisualElement.Q<TabView>("tabs").activeTab.name, Is.EqualTo("tab-plan"));
                Call(w, "Validate");
                Assert.That(((Button)apply).text, Is.EqualTo("Apply"));

                Undo.PerformUndo();
                Assert.That(cfg.modules[0].scripts.Select(e => e.guid), Is.EqualTo(new[] { Guid("Scope/FxA.cs") }));
                Assert.That(apply.enabledSelf, Is.False, "undo re-validates");

                // An earlier Apply's asset migration still owed blocks Apply; Scan (compiled project) settles it.
                cfg.owedReserializeKeys.Add("Game.Old|Assembly-CSharp|Nothing");
                var blocker = typeof(ModuleOrganizerWindow).GetMethod("ApplyBlocker", Any);
                StringAssert.Contains("asset migration", (string)blocker.Invoke(w, null));
                Call(w, "Scan");
                Assert.That(cfg.owedReserializeKeys, Is.Empty);
                Assert.That((string)blocker.Invoke(w, null), Does.Not.Contain("asset migration"));
            }
            finally
            {
                Object.DestroyImmediate(w);
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
