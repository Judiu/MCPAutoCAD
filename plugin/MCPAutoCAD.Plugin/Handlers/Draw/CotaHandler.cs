using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// cota (protocol.md §5D.6). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). lineal → RotatedDimension,
    /// alineada → AlignedDimension, con el estilo pedido (o Database.Dimstyle) y la capa del concepto 'cotas' por defecto.
    /// Escala: por defecto, la del estilo y la del dibujo tal cual (no se toca ninguna variable del dibujo). Con
    /// escala_global (0.10.x) se fija el DIMSCALE de ESTA cota (override de la entidad, no del estilo); en una cota o
    /// estilo anotativo se ignora y se avisa.
    /// texto_sustituto: TextEscape.EscapeDbText (R7) — el marcador '&lt;&gt;' (medida real) se conserva tal cual porque
    /// EscapeDbText solo cambia las parejas '%%'. Ver nota de la clase sobre los códigos MTEXT.
    /// Nota (duda abierta, no cambia el contrato): el texto de una cota admite además códigos de formato MTEXT
    /// (\P, \f…;, {…}); el contrato §5D.6 solo pide EscapeDbText, así que una barra inversa del cliente puede
    /// interpretarse como código de formato.
    /// </summary>
    internal sealed class CotaHandler : IToolHandler
    {
        public const string Lineal = "lineal";
        public const string Alineada = "alineada";
        public const string CoincidentPoints = "p2: coincide con p1";
        public const string RotationOnlyLinear = "rotacion: solo aplica a tipo lineal";
        private const string Concepto = "cotas";
        private const int MaxOverrideLength = 500;
        private const double CoincidenceTol = 1e-9;

        private static readonly string[] Tipos = { Lineal, Alineada };
        private static readonly string[] Own = { "tipo", "p1", "p2", "posicion_linea", "rotacion", "estilo", "texto_sustituto", "escala_global" };
        private static readonly string[] Allowed = BuildAllowed();

        public string Name => "cota";

        public string Description =>
            "ESCRIBE en el dibujo: crea una cota lineal (horizontal/vertical/rotada según 'rotacion') o alineada entre p1 y p2, " +
            "con la línea de cota por 'posicion_linea', en un solo paso de deshacer. Usa el estilo de cota indicado o el actual, " +
            "y la capa del concepto 'cotas' del estándar LPS si no se da otra. Devuelve la medida calculada por AutoCAD.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""tipo"": { ""type"": ""string"", ""enum"": [""lineal"", ""alineada""], ""description"": ""'lineal' = RotatedDimension (dirección fijada por 'rotacion'); 'alineada' = AlignedDimension paralela a p1-p2."" },
    ""p1"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Origen de la primera línea de referencia [x, y] o [x, y, z] (WCS)."" },
    ""p2"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Origen de la segunda línea de referencia; distinto de p1."" },
    ""posicion_linea"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Punto por el que pasa la línea de cota."" },
    ""rotacion"": { ""type"": ""number"", ""default"": 0, ""description"": ""Solo tipo 'lineal': dirección de la línea de cota en grados (0 = horizontal, 90 = vertical)."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de cota existente (sin distinguir mayúsculas); si no existe, NOT_FOUND con la lista de estilos disponibles (data.disponibles). Por defecto, el actual. Crear estilos nuevos: estilo_cota."" },
    ""escala_global"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Escala global (DIMSCALE) de ESTA cota, como override de la del estilo (> 0). Sin él, se usa la del estilo."" },
    ""texto_sustituto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 500, ""description"": ""Texto literal que sustituye a la medida; '<>' representa la medida real (p. ej. '<> m')."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""tipo"", ""p1"", ""p2"", ""posicion_linea""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""entidad"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" },
      ""tipo"": { ""type"": ""string"" },
      ""medida"": { ""type"": [""number"", ""null""] },
      ""estilo"": { ""type"": ""string"" },
      ""texto"": { ""type"": ""string"" },
      ""p1"": { ""type"": ""array"" },
      ""p2"": { ""type"": ""array"" },
      ""posicion_linea"": { ""type"": ""array"" },
      ""bbox"": { ""type"": [""array"", ""null""] },
      ""escala_global"": { ""type"": [""number"", ""null""], ""description"": ""DIMSCALE efectivo de la cota (null si no se puede leer)."" } } },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""handles"", ""entidad"", ""capa"", ""capa_creada"", ""espacio"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class CotaParams
        {
            public CotaParams(string tipo, Point3 p1, Point3 p2, Point3 linea, double rotacion, string? estilo, string? texto, DrawWrite.Common common, double? escala)
            {
                Escala = escala;
                Tipo = tipo;
                P1 = p1;
                P2 = p2;
                Linea = linea;
                Rotacion = rotacion;
                Estilo = estilo;
                Texto = texto;
                Common = common;
            }

            public string Tipo { get; }
            public Point3 P1 { get; }
            public Point3 P2 { get; }
            public Point3 Linea { get; }
            public double Rotacion { get; }
            public string? Estilo { get; }
            public string? Texto { get; }
            public DrawWrite.Common Common { get; }

            /// <summary>escala_global: override DIMSCALE de la cota; null = la del estilo.</summary>
            public double? Escala { get; }
        }

        private static string[] BuildAllowed()
        {
            var list = new List<string>(Own);
            list.AddRange(DrawWrite.CommonParams);
            return list.ToArray();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Valida y convierte params. Sin AutoCAD: hilo del pipe y otra vez en el principal.</summary>
        private static CotaParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            if (!ParamReader.Has(p, "tipo"))
            {
                throw ToolException.InvalidParams("tipo: obligatorio");
            }

            string tipo = ParamReader.OptionalEnum(p, "tipo", Tipos)!;
            Point3 p1 = ParamReader.RequiredPoint(p, "p1");
            Point3 p2 = ParamReader.RequiredPoint(p, "p2");
            Point3 linea = ParamReader.RequiredPoint(p, "posicion_linea");
            if (p1.Distance(p2) <= CoincidenceTol)
            {
                throw ToolException.InvalidParams(CoincidentPoints);
            }

            double rotacion = 0.0;
            if (p.TryGetProperty("rotacion", out JsonElement rotEl))
            {
                if (string.Equals(tipo, Alineada, StringComparison.Ordinal))
                {
                    throw ToolException.InvalidParams(RotationOnlyLinear);
                }

                if (rotEl.ValueKind != JsonValueKind.Number || !rotEl.TryGetDouble(out rotacion) || !double.IsFinite(rotacion))
                {
                    throw ToolException.InvalidParams("rotacion: debe ser un número finito (grados)");
                }
            }

            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            string? texto = ParamReader.Has(p, "texto_sustituto")
                ? ParamReader.RequiredText(p, "texto_sustituto", 1, MaxOverrideLength)
                : null;
            DrawWrite.Common common = DrawWrite.ReadCommon(p, Concepto);
            double? escala = ParamReader.OptionalNumber(p, "escala_global", 0, double.PositiveInfinity, exclusiveMin: true);
            return new CotaParams(tipo, p1, p2, linea, rotacion, estilo, texto, common, escala);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            CotaParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);                       // R2
            BBox2 box = BBox2.FromPoints(new[] { p.P1, p.P2, p.Linea });
            PlacementCheck.Run(db, p.Common.Espacio, new[] { box }, p.Common.Forzar, "entidades", warnings); // R4/R5

            // Estilo ANTES del UndoGroup (lectura): un NOT_FOUND no crea paso de deshacer.
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                ResolveStyle(db, ro, p.Estilo);
                ro.Commit();
            }

            return UndoGroup.Run(doc, d => Write(d, p, ctx.Pid, ins, warnings));
        }

        /// <summary>Estilo pedido (sin mayúsculas) o el actual (Database.Dimstyle). Inexistente → NOT_FOUND data.estilo.</summary>
        private static (ObjectId id, string name) ResolveStyle(Database db, Transaction tr, string? estilo)
        {
            if (estilo == null)
            {
                ObjectId cur = db.Dimstyle;
                if (cur.IsNull || cur.IsErased)
                {
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        "El estilo de cota actual del dibujo no es válido (nulo o borrado); indique 'estilo'.",
                        new Dictionary<string, object?> { ["estilo"] = null });
                }

                var curRec = (DimStyleTableRecord)tr.GetObject(cur, OpenMode.ForRead);
                RequireNoXref(curRec);
                return (cur, curRec.Name);
            }

            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            bool has;
            try
            {
                has = table.Has(estilo);
            }
            catch (AcRuntimeException)
            {
                has = false;
            }

            ObjectId id = has ? table[estilo] : ObjectId.Null;
            if (id.IsNull || id.IsErased)
            {
                // Lista de estilos disponibles para que el cliente pueda corregir el nombre.
                var disponibles = new List<string>();
                foreach (ObjectId candidate in table)
                {
                    if (!candidate.IsErased)
                    {
                        disponibles.Add(((DimStyleTableRecord)tr.GetObject(candidate, OpenMode.ForRead)).Name);
                    }
                }

                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El estilo de cota '" + estilo + "' no existe en el dibujo. Disponibles: " + string.Join(", ", disponibles) + ".",
                    new Dictionary<string, object?> { ["estilo"] = estilo, ["disponibles"] = disponibles });
            }

            var rec = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            RequireNoXref(rec);
            return (id, rec.Name);
        }

        /// <summary>Estilo dependiente de una xref (IsDependent o nombre con '|'): no se usa ni se modifica. INVALID_PARAMS.</summary>
        internal static void RequireNoXref(DimStyleTableRecord rec)
        {
            if (rec.IsDependent || rec.Name.IndexOf('|') >= 0)
            {
                throw ToolException.InvalidParams(
                    "el estilo de cota '" + rec.Name + "' es dependiente de una xref; use un estilo del dibujo.",
                    new Dictionary<string, object?> { ["estilo"] = rec.Name, ["xref"] = true });
            }
        }

        /// <summary>Escritura dentro de UNA transacción (R1); cualquier excepción la descarta.</summary>
        private static object Write(Document doc, CotaParams p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);    // R5
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Common.Capa, Concepto, warnings); // R3
                (ObjectId styleId, string styleName) = ResolveStyle(db, tr, p.Estilo);

                string dimText = p.Texto == null ? "" : TextEscape.EscapeDbText(p.Texto);   // R7; '<>' se conserva
                Point3d a = AcadUtil.ToPoint3d(p.P1);
                Point3d b = AcadUtil.ToPoint3d(p.P2);
                Point3d l = AcadUtil.ToPoint3d(p.Linea);

                Dimension dim = string.Equals(p.Tipo, Lineal, StringComparison.Ordinal)
                    ? new RotatedDimension(Normalize360(p.Rotacion) * Math.PI / 180.0, a, b, l, dimText, styleId)
                    : new AlignedDimension(a, b, l, dimText, styleId);

                DrawWrite.ApplyByLayer(dim, db, layer.Id);
                if (!styleId.IsNull)
                {
                    // SetDatabaseDefaults (en ApplyByLayer) podría fijar el estilo actual: se reafirma el pedido.
                    dim.DimensionStyle = styleId;
                }

                if (p.Escala.HasValue)
                {
                    // Anotativo: la escala la gobierna la escala de anotación; DIMSCALE no se aplica (se avisa).
                    bool anotativo = dim.Annotative == AnnotativeStates.True
                        || ((DimStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead)).Annotative == AnnotativeStates.True;
                    if (anotativo)
                    {
                        warnings.Add("escala_global ignorada: la cota o su estilo es anotativo (la escala la fija la escala de anotación).");
                    }
                    else
                    {
                        dim.Dimscale = p.Escala.Value;   // override de esta cota (después del estilo)
                    }
                }

                var space = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);
                string handle = DrawWrite.Append(space, tr, dim);

                try
                {
                    dim.RecomputeDimensionBlock(true);
                }
                catch (AcRuntimeException)
                {
                    // Sin bloque de cota regenerado: la medida y la caja se leen igualmente (pueden salir null).
                }

                double? measurement = null;
                try
                {
                    double m = dim.Measurement;
                    measurement = double.IsFinite(m) ? m : null;
                }
                catch (AcRuntimeException)
                {
                    measurement = null;
                }

                Point3d x1 = a, x2 = b, dl = l;
                if (dim is RotatedDimension rd)
                {
                    x1 = rd.XLine1Point;
                    x2 = rd.XLine2Point;
                    dl = rd.DimLinePoint;
                }
                else if (dim is AlignedDimension ad)
                {
                    x1 = ad.XLine1Point;
                    x2 = ad.XLine2Point;
                    dl = ad.DimLinePoint;
                }

                BBox2? bbox = AcadUtil.TryBBox2(dim);
                var entity = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = handle,
                    ["tipo"] = p.Tipo,
                    ["medida"] = measurement,
                    ["estilo"] = styleName,
                    ["texto"] = p.Texto ?? "",
                    ["p1"] = AcadUtil.ToArray(x1),
                    ["p2"] = AcadUtil.ToArray(x2),
                    ["posicion_linea"] = AcadUtil.ToArray(dl),
                    ["bbox"] = bbox?.ToArray2D(),
                    ["escala_global"] = AcadUtil.NumberOrNull(dim.Dimscale),
                };

                Dictionary<string, object?> result = DrawWrite.Response(pid, new List<string> { handle }, layer, resolved.Name, ins, warnings);
                result["entidad"] = entity;
                tr.Commit();
                return result;
            }
        }

        private static double Normalize360(double grados)
        {
            double r = grados % 360.0;
            if (r < 0)
            {
                r += 360.0;
            }

            return r >= 360.0 ? 0.0 : r;
        }
    }
}
