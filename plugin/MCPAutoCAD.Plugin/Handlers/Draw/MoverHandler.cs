using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// mover (protocol.md §5D.8). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Traslada las entidades
    /// por handle con TransformBy(Matrix3d.Displacement) tras las comprobaciones de <see cref="HandleGuard"/> (pasos 1–5)
    /// y R4 con la caja trasladada (<see cref="Transform2D.Translate"/>). Desplazamiento todo cero → INVALID_PARAMS.
    /// </summary>
    internal sealed class MoverHandler : IToolHandler
    {
        private static readonly string[] Allowed = ModifySupport.CommonParams.Append("desplazamiento").ToArray();

        public string Name => "mover";

        public string Description =>
            "Mueve entidades existentes (por handle) un desplazamiento [dx, dy(, dz)] en un solo paso de deshacer; todo o nada. " +
            "Si alguna entidad no está en una capa MCP-* exige confirmar: true; rechaza atributos, entidades de bloques, viewports de papel, " +
            "capas bloqueadas o de xref, y destinos fuera de la extensión del espacio (salvo forzar: true).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = ModifySupport.BuildSchema(
            "    \"desplazamiento\": " + ModifySupport.SchemaDesplazamiento,
            "desplazamiento");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(
            @"    ""desplazamiento"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (ModifySupport.Common common, Point3 d) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            ModifySupport.Common common = ModifySupport.ReadCommon(p);
            Point3 d = ModifySupport.ReadDisplacement(p, allowZero: false);
            return (common, d);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (ModifySupport.Common c, Point3 d) = Parse(parameters);
            return ModifySupport.Run(
                ctx,
                c,
                ModifySupport.Operation.Transform,
                Transform2D.Translate(d.X, d.Y),
                Matrix3d.Displacement(ModifySupport.ToVector(d)),
                r => r["desplazamiento"] = new[] { d.X, d.Y, d.Z });
        }
    }
}
