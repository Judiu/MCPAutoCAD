using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// <c>tabla</c> (fallo 47, plugin 0.10.5). Hilo principal; requiere documento; ESCRITURA solo con <c>confirmar: true</c>
    /// (sin él previsualiza medidas, márgenes y colocación sin abrir <see cref="UndoGroup"/>). Crea una tabla nativa
    /// (<c>AcDbTable</c>) en modelo o layout en un solo paso de deshacer.
    /// </summary>
    internal sealed class TablaHandler : IToolHandler
    {
        private static readonly string[] Own =
        {
            "posicion", "filas", "columnas", "alto_fila", "altos_filas", "ancho_columna", "ancho_columnas",
            "altura_texto", "estilo", "estilo_texto", "alineacion", "margen_vertical", "margen_horizontal",
            "celdas", "confirmar", "preview",
        };

        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            Own,
            PrimitiveSupport.Concat(DrawWrite.CommonParams, EntityColor.ParamNames));

        public string Name => "tabla";

        public string Description =>
            "ESCRIBE una tabla nativa de AutoCAD (ACAD_TABLE) en 'posicion' [x, y] (esquina superior izquierda, crece hacia +X y -Y) " +
            "en el espacio indicado (modelo o un layout), con número de filas y columnas, altos y anchos (uniformes o por lista), " +
            "altura de texto, estilo de tabla, celdas con texto o texto_formato, alineación y rangos combinados. " +
            "Sin confirmar: true solo PREVISUALIZA sin modificar el dibujo. Con confirmar: true la crea en un solo paso de deshacer.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        private const string CellSchema = @"{
      ""type"": ""object"",
      ""properties"": {
        ""fila"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 199, ""description"": ""Índice de fila (base 0)."" },
        ""columna"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 49, ""description"": ""Índice de columna (base 0)."" },
        ""texto"": { ""type"": ""string"", ""minLength"": 0, ""maxLength"": 2000, ""description"": ""Texto literal de la celda (los saltos de línea se conservan). Excluyente con texto_formato."" },
        ""texto_formato"": { ""type"": ""string"", ""minLength"": 0, ""maxLength"": 4000, ""description"": ""Contenido MTEXT crudo con códigos de formato (\\L, \\P, {…}). Excluyente con texto."" },
        ""alineacion"": " + TableSupport.SchemaCellAlignment + @",
        ""altura_texto"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Altura de texto de esta celda (sobreescribe la general)."" },
        ""estilo_texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Estilo de texto existente para esta celda."" },
        ""color_texto"": " + TableSupport.SchemaColorTextoCell + @",
        ""color_fondo"": " + TableSupport.SchemaColorFondo + @",
        ""combinar"": {
          ""type"": ""object"",
          ""properties"": {
            ""filas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 },
            ""columnas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50 }
          },
          ""required"": [""filas"", ""columnas""],
          ""additionalProperties"": false,
          ""description"": ""Combina desde esta celda (ancla superior izquierda) el número indicado de filas y columnas.""
        }
      },
      ""required"": [""fila"", ""columna""],
      ""additionalProperties"": false
    }";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""posicion"": " + PrimitiveSupport.PointSchema + @",
    ""filas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200, ""description"": ""Número total de filas (1–200; filas × columnas ≤ 2500)."" },
    ""columnas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50, ""description"": ""Número total de columnas (1–50; filas × columnas ≤ 2500)."" },
    ""alto_fila"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Alto uniforme para todas las filas en unidades del dibujo (> 0). Excluyente con altos_filas."" },
    ""altos_filas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }, ""description"": ""Alto individual por fila (longitud = filas). Excluyente con alto_fila."" },
    ""ancho_columna"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Ancho uniforme para todas las columnas en unidades del dibujo (> 0). Excluyente con ancho_columnas."" },
    ""ancho_columnas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }, ""description"": ""Ancho individual por columna (longitud = columnas). Excluyente con ancho_columna."" },
    ""altura_texto"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Altura por defecto del texto de la tabla en unidades del dibujo (> 0)."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de tabla existente (por defecto el actual)."" },
    ""estilo_texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de texto existente para las celdas (por defecto el del estilo de tabla o el actual)."" },
    ""alineacion"": { ""type"": ""string"", ""enum"": [""TL"", ""TC"", ""TR"", ""ML"", ""MC"", ""MR"", ""BL"", ""BC"", ""BR""], ""default"": ""MC"", ""description"": ""Alineación por defecto de las celdas (defecto MC)."" },
    ""margen_vertical"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""Margen superior/inferior de celda en unidades del dibujo. Por defecto usa el del estilo, acotado automáticamente si excede el alto de fila."" },
    ""margen_horizontal"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""Margen izquierdo/derecho de celda en unidades del dibujo. Por defecto usa el del estilo, acotado automáticamente si excede el ancho de columna."" },
    ""celdas"": { ""type"": ""array"", ""maxItems"": 2500, ""items"": " + CellSchema + @", ""description"": ""Contenido, formato y combinaciones por celda."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @",
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = CREAR la tabla en el dibujo en un solo paso de deshacer. Sin él solo se previsualiza y no escribe nada."" },
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""posicion"", ""filas"", ""columnas"", ""altura_texto""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""handle"": { ""type"": [""string"", ""null""] },
    ""espacio"": { ""type"": ""string"" },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""estilo"": { ""type"": ""string"" },
    ""filas"": { ""type"": ""integer"" },
    ""columnas"": { ""type"": ""integer"" },
    ""ancho_total"": { ""type"": ""number"" },
    ""alto_total"": { ""type"": ""number"" },
    ""anchos_columnas"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""altos_filas"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""n_celdas_con_datos"": { ""type"": ""integer"" },
    ""n_combinaciones"": { ""type"": ""integer"" },
    ""bbox"": { ""type"": [""array"", ""null""] },
    ""colocacion_ok"": { ""type"": ""boolean"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""handles"", ""espacio"", ""capa"", ""capa_creada"", ""estilo"", ""filas"", ""columnas"", ""ancho_total"", ""alto_total"", ""anchos_columnas"", ""altos_filas"", ""n_celdas_con_datos"", ""n_combinaciones""]
}");

        private sealed class Params
        {
            public Point3 Posicion;
            public int Filas;
            public int Columnas;
            public double[] RowHeights = Array.Empty<double>();
            public double[] ColWidths = Array.Empty<double>();
            public double AlturaTexto;
            public string? Estilo;
            public string? EstiloTexto;
            public string Alineacion = "MC";
            public double? MargenVertical;
            public double? MargenHorizontal;
            public List<TableCellSpec> Celdas = new List<TableCellSpec>();
            public List<TableMerge> Merges = new List<TableMerge>();
            public DrawWrite.Common Common = new DrawWrite.Common(null, null, false, false);
            public ColorSpec? Color;
            public CleanWrite.Mode Mode;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            Point3 posicion = ParamReader.RequiredPoint(p, "posicion");
            int filas = TableSupport.OptionalRequiredInt(p, "filas", 1, TableRules.MaxRows, "");
            int columnas = TableSupport.OptionalRequiredInt(p, "columnas", 1, TableRules.MaxColumns, "");

            string? dimErr = TableRules.ValidateDimensions(filas, columnas);
            if (dimErr != null)
            {
                throw ToolException.InvalidParams(dimErr);
            }

            double? altoFila = ParamReader.OptionalNumber(p, "alto_fila", 0, double.PositiveInfinity, exclusiveMin: true);
            List<double>? altosFilas = PrimitiveSupport.OptionalNumberArray(p, "altos_filas");
            double[] rowHeights;
            try
            {
                rowHeights = TableRules.ResolveRowHeights(filas, altoFila, altosFilas);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            double? anchoCol = ParamReader.OptionalNumber(p, "ancho_columna", 0, double.PositiveInfinity, exclusiveMin: true);
            List<double>? anchoCols = PrimitiveSupport.OptionalNumberArray(p, "ancho_columnas");
            double[] colWidths;
            try
            {
                colWidths = TableRules.ResolveColumnWidths(columnas, anchoCol, anchoCols);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            double alturaTexto = ParamReader.RequiredNumber(p, "altura_texto", 0, double.PositiveInfinity, exclusiveMin: true);
            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            string? estiloTexto = ParamReader.OptionalString(p, "estilo_texto", 1, 255);
            string alineacion = ParamReader.OptionalEnum(p, "alineacion", TableRules.ValidAlignments) ?? "MC";
            double? margenV = PrimitiveSupport.OptionalNonNegative(p, "margen_vertical");
            double? margenH = PrimitiveSupport.OptionalNonNegative(p, "margen_horizontal");

            var celdas = new List<TableCellSpec>();
            if (p.TryGetProperty("celdas", out JsonElement celdasEl))
            {
                if (celdasEl.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams("celdas: debe ser un array de objetos");
                }

                if (celdasEl.GetArrayLength() > TableRules.MaxTotalCells)
                {
                    throw ToolException.InvalidParams(
                        string.Format(CultureInfo.InvariantCulture, "celdas: supera el máximo de {0} elementos", TableRules.MaxTotalCells));
                }

                int idx = 0;
                foreach (JsonElement cellEl in celdasEl.EnumerateArray())
                {
                    string prefix = string.Format(CultureInfo.InvariantCulture, "celdas[{0}].", idx);
                    celdas.Add(TableSupport.ParseCellSpec(cellEl, prefix, allowCombinar: true, requireAnyChange: false));
                    idx++;
                }
            }

            List<TableMerge> merges;
            try
            {
                merges = TableRules.ValidateCellsAndMerges(filas, columnas, celdas);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            DrawWrite.Common common = DrawWrite.ReadCommon(p, null);
            ColorSpec? color = EntityColor.Read(p);
            CleanWrite.Common cw = CleanWrite.ReadCommon(p);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(cw.Confirmar, cw.Preview);

            return new Params
            {
                Posicion = posicion,
                Filas = filas,
                Columnas = columnas,
                RowHeights = rowHeights,
                ColWidths = colWidths,
                AlturaTexto = alturaTexto,
                Estilo = estilo,
                EstiloTexto = estiloTexto,
                Alineacion = alineacion,
                MargenVertical = margenV,
                MargenHorizontal = margenH,
                Celdas = celdas,
                Merges = merges,
                Common = common,
                Color = color,
                Mode = mode,
            };
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int pid = ctx.Pid;

            TableSupport.TableStyleInfo styleInfo;
            string spaceName;
            string layerName;
            bool layerExists;

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);
                spaceName = resolved.Name;

                LayerPlan layerPlan = DrawLayerFactory.Check(db, tr, p.Common.Capa, null, out layerExists, warnings);
                layerName = layerPlan.Nombre;
                if (layerExists)
                {
                    var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    ObjectId layerId = McpLayerFactory.FindLayer(table, layerName);
                    if (!layerId.IsNull)
                    {
                        layerName = ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Name;
                    }
                }

                styleInfo = TableSupport.ResolveTableStyle(db, tr, p.Estilo);
                CheckTextStyles(db, tr, p);
                tr.Commit();
            }

            double minRowH = p.RowHeights.Min();
            double minColW = p.ColWidths.Min();
            SafeMarginsResult margins = TableRules.ComputeSafeMargins(
                styleInfo.VertMargin, styleInfo.HorizMargin, minRowH, minColW, p.AlturaTexto, p.MargenVertical, p.MargenHorizontal);
            if (margins.AutoAdjusted)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Márgenes del estilo '{0}' (V={1:0.###}, H={2:0.###}) acotados automáticamente a V={3:0.###}, H={4:0.###} para respetar el alto/ancho pedido.",
                    styleInfo.Name,
                    styleInfo.VertMargin,
                    styleInfo.HorizMargin,
                    margins.Vertical ?? styleInfo.VertMargin,
                    margins.Horizontal ?? styleInfo.HorizMargin));
            }

            BBox2 bboxEstimado = TableRules.EstimateBBox(p.Posicion, p.ColWidths, p.RowHeights);
            int nConDatos = p.Celdas.Count(c => c.Texto != null || c.TextoFormato != null);

            if (p.Mode == CleanWrite.Mode.Preview)
            {
                int insRead = CleanWrite.ReadUnits(db);
                var placeWarnings = new List<string>();
                PlacementCheck.Run(db, p.Common.Espacio, new[] { bboxEstimado }, forzar: true, "entidades", placeWarnings);
                bool colocacionOk = placeWarnings.Count == 0;
                if (!colocacionOk)
                {
                    warnings.Add("La tabla estimada queda fuera de la extensión del espacio '" + spaceName + "'; al confirmar necesitará forzar: true.");
                }

                Dictionary<string, object?> prev = CleanWrite.Response(pid, CleanWrite.Mode.Preview, insRead, warnings);
                prev["handles"] = new List<string>();
                prev["handle"] = null;
                prev["espacio"] = spaceName;
                prev["capa"] = layerName;
                prev["capa_creada"] = !layerExists;
                prev["estilo"] = styleInfo.Name;
                prev["filas"] = p.Filas;
                prev["columnas"] = p.Columnas;
                prev["ancho_total"] = p.ColWidths.Sum();
                prev["alto_total"] = p.RowHeights.Sum();
                prev["anchos_columnas"] = p.ColWidths;
                prev["altos_filas"] = p.RowHeights;
                prev["n_celdas_con_datos"] = nConDatos;
                prev["n_combinaciones"] = p.Merges.Count;
                prev["bbox"] = bboxEstimado.ToArray2D();
                prev["colocacion_ok"] = colocacionOk;
                return prev;
            }

            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);
            PlacementCheck.Run(db, p.Common.Espacio, new[] { bboxEstimado }, p.Common.Forzar, "entidades", warnings);

            return UndoGroup.Run(doc, d => Write(d, p, margins, nConDatos, pid, ins, warnings));
        }

        private static object Write(
            Document doc,
            Params p,
            SafeMarginsResult margins,
            int nConDatos,
            int pid,
            int ins,
            List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Common.Capa, null, warnings);
                BlockTableRecord space = PrimitiveSupport.OpenForWrite(tr, resolved);
                TableSupport.TableStyleInfo styleInfo = TableSupport.ResolveTableStyle(db, tr, p.Estilo);

                ObjectId globalTextStyleId = p.EstiloTexto != null
                    ? PrimitiveSupport.ResolveTextStyle(db, tr, p.EstiloTexto).id
                    : ObjectId.Null;

                var cellTextStyles = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (TableCellSpec c in p.Celdas)
                {
                    if (c.EstiloTexto != null && !cellTextStyles.ContainsKey(c.EstiloTexto))
                    {
                        cellTextStyles[c.EstiloTexto] = PrimitiveSupport.ResolveTextStyle(db, tr, c.EstiloTexto).id;
                    }
                }

                var table = new Table();
                DrawWrite.ApplyByLayer(table, db, layer.Id);
                EntityColor.Apply(table, p.Color);
                table.TableStyle = styleInfo.Id;
                table.Position = AcadUtil.ToPoint3d(p.Posicion);
                table.SetSize(p.Filas, p.Columnas);

                table.SuppressRegenerateTable(true);
                try
                {
                    TableSupport.ClearAutoMerges(table);

                    string? dataStyle = TableSupport.FindDataCellStyle(styleInfo.CellStyles);
                    if (dataStyle != null)
                    {
                        table.Cells.Style = dataStyle;
                    }

                    if (margins.Vertical.HasValue)
                    {
                        table.Cells.Borders.Top.Margin = margins.Vertical.Value;
                        table.Cells.Borders.Bottom.Margin = margins.Vertical.Value;
                    }

                    if (margins.Horizontal.HasValue)
                    {
                        table.Cells.Borders.Left.Margin = margins.Horizontal.Value;
                        table.Cells.Borders.Right.Margin = margins.Horizontal.Value;
                    }

                    if (!globalTextStyleId.IsNull)
                    {
                        table.Cells.TextStyleId = globalTextStyleId;
                    }

                    table.Cells.TextHeight = p.AlturaTexto;
                    table.Cells.Alignment = TableSupport.ToCellAlignment(p.Alineacion);

                    for (int r = 0; r < p.Filas; r++)
                    {
                        table.Rows[r].Height = p.RowHeights[r];
                    }

                    for (int c = 0; c < p.Columnas; c++)
                    {
                        table.Columns[c].Width = p.ColWidths[c];
                    }

                    foreach (TableMerge m in p.Merges)
                    {
                        table.MergeCells(CellRange.Create(table, m.Fila, m.Columna, m.BottomRow, m.RightColumn));
                    }

                    foreach (TableCellSpec spec in p.Celdas)
                    {
                        Cell cell = table.Cells[spec.Fila, spec.Columna];
                        ObjectId tsId = spec.EstiloTexto != null ? cellTextStyles[spec.EstiloTexto] : ObjectId.Null;
                        TableSupport.ApplyCellFormatting(cell, spec.Alineacion, spec.AlturaTexto, tsId, spec.ColorTexto, spec.ColorFondo);
                        TableSupport.ApplyCellContent(cell, spec.Texto, spec.TextoFormato);
                    }
                }
                finally
                {
                    table.SuppressRegenerateTable(false);
                }

                table.GenerateLayout();
                string handle = DrawWrite.Append(space, tr, table);

                var actualCols = new double[p.Columnas];
                bool adjustedByAcad = false;
                for (int c = 0; c < p.Columnas; c++)
                {
                    actualCols[c] = table.Columns[c].Width;
                    if (Math.Abs(actualCols[c] - p.ColWidths[c]) > 1e-3)
                    {
                        adjustedByAcad = true;
                    }
                }

                var actualRows = new double[p.Filas];
                for (int r = 0; r < p.Filas; r++)
                {
                    actualRows[r] = table.Rows[r].Height;
                    if (Math.Abs(actualRows[r] - p.RowHeights[r]) > 1e-3)
                    {
                        adjustedByAcad = true;
                    }
                }

                if (adjustedByAcad)
                {
                    warnings.Add("AutoCAD ajustó el ancho de alguna columna o el alto de alguna fila para dar cabida al texto/márgenes.");
                }

                double totalW = table.Width;
                double totalH = table.Height;
                double[][]? bbox = PrimitiveSupport.Box2D(table);
                string styleName = table.TableStyleName;

                tr.Commit();

                Dictionary<string, object?> res = CleanWrite.Response(pid, CleanWrite.Mode.Write, ins, warnings);
                res["handles"] = new List<string> { handle };
                res["handle"] = handle;
                res["espacio"] = resolved.Name;
                res["capa"] = layer.Nombre;
                res["capa_creada"] = layer.Creada;
                res["estilo"] = styleName;
                res["filas"] = p.Filas;
                res["columnas"] = p.Columnas;
                res["ancho_total"] = totalW;
                res["alto_total"] = totalH;
                res["anchos_columnas"] = actualCols;
                res["altos_filas"] = actualRows;
                res["n_celdas_con_datos"] = nConDatos;
                res["n_combinaciones"] = p.Merges.Count;
                res["bbox"] = bbox;
                res["colocacion_ok"] = true;
                return res;
            }
        }

        private static void CheckTextStyles(Database db, Transaction tr, Params p)
        {
            var checkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (p.EstiloTexto != null && checkedNames.Add(p.EstiloTexto))
            {
                PrimitiveSupport.ResolveTextStyle(db, tr, p.EstiloTexto);
            }

            foreach (TableCellSpec c in p.Celdas)
            {
                if (c.EstiloTexto != null && checkedNames.Add(c.EstiloTexto))
                {
                    PrimitiveSupport.ResolveTextStyle(db, tr, c.EstiloTexto);
                }
            }
        }
    }
}
