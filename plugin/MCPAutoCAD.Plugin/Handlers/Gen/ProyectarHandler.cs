using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// proyectar (protocol.md §5E.2): punto más cercano de una curva existente a un PuntoRef
    /// (<c>Curve.GetClosestPointTo(p, false)</c>, sin extender la curva). Hilo principal; requiere documento; SOLO
    /// LECTURA (StartOpenCloseTransaction, sin paso de deshacer). INSUNITS = 0 no se rechaza.
    /// </summary>
    internal sealed class ProyectarHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "punto", "handle" };

        public string Name => "proyectar";

        public string Description =>
            "LEE el punto más cercano de una curva existente (handle) a un PuntoRef: punto proyectado, distancia, parámetro y distancia desde el inicio de la curva. No modifica nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""punto"": " + PointRef.Schema + @",
    ""handle"": " + ParamReader.SchemaHandle + @"
  },
  ""required"": [""punto"", ""handle""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""punto"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""punto_proyectado"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""distancia"": { ""type"": ""number"" },
    ""parametro"": { ""type"": [""number"", ""null""] },
    ""distancia_desde_inicio"": { ""type"": [""number"", ""null""] },
    ""longitud_curva"": { ""type"": [""number"", ""null""] },
    ""handle"": { ""type"": ""string"" },
    ""tipo_entidad"": { ""type"": ""string"" },
    ""puntos_resueltos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""punto"", ""punto_proyectado"", ""distancia"", ""parametro"", ""distancia_desde_inicio"", ""longitud_curva"", ""handle"", ""tipo_entidad"", ""puntos_resueltos"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (PointRef punto, string handle) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            PointRef r = GenSchema.RequiredPointRef(p, "punto");
            string handle = ParamReader.OptionalHandle(p, "handle") ?? throw ToolException.InvalidParams("handle: obligatorio");
            return (r, handle.ToUpperInvariant());
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (PointRef r, string handle) = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> advertencias = AcadUtil.UnitWarnings(db);

            ResolvedPoint rp;
            string tipo;
            Point3 proy;
            double? parametro, desdeInicio, longitud;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                // Primero la curva (NOT_FOUND / no es curva) y después el punto.
                Entity e = PointRefResolver.Require(db, tr, handle);
                tipo = AcadUtil.DxfName(e);
                if (e is not Curve c)
                {
                    throw ToolException.InvalidParams("handle: la entidad " + handle + " (" + tipo + ") no es una curva",
                        new Dictionary<string, object?> { ["handle"] = handle });
                }

                rp = PointRefResolver.Resolve(db, tr, r, "punto", advertencias);
                Point3d closest;
                try
                {
                    closest = c.GetClosestPointTo(AcadUtil.ToPoint3d(rp.Punto), false);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    throw ToolException.InvalidGeometry("handle: no se pudo proyectar sobre " + handle + " (" + tipo + "): " + ex.ErrorStatus,
                        new Dictionary<string, object?> { ["handle"] = handle });
                }

                proy = PointRefResolver.ToPoint3(closest);
                parametro = TryNumber(() => c.GetParameterAtPoint(closest));
                desdeInicio = TryNumber(() => c.GetDistAtPoint(closest));
                longitud = TryNumber(() => c.GetDistanceAtParameter(c.EndParam));
                tr.Commit();
            }

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["punto"] = rp.Punto.ToArray(),
                ["punto_proyectado"] = proy.ToArray(),
                ["distancia"] = rp.Punto.Distance(proy),
                ["parametro"] = parametro,
                ["distancia_desde_inicio"] = desdeInicio,
                ["longitud_curva"] = longitud,
                ["handle"] = handle,
                ["tipo_entidad"] = tipo,
                ["puntos_resueltos"] = new List<Dictionary<string, object?>> { rp.ToJson() },
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = advertencias,
            };
        }

        /// <summary>Valor numérico finito o null si AutoCAD lanza (p. ej. eInvalidInput en curvas degeneradas).</summary>
        private static double? TryNumber(Func<double> f)
        {
            try
            {
                double v = f();
                return double.IsFinite(v) ? v : null;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
