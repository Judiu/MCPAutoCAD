using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// punto_de (protocol.md §5E.2). Hilo principal; requiere documento; SOLO LECTURA (§3.9: StartOpenCloseTransaction,
    /// sin DocumentLock ni paso de deshacer). INSUNITS = 0 no se rechaza (es una lectura): solo se advierte.
    /// </summary>
    internal sealed class PuntoDeHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "ref" };

        public string Name => "punto_de";

        public string Description =>
            "LEE un punto del dibujo: resuelve un PuntoRef ([x, y], {handle, punto: inicio|fin|medio|centro|vertice, indice}, o {handle, interseccion_con, cerca_de}) a coordenadas WCS. No modifica nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ref"": " + PointRef.Schema + @"
  },
  ""required"": [""ref""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""punto"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""ref"": { ""type"": [""array"", ""object""] },
    ""ancla"": { ""type"": [""string"", ""null""] },
    ""tipo_entidad"": { ""type"": [""string"", ""null""] },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""punto"", ""ref"", ""ancla"", ""tipo_entidad"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static PointRef Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            return GenSchema.RequiredPointRef(p, "ref");
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            PointRef r = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> advertencias = AcadUtil.UnitWarnings(db);

            ResolvedPoint rp;
            string? tipo = null;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                rp = PointRefResolver.Resolve(db, tr, r, "ref", advertencias);
                if (r.Handle != null)
                {
                    // Para una intersección, el tipo de la primera curva (handle).
                    Entity? e = AcadUtil.TryGetEntityByHandle(db, r.Handle, tr);
                    tipo = e != null ? AcadUtil.DxfName(e) : null;
                }

                tr.Commit();
            }

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["punto"] = rp.Punto.ToArray(),
                ["ref"] = r.ToJson(),
                ["ancla"] = rp.Ancla?.Handle,
                ["tipo_entidad"] = tipo,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = advertencias,
            };
        }
    }
}
