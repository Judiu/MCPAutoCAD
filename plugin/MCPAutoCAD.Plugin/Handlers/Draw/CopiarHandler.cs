using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// copiar (protocol.md §5D.8). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Clona las entidades en su
    /// mismo espacio (Database.DeepCloneObjects: un INSERT conserva sus atributos) y traslada cada clon con
    /// TransformBy(Matrix3d.Displacement). Desplazamiento cero admitido (copia superpuesta). handles = los NUEVOS;
    /// handles_origen = los originales en el mismo orden.
    /// </summary>
    internal sealed class CopiarHandler : IToolHandler
    {
        private static readonly string[] Allowed = ModifySupport.CommonParams.Append("desplazamiento").ToArray();

        public string Name => "copiar";

        public string Description =>
            "Copia entidades existentes (por handle) en su mismo espacio, desplazadas [dx, dy(, dz)] (cero = copia superpuesta), en un solo paso de deshacer; " +
            "devuelve los handles nuevos y handles_origen en el mismo orden. Si alguna entidad no está en una capa MCP-* exige confirmar: true; " +
            "rechaza atributos, entidades de bloques, viewports de papel, capas bloqueadas o de xref, y destinos fuera de la extensión del espacio (salvo forzar: true).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = ModifySupport.BuildSchema(
            "    \"desplazamiento\": " + ModifySupport.SchemaDesplazamiento,
            "desplazamiento");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(
            @"    ""desplazamiento"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""handles_origen"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (ModifySupport.Common common, Point3 d) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            ModifySupport.Common common = ModifySupport.ReadCommon(p);
            Point3 d = ModifySupport.ReadDisplacement(p, allowZero: true);
            return (common, d);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (ModifySupport.Common c, Point3 d) = Parse(parameters);
            return ModifySupport.Run(
                ctx,
                c,
                ModifySupport.Operation.Copy,
                Transform2D.Translate(d.X, d.Y),
                Matrix3d.Displacement(ModifySupport.ToVector(d)),
                r => r["desplazamiento"] = new[] { d.X, d.Y, d.Z });
        }
    }
}
