using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>Color de fondo de una celda: o bien transparente ('ninguno'), o un color explícito.</summary>
    public readonly record struct TableBgSpec(bool IsNone, ColorSpec? Color)
    {
        public static TableBgSpec None() => new TableBgSpec(true, null);

        public static TableBgSpec FromColor(ColorSpec color) => new TableBgSpec(false, color);
    }

    /// <summary>Rango combinado rectangular (fila/columna base 0, número de filas y columnas &gt;= 1).</summary>
    public sealed record TableMerge(int Fila, int Columna, int Filas, int Columnas)
    {
        public int BottomRow => Fila + Filas - 1;

        public int RightColumn => Columna + Columnas - 1;

        public bool IsTrivial => Filas == 1 && Columnas == 1;

        public bool Contains(int r, int c) =>
            r >= Fila && r <= BottomRow && c >= Columna && c <= RightColumn;

        public bool IsAnchor(int r, int c) => r == Fila && c == Columna;

        public bool Overlaps(TableMerge other) =>
            Fila <= other.BottomRow && BottomRow >= other.Fila
            && Columna <= other.RightColumn && RightColumn >= other.Columna;
    }

    /// <summary>Especificación validada de una celda para crear o editar tablas.</summary>
    public sealed record TableCellSpec(
        int Fila,
        int Columna,
        string? Texto = null,
        string? TextoFormato = null,
        string? Alineacion = null,
        double? AlturaTexto = null,
        string? EstiloTexto = null,
        ColorSpec? ColorTexto = null,
        TableBgSpec? ColorFondo = null,
        TableMerge? Combinar = null);

    /// <summary>Operación estructural (inserción o borrado de filas/columnas) en <c>editar_tabla</c>.</summary>
    public sealed record TableStructuralOp(int Indice, int Cantidad, double? Medida = null, int? HeredarDe = null);

    /// <summary>Márgenes seguros calculados para una tabla (null = conservar el del estilo).</summary>
    public readonly record struct SafeMarginsResult(double? Vertical, double? Horizontal, bool AutoAdjusted);

    /// <summary>
    /// Reglas puras para creación, edición y lectura de tablas nativas (<c>AcDbTable</c>, fallo 47).
    /// Sin dependencias de AutoCAD.
    /// </summary>
    public static class TableRules
    {
        public const int MaxRows = 200;
        public const int MaxColumns = 50;
        public const int MaxTotalCells = 2500;
        public const int MaxCellTextLength = 2000;
        public const int MaxCellFormatLength = 4000;
        public const int MaxStructuralOps = 50;

        public static readonly string[] ValidAlignments =
        {
            "TL", "TC", "TR",
            "ML", "MC", "MR",
            "BL", "BC", "BR",
        };

        public static bool IsValidAlignment(string? value)
        {
            if (value == null)
            {
                return false;
            }

            foreach (string a in ValidAlignments)
            {
                if (string.Equals(a, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Comprueba dimensiones dentro de [1..MaxRows] × [1..MaxColumns] y celdas &lt;= MaxTotalCells.</summary>
        public static string? ValidateDimensions(int filas, int columnas)
        {
            if (filas < 1 || filas > MaxRows)
            {
                return string.Format(CultureInfo.InvariantCulture, "filas: debe ser un entero entre 1 y {0}", MaxRows);
            }

            if (columnas < 1 || columnas > MaxColumns)
            {
                return string.Format(CultureInfo.InvariantCulture, "columnas: debe ser un entero entre 1 y {0}", MaxColumns);
            }

            long total = (long)filas * columnas;
            if (total > MaxTotalCells)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "tabla demasiado grande ({0} × {1} = {2} celdas; máximo {3})",
                    filas, columnas, total, MaxTotalCells);
            }

            return null;
        }

        /// <summary>Resuelve los anchos por columna a partir de ancho_columna o ancho_columnas[] (exactamente uno).</summary>
        public static double[] ResolveColumnWidths(int columnas, double? anchoColumna, IReadOnlyList<double>? anchoColumnas)
        {
            if (columnas < 1 || columnas > MaxColumns)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, "columnas: debe ser un entero entre 1 y {0}", MaxColumns));
            }

            bool hasSingle = anchoColumna.HasValue;
            bool hasArray = anchoColumnas != null;
            if (hasSingle == hasArray)
            {
                throw new ArgumentException("indique exactamente uno de ancho_columna o ancho_columnas");
            }

            var widths = new double[columnas];
            if (hasSingle)
            {
                double w = anchoColumna!.Value;
                if (!double.IsFinite(w) || w <= 0)
                {
                    throw new ArgumentException("ancho_columna: debe ser un número mayor que 0");
                }

                for (int i = 0; i < columnas; i++)
                {
                    widths[i] = w;
                }

                return widths;
            }

            if (anchoColumnas!.Count != columnas)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "ancho_columnas: debe tener exactamente {0} elementos (tiene {1})",
                    columnas, anchoColumnas.Count));
            }

            for (int i = 0; i < columnas; i++)
            {
                double w = anchoColumnas[i];
                if (!double.IsFinite(w) || w <= 0)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "ancho_columnas[{0}]: debe ser un número mayor que 0",
                        i));
                }

                widths[i] = w;
            }

            return widths;
        }

        /// <summary>Resuelve los altos por fila a partir de alto_fila o altos_filas[] (exactamente uno).</summary>
        public static double[] ResolveRowHeights(int filas, double? altoFila, IReadOnlyList<double>? altosFilas)
        {
            if (filas < 1 || filas > MaxRows)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, "filas: debe ser un entero entre 1 y {0}", MaxRows));
            }

            bool hasSingle = altoFila.HasValue;
            bool hasArray = altosFilas != null;
            if (hasSingle == hasArray)
            {
                throw new ArgumentException("indique exactamente uno de alto_fila o altos_filas");
            }

            var heights = new double[filas];
            if (hasSingle)
            {
                double h = altoFila!.Value;
                if (!double.IsFinite(h) || h <= 0)
                {
                    throw new ArgumentException("alto_fila: debe ser un número mayor que 0");
                }

                for (int i = 0; i < filas; i++)
                {
                    heights[i] = h;
                }

                return heights;
            }

            if (altosFilas!.Count != filas)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "altos_filas: debe tener exactamente {0} elementos (tiene {1})",
                    filas, altosFilas.Count));
            }

            for (int i = 0; i < filas; i++)
            {
                double h = altosFilas[i];
                if (!double.IsFinite(h) || h <= 0)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "altos_filas[{0}]: debe ser un número mayor que 0",
                        i));
                }

                heights[i] = h;
            }

            return heights;
        }

        /// <summary>
        /// Valida celdas y combinaciones: límites, duplicados, exclusión texto/texto_formato, sintaxis MTEXT,
        /// alineación, rangos sin solapes y prohibición de apuntar a celdas secundarias cubiertas por un merge.
        /// Devuelve la lista de combinaciones no triviales (Filas &gt; 1 o Columnas &gt; 1).
        /// </summary>
        public static List<TableMerge> ValidateCellsAndMerges(
            int filas,
            int columnas,
            IReadOnlyList<TableCellSpec> celdas,
            IReadOnlyList<TableMerge>? extraMerges = null)
        {
            string? dimErr = ValidateDimensions(filas, columnas);
            if (dimErr != null)
            {
                throw new ArgumentException(dimErr);
            }

            var merges = new List<TableMerge>();
            var seenCells = new HashSet<(int, int)>();

            for (int i = 0; i < celdas.Count; i++)
            {
                TableCellSpec c = celdas[i];
                string prefix = string.Format(CultureInfo.InvariantCulture, "celdas[{0}].", i);
                if (c.Fila < 0 || c.Fila >= filas)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}fila: fuera de rango [0, {1}]",
                        prefix, filas - 1));
                }

                if (c.Columna < 0 || c.Columna >= columnas)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}columna: fuera de rango [0, {1}]",
                        prefix, columnas - 1));
                }

                if (!seenCells.Add((c.Fila, c.Columna)))
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}celda ({1}, {2}) duplicada en celdas",
                        prefix, c.Fila, c.Columna));
                }

                if (c.Texto != null && c.TextoFormato != null)
                {
                    throw new ArgumentException(prefix + "texto y texto_formato son excluyentes");
                }

                if (c.Texto != null && c.Texto.Length > MaxCellTextLength)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}texto: supera el máximo de {1} caracteres",
                        prefix, MaxCellTextLength));
                }

                if (c.TextoFormato != null)
                {
                    if (c.TextoFormato.Length > MaxCellFormatLength)
                    {
                        throw new ArgumentException(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}texto_formato: supera el máximo de {1} caracteres",
                            prefix, MaxCellFormatLength));
                    }

                    if (c.TextoFormato.Length > 0)
                    {
                        string? motivo = TextFormatParser.ValidateMText(c.TextoFormato);
                        if (motivo != null)
                        {
                            throw new ArgumentException(prefix + "texto_formato: " + motivo);
                        }
                    }
                }

                if (c.Alineacion != null && !IsValidAlignment(c.Alineacion))
                {
                    throw new ArgumentException(prefix + "alineacion: debe ser uno de " + string.Join(", ", ValidAlignments));
                }

                if (c.AlturaTexto.HasValue && (!double.IsFinite(c.AlturaTexto.Value) || c.AlturaTexto.Value <= 0))
                {
                    throw new ArgumentException(prefix + "altura_texto: debe ser un número mayor que 0");
                }

                if (c.Combinar != null)
                {
                    ValidateMerge(c.Combinar, filas, columnas, prefix + "combinar.");
                    if (!c.Combinar.IsTrivial)
                    {
                        AddUniqueMerge(merges, c.Combinar, prefix + "combinar");
                    }
                }
            }

            if (extraMerges != null)
            {
                for (int i = 0; i < extraMerges.Count; i++)
                {
                    TableMerge m = extraMerges[i];
                    string path = string.Format(CultureInfo.InvariantCulture, "combinar_celdas[{0}]", i);
                    ValidateMerge(m, filas, columnas, path + ".");
                    if (!m.IsTrivial)
                    {
                        AddUniqueMerge(merges, m, path);
                    }
                }
            }

            for (int i = 0; i < celdas.Count; i++)
            {
                TableCellSpec c = celdas[i];
                foreach (TableMerge m in merges)
                {
                    if (m.Contains(c.Fila, c.Columna) && !m.IsAnchor(c.Fila, c.Columna))
                    {
                        throw new ArgumentException(string.Format(
                            CultureInfo.InvariantCulture,
                            "celdas[{0}]: la celda ({1}, {2}) queda cubierta por la combinación ({3}, {4}, {5}×{6}); escriba solo en la celda superior izquierda ({3}, {4})",
                            i, c.Fila, c.Columna, m.Fila, m.Columna, m.Filas, m.Columnas));
                    }
                }
            }

            return merges;
        }

        private static void ValidateMerge(TableMerge m, int filas, int columnas, string prefix)
        {
            if (m.Fila < 0 || m.Fila >= filas)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, "{0}fila: fuera de rango [0, {1}]", prefix, filas - 1));
            }

            if (m.Columna < 0 || m.Columna >= columnas)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, "{0}columna: fuera de rango [0, {1}]", prefix, columnas - 1));
            }

            if (m.Filas < 1)
            {
                throw new ArgumentException(prefix + "filas: debe ser mayor o igual que 1");
            }

            if (m.Columnas < 1)
            {
                throw new ArgumentException(prefix + "columnas: debe ser mayor o igual que 1");
            }

            if (m.BottomRow >= filas)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}filas: la combinación excede el número de filas ({1} + {2} > {3})",
                    prefix, m.Fila, m.Filas, filas));
            }

            if (m.RightColumn >= columnas)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}columnas: la combinación excede el número de columnas ({1} + {2} > {3})",
                    prefix, m.Columna, m.Columnas, columnas));
            }
        }

        private static void AddUniqueMerge(List<TableMerge> merges, TableMerge candidate, string path)
        {
            foreach (TableMerge existing in merges)
            {
                if (existing.Overlaps(candidate))
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: se solapa con otra combinación ({1}, {2}, {3}×{4})",
                        path, existing.Fila, existing.Columna, existing.Filas, existing.Columnas));
                }
            }

            merges.Add(candidate);
        }

        /// <summary>
        /// Estima la caja 2D de una tabla horizontal cuya esquina superior izquierda está en <paramref name="posicion"/>
        /// y crece hacia +X y −Y (dirección por defecto [1, 0, 0]).
        /// </summary>
        public static BBox2 EstimateBBox(Point3 posicion, IReadOnlyList<double> colWidths, IReadOnlyList<double> rowHeights)
        {
            double w = 0.0;
            foreach (double cw in colWidths)
            {
                w += cw;
            }

            double h = 0.0;
            foreach (double rh in rowHeights)
            {
                h += rh;
            }

            return BBox2.FromCorners(posicion.X, posicion.Y - h, posicion.X + w, posicion.Y);
        }

        /// <summary>
        /// Calcula márgenes seguros cuando el estilo (p. ej. Standard en mm con margen 1.5) excede el tamaño pedido
        /// en planos en metros, evitando que AutoCAD infle la tabla por MinimumHeight/MinimumWidth.
        /// </summary>
        public static SafeMarginsResult ComputeSafeMargins(
            double styleVert,
            double styleHoriz,
            double minRowHeight,
            double minColWidth,
            double textHeight,
            double? reqVert,
            double? reqHoriz)
        {
            double? outVert = reqVert;
            double? outHoriz = reqHoriz;
            bool autoAdjusted = false;

            if (!outVert.HasValue)
            {
                double maxAllowedV = Math.Max(0.0, (minRowHeight - textHeight) * 0.45);
                if (styleVert > maxAllowedV)
                {
                    outVert = Math.Round(Math.Min(maxAllowedV, textHeight * 0.25), 6);
                    autoAdjusted = true;
                }
            }

            if (!outHoriz.HasValue)
            {
                double maxAllowedH = Math.Max(0.0, minColWidth * 0.25);
                if (styleHoriz > maxAllowedH)
                {
                    outHoriz = Math.Round(Math.Min(maxAllowedH, textHeight * 0.35), 6);
                    autoAdjusted = true;
                }
            }

            return new SafeMarginsResult(outVert, outHoriz, autoAdjusted);
        }

        /// <summary>
        /// Valida operaciones de inserción de filas o columnas y las devuelve ordenadas de mayor a menor índice
        /// para aplicarlas sin desplazar los índices de las siguientes operaciones.
        /// </summary>
        public static (List<TableStructuralOp> Ordered, int NewCount) PlanInsert(
            int currentCount,
            int otherDim,
            int maxCount,
            bool isRows,
            IReadOnlyList<TableStructuralOp> ops)
        {
            string kind = isRows ? "insertar_filas" : "insertar_columnas";
            string measure = isRows ? "alto" : "ancho";
            if (ops.Count == 0 || ops.Count > MaxStructuralOps)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: debe tener entre 1 y {1} operaciones",
                    kind, MaxStructuralOps));
            }

            var seen = new HashSet<int>();
            long added = 0;
            var list = new List<TableStructuralOp>(ops.Count);
            for (int i = 0; i < ops.Count; i++)
            {
                TableStructuralOp op = ops[i];
                string prefix = string.Format(CultureInfo.InvariantCulture, "{0}[{1}].", kind, i);
                if (op.Indice < 0 || op.Indice > currentCount)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}indice: fuera de rango [0, {1}]",
                        prefix, currentCount));
                }

                if (!seen.Add(op.Indice))
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}indice {1} repetido",
                        prefix, op.Indice));
                }

                if (op.Cantidad < 1 || op.Cantidad > maxCount)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}cantidad: debe ser un entero entre 1 y {1}",
                        prefix, maxCount));
                }

                if (op.Medida.HasValue && (!double.IsFinite(op.Medida.Value) || op.Medida.Value <= 0))
                {
                    throw new ArgumentException(prefix + measure + ": debe ser un número mayor que 0");
                }

                if (op.HeredarDe.HasValue && (op.HeredarDe.Value < 0 || op.HeredarDe.Value >= currentCount))
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}heredar_de: fuera de rango [0, {1}]",
                        prefix, currentCount - 1));
                }

                added += op.Cantidad;
                list.Add(op);
            }

            long finalLong = currentCount + added;
            if (finalLong > maxCount)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: el total resultante ({1}) supera el máximo ({2})",
                    kind, finalLong, maxCount));
            }

            int newCount = (int)finalLong;
            string? dimErr = isRows ? ValidateDimensions(newCount, otherDim) : ValidateDimensions(otherDim, newCount);
            if (dimErr != null)
            {
                throw new ArgumentException(dimErr);
            }

            list.Sort((a, b) => b.Indice.CompareTo(a.Indice));
            return (list, newCount);
        }

        /// <summary>
        /// Valida operaciones de borrado de filas o columnas (sin solape y dejando al menos 1) y las ordena
        /// de mayor a menor índice.
        /// </summary>
        public static (List<TableStructuralOp> Ordered, int NewCount) PlanDelete(
            int currentCount,
            bool isRows,
            IReadOnlyList<TableStructuralOp> ops)
        {
            string kind = isRows ? "borrar_filas" : "borrar_columnas";
            if (ops.Count == 0 || ops.Count > MaxStructuralOps)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: debe tener entre 1 y {1} operaciones",
                    kind, MaxStructuralOps));
            }

            var list = new List<TableStructuralOp>(ops.Count);
            for (int i = 0; i < ops.Count; i++)
            {
                TableStructuralOp op = ops[i];
                string prefix = string.Format(CultureInfo.InvariantCulture, "{0}[{1}].", kind, i);
                if (op.Indice < 0 || op.Indice >= currentCount)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}indice: fuera de rango [0, {1}]",
                        prefix, currentCount - 1));
                }

                if (op.Cantidad < 1)
                {
                    throw new ArgumentException(prefix + "cantidad: debe ser mayor o igual que 1");
                }

                if (op.Indice + op.Cantidad > currentCount)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}cantidad: el rango [{1}..{2}] excede el tamaño actual ({3})",
                        prefix, op.Indice, op.Indice + op.Cantidad - 1, currentCount));
                }

                list.Add(op);
            }

            list.Sort((a, b) => a.Indice.CompareTo(b.Indice));
            int totalDeleted = 0;
            for (int i = 0; i < list.Count; i++)
            {
                totalDeleted += list[i].Cantidad;
                if (i + 1 < list.Count && list[i].Indice + list[i].Cantidad > list[i + 1].Indice)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: los rangos [{1}..{2}] y [{3}..{4}] se solapan",
                        kind,
                        list[i].Indice, list[i].Indice + list[i].Cantidad - 1,
                        list[i + 1].Indice, list[i + 1].Indice + list[i + 1].Cantidad - 1));
                }
            }

            int newCount = currentCount - totalDeleted;
            if (newCount < 1)
            {
                throw new ArgumentException(kind + ": la tabla debe conservar al menos 1 " + (isRows ? "fila" : "columna"));
            }

            list.Reverse();
            return (list, newCount);
        }

        /// <summary>Valida el rango de filas [fila_inicio, fila_fin] (base 0, inclusivo) para <c>leer_tabla</c>.</summary>
        public static (int Start, int End) ResolveRowSlice(int totalRows, int? filaInicio, int? filaFin)
        {
            if (totalRows <= 0)
            {
                return (0, -1);
            }

            int start = filaInicio ?? 0;
            int end = filaFin ?? (totalRows - 1);
            if (start < 0 || start >= totalRows)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "fila_inicio: fuera de rango [0, {0}]",
                    totalRows - 1));
            }

            if (end < 0 || end >= totalRows)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "fila_fin: fuera de rango [0, {0}]",
                    totalRows - 1));
            }

            if (start > end)
            {
                throw new ArgumentException("fila_inicio: no puede ser mayor que fila_fin");
            }

            return (start, end);
        }
    }
}
