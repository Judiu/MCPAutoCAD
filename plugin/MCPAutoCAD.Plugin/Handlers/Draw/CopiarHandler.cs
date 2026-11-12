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
        private static readonly string[] Allowed =
            ModifySupport.CommonParams.Append("desplazamiento").Append("permitir_origen_bloqueado").ToArray();

        public string Name => "copiar";

        public string Description =>
            "Copia entidades existentes (por handle) en su mismo espacio, desplazadas [dx, dy(, dz)] (cero = copia superpuesta), en un solo paso de deshacer; " +
            "devuelve los handles nuevos y handles_origen en el mismo orden. Si alguna entidad no está en una capa MCP-* exige confirmar: true; " +
            "rechaza atributos, entidades de bloques, viewports de papel, capas de xref, capas bloqueadas (salvo permitir_origen_bloqueado: true; " +
            "las copias quedan en esa capa bloqueada) y destinos fuera de la extensión del espacio (salvo forzar: true).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = ModifySupport.BuildSchemaConHandles(
            SchemaHandlesCopiar,
            "    \"desplazamiento\": " + ModifySupport.SchemaDesplazamiento + ",\n"
            + "    \"permitir_origen_bloqueado\": " + SchemaPermitirOrigenBloqueado,
            "desplazamiento");

        private const string SchemaHandlesCopiar =
            @"{ ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Handles de las entidades a copiar (1–500, sin repetidos), todas en el mismo espacio (modelo o un mismo layout). No se admiten atributos (use el INSERT), entidades dentro de bloques, viewports de papel ni capas de xref; capas bloqueadas solo con permitir_origen_bloqueado: true."" }";

        private const string SchemaPermitirOrigenBloqueado =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = admite entidades de origen en capas bloqueadas (el origen no se modifica). Las copias quedan en la misma capa bloqueada: para moverlas o borrarlas después, desbloquee con estado_capas. Las capas de xref se siguen rechazando."" }";

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(
            @"    ""desplazamiento"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""handles_origen"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (ModifySupport.Common common, Point3 d) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            ModifySupport.Common leido = ModifySupport.ReadCommon(p);
            bool permitir = ParamReader.OptionalBool(p, "permitir_origen_bloqueado") ?? false;
            var common = new ModifySupport.Common(leido.Handles, leido.Confirmar, leido.Forzar, leido.AceptarSinUnidades, permitir);
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
