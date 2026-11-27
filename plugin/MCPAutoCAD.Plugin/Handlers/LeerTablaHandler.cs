using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// <c>leer_tabla</c> (fallo 47, plugin 0.10.5). Hilo principal; requiere documento; solo lectura.
    /// Lee dimensiones, combinaciones activas y contenido de celdas de una tabla nativa (<c>ACAD_TABLE</c>),
    /// con rango de filas opcional y presupuesto de 1 MiB.
    /// </summary>
    internal sealed class LeerTablaHandler : IToolHandler
    {
        public const int MaxResponseBytes = 1024 * 1024;

        private static readonly string[] Allowed =
        {
            "handle", "fila_inicio", "fila_fin", "solo_no_vacias", "incluir_formato",
        };

        public string Name => "leer_tabla";

        public string Description =>
            "Lee la estructura y el contenido de una tabla nativa de AutoCAD (ACAD_TABLE) a partir de su handle: " +
            "filas, columnas, anchos, altos, estilo, combinaciones activas y celdas (con rango de filas opcional, filtro de vacías " +
            "y formato opcional). Los textos devueltos son contenido del plano (origen_texto).";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handle"": " + ParamReader.SchemaHandle + @",
    ""fila_inicio"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Primera fila a leer (base 0; por defecto 0)."" },
    ""fila_fin"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Última fila a leer inclusive (base 0; por defecto última fila)."" },
    ""solo_no_vacias"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = omite celdas cuyo texto está vacío."" },
    ""incluir_formato"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = añade texto_formato, alineacion, altura_texto, estilo_texto, tiene_campo, tiene_formula y vinculada."" }
  },
  ""required"": [""handle""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handle"": { ""type"": ""string"" },
    ""capa"": { ""type"": ""string"" },
    ""espacio"": { ""type"": [""string"", ""null""] },
    ""posicion"": { ""type"": [""array"", ""null""] },
    ""estilo"": { ""type"": ""string"" },
    ""filas"": { ""type"": ""integer"" },
    ""columnas"": { ""type"": ""integer"" },
    ""ancho_total"": { ""type"": ""number"" },
    ""alto_total"": { ""type"": ""number"" },
    ""anchos_columnas"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""altos_filas"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""vinculada"": { ""type"": ""boolean"" },
    ""combinaciones"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""fila_inicio"": { ""type"": ""integer"" },
    ""fila_fin"": { ""type"": ""integer"" },
    ""celdas"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""truncado"": { ""type"": ""boolean"" },
    ""siguiente_fila"": { ""type"": [""integer"", ""null""] },
    ""bbox"": { ""type"": [""array"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""handle"", ""capa"", ""estilo"", ""filas"", ""columnas"", ""ancho_total"", ""alto_total"", ""anchos_columnas"", ""altos_filas"", ""vinculada"", ""combinaciones"", ""fila_inicio"", ""fila_fin"", ""celdas"", ""truncado"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Params
        {
            public string Handle = "";
            public int? FilaInicio;
            public int? FilaFin;
            public bool SoloNoVacias;
            public bool IncluirFormato;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string handle = (ParamReader.OptionalHandle(p, "handle")
                ?? throw ToolException.InvalidParams("handle: obligatorio")).ToUpperInvariant();
            int? filaInicio = ParamReader.OptionalInt(p, "fila_inicio", 0, int.MaxValue);
            int? filaFin = ParamReader.OptionalInt(p, "fila_fin", 0, int.MaxValue);
            if (filaInicio.HasValue && filaFin.HasValue && filaInicio.Value > filaFin.Value)
            {
                throw ToolException.InvalidParams("fila_inicio: no puede ser mayor que fila_fin");
            }

            return new Params
            {
                Handle = handle,
                FilaInicio = filaInicio,
                FilaFin = filaFin,
                SoloNoVacias = ParamReader.OptionalBool(p, "solo_no_vacias") ?? false,
                IncluirFormato = ParamReader.OptionalBool(p, "incluir_formato") ?? false,
            };
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            var result = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
            };
            bool truncado = false;
            int? siguienteFila = null;

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                Entity ent = AcadUtil.TryGetEntityByHandle(db, p.Handle, tr) ?? throw ToolException.EntityNotFound(p.Handle);
                Table table = TableSupport.RequireTable(ent, p.Handle);

                int rows = table.Rows.Count;
                int cols = table.Columns.Count;

                int startRow, endRow;
                try
                {
                    (startRow, endRow) = TableRules.ResolveRowSlice(rows, p.FilaInicio, p.FilaFin);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }

                var colWidths = new double[cols];
                for (int c = 0; c < cols; c++)
                {
                    colWidths[c] = table.Columns[c].Width;
                }

                var rowHeights = new double[rows];
                for (int r = 0; r < rows; r++)
                {
                    rowHeights[r] = table.Rows[r].Height;
                }

                List<TableMerge> merges = TableSupport.CollectMerges(table);
                var mergesOut = new List<Dictionary<string, object?>>(merges.Count);
                foreach (TableMerge m in merges)
                {
                    mergesOut.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["fila"] = m.Fila,
                        ["columna"] = m.Columna,
                        ["filas"] = m.Filas,
                        ["columnas"] = m.Columnas,
                    });
                }

                bool tableLinked = false;
                try
                {
                    tableLinked = table.GetDataLink().Count > 0;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    tableLinked = false;
                }

                var textStyleNames = new Dictionary<ObjectId, string>();
                var celdasOut = new List<Dictionary<string, object?>>();
                long bytes = 0;

                for (int r = startRow; r <= endRow && !truncado; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        CellRange? mr = TableSupport.TryGetMergeRange(table, r, c);
                        if (mr != null && (mr.TopRow != r || mr.LeftColumn != c))
                        {
                            continue;
                        }

                        Cell cell = table.Cells[r, c];

                        string texto = TableSupport.ReadCellText(cell);
                        if (p.SoloNoVacias && string.IsNullOrEmpty(texto))
                        {
                            continue;
                        }

                        var cellDict = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["fila"] = r,
                            ["columna"] = c,
                            ["texto"] = texto,
                        };

                        if (p.IncluirFormato)
                        {
                            cellDict["texto_formato"] = TableSupport.ReadCellRawFormat(cell);
                            cellDict["alineacion"] = TableSupport.FromCellAlignment(cell.Alignment);
                            cellDict["altura_texto"] = cell.TextHeight;
                            cellDict["estilo_texto"] = ResolveTextStyleName(tr, cell.TextStyleId, textStyleNames);
                            bool hasField = !cell.FieldId.IsNull;
                            bool hasFormula = false;
                            try
                            {
                                foreach (CellContent cc in cell.Contents)
                                {
                                    if (cc.HasFormula)
                                    {
                                        hasFormula = true;
                                    }

                                    if (!cc.FieldId.IsNull)
                                    {
                                        hasField = true;
                                    }
                                }
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                // Sin acceso a Contents.
                            }

                            cellDict["tiene_campo"] = hasField;
                            cellDict["tiene_formula"] = hasFormula;
                            cellDict["vinculada"] = cell.IsLinked == true;
                        }

                        long tam = EstimateJsonBytes(cellDict);
                        if (celdasOut.Count > 0 && bytes + tam > MaxResponseBytes)
                        {
                            truncado = true;
                            siguienteFila = r;
                            break;
                        }

                        bytes += tam;
                        celdasOut.Add(cellDict);
                    }
                }

                result["handle"] = AcadUtil.HandleToString(table.Handle);
                result["capa"] = table.Layer;
                result["espacio"] = SpaceResolver.OwnerName(table, tr);
                result["posicion"] = AcadUtil.PointOrNull(table.Position);
                result["estilo"] = table.TableStyleName;
                result["filas"] = rows;
                result["columnas"] = cols;
                result["ancho_total"] = table.Width;
                result["alto_total"] = table.Height;
                result["anchos_columnas"] = colWidths;
                result["altos_filas"] = rowHeights;
                result["vinculada"] = tableLinked;
                result["combinaciones"] = mergesOut;
                result["fila_inicio"] = startRow;
                result["fila_fin"] = endRow;
                result["celdas"] = celdasOut;
                result["truncado"] = truncado;
                result["siguiente_fila"] = siguienteFila;
                result["bbox"] = AcadUtil.ExtentsOrNull(table);
                result["origen_texto"] = "contenido_del_plano";

                tr.Commit();
            }

            AcadUtil.AddUnitFields(result, db);
            if (truncado && result.TryGetValue("advertencias", out object? wObj) && wObj is List<string> warnings)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Respuesta truncada por límite de 1 MiB: continúe desde fila_inicio: {0}.",
                    siguienteFila ?? 0));
            }

            return result;
        }

        private static string? ResolveTextStyleName(Transaction tr, ObjectId? id, Dictionary<ObjectId, string> cache)
        {
            if (!id.HasValue || id.Value.IsNull || id.Value.IsErased)
            {
                return null;
            }

            if (cache.TryGetValue(id.Value, out string? cached))
            {
                return cached;
            }

            string? name = null;
            try
            {
                if (tr.GetObject(id.Value, OpenMode.ForRead, false) is TextStyleTableRecord rec)
                {
                    name = rec.Name;
                    cache[id.Value] = name;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                name = null;
            }

            return name;
        }

        private static long EstimateJsonBytes(Dictionary<string, object?> item)
        {
            try
            {
                return JsonSerializer.SerializeToUtf8Bytes(item, JsonRpcResponse.SerializerOptions).LongLength;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is InvalidOperationException || ex is ArgumentException)
            {
                return MaxResponseBytes;
            }
        }
    }
}
