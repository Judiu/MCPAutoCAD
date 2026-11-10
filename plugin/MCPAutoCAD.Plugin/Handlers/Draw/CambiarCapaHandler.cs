using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// cambiar_capa (0.8.1). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Pasa las entidades (por handle, 1 a
    /// 1000) a la capa indicada, resuelta como el resto de herramientas de dibujo (concepto LPS, capa existente o capa
    /// MCP-* / estándar que se crea). Comprobaciones de <see cref="HandleGuard"/> (existencia, atributos, bloques, viewports,
    /// capas bloqueadas o de xref, confirmar para capas sin MCP-*) antes de abrir el grupo de deshacer; la capa destino se
    /// valida con <see cref="DrawLayerFactory.Check"/> en la pasada de lectura. Un solo paso de deshacer.
    /// </summary>
    internal sealed class CambiarCapaHandler : IToolHandler
    {
        /// <summary>Máximo de handles por llamada (más que el 500 de mover/borrar: el cambio de capa es barato).</summary>
        public const int MaxHandles = 1000;

        private static readonly string[] Allowed = { "handles", "capa", "confirmar" };

        public string Name => "cambiar_capa";

        public string Description =>
            "CAMBIA DE CAPA entidades existentes (por handle, 1 a 1000) en un solo paso de deshacer. La capa destino es un concepto de lps.json, una capa existente o una capa MCP-*/estándar que se crea. " +
            "Entidades fuera de capas MCP-* exigen confirmar: true; rechaza handles inexistentes, atributos, entidades de bloques, viewports de papel y capas bloqueadas o de xref.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 1000, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Handles de las entidades a cambiar (1–1000, sin repetidos). No se admiten atributos, entidades de bloques, viewports de papel ni capas bloqueadas o de xref."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""confirmar"": " + ModifySupport.SchemaConfirmar + @"
  },
  ""required"": [""handles"", ""capa""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(@"
    ""capas_origen"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capa_creada"": { ""type"": ""boolean"" }");

        private sealed class Params
        {
            public Params(List<string> handles, string capa, bool confirmar)
            {
                Handles = handles;
                Capa = capa;
                Confirmar = confirmar;
            }

            public List<string> Handles { get; }

            public string Capa { get; }

            public bool Confirmar { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            List<string> handles = ParamReader.RequiredHandles(p, "handles", 1, MaxHandles);
            string capa = ParamReader.RequiredText(p, "capa", 1, 255);
            DrawLayerFactory.Plan(capa, null);
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            return new Params(handles, capa, confirmar);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            var warnings = new List<string>();
            int ins = AcadUtil.InsUnits(db);

            // Pasada de solo lectura: protección de entidades y destino válido, sin abrir el grupo de deshacer.
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                HandleGuard.Resolve(db, tr, p.Handles, p.Confirmar, warnings);
                DrawLayerFactory.Check(db, tr, p.Capa, null, out _);
                tr.Commit();
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, p.Confirmar, new List<string>());
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Capa, null, warnings);

                foreach (HandleGuard.Item item in res.Items)
                {
                    item.Entity.UpgradeOpen();
                    item.Entity.LayerId = layer.Id;
                }

                tr.Commit();

                Dictionary<string, object?> r = DrawWrite.Response(pid, p.Handles, layer, res.Espacio, ins, warnings);
                r["n"] = res.Items.Count;
                r["n_usuario"] = res.NUsuario;
                r["capas"] = new List<string> { layer.Nombre };
                r["capas_origen"] = res.Capas;
                r["capa_creada"] = layer.Creada;
                return r;
            }
        }
    }
}
