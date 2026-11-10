using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// rotar (protocol.md §5D.8). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Gira las entidades
    /// alrededor de 'base' con TransformBy(Matrix3d.Rotation(rad, Z, base)); 'angulo' en grados antihorarios desde +X;
    /// 0 (mod 360) → INVALID_PARAMS. R4 con la caja girada (<see cref="Transform2D.RotateAbout"/>).
    /// </summary>
    internal sealed class RotarHandler : IToolHandler
    {
        private static readonly string[] Allowed = ModifySupport.CommonParams.Append("base").Append("angulo").ToArray();

        public string Name => "rotar";

        public string Description =>
            "Gira entidades existentes (por handle) alrededor del punto base, angulo en grados antihorarios desde +X, en un solo paso de deshacer; todo o nada. " +
            "Si alguna entidad no está en una capa MCP-* exige confirmar: true; rechaza atributos, entidades de bloques, viewports de papel, " +
            "capas bloqueadas o de xref, y destinos fuera de la extensión del espacio (salvo forzar: true).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = ModifySupport.BuildSchema(
            "    \"base\": " + ModifySupport.SchemaBase + ",\n"
            + @"    ""angulo"": { ""type"": ""number"", ""description"": ""Grados, antihorario desde +X; cualquier valor finito salvo 0 (mod 360)."" }",
            "base",
            "angulo");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(
            @"    ""base"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""angulo"": { ""type"": ""number"" }");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (ModifySupport.Common common, Point3 b, double deg) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            ModifySupport.Common common = ModifySupport.ReadCommon(p);
            Point3 b = ParamReader.RequiredPoint(p, "base");
            double deg = ModifySupport.RequiredFinite(p, "angulo");
            if (Transform2D.IsNullRotation(deg))
            {
                throw ToolException.InvalidParams("angulo: no puede ser 0");
            }

            return (common, b, deg);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (ModifySupport.Common c, Point3 b, double deg) = Parse(parameters);
            double rad = Transform2D.ToRadians(deg);
            return ModifySupport.Run(
                ctx,
                c,
                ModifySupport.Operation.Transform,
                Transform2D.RotateAbout(b.X, b.Y, rad),
                Matrix3d.Rotation(rad, Vector3d.ZAxis, new Point3d(b.X, b.Y, b.Z)),
                r =>
                {
                    r["base"] = new[] { b.X, b.Y, b.Z };
                    r["angulo"] = Transform2D.Normalize360(deg);
                });
        }
    }
}
