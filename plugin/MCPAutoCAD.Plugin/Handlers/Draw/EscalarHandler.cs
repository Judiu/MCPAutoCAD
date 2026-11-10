using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// escalar (protocol.md §5D.8). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Escala las entidades
    /// desde 'base' con TransformBy(Matrix3d.Scaling(factor, base)); factor &gt; 0 y distinto de 1. R4 con la caja
    /// escalada (<see cref="Transform2D.ScaleAbout"/>).
    /// </summary>
    internal sealed class EscalarHandler : IToolHandler
    {
        private static readonly string[] Allowed = ModifySupport.CommonParams.Append("base").Append("factor").ToArray();

        public string Name => "escalar";

        public string Description =>
            "Escala entidades existentes (por handle) desde el punto base con un factor > 0 y distinto de 1, en un solo paso de deshacer; todo o nada. " +
            "Si alguna entidad no está en una capa MCP-* exige confirmar: true; rechaza atributos, entidades de bloques, viewports de papel, " +
            "capas bloqueadas o de xref, y destinos fuera de la extensión del espacio (salvo forzar: true).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = ModifySupport.BuildSchema(
            "    \"base\": " + ModifySupport.SchemaBase + ",\n"
            + @"    ""factor"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Factor de escala uniforme, mayor que 0 y distinto de 1."" }",
            "base",
            "factor");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(
            @"    ""base"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""factor"": { ""type"": ""number"" }");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (ModifySupport.Common common, Point3 b, double f) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            ModifySupport.Common common = ModifySupport.ReadCommon(p);
            Point3 b = ParamReader.RequiredPoint(p, "base");
            double f = ParamReader.RequiredNumber(p, "factor", 0, double.PositiveInfinity, exclusiveMin: true);
            if (f == 1.0)
            {
                throw ToolException.InvalidParams("factor: no puede ser 1");
            }

            return (common, b, f);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (ModifySupport.Common c, Point3 b, double f) = Parse(parameters);
            return ModifySupport.Run(
                ctx,
                c,
                ModifySupport.Operation.Transform,
                Transform2D.ScaleAbout(b.X, b.Y, f),
                Matrix3d.Scaling(f, new Point3d(b.X, b.Y, b.Z)),
                r =>
                {
                    r["base"] = new[] { b.X, b.Y, b.Z };
                    r["factor"] = f;
                });
        }
    }
}
