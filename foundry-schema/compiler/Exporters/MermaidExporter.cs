using System.Text;

namespace Foundry.Schema.Compiler.Exporters;

/// <summary>
/// Exporter that converts domain entities and relationships into Mermaid class/ERD diagram markdown text.
/// </summary>
public static class MermaidExporter
{
    public static string ExportMermaid(SchemaModel schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine("classDiagram");

        if (schema.Entities != null)
        {
            foreach (var entity in schema.Entities)
            {
                sb.AppendLine($"    class {entity.Name} {{");

                foreach (var prop in entity.Properties)
                {
                    var prefix = prop.IsKey ? "+" : "~";
                    sb.AppendLine($"        {prefix}{prop.Type} {prop.Name}");
                }

                sb.AppendLine("    }");
            }
        }

        return sb.ToString();
    }
}
