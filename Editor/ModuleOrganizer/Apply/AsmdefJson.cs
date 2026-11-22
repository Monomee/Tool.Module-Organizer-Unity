using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // An .asmdef laid out the way Unity's own inspector writes it (4-space indent, its field order, `[]` when empty).
    public static class AsmdefJson
    {
        public static string Write(AsmdefOp op)
        {
            var sb = new StringBuilder("{\n");
            sb.Append("    \"name\": ").Append(Str(op.name)).Append(",\n");
            sb.Append("    \"rootNamespace\": ").Append(Str(op.rootNamespace)).Append(",\n");
            Array(sb, "references", op.references);
            Array(sb, "includePlatforms", op.editorOnly ? new List<string> { "Editor" } : null);
            Array(sb, "excludePlatforms", null);
            sb.Append("    \"allowUnsafeCode\": ").Append(op.allowUnsafeCode ? "true" : "false").Append(",\n");
            sb.Append("    \"overrideReferences\": false,\n");
            Array(sb, "precompiledReferences", null);
            sb.Append("    \"autoReferenced\": true,\n");
            Array(sb, "defineConstraints", null);
            Array(sb, "versionDefines", null);
            sb.Append("    \"noEngineReferences\": false\n}");
            return sb.ToString();
        }

        static void Array(StringBuilder sb, string field, List<string> items)
        {
            sb.Append("    \"").Append(field).Append("\": ");
            if (items == null || items.Count == 0) sb.Append("[]");
            else sb.Append("[\n").Append(string.Join(",\n", items.Select(i => "        " + Str(i)))).Append("\n    ]");
            sb.Append(",\n");
        }

        static string Str(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
