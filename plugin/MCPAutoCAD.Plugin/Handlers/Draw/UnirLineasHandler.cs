using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// unir_lineas (0.8.1). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Une 2 a 100 LINE colineales (solapadas
    /// o con huecos) en UNA sola LINE entre sus dos extremos más alejados. La LINE del PRIMER handle se modifica (conserva su
    /// capa y propiedades) y el resto se borra; un solo paso de deshacer. Geometría en <see cref="LineMerge"/> (GeometryCore).
    /// Tolerancia: <c>tolerancia</c> si se indica (unidades de dibujo, &gt; 0); si no, 1e-6 × el mayor lado de la extensión del
    /// dibujo (<c>Database.Extmin</c>/<c>Extmax</c>), con suelo 1e-9 (<see cref="LineMerge.DefaultTolerance"/>).
    /// Rechazos (todos antes de abrir el grupo de deshacer): handle que no es LINE, longitud cero, o no colineal (se nombra el
    /// handle que rompe la colinealidad).
    /// </summary>
    internal sealed class UnirLineasHandler : IToolHandler
    {
        public const int MinHandles = 2;
        public const int MaxHandles = 100;

        private static readonly string[] Allowed = { "handles", "tolerancia", "confirmar", "aceptar_sin_unidades" };

        public string Name => "unir_lineas";

        public string Description =>
            "UNE 2 a 100 LINE colineales (solapadas o con huecos, p. ej. tras borrar un tabique) en UNA LINE entre sus extremos más alejados, en un solo paso de deshacer. " +
            "Conserva la capa y propiedades de la PRIMERA LINE y borra las demás. Tolerancia por defecto 1e-6 × extensión del dibujo, o la indicada en unidades de dibujo. " +
            "Rechaza líneas no colineales (indica cuál rompe la colinealidad), de longitud cero, o que no sean LINE.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handles"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Handles de LINE colineales (2–100, sin repetidos). La primera conserva su capa y propiedades."" },
    ""tolerancia"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Distancia máxima de desviación respecto de la recta, en unidades de dibujo. Por defecto 1e-6 × el mayor lado de la extensión del dibujo (mínimo 1e-9)."" },
    ""confirmar"": " + ModifySupport.SchemaConfirmar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""handles""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(@"
    ""entidad"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" },
      ""inicio"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
      ""fin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
      ""longitud"": { ""type"": ""number"" } } },
    ""eliminadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""tolerancia"": { ""type"": ""number"" }");

        private sealed class Params
        {
            public Params(List<string> handles, double? tolerancia, bool confirmar, bool aceptar)
            {
                Handles = handles;
                Tolerancia = tolerancia;
                Confirmar = confirmar;
                AceptarSinUnidades = aceptar;
            }

            public List<string> Handles { get; }

            public double? Tolerancia { get; }

            public bool Confirmar { get; }

            public bool AceptarSinUnidades { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            List<string> handles = ParamReader.RequiredHandles(p, "handles", MinHandles, MaxHandles);
            double? tol = ParamReader.OptionalNumber(p, "tolerancia", 0, double.PositiveInfinity, exclusiveMin: true);
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            bool aceptar = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            return new Params(handles, tol, confirmar, aceptar);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.AceptarSinUnidades, warnings);

            // Pasada de solo lectura: todo rechazo ocurre aquí, fuera del grupo de deshacer.
            double tol;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, p.Confirmar, warnings);
                List<(Point3 A, Point3 B)> segs = SegmentsOf(res, p.Handles);
                tol = p.Tolerancia ?? LineMerge.DefaultTolerance(ExtentsSpan(db));
                Check(segs, tol, p.Handles);
                tr.Commit();
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings, tol));
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings, double tol)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Segunda resolución y análisis: lo que no siga siendo válido se rechaza antes de tocar nada.
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, p.Confirmar, new List<string>());
                List<(Point3 A, Point3 B)> segs = SegmentsOf(res, p.Handles);
                LineMerge.Analysis analysis = Check(segs, tol, p.Handles);

                string firstHandle = p.Handles[0];
                var eliminadas = new List<string>();
                for (int i = 0; i < res.Items.Count; i++)
                {
                    HandleGuard.Item item = res.Items[i];
                    item.Entity.UpgradeOpen();
                    if (i == 0)
                    {
                        var line = (Line)item.Entity;
                        line.StartPoint = AcadUtil.ToPoint3d(analysis.Inicio);
                        line.EndPoint = AcadUtil.ToPoint3d(analysis.Fin);
                    }
                    else
                    {
                        item.Entity.Erase();
                        eliminadas.Add(item.Info.Handle);
                    }
                }

                tr.Commit();

                var entidad = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = firstHandle,
                    ["inicio"] = AcadUtil.ToArray(AcadUtil.ToPoint3d(analysis.Inicio)),
                    ["fin"] = AcadUtil.ToArray(AcadUtil.ToPoint3d(analysis.Fin)),
                    ["longitud"] = LineMerge.Length((analysis.Inicio, analysis.Fin)),
                };

                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["pid"] = pid,
                    ["handles"] = new List<string> { firstHandle },
                    ["n"] = res.Items.Count,
                    ["n_usuario"] = res.NUsuario,
                    ["espacio"] = res.Espacio,
                    ["capas"] = res.Capas,
                    ["insunits"] = ins,
                    ["unidades"] = UnitsTable.Name(ins),
                    ["advertencias"] = warnings,
                    ["entidad"] = entidad,
                    ["eliminadas"] = eliminadas,
                    ["tolerancia"] = tol,
                };
            }
        }

        /// <summary>Extremos de cada entidad, en el orden de handles. Rechaza las que no son LINE.</summary>
        private static List<(Point3 A, Point3 B)> SegmentsOf(HandleGuard.Resolved res, List<string> handles)
        {
            var segs = new List<(Point3 A, Point3 B)>(res.Items.Count);
            for (int i = 0; i < res.Items.Count; i++)
            {
                if (!(res.Items[i].Entity is Line line))
                {
                    throw ToolException.InvalidParams(
                        "handles[" + i + "]: no es una LINE",
                        new Dictionary<string, object?> { ["handle"] = handles[i], ["tipo"] = AcadUtil.DxfName(res.Items[i].Entity) });
                }

                segs.Add((ToPoint3(line.StartPoint), ToPoint3(line.EndPoint)));
            }

            return segs;
        }

        /// <summary>Aplica <see cref="LineMerge.Analyze"/> y convierte sus rechazos en INVALID_PARAMS con el handle concreto.</summary>
        private static LineMerge.Analysis Check(List<(Point3 A, Point3 B)> segs, double tol, List<string> handles)
        {
            LineMerge.Analysis a;
            try
            {
                a = LineMerge.Analyze(segs, tol);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            if (a.Degenerate.Count > 0)
            {
                int i = a.Degenerate[0];
                throw ToolException.InvalidParams(
                    "handles[" + i + "]: la LINE tiene longitud cero",
                    new Dictionary<string, object?> { ["handle"] = handles[i] });
            }

            if (a.Breaking.Count > 0)
            {
                var rompen = new List<string>();
                foreach (int i in a.Breaking)
                {
                    rompen.Add(handles[i]);
                }

                throw ToolException.InvalidParams(
                    "handles: no son colineales; rompe la colinealidad el handle " + rompen[0]
                    + " (desviación " + a.MaxDistance.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
                    + " > tolerancia " + tol.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) + ")",
                    new Dictionary<string, object?>
                    {
                        ["handle"] = rompen[0],
                        ["fuera_de_recta"] = rompen,
                        ["tolerancia"] = tol,
                    });
            }

            return a;
        }

        /// <summary>Mayor lado de la extensión del dibujo; 1 si no se puede calcular (la tolerancia por defecto queda en 1e-6).</summary>
        internal static double ExtentsSpan(Database db)
        {
            try
            {
                // EXTMIN/EXTMAX del dibujo (Database.Extmin/Extmax).
                double span = Math.Max(db.Extmax.X - db.Extmin.X, db.Extmax.Y - db.Extmin.Y);
                return double.IsFinite(span) && span > 0 ? span : 1.0;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return 1.0;
            }
        }

        private static Point3 ToPoint3(Point3d p) => new Point3(p.X, p.Y, p.Z);
    }
}
