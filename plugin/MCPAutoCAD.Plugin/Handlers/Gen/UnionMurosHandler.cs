using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// union_muros (protocol.md §5G.2): sustituye dos contornos cerrados de 4 vértices por su unión. Geometría en
    /// <see cref="WallJoin.Union"/> (GeometryCore); plan: borrar los dos contornos y sus sombreados, crear el contorno unido
    /// y, si alguno tenía sombreado, uno solo con el patrón del de muro_a (o el de muro_b si solo ese tenía).
    /// </summary>
    internal sealed class UnionMurosHandler : GeneratorBase
    {
        private static readonly string[] Tipos = { WallJoin.TipoL, WallJoin.TipoT, WallJoin.TipoCruce, WallJoin.TipoAuto };
        private static readonly string[] AllowedNames = BuildAllowed();

        public override string Name => "union_muros";

        public override string Description =>
            "ESCRIBE la unión de dos muros rectos (handles de sus contornos cerrados de 4 vértices) en un solo contorno limpio: recorta el solape o la tapa apoyada y encadena las caras (encuentros en L, T, cruce o colineales, a cualquier ángulo; cadenas de más muros se unen por pares), rehaciendo sus sombreados asociativos. Los muros son del usuario: exige confirmar: true si no están en capas MCP-*. Con preview: true solo calcula y devuelve números + preview_id.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            @"    ""muro_a"": " + ParamReader.SchemaHandle + @",
    ""muro_b"": " + ParamReader.SchemaHandle + @",
    ""tipo"": { ""type"": ""string"", ""enum"": [""L"", ""T"", ""cruce"", ""auto""], ""default"": ""auto"", ""description"": ""Encuentro esperado; si no coincide con el detectado se rechaza. auto = el que resulte."" },
    ""confirmar"": " + WallEntitySupport.SchemaConfirmar + @",
    ""capa"": " + DrawWrite.SchemaCapa,
            "muro_a", "muro_b");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(
            @"        ""tipo_detectado"": { ""type"": ""string"" },
        ""area_a"": { ""type"": ""number"" },
        ""area_b"": { ""type"": ""number"" },
        ""area_solape"": { ""type"": ""number"" },
        ""area_union"": { ""type"": ""number"" },
        ""n_vertices"": { ""type"": ""integer"" },
        ""longitud_contorno"": { ""type"": ""number"" }");

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => "muros";

        private static string[] BuildAllowed()
        {
            var list = new List<string> { "muro_a", "muro_b", "tipo", "confirmar", "capa" };
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }

        private sealed record Params(string MuroA, string MuroB, string Tipo, bool Confirmar, string? Capa, string? Espacio);

        protected override object ParseOwn(JsonElement p)
        {
            string a = RequiredHandle(p, "muro_a");
            string b = RequiredHandle(p, "muro_b");
            if (a == b)
            {
                throw ToolException.InvalidParams("muro_b: debe ser distinto de muro_a");
            }

            string tipo = ParamReader.OptionalEnum(p, "tipo", Tipos) ?? WallJoin.TipoAuto;
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, "muros");
            }

            return new Params(a, b, tipo, confirmar, capa, ParamReader.OptionalString(p, "espacio", 1, 255));
        }

        private static string RequiredHandle(JsonElement p, string name) =>
            p.TryGetProperty(name, out JsonElement el) ? AxisReader.ReadHandle(el, name) : throw ToolException.InvalidParams(name + ": obligatorio");

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own) => Array.Empty<(PointRef Ref, string Path)>();

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;
            HandleGuard.Resolved muros = HandleGuard.Resolve(db, tr, new[] { p.MuroA, p.MuroB }, p.Confirmar, advertencias);
            WallEntitySupport.CheckSpace(db, tr, muros, p.Espacio);
            WallEntitySupport.ClosedContour a = WallEntitySupport.ReadClosedContour(db, tr, p.MuroA, "muro_a", advertencias);
            WallEntitySupport.ClosedContour b = WallEntitySupport.ReadClosedContour(db, tr, p.MuroB, "muro_b", advertencias);
            WallJoin.JoinResult u = WallEntitySupport.Geometry(() => WallJoin.Union(a.Vertices, b.Vertices, p.Tipo));

            string capa = p.Capa ?? a.Capa;
            DrawLayerFactory.Check(db, tr, capa, "muros", out _);
            if (p.Capa == null && !string.Equals(a.Capa, b.Capa, StringComparison.OrdinalIgnoreCase))
            {
                advertencias.Add("union_muros: los muros están en capas distintas ('" + a.Capa + "', '" + b.Capa + "'); el contorno unido va a '" + a.Capa + "'.");
            }

            List<WallEntitySupport.AssociatedHatch> hatches = WallEntitySupport.AssociatedHatches(tr, a.Entidad, b.Entidad);
            WallEntitySupport.HatchRebuild rehacer = WallEntitySupport.PlanHatchRebuild(db, tr, hatches, p.Confirmar, advertencias);

            var entidades = new List<PlannedEntity> { new PlannedErase(a.Handle, a.Tipo), new PlannedErase(b.Handle, b.Tipo) };
            entidades.AddRange(rehacer.Borrados);
            int indiceContorno = entidades.Count;
            var unido = new PlannedPolyline(u.Contorno, true, null, 0.0, "muros", capa);
            entidades.Add(unido);
            // AssociatedHatches lista primero los de muro_a: el primero es "el de muro_a, o el de muro_b si solo ese tenía".
            WallEntitySupport.AssociatedHatch? propio = rehacer.Hatches.Find(h => h.SoloEsteContorno);
            if (propio != null)
            {
                entidades.Add(propio.ToPlanned(indiceContorno));
            }

            // Los que además tienen otros bucles no se funden con él: conservan esos bucles (§5G.0).
            foreach (WallEntitySupport.AssociatedHatch h in rehacer.Hatches.FindAll(h => !h.SoloEsteContorno))
            {
                entidades.Add(h.ReplacingWallLoops(indiceContorno));
            }

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tipo_detectado"] = u.TipoDetectado,
                ["area_a"] = u.AreaA,
                ["area_b"] = u.AreaB,
                ["area_solape"] = u.AreaSolape,
                ["area_union"] = u.AreaUnion,
                ["n_vertices"] = u.NVertices,
                ["longitud_contorno"] = unido.Metrics().longitud,
                ["n_entidades"] = entidades.Count - 2 - rehacer.Borrados.Count,   // solo lo creado, como entidades[] (§5G.0)
            };
            var geo = new GeneratedGeometry(entidades, numeros);
            geo.AnclasExtra.Add(a.Huella);
            geo.AnclasExtra.Add(b.Huella);
            geo.AnclasExtra.AddRange(rehacer.Huellas);
            return geo;
        }
    }
}
