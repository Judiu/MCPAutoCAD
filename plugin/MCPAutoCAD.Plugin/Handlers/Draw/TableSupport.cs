using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Utilidades compartidas en el hilo principal para <c>tabla</c>, <c>editar_tabla</c> y <c>leer_tabla</c>.
    /// Solo usa la API moderna (no obsoleta) de <c>Table</c>, <c>CellRange</c>, <c>Cell</c> y <c>TableStyle</c>.
    /// </summary>
    internal static class TableSupport
    {
        public sealed record TableStyleInfo(
            ObjectId Id,
            string Name,
            double VertMargin,
            double HorizMargin,
            HashSet<string> CellStyles);

        public const string SchemaCellAlignment =
            @"{ ""type"": ""string"", ""enum"": [""TL"", ""TC"", ""TR"", ""ML"", ""MC"", ""MR"", ""BL"", ""BC"", ""BR""], ""description"": ""Alineación del texto en la celda (T/M/B = superior/medio/inferior, L/C/R = izquierda/centro/derecha)."" }";

        public const string SchemaColorFondo =
            @"{ ""anyOf"": [ { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 }, { ""type"": ""string"", ""const"": ""ninguno"" }, { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 } } ], ""description"": ""Color de fondo de la celda: ACI 1–255, 'ninguno' (sin relleno) o [r, g, b]."" }";

        public const string SchemaColorTextoCell =
            @"{ ""anyOf"": [ { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 256 }, { ""type"": ""string"", ""enum"": [""porcapa"", ""porbloque""] }, { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 } } ], ""description"": ""Color del contenido de la celda: ACI 0–256, 'porcapa', 'porbloque' o [r, g, b]."" }";

        private static readonly string[] CellAllowedCreate =
        {
            "fila", "columna", "texto", "texto_formato", "alineacion", "altura_texto", "estilo_texto",
            "color_texto", "color_fondo", "combinar",
        };

        private static readonly string[] CellAllowedEdit =
        {
            "fila", "columna", "texto", "texto_formato", "alineacion", "altura_texto", "estilo_texto",
            "color_texto", "color_fondo",
        };

        private static readonly string[] MergeAllowed = { "filas", "columnas" };

        private static readonly string[] FullMergeAllowed = { "fila", "columna", "filas", "columnas" };

        /// <summary>Comprueba que la entidad sea una tabla nativa (<c>ACAD_TABLE</c>).</summary>
        public static Table RequireTable(Entity e, string handle)
        {
            if (e is Table t)
            {
                return t;
            }

            throw ToolException.InvalidParams(
                "handle: el tipo " + AcadUtil.DxfName(e) + " no es ACAD_TABLE",
                new Dictionary<string, object?> { ["handle"] = handle, ["tipo"] = AcadUtil.DxfName(e) });
        }

        /// <summary>
        /// Resuelve un estilo de tabla por nombre (sin distinguir mayúsculas) o el actual (<see cref="Database.Tablestyle"/>).
        /// Rechaza estilos dependientes de xref y lanza NOT_FOUND con la lista de disponibles si no existe.
        /// </summary>
        public static TableStyleInfo ResolveTableStyle(Database db, Transaction tr, string? nombre)
        {
            var dict = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
            var disponibles = new List<string>();
            ObjectId matchedId = ObjectId.Null;
            string? matchedKey = null;

            foreach (DBDictionaryEntry entry in dict)
            {
                if (entry.Value.IsNull || entry.Value.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(entry.Value, OpenMode.ForRead, false) is not TableStyle ts)
                {
                    continue;
                }

                string tsName = string.IsNullOrEmpty(ts.Name) ? entry.Key : ts.Name;
                if (tsName.IndexOf('|') < 0 && entry.Key.IndexOf('|') < 0)
                {
                    disponibles.Add(tsName);
                }

                if (nombre != null && matchedId.IsNull
                    && (string.Equals(tsName, nombre, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(entry.Key, nombre, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedId = entry.Value;
                    matchedKey = entry.Key;
                }
            }

            ObjectId targetId;
            if (nombre == null)
            {
                targetId = db.Tablestyle;
                if (targetId.IsNull || targetId.IsErased)
                {
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        "El estilo de tabla actual del dibujo no es válido (nulo o borrado); indique 'estilo'.",
                        new Dictionary<string, object?> { ["estilo"] = null, ["disponibles"] = disponibles });
                }
            }
            else
            {
                if (matchedId.IsNull)
                {
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        "El estilo de tabla '" + nombre + "' no existe en el dibujo. Disponibles: "
                        + string.Join(", ", disponibles) + ".",
                        new Dictionary<string, object?> { ["estilo"] = nombre, ["disponibles"] = disponibles });
                }

                targetId = matchedId;
            }

            var resolved = (TableStyle)tr.GetObject(targetId, OpenMode.ForRead);
            string finalName = string.IsNullOrEmpty(resolved.Name) ? (matchedKey ?? "Standard") : resolved.Name;
            if (finalName.IndexOf('|') >= 0 || (matchedKey != null && matchedKey.IndexOf('|') >= 0))
            {
                throw ToolException.InvalidParams(
                    "estilo: el estilo de tabla '" + finalName + "' es dependiente de una xref; use un estilo del dibujo.",
                    new Dictionary<string, object?> { ["estilo"] = finalName, ["xref"] = true });
            }

            var cellStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (object? cs in resolved.CellStyles)
                {
                    if (cs is string s && !string.IsNullOrEmpty(s))
                    {
                        cellStyles.Add(s);
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Si no expone CellStyles, se continúa sin cambiar el estilo de celda.
            }

            double vMargin = ReadStyleMargin(resolved, dataStyleCandidates: new[] { "Data", "_DATA" }, isVertical: true);
            double hMargin = ReadStyleMargin(resolved, dataStyleCandidates: new[] { "Data", "_DATA" }, isVertical: false);
            return new TableStyleInfo(targetId, finalName, vMargin, hMargin, cellStyles);
        }

        private static double ReadStyleMargin(TableStyle ts, string[] dataStyleCandidates, bool isVertical)
        {
            double maxVal = 0.0;
#pragma warning disable CS0618
            double topLevel = isVertical ? ts.VerticalCellMargin : ts.HorizontalCellMargin;
#pragma warning restore CS0618
            if (double.IsFinite(topLevel) && topLevel > maxVal)
            {
                maxVal = topLevel;
            }

            foreach (string style in dataStyleCandidates)
            {
                try
                {
                    if (isVertical)
                    {
                        double top = ts.Margin(CellMargins.Top, style);
                        double bot = ts.Margin(CellMargins.Bottom, style);
                        if (double.IsFinite(top) && top > maxVal) maxVal = top;
                        if (double.IsFinite(bot) && bot > maxVal) maxVal = bot;
                    }
                    else
                    {
                        double left = ts.Margin(CellMargins.Left, style);
                        double right = ts.Margin(CellMargins.Right, style);
                        if (double.IsFinite(left) && left > maxVal) maxVal = left;
                        if (double.IsFinite(right) && right > maxVal) maxVal = right;
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // El estilo no expone ese CellStyle: se ignora.
                }
            }

            return maxVal;
        }

        /// <summary>
        /// Lee el margen efectivo actual de una tabla (nivel tabla y celda 0,0) para decidir reducciones seguras al editar anchos/altos.
        /// </summary>
        public static (double Vertical, double Horizontal) ReadCurrentMargins(Table table)
        {
            double v = 0.0;
            double h = 0.0;
            try
            {
#pragma warning disable CS0618
                if (double.IsFinite(table.VerticalCellMargin)) v = Math.Max(v, table.VerticalCellMargin);
                if (double.IsFinite(table.HorizontalCellMargin)) h = Math.Max(h, table.HorizontalCellMargin);
                if (table.Rows.Count > 0 && table.Columns.Count > 0)
                {
                    double t = table.GetMargin(0, 0, CellMargins.Top);
                    double b = table.GetMargin(0, 0, CellMargins.Bottom);
                    double l = table.GetMargin(0, 0, CellMargins.Left);
                    double r = table.GetMargin(0, 0, CellMargins.Right);
                    if (double.IsFinite(t)) v = Math.Max(v, t);
                    if (double.IsFinite(b)) v = Math.Max(v, b);
                    if (double.IsFinite(l)) h = Math.Max(h, l);
                    if (double.IsFinite(r)) h = Math.Max(h, r);
                }
#pragma warning restore CS0618
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Ignorar si la tabla aún no expone márgenes por celda.
            }

            return (v, h);
        }

        /// <summary>
        /// Aplica márgenes en los tres niveles de AutoCAD (propiedad de tabla, bordes del rango global y cada celda)
        /// para neutralizar los márgenes por defecto del estilo Standard (1.5 u = 3.0 u extra por columna).
        /// </summary>
        public static void ApplyMargins(Table table, double? vert, double? horiz)
        {
            if (!vert.HasValue && !horiz.HasValue)
            {
                return;
            }

#pragma warning disable CS0618
            if (vert.HasValue)
            {
                table.VerticalCellMargin = vert.Value;
            }

            if (horiz.HasValue)
            {
                table.HorizontalCellMargin = horiz.Value;
            }
#pragma warning restore CS0618

            if (vert.HasValue)
            {
                TrySetBorderMargin(() => table.Cells.Borders.Top.Margin = vert.Value);
                TrySetBorderMargin(() => table.Cells.Borders.Bottom.Margin = vert.Value);
                TrySetBorderMargin(() => table.Cells.Borders.Horizontal.Margin = vert.Value);
            }

            if (horiz.HasValue)
            {
                TrySetBorderMargin(() => table.Cells.Borders.Left.Margin = horiz.Value);
                TrySetBorderMargin(() => table.Cells.Borders.Right.Margin = horiz.Value);
                TrySetBorderMargin(() => table.Cells.Borders.Vertical.Margin = horiz.Value);
            }

            int rows = table.Rows.Count;
            int cols = table.Columns.Count;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
#pragma warning disable CS0618
                    if (vert.HasValue)
                    {
                        TrySetBorderMargin(() => table.SetMargin(r, c, CellMargins.Top, vert.Value));
                        TrySetBorderMargin(() => table.SetMargin(r, c, CellMargins.Bottom, vert.Value));
                    }

                    if (horiz.HasValue)
                    {
                        TrySetBorderMargin(() => table.SetMargin(r, c, CellMargins.Left, horiz.Value));
                        TrySetBorderMargin(() => table.SetMargin(r, c, CellMargins.Right, horiz.Value));
                    }
#pragma warning restore CS0618
                    Cell cell = table.Cells[r, c];
                    if (vert.HasValue)
                    {
                        TrySetBorderMargin(() => cell.Borders.Top.Margin = vert.Value);
                        TrySetBorderMargin(() => cell.Borders.Bottom.Margin = vert.Value);
                    }

                    if (horiz.HasValue)
                    {
                        TrySetBorderMargin(() => cell.Borders.Left.Margin = horiz.Value);
                        TrySetBorderMargin(() => cell.Borders.Right.Margin = horiz.Value);
                    }
                }
            }
        }

        private static void TrySetBorderMargin(Action setter)
        {
            try
            {
                setter();
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Bordes internos pueden no aplicar en tablas 1×1 o celdas combinadas.
            }
        }

        /// <summary>Busca el estilo de celda de datos ('Data' o '_DATA') para neutralizar los estilos Title/Header por defecto.</summary>
        public static string? FindDataCellStyle(HashSet<string> cellStyles)
        {
            foreach (string s in cellStyles)
            {
                if (string.Equals(s, "Data", StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }
            }

            foreach (string s in cellStyles)
            {
                if (string.Equals(s, "_DATA", StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }
            }

            return null;
        }

        public static CellAlignment ToCellAlignment(string code) => code switch
        {
            "TL" => CellAlignment.TopLeft,
            "TC" => CellAlignment.TopCenter,
            "TR" => CellAlignment.TopRight,
            "ML" => CellAlignment.MiddleLeft,
            "MC" => CellAlignment.MiddleCenter,
            "MR" => CellAlignment.MiddleRight,
            "BL" => CellAlignment.BottomLeft,
            "BC" => CellAlignment.BottomCenter,
            "BR" => CellAlignment.BottomRight,
            _ => throw ToolException.InvalidParams("alineacion: valor inválido"),
        };

        public static string? FromCellAlignment(CellAlignment? a) => a switch
        {
            CellAlignment.TopLeft => "TL",
            CellAlignment.TopCenter => "TC",
            CellAlignment.TopRight => "TR",
            CellAlignment.MiddleLeft => "ML",
            CellAlignment.MiddleCenter => "MC",
            CellAlignment.MiddleRight => "MR",
            CellAlignment.BottomLeft => "BL",
            CellAlignment.BottomCenter => "BC",
            CellAlignment.BottomRight => "BR",
            _ => null,
        };

        /// <summary>Lee un objeto celda del JSON para <c>tabla</c> o <c>editar_tabla</c>.</summary>
        public static TableCellSpec ParseCellSpec(JsonElement el, string prefix, bool allowCombinar, bool requireAnyChange)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto");
            }

            ParamReader.RejectUnknown(el, allowCombinar ? CellAllowedCreate : CellAllowedEdit, prefix);

            int fila = OptionalRequiredInt(el, "fila", 0, TableRules.MaxRows - 1, prefix);
            int columna = OptionalRequiredInt(el, "columna", 0, TableRules.MaxColumns - 1, prefix);

            string? texto = OptionalStringPrefixed(el, "texto", 0, TableRules.MaxCellTextLength, prefix);
            string? textoFormato = OptionalStringPrefixed(el, "texto_formato", 0, TableRules.MaxCellFormatLength, prefix);
            if (texto != null && textoFormato != null)
            {
                throw ToolException.InvalidParams(prefix + "texto y texto_formato son excluyentes");
            }

            string? alineacion = OptionalEnumPrefixed(el, "alineacion", TableRules.ValidAlignments, prefix);
            double? alturaTexto = OptionalNumberPrefixed(el, "altura_texto", 0, double.PositiveInfinity, true, prefix);
            string? estiloTexto = OptionalStringPrefixed(el, "estilo_texto", 1, 255, prefix);
            ColorSpec? colorTexto = ReadCellColor(el, "color_texto", prefix);
            TableBgSpec? colorFondo = ReadBgColor(el, "color_fondo", prefix);

            TableMerge? combinar = null;
            if (allowCombinar && el.TryGetProperty("combinar", out JsonElement combEl))
            {
                string combPrefix = prefix + "combinar.";
                if (combEl.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(prefix + "combinar: debe ser un objeto {filas, columnas}");
                }

                ParamReader.RejectUnknown(combEl, MergeAllowed, combPrefix);
                int mFilas = OptionalRequiredInt(combEl, "filas", 1, TableRules.MaxRows, combPrefix);
                int mCols = OptionalRequiredInt(combEl, "columnas", 1, TableRules.MaxColumns, combPrefix);
                combinar = new TableMerge(fila, columna, mFilas, mCols);
            }

            if (requireAnyChange && texto == null && textoFormato == null && alineacion == null
                && !alturaTexto.HasValue && estiloTexto == null && !colorTexto.HasValue && !colorFondo.HasValue)
            {
                throw ToolException.InvalidParams(
                    prefix + "indique al menos una propiedad a modificar (texto, texto_formato, alineacion, altura_texto, estilo_texto, color_texto o color_fondo)");
            }

            return new TableCellSpec(fila, columna, texto, textoFormato, alineacion, alturaTexto, estiloTexto, colorTexto, colorFondo, combinar);
        }

        /// <summary>Lee un rango {fila, columna, filas, columnas} para <c>combinar_celdas</c>.</summary>
        public static TableMerge ParseFullMerge(JsonElement el, string prefix)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {fila, columna, filas, columnas}");
            }

            ParamReader.RejectUnknown(el, FullMergeAllowed, prefix);
            int fila = OptionalRequiredInt(el, "fila", 0, TableRules.MaxRows - 1, prefix);
            int col = OptionalRequiredInt(el, "columna", 0, TableRules.MaxColumns - 1, prefix);
            int filas = OptionalRequiredInt(el, "filas", 1, TableRules.MaxRows, prefix);
            int cols = OptionalRequiredInt(el, "columnas", 1, TableRules.MaxColumns, prefix);
            return new TableMerge(fila, col, filas, cols);
        }

        public static ColorSpec? ReadCellColor(JsonElement obj, string propName, string prefix)
        {
            if (!obj.TryGetProperty(propName, out JsonElement c))
            {
                return null;
            }

            if (c.ValueKind == JsonValueKind.String)
            {
                string text = c.GetString() ?? "";
                ColorSpec? word = ColorRules.FromWord(text);
                if (word != null)
                {
                    return word;
                }

                if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int aci)
                    && ColorRules.IsAci(aci))
                {
                    return ColorRules.FromAci(aci);
                }

                throw ToolException.InvalidParams(prefix + propName + ": debe ser ACI 0–256, 'porcapa', 'porbloque' o [r, g, b]");
            }

            if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out double v) && ColorRules.IsAci(v))
            {
                return ColorRules.FromAci((int)v);
            }

            if (c.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    return EntityColor.ReadRgb(c);
                }
                catch (ToolException)
                {
                    throw ToolException.InvalidParams(prefix + propName + ": el color RGB debe ser [r, g, b] con enteros 0–255");
                }
            }

            throw ToolException.InvalidParams(prefix + propName + ": debe ser ACI 0–256, 'porcapa', 'porbloque' o [r, g, b]");
        }

        public static TableBgSpec? ReadBgColor(JsonElement obj, string propName, string prefix)
        {
            if (!obj.TryGetProperty(propName, out JsonElement c))
            {
                return null;
            }

            if (c.ValueKind == JsonValueKind.String)
            {
                string text = (c.GetString() ?? "").Trim();
                if (string.Equals(text, "ninguno", StringComparison.OrdinalIgnoreCase))
                {
                    return TableBgSpec.None();
                }

                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int aci)
                    && aci >= 1 && aci <= 255)
                {
                    return TableBgSpec.FromColor(ColorRules.FromAci(aci));
                }

                throw ToolException.InvalidParams(prefix + propName + ": debe ser ACI 1–255, 'ninguno' o [r, g, b]");
            }

            if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out double v)
                && double.IsFinite(v) && Math.Floor(v) == v && v >= 1 && v <= 255)
            {
                return TableBgSpec.FromColor(ColorRules.FromAci((int)v));
            }

            if (c.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    return TableBgSpec.FromColor(EntityColor.ReadRgb(c));
                }
                catch (ToolException)
                {
                    throw ToolException.InvalidParams(prefix + propName + ": el color RGB debe ser [r, g, b] con enteros 0–255");
                }
            }

            throw ToolException.InvalidParams(prefix + propName + ": debe ser ACI 1–255, 'ninguno' o [r, g, b]");
        }

        /// <summary>Comprueba si una celda admite edición de contenido/formato y protege vínculos DataLink, campos y fórmulas.</summary>
        public static void CheckCellEditable(
            Table table,
            int row,
            int col,
            bool changingText,
            bool changingFormat,
            bool aceptarPerdidaCampos,
            string path)
        {
            Cell cell = table.Cells[row, col];
            if (cell.IsLinked == true)
            {
                throw ToolException.InvalidParams(
                    path + ": la celda (" + row.ToString(CultureInfo.InvariantCulture) + ", "
                    + col.ToString(CultureInfo.InvariantCulture) + ") está vinculada a datos externos (DataLink) y no se puede editar aquí.");
            }

            if (changingText && cell.IsContentEditable == false)
            {
                throw ToolException.InvalidParams(
                    path + ": el contenido de la celda (" + row.ToString(CultureInfo.InvariantCulture) + ", "
                    + col.ToString(CultureInfo.InvariantCulture) + ") es de solo lectura o está bloqueado.");
            }

            if (changingFormat && cell.IsFormatEditable == false)
            {
                throw ToolException.InvalidParams(
                    path + ": el formato de la celda (" + row.ToString(CultureInfo.InvariantCulture) + ", "
                    + col.ToString(CultureInfo.InvariantCulture) + ") es de solo lectura o está bloqueado.");
            }

            if (changingText && !aceptarPerdidaCampos)
            {
                bool hasField = !cell.FieldId.IsNull;
                bool hasFormula = false;
                try
                {
                    foreach (CellContent content in cell.Contents)
                    {
                        if (content.HasFormula || !content.FieldId.IsNull)
                        {
                            hasFormula = hasFormula || content.HasFormula;
                            hasField = hasField || !content.FieldId.IsNull;
                        }
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // Si Contents no es accesible, basta el FieldId de la celda.
                }

                if (hasField || hasFormula)
                {
                    throw ToolException.InvalidParams(
                        path + ": la celda (" + row.ToString(CultureInfo.InvariantCulture) + ", "
                        + col.ToString(CultureInfo.InvariantCulture) + ") contiene un campo (FIELD) o fórmula: cambiar su texto lo sustituye por texto fijo. "
                        + "Pase aceptar_perdida_campos: true para aceptarlo.",
                        new Dictionary<string, object?>
                        {
                            ["fila"] = row,
                            ["columna"] = col,
                            ["campos"] = true,
                        });
                }
            }
        }

        public static void ApplyCellContent(Cell cell, string? texto, string? textoFormato)
        {
            if (texto == null && textoFormato == null)
            {
                return;
            }

            if (texto == "" || textoFormato == "")
            {
                cell.DeleteContent();
                return;
            }

            cell.TextString = texto != null ? MTextEscape.Escape(texto) : textoFormato!;
        }

        public static void ApplyCellFormatting(
            Cell cell,
            string? alineacion,
            double? alturaTexto,
            ObjectId textStyleId,
            ColorSpec? colorTexto,
            TableBgSpec? colorFondo)
        {
            if (alineacion != null)
            {
                cell.Alignment = ToCellAlignment(alineacion);
            }

            if (alturaTexto.HasValue)
            {
                cell.TextHeight = alturaTexto.Value;
            }

            if (!textStyleId.IsNull)
            {
                cell.TextStyleId = textStyleId;
            }

            if (colorTexto.HasValue)
            {
                cell.ContentColor = EntityColor.ToColor(colorTexto.Value);
            }

            if (colorFondo.HasValue)
            {
                if (colorFondo.Value.IsNone)
                {
                    cell.IsBackgroundColorNone = true;
                }
                else
                {
                    cell.IsBackgroundColorNone = false;
                    cell.BackgroundColor = EntityColor.ToColor(colorFondo.Value.Color!.Value);
                }
            }
        }

        /// <summary>
        /// Devuelve el rango combinado no trivial de la celda (o null si no está combinada). No confía en
        /// <c>Cell.IsMerged</c>, que en AutoCAD puede devolver false aunque la celda pertenezca a un rango combinado.
        /// </summary>
        public static CellRange? TryGetMergeRange(Table table, int row, int col)
        {
            try
            {
                Cell cell = table.Cells[row, col];
                CellRange? mr = cell.GetMergeRange();
                if (mr != null && !mr.IsNull && (mr.BottomRow > mr.TopRow || mr.RightColumn > mr.LeftColumn))
                {
                    return mr;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Se intenta el método directo sobre Table.
            }

            try
            {
#pragma warning disable CS0618
                CellRange? mr = table.GetMergeRange(row, col);
#pragma warning restore CS0618
                if (mr != null && !mr.IsNull && (mr.BottomRow > mr.TopRow || mr.RightColumn > mr.LeftColumn))
                {
                    return mr;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Celda no combinada.
            }

            return null;
        }

        /// <summary>Recoge todas las combinaciones activas únicas (no triviales) de la tabla.</summary>
        public static List<TableMerge> CollectMerges(Table table)
        {
            int rows = table.Rows.Count;
            int cols = table.Columns.Count;
            var seen = new HashSet<(int, int)>();
            var list = new List<TableMerge>();

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    CellRange? mr = TryGetMergeRange(table, r, c);
                    if (mr == null)
                    {
                        continue;
                    }

                    if (seen.Add((mr.TopRow, mr.LeftColumn)))
                    {
                        list.Add(new TableMerge(
                            mr.TopRow,
                            mr.LeftColumn,
                            mr.BottomRow - mr.TopRow + 1,
                            mr.RightColumn - mr.LeftColumn + 1));
                    }
                }
            }

            return list;
        }

        /// <summary>Descombina cualquier combinación automática que cree el TableStyle al inicializar la tabla.</summary>
        public static void ClearAutoMerges(Table table)
        {
            foreach (TableMerge m in CollectMerges(table))
            {
                table.UnmergeCells(CellRange.Create(table, m.Fila, m.Columna, m.BottomRow, m.RightColumn));
            }
        }

        public static string ReadCellText(Cell cell)
        {
            try
            {
                return cell.GetTextString(FormatOption.IgnoreMtextFormat) ?? "";
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return cell.TextString ?? "";
            }
        }

        public static string ReadCellRawFormat(Cell cell)
        {
            try
            {
                return cell.GetTextString(FormatOption.ForEditing) ?? "";
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return cell.TextString ?? "";
            }
        }

        public static int OptionalRequiredInt(JsonElement obj, string name, int min, int max, string prefix)
        {
            if (!obj.TryGetProperty(name, out _))
            {
                throw ToolException.InvalidParams(prefix + name + ": obligatorio");
            }

            try
            {
                return ParamReader.OptionalInt(obj, name, min, max)!.Value;
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }

        public static double? OptionalNumberPrefixed(JsonElement obj, string name, double min, double max, bool exclusiveMin, string prefix)
        {
            try
            {
                return ParamReader.OptionalNumber(obj, name, min, max, exclusiveMin);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }

        public static double RequiredNumberPrefixed(JsonElement obj, string name, double min, double max, bool exclusiveMin, string prefix)
        {
            if (!obj.TryGetProperty(name, out _))
            {
                throw ToolException.InvalidParams(prefix + name + ": obligatorio");
            }

            return OptionalNumberPrefixed(obj, name, min, max, exclusiveMin, prefix)!.Value;
        }

        private static string? OptionalStringPrefixed(JsonElement obj, string name, int minLen, int maxLen, string prefix)
        {
            try
            {
                return ParamReader.OptionalString(obj, name, minLen, maxLen);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }

        private static string? OptionalEnumPrefixed(JsonElement obj, string name, string[] allowed, string prefix)
        {
            try
            {
                return ParamReader.OptionalEnum(obj, name, allowed);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }
    }
}
