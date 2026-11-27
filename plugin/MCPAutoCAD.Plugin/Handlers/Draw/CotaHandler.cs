using System;
using System.Collections.Generic;
using System.Globalization;
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
        public const int MaxItems = 100;
        public const string Lineal = "lineal";
        public const string Alineada = "alineada";
        public const string CoincidentPoints = "p2: coincide con p1";
        public const string RotationOnlyLinear = "rotacion: solo aplica a tipo lineal";
        private const string Concepto = "cotas";
        private const int MaxOverrideLength = 500;
        private const double CoincidenceTol = 1e-9;

        private static readonly string[] Tipos = { Lineal, Alineada };
        private static readonly string[] Own = { "tipo", "p1", "p2", "posicion_linea", "rotacion", "estilo", "texto_sustituto", "escala_global" };
        private static readonly string[] Batch = { "cotas" };
        private static readonly string[] ItemAllowed = { "tipo", "p1", "p2", "posicion_linea", "rotacion", "estilo", "texto_sustituto", "escala_global" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            PrimitiveSupport.Concat(Own, Batch),
            PrimitiveSupport.Concat(DrawWrite.CommonParams, PrimitiveSupport.RespuestaParam));

        public string Name => "cota";

        public string Description =>
            "ESCRIBE en el dibujo: crea una cota lineal (horizontal/vertical/rotada según 'rotacion') o alineada entre p1 y p2 " +
            "con la línea de cota por 'posicion_linea', o un lote (cotas: 1 a 100) en un solo paso de deshacer. En cotas " +
            "alineadas AutoCAD proyecta 'posicion_linea' perpendicularmente sobre la cota (use bbox para verificar). Usa el " +
            "estilo indicado o el actual, y la capa del concepto 'cotas' del estándar LPS si no se da otra.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        private const string ItemSchema = @"{
        ""type"": ""object"",
        ""properties"": {
          ""tipo"": { ""type"": ""string"", ""enum"": [""lineal"", ""alineada""], ""description"": ""Opcional si se indica 'tipo' en la raíz como defecto."" },
          ""p1"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""p2"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""posicion_linea"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""rotacion"": { ""type"": ""number"", ""default"": 0 },
          ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
          ""escala_global"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
          ""texto_sustituto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 500 }
        },
        ""required"": [""p1"", ""p2"", ""posicion_linea""],
        ""additionalProperties"": false
      }";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""tipo"": { ""type"": ""string"", ""enum"": [""lineal"", ""alineada""], ""description"": ""'lineal' = RotatedDimension (dirección fijada por 'rotacion'); 'alineada' = AlignedDimension paralela a p1-p2. En modo lote ('cotas') actúa como valor por defecto."" },
    ""p1"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Origen de la primera línea de referencia [x, y] o [x, y, z] (WCS). Forma simple: no combinar con cotas."" },
    ""p2"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Origen de la segunda línea de referencia; distinto de p1."" },
    ""posicion_linea"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Punto por el que pasa la línea de cota."" },
    ""rotacion"": { ""type"": ""number"", ""default"": 0, ""description"": ""Solo tipo 'lineal': dirección de la línea de cota en grados (0 = horizontal, 90 = vertical)."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de cota existente (sin distinguir mayúsculas); si no existe, NOT_FOUND con la lista de estilos disponibles (data.disponibles). Por defecto, el actual. En lote actúa como valor por defecto."" },
    ""escala_global"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Escala global (DIMSCALE) de la cota, como override de la del estilo (> 0). En lote actúa como valor por defecto."" },
    ""texto_sustituto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 500, ""description"": ""Texto literal que sustituye a la medida; '<>' representa la medida real (p. ej. '<> m')."" },
    ""cotas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": " + ItemSchema + @", ""description"": ""Modo lote (excluyente con p1/p2/posicion_linea/rotacion/texto_sustituto): 1 a 100 cotas creadas en un solo paso de deshacer, compartiendo capa, espacio, forzar y respuesta. Cada elemento hereda tipo, estilo y escala_global de la raíz si los omite."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""respuesta"": " + PrimitiveSupport.SchemaRespuesta + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = PrimitiveSupport.OutputSchema(@"{
      ""type"": ""object"",
      ""properties"": {
        ""handle"": { ""type"": ""string"" },
        ""tipo"": { ""type"": ""string"" },
        ""medida"": { ""type"": [""number"", ""null""] },
        ""estilo"": { ""type"": ""string"" },
        ""texto"": { ""type"": ""string"" },
        ""p1"": { ""type"": ""array"" },
        ""p2"": { ""type"": ""array"" },
        ""posicion_linea"": { ""type"": ""array"" },
        ""bbox"": { ""type"": [""array"", ""null""] },
        ""escala_global"": { ""type"": [""number"", ""null""], ""description"": ""DIMSCALE efectivo de la cota (null si no se puede leer)."" }
      }
    }");

        private sealed class CotaItem
        {
            public CotaItem(string tipo, Point3 p1, Point3 p2, Point3 linea, double rotacion, string? estilo, string? texto, double? escala)
            {
                Tipo = tipo;
                P1 = p1;
                P2 = p2;
                Linea = linea;
                Rotacion = rotacion;
                Estilo = estilo;
                Texto = texto;
                Escala = escala;
            }

            public string Tipo { get; }
            public Point3 P1 { get; }
            public Point3 P2 { get; }
            public Point3 Linea { get; }
            public double Rotacion { get; }
            public string? Estilo { get; }
            public string? Texto { get; }

            /// <summary>escala_global: override DIMSCALE de la cota; null = la del estilo.</summary>
            public double? Escala { get; }
        }

        private sealed class CotaParams
        {
            public CotaParams(List<CotaItem> items, bool batch, DrawWrite.Common common, string respuesta)
            {
                Items = items;
                Batch = batch;
                Common = common;
                Respuesta = respuesta;
            }

            public List<CotaItem> Items { get; }
            public bool Batch { get; }
            public DrawWrite.Common Common { get; }
            public string Respuesta { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Valida y convierte params. Sin AutoCAD: hilo del pipe y otra vez en el principal.</summary>
        private static CotaParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasBatch = ParamReader.Has(p, "cotas");
            bool hasSimpleOnly = ParamReader.Has(p, "p1") || ParamReader.Has(p, "p2")
                || ParamReader.Has(p, "posicion_linea") || ParamReader.Has(p, "rotacion")
                || ParamReader.Has(p, "texto_sustituto");
            if (hasBatch && hasSimpleOnly)
            {
                throw ToolException.InvalidParams(
                    "cotas: no se puede combinar con p1, p2, posicion_linea, rotacion o texto_sustituto (tipo, estilo y escala_global sí sirven como defecto)");
            }

            string? defTipo = ParamReader.Has(p, "tipo") ? ParamReader.OptionalEnum(p, "tipo", Tipos) : null;
            string? defEstilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            double? defEscala = ParamReader.OptionalNumber(p, "escala_global", 0, double.PositiveInfinity, exclusiveMin: true);

            var items = new List<CotaItem>();
            if (hasBatch)
            {
                JsonElement arr = p.GetProperty("cotas");
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams("cotas: debe ser un array de 1 a 100 objetos {p1, p2, posicion_linea, ...}");
                }

                int n = arr.GetArrayLength();
                if (n < 1 || n > MaxItems)
                {
                    throw ToolException.InvalidParams("cotas: debe tener entre 1 y 100 elementos");
                }

                int i = 0;
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string prefix = "cotas[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {p1, p2, posicion_linea, ...}");
                    }

                    ParamReader.RejectUnknown(el, ItemAllowed, prefix);
                    items.Add(ParseItem(el, prefix, defTipo, defEstilo, defEscala));
                    i++;
                }
            }
            else
            {
                items.Add(ParseItem(p, "", null, null, null));
            }

            DrawWrite.Common common = DrawWrite.ReadCommon(p, Concepto);
            string respuesta = PrimitiveSupport.ReadRespuesta(p);
            return new CotaParams(items, hasBatch, common, respuesta);
        }

        private static CotaItem ParseItem(
            JsonElement src, string prefix, string? defTipo, string? defEstilo, double? defEscala)
        {
            try
            {
                string? tipo = ParamReader.Has(src, "tipo")
                    ? ParamReader.OptionalEnum(src, "tipo", Tipos)
                    : defTipo;
                if (tipo == null)
                {
                    throw ToolException.InvalidParams("tipo: obligatorio");
                }

                Point3 p1 = ParamReader.RequiredPoint(src, "p1");
                Point3 p2 = ParamReader.RequiredPoint(src, "p2");
                Point3 linea = ParamReader.RequiredPoint(src, "posicion_linea");
                if (p1.Distance(p2) <= CoincidenceTol)
                {
                    throw ToolException.InvalidParams(CoincidentPoints);
                }

                double rotacion = 0.0;
                if (src.TryGetProperty("rotacion", out JsonElement rotEl))
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

                string? estilo = ParamReader.OptionalString(src, "estilo", 1, 255) ?? defEstilo;
                string? texto = ParamReader.Has(src, "texto_sustituto")
                    ? ParamReader.RequiredText(src, "texto_sustituto", 1, MaxOverrideLength)
                    : null;
                double? escala = ParamReader.Has(src, "escala_global")
                    ? ParamReader.OptionalNumber(src, "escala_global", 0, double.PositiveInfinity, exclusiveMin: true)
                    : defEscala;
                return new CotaItem(tipo, p1, p2, linea, rotacion, estilo, texto, escala);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string detalle = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + detalle);
            }
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            CotaParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);                       // R2
            var boxes = new List<BBox2>(p.Items.Count);
            foreach (CotaItem item in p.Items)
            {
                boxes.Add(BBox2.FromPoints(new[] { item.P1, item.P2, item.Linea }));
            }

            PlacementCheck.Run(db, p.Common.Espacio, boxes, p.Common.Forzar, p.Batch ? "cotas" : "entidades", warnings); // R4/R5

            // Estilos ANTES del UndoGroup (lectura): un NOT_FOUND no crea paso de deshacer.
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                var checkedStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CotaItem item in p.Items)
                {
                    string key = item.Estilo ?? "";
                    if (checkedStyles.Add(key))
                    {
                        ResolveStyle(db, ro, item.Estilo);
                    }
                }

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
                var space = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);
                var styleCache = new Dictionary<string, (ObjectId Id, string Name, bool Annotative)>(StringComparer.OrdinalIgnoreCase);
                bool warnedAnnotative = false;

                var handles = new List<string>(p.Items.Count);
                var entidades = new List<object>(p.Items.Count);

                foreach (CotaItem item in p.Items)
                {
                    string styleKey = item.Estilo ?? "";
                    if (!styleCache.TryGetValue(styleKey, out (ObjectId Id, string Name, bool Annotative) styleInfo))
                    {
                        (ObjectId id, string name) = ResolveStyle(db, tr, item.Estilo);
                        bool annotative = !id.IsNull && ((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Annotative == AnnotativeStates.True;
                        styleInfo = (id, name, annotative);
                        styleCache[styleKey] = styleInfo;
                    }

                    string dimText = item.Texto == null ? "" : TextEscape.EscapeDbText(item.Texto);   // R7; '<>' se conserva
                    Point3d a = AcadUtil.ToPoint3d(item.P1);
                    Point3d b = AcadUtil.ToPoint3d(item.P2);
                    Point3d l = AcadUtil.ToPoint3d(item.Linea);

                    Dimension dim = string.Equals(item.Tipo, Lineal, StringComparison.Ordinal)
                        ? new RotatedDimension(Normalize360(item.Rotacion) * Math.PI / 180.0, a, b, l, dimText, styleInfo.Id)
                        : new AlignedDimension(a, b, l, dimText, styleInfo.Id);

                    DrawWrite.ApplyByLayer(dim, db, layer.Id);
                    if (!styleInfo.Id.IsNull)
                    {
                        // SetDatabaseDefaults (en ApplyByLayer) podría fijar el estilo actual: se reafirma el pedido.
                        dim.DimensionStyle = styleInfo.Id;
                    }

                    if (item.Escala.HasValue)
                    {
                        // Anotativo: la escala la gobierna la escala de anotación; DIMSCALE no se aplica (se avisa).
                        bool anotativo = dim.Annotative == AnnotativeStates.True || styleInfo.Annotative;
                        if (anotativo)
                        {
                            if (!warnedAnnotative)
                            {
                                warnings.Add("escala_global ignorada: la cota o su estilo es anotativo (la escala la fija la escala de anotación).");
                                warnedAnnotative = true;
                            }
                        }
                        else
                        {
                            dim.Dimscale = item.Escala.Value;   // override de esta cota (después del estilo)
                        }
                    }

                    string handle = DrawWrite.Append(space, tr, dim);
                    handles.Add(handle);

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
                    entidades.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = handle,
                        ["tipo"] = item.Tipo,
                        ["medida"] = measurement,
                        ["estilo"] = styleInfo.Name,
                        ["texto"] = item.Texto ?? "",
                        ["p1"] = AcadUtil.ToArray(x1),
                        ["p2"] = AcadUtil.ToArray(x2),
                        ["posicion_linea"] = AcadUtil.ToArray(dl),
                        ["bbox"] = bbox?.ToArray2D(),
                        ["escala_global"] = AcadUtil.NumberOrNull(dim.Dimscale),
                    });
                }

                Dictionary<string, object?> result = DrawWrite.Response(pid, handles, layer, resolved.Name, ins, warnings);
                PrimitiveSupport.PutEntities(result, entidades, p.Batch, p.Respuesta);
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
