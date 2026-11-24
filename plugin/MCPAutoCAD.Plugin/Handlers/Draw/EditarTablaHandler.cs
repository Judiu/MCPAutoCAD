using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <c>editar_tabla</c> (fallo 47, plugin 0.10.5). Hilo principal; requiere documento; ESCRITURA solo con
    /// <c>confirmar: true</c> (sin él previsualiza cambios sin modificar el dibujo). Permite insertar/borrar filas o
    /// columnas, separar/combinar celdas, cambiar anchos/altos, editar texto/formato/alineación/colores de celdas y
    /// cambiar estilo/capa/posición en un solo paso de deshacer.
    /// </summary>
    internal sealed class EditarTablaHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "handle", "insertar_filas", "borrar_filas", "insertar_columnas", "borrar_columnas",
            "separar_celdas", "combinar_celdas", "anchos_columnas", "altos_filas", "celdas",
            "capa", "estilo", "posicion", "forzar", "aceptar_sin_unidades", "aceptar_perdida_campos",
            "confirmar", "preview",
        };

        private static readonly string[] InsertRowAllowed = { "indice", "cantidad", "alto", "heredar_de" };
        private static readonly string[] InsertColAllowed = { "indice", "cantidad", "ancho", "heredar_de" };
        private static readonly string[] DeleteAllowed = { "indice", "cantidad" };
        private static readonly string[] UnmergeAllowed = { "fila", "columna" };
        private static readonly string[] ColWidthAllowed = { "columna", "ancho" };
        private static readonly string[] RowHeightAllowed = { "fila", "alto" };

        public string Name => "editar_tabla";

        public string Description =>
            "EDITA una tabla nativa existente (ACAD_TABLE) por handle en un solo paso de deshacer: inserta o borra filas y " +
            "columnas, separa o combina celdas, ajusta anchos de columna y altos de fila, modifica contenido/formato/alineación/colores " +
            "de celdas, y opcionalmente cambia capa, estilo o posición. Todos los índices de celdas, anchos, altos y combinaciones " +
            "aplican sobre las dimensiones posteriores a las inserciones/borrados. Sin confirmar: true solo PREVISUALIZA.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        private const string EditCellSchema = @"{
      ""type"": ""object"",
      ""properties"": {
        ""fila"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 199 },
        ""columna"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 49 },
        ""texto"": { ""type"": ""string"", ""minLength"": 0, ""maxLength"": 2000, ""description"": ""Texto literal ('''' borra el contenido). Excluyente con texto_formato."" },
        ""texto_formato"": { ""type"": ""string"", ""minLength"": 0, ""maxLength"": 4000, ""description"": ""Contenido MTEXT crudo con códigos. Excluyente con texto."" },
        ""alineacion"": " + TableSupport.SchemaCellAlignment + @",
        ""altura_texto"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
        ""estilo_texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
        ""color_texto"": " + TableSupport.SchemaColorTextoCell + @",
        ""color_fondo"": " + TableSupport.SchemaColorFondo + @"
      },
      ""required"": [""fila"", ""columna""],
      ""additionalProperties"": false
    }";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handle"": " + ParamReader.SchemaHandle + @",
    ""insertar_filas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""indice"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 200, ""description"": ""Índice donde insertar (0..filas_actuales)."" },
          ""cantidad"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 },
          ""alto"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Alto para las filas nuevas."" },
          ""heredar_de"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 199, ""description"": ""Índice de una fila existente de la que heredar formato."" }
        },
        ""required"": [""indice"", ""cantidad""],
        ""additionalProperties"": false
      },
      ""description"": ""Excluyente con borrar_filas.""
    },
    ""borrar_filas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""indice"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 199 },
          ""cantidad"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 }
        },
        ""required"": [""indice"", ""cantidad""],
        ""additionalProperties"": false
      },
      ""description"": ""Excluyente con insertar_filas.""
    },
    ""insertar_columnas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""indice"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 50 },
          ""cantidad"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50 },
          ""ancho"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
          ""heredar_de"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 49 }
        },
        ""required"": [""indice"", ""cantidad""],
        ""additionalProperties"": false
      },
      ""description"": ""Excluyente con borrar_columnas.""
    },
    ""borrar_columnas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""indice"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 49 },
          ""cantidad"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50 }
        },
        ""required"": [""indice"", ""cantidad""],
        ""additionalProperties"": false
      },
      ""description"": ""Excluyente con insertar_columnas.""
    },
    ""separar_celdas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""fila"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 199 },
          ""columna"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 49 }
        },
        ""required"": [""fila"", ""columna""],
        ""additionalProperties"": false
      },
      ""description"": ""Celdas combinadas a separar (basta cualquier celda del rango combinado).""
    },
    ""combinar_celdas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""fila"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 199 },
          ""columna"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 49 },
          ""filas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200 },
          ""columnas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50 }
        },
        ""required"": [""fila"", ""columna"", ""filas"", ""columnas""],
        ""additionalProperties"": false
      },
      ""description"": ""Rangos rectangulares nuevos a combinar.""
    },
    ""anchos_columnas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""columna"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 49 },
          ""ancho"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }
        },
        ""required"": [""columna"", ""ancho""],
        ""additionalProperties"": false
      }
    },
    ""altos_filas"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""fila"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 199 },
          ""alto"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }
        },
        ""required"": [""fila"", ""alto""],
        ""additionalProperties"": false
      }
    },
    ""celdas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2500, ""items"": " + EditCellSchema + @" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nuevo estilo de tabla existente."" },
    ""posicion"": " + PrimitiveSupport.PointSchema + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""aceptar_perdida_campos"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = permite sobreescribir celdas con campos FIELD o fórmulas."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = APLICAR la edición en un solo paso de deshacer. Sin él solo se previsualiza."" },
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""handle""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""handle"": { ""type"": ""string"" },
    ""espacio"": { ""type"": ""string"" },
    ""capa"": { ""type"": ""string"" },
    ""n_usuario"": { ""type"": ""integer"" },
    ""filas_antes"": { ""type"": ""integer"" },
    ""columnas_antes"": { ""type"": ""integer"" },
    ""filas_despues"": { ""type"": ""integer"" },
    ""columnas_despues"": { ""type"": ""integer"" },
    ""resumen_cambios"": { ""type"": ""object"" },
    ""bbox"": { ""type"": [""array"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""handle"", ""espacio"", ""capa"", ""n_usuario"", ""filas_antes"", ""columnas_antes"", ""filas_despues"", ""columnas_despues"", ""resumen_cambios"", ""origen_texto""]
}");

        private sealed class Params
        {
            public string Handle = "";
            public List<TableStructuralOp>? InsertRows;
            public List<TableStructuralOp>? DeleteRows;
            public List<TableStructuralOp>? InsertCols;
            public List<TableStructuralOp>? DeleteCols;
            public List<(int Fila, int Columna)> Unmerges = new List<(int, int)>();
            public List<TableMerge> Merges = new List<TableMerge>();
            public List<(int Columna, double Ancho)> ColWidths = new List<(int, double)>();
            public List<(int Fila, double Alto)> RowHeights = new List<(int, double)>();
            public List<TableCellSpec> Celdas = new List<TableCellSpec>();
            public string? Capa;
            public string? Estilo;
            public Point3? Posicion;
            public bool Forzar;
            public bool AceptarSinUnidades;
            public bool AceptarPerdidaCampos;
            public CleanWrite.Mode Mode;

            public bool HasStructuralOps =>
                InsertRows != null || DeleteRows != null || InsertCols != null || DeleteCols != null
                || Unmerges.Count > 0 || Merges.Count > 0;
        }

        private sealed class Plan
        {
            public string Espacio = "";
            public string CapaActual = "";
            public string CapaFinal = "";
            public int NUsuario;
            public int FilasAntes;
            public int ColsAntes;
            public int FilasDespues;
            public int ColsDespues;
            public List<TableStructuralOp> OrderedInsertRows = new List<TableStructuralOp>();
            public List<TableStructuralOp> OrderedDeleteRows = new List<TableStructuralOp>();
            public List<TableStructuralOp> OrderedInsertCols = new List<TableStructuralOp>();
            public List<TableStructuralOp> OrderedDeleteCols = new List<TableStructuralOp>();
            public string? EstiloFinal;
            public double DefaultRowHeight = 1.0;
            public double DefaultColWidth = 1.0;
            public BBox2? CurrentBBox;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string handle = (ParamReader.OptionalHandle(p, "handle")
                ?? throw ToolException.InvalidParams("handle: obligatorio")).ToUpperInvariant();

            List<TableStructuralOp>? insertRows = ParseInsertOps(p, "insertar_filas", InsertRowAllowed, "alto");
            List<TableStructuralOp>? deleteRows = ParseDeleteOps(p, "borrar_filas");
            if (insertRows != null && deleteRows != null)
            {
                throw ToolException.InvalidParams("insertar_filas y borrar_filas son excluyentes");
            }

            List<TableStructuralOp>? insertCols = ParseInsertOps(p, "insertar_columnas", InsertColAllowed, "ancho");
            List<TableStructuralOp>? deleteCols = ParseDeleteOps(p, "borrar_columnas");
            if (insertCols != null && deleteCols != null)
            {
                throw ToolException.InvalidParams("insertar_columnas y borrar_columnas son excluyentes");
            }

            var unmerges = ParseUnmerges(p);
            var merges = ParseMerges(p);
            var colWidths = ParseColWidths(p);
            var rowHeights = ParseRowHeights(p);
            var celdas = ParseCells(p);

            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, null);
            }

            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            Point3? posicion = ParamReader.OptionalPoint(p, "posicion");

            if (insertRows == null && deleteRows == null && insertCols == null && deleteCols == null
                && unmerges.Count == 0 && merges.Count == 0 && colWidths.Count == 0 && rowHeights.Count == 0
                && celdas.Count == 0 && capa == null && estilo == null && posicion == null)
            {
                throw ToolException.InvalidParams("editar_tabla: indique al menos una modificación sobre la tabla");
            }

            bool forzar = ParamReader.OptionalBool(p, "forzar") ?? false;
            bool aceptarSinUnidades = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            bool aceptarCampos = ParamReader.OptionalBool(p, "aceptar_perdida_campos") ?? false;
            CleanWrite.Common cw = CleanWrite.ReadCommon(p);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(cw.Confirmar, cw.Preview);

            return new Params
            {
                Handle = handle,
                InsertRows = insertRows,
                DeleteRows = deleteRows,
                InsertCols = insertCols,
                DeleteCols = deleteCols,
                Unmerges = unmerges,
                Merges = merges,
                ColWidths = colWidths,
                RowHeights = rowHeights,
                Celdas = celdas,
                Capa = capa,
                Estilo = estilo,
                Posicion = posicion,
                Forzar = forzar,
                AceptarSinUnidades = aceptarSinUnidades,
                AceptarPerdidaCampos = aceptarCampos,
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

            Plan plan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                plan = BuildAndValidatePlan(db, tr, p, warnings);
                tr.Commit();
            }

            if (p.Mode == CleanWrite.Mode.Preview)
            {
                int insRead = CleanWrite.ReadUnits(db);
                if (p.Posicion != null)
                {
                    var pw = new List<string>();
                    PlacementCheck.Run(db, plan.Espacio, new[] { PlacementCheck.PointBox(p.Posicion.Value) }, forzar: true, "entidades", pw);
                    if (pw.Count > 0)
                    {
                        warnings.Add("La nueva posición queda fuera de la extensión del espacio '" + plan.Espacio + "'; al confirmar necesitará forzar: true.");
                    }
                }

                return BuildResponse(pid, CleanWrite.Mode.Preview, insRead, warnings, p, plan, PrimitiveBoxArray(plan.CurrentBBox));
            }

            int ins = DrawWrite.CheckUnits(db, p.AceptarSinUnidades, warnings);
            if (p.Posicion != null)
            {
                PlacementCheck.Run(db, plan.Espacio, new[] { PlacementCheck.PointBox(p.Posicion.Value) }, p.Forzar, "entidades", warnings);
            }

            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        private static Plan BuildAndValidatePlan(Database db, Transaction tr, Params p, List<string> warnings)
        {
            // En preview (o write) las capas bloqueadas/xref/bloque se rechazan siempre; en preview no exige confirmar para previsualizar.
            HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, new[] { p.Handle }, confirmar: true, warnings);
            Entity e = res.Items[0].Entity;
            Table table = TableSupport.RequireTable(e, p.Handle);

            bool hasDataLink = false;
            try
            {
                hasDataLink = table.GetDataLink().Count > 0;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                hasDataLink = false;
            }

            if (hasDataLink && p.HasStructuralOps)
            {
                throw ToolException.InvalidParams(
                    "la tabla " + p.Handle + " tiene un vínculo DataLink activo y no admite cambios estructurales (filas, columnas o combinaciones)");
            }

            int rows = table.Rows.Count;
            int cols = table.Columns.Count;

            List<TableStructuralOp> insRows = new List<TableStructuralOp>();
            List<TableStructuralOp> delRows = new List<TableStructuralOp>();
            int newRows = rows;
            if (p.InsertRows != null)
            {
                try
                {
                    (insRows, newRows) = TableRules.PlanInsert(rows, cols, TableRules.MaxRows, true, p.InsertRows);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }
            }
            else if (p.DeleteRows != null)
            {
                try
                {
                    (delRows, newRows) = TableRules.PlanDelete(rows, true, p.DeleteRows);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }
            }

            List<TableStructuralOp> insCols = new List<TableStructuralOp>();
            List<TableStructuralOp> delCols = new List<TableStructuralOp>();
            int newCols = cols;
            if (p.InsertCols != null)
            {
                try
                {
                    (insCols, newCols) = TableRules.PlanInsert(cols, newRows, TableRules.MaxColumns, false, p.InsertCols);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }
            }
            else if (p.DeleteCols != null)
            {
                try
                {
                    (delCols, newCols) = TableRules.PlanDelete(cols, false, p.DeleteCols);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }
            }

            string? finalDimErr = TableRules.ValidateDimensions(newRows, newCols);
            if (finalDimErr != null)
            {
                throw ToolException.InvalidParams(finalDimErr);
            }

            for (int i = 0; i < p.Unmerges.Count; i++)
            {
                (int r, int c) = p.Unmerges[i];
                if (r < 0 || r >= newRows || c < 0 || c >= newCols)
                {
                    throw ToolException.InvalidParams(string.Format(
                        CultureInfo.InvariantCulture,
                        "separar_celdas[{0}]: celda ({1}, {2}) fuera de las dimensiones resultantes ({3}×{4})",
                        i, r, c, newRows, newCols));
                }
            }

            var seenColW = new HashSet<int>();
            for (int i = 0; i < p.ColWidths.Count; i++)
            {
                int col = p.ColWidths[i].Columna;
                if (col < 0 || col >= newCols)
                {
                    throw ToolException.InvalidParams(string.Format(
                        CultureInfo.InvariantCulture,
                        "anchos_columnas[{0}].columna: fuera de rango [0, {1}]",
                        i, newCols - 1));
                }

                if (!seenColW.Add(col))
                {
                    throw ToolException.InvalidParams(string.Format(
                        CultureInfo.InvariantCulture,
                        "anchos_columnas[{0}].columna: columna {1} repetida",
                        i, col));
                }
            }

            var seenRowH = new HashSet<int>();
            for (int i = 0; i < p.RowHeights.Count; i++)
            {
                int row = p.RowHeights[i].Fila;
                if (row < 0 || row >= newRows)
                {
                    throw ToolException.InvalidParams(string.Format(
                        CultureInfo.InvariantCulture,
                        "altos_filas[{0}].fila: fuera de rango [0, {1}]",
                        i, newRows - 1));
                }

                if (!seenRowH.Add(row))
                {
                    throw ToolException.InvalidParams(string.Format(
                        CultureInfo.InvariantCulture,
                        "altos_filas[{0}].fila: fila {1} repetida",
                        i, row));
                }
            }

            try
            {
                TableRules.ValidateCellsAndMerges(newRows, newCols, p.Celdas, p.Merges);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            // Si no hay cambio estructural previo en filas/columnas, validamos también celdas existentes (DataLink/FIELD/merges actuales).
            bool resized = insRows.Count > 0 || delRows.Count > 0 || insCols.Count > 0 || delCols.Count > 0;
            if (!resized && p.Unmerges.Count == 0)
            {
                var unmergedLookup = new HashSet<(int, int)>();
                foreach (TableMerge existing in TableSupport.CollectMerges(table))
                {
                    for (int i = 0; i < p.Celdas.Count; i++)
                    {
                        TableCellSpec cs = p.Celdas[i];
                        if (existing.Contains(cs.Fila, cs.Columna) && !existing.IsAnchor(cs.Fila, cs.Columna))
                        {
                            throw ToolException.InvalidParams(string.Format(
                                CultureInfo.InvariantCulture,
                                "celdas[{0}]: la celda ({1}, {2}) pertenece a una combinación existente ({3}, {4}, {5}×{6}); separe antes o edite ({3}, {4})",
                                i, cs.Fila, cs.Columna, existing.Fila, existing.Columna, existing.Filas, existing.Columnas));
                        }
                    }
                }
            }

            if (!resized)
            {
                for (int i = 0; i < p.Celdas.Count; i++)
                {
                    TableCellSpec cs = p.Celdas[i];
                    bool changingText = cs.Texto != null || cs.TextoFormato != null;
                    bool changingFmt = cs.Alineacion != null || cs.AlturaTexto.HasValue || cs.EstiloTexto != null
                        || cs.ColorTexto.HasValue || cs.ColorFondo.HasValue;
                    TableSupport.CheckCellEditable(
                        table, cs.Fila, cs.Columna, changingText, changingFmt, p.AceptarPerdidaCampos,
                        string.Format(CultureInfo.InvariantCulture, "celdas[{0}]", i));
                }
            }

            var checkedTextStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TableCellSpec cs in p.Celdas)
            {
                if (cs.EstiloTexto != null && checkedTextStyles.Add(cs.EstiloTexto))
                {
                    PrimitiveSupport.ResolveTextStyle(db, tr, cs.EstiloTexto);
                }
            }

            string? estiloFinal = table.TableStyleName;
            if (p.Estilo != null)
            {
                estiloFinal = TableSupport.ResolveTableStyle(db, tr, p.Estilo).Name;
            }

            string capaFinal = e.Layer;
            if (p.Capa != null)
            {
                LayerPlan lp = DrawLayerFactory.Check(db, tr, p.Capa, null, out bool exists, warnings);
                capaFinal = lp.Nombre;
                if (exists)
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    ObjectId lid = McpLayerFactory.FindLayer(lt, capaFinal);
                    if (!lid.IsNull)
                    {
                        capaFinal = ((LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead)).Name;
                    }
                }
            }

            return new Plan
            {
                Espacio = res.Espacio,
                CapaActual = e.Layer,
                CapaFinal = capaFinal,
                NUsuario = res.NUsuario,
                FilasAntes = rows,
                ColsAntes = cols,
                FilasDespues = newRows,
                ColsDespues = newCols,
                OrderedInsertRows = insRows,
                OrderedDeleteRows = delRows,
                OrderedInsertCols = insCols,
                OrderedDeleteCols = delCols,
                EstiloFinal = estiloFinal,
                DefaultRowHeight = rows > 0 && double.IsFinite(table.Rows[0].Height) && table.Rows[0].Height > 0 ? table.Rows[0].Height : 1.0,
                DefaultColWidth = cols > 0 && double.IsFinite(table.Columns[0].Width) && table.Columns[0].Width > 0 ? table.Columns[0].Width : 1.0,
                CurrentBBox = AcadUtil.TryBBox2(table),
            };
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Plan plan = BuildAndValidatePlan(db, tr, p, new List<string>());
                Entity e = AcadUtil.TryGetEntityByHandle(db, p.Handle, tr) ?? throw ToolException.EntityNotFound(p.Handle);
                Table table = TableSupport.RequireTable(e, p.Handle);

                var cellTextStyles = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (TableCellSpec cs in p.Celdas)
                {
                    if (cs.EstiloTexto != null && !cellTextStyles.ContainsKey(cs.EstiloTexto))
                    {
                        cellTextStyles[cs.EstiloTexto] = PrimitiveSupport.ResolveTextStyle(db, tr, cs.EstiloTexto).id;
                    }
                }

                ObjectId newTableStyleId = ObjectId.Null;
                if (p.Estilo != null)
                {
                    newTableStyleId = TableSupport.ResolveTableStyle(db, tr, p.Estilo).Id;
                }

                DrawLayerFactory.Result? newLayer = null;
                if (p.Capa != null)
                {
                    newLayer = DrawLayerFactory.Ensure(db, tr, p.Capa, null, warnings);
                }

                table.UpgradeOpen();
                table.SuppressRegenerateTable(true);
                try
                {
                    // 1. Filas (en orden descendente de índice)
                    foreach (TableStructuralOp op in plan.OrderedInsertRows)
                    {
                        if (op.HeredarDe.HasValue)
                        {
                            table.InsertRowsAndInherit(op.Indice, op.HeredarDe.Value, op.Cantidad);
                            if (op.Medida.HasValue)
                            {
                                for (int k = 0; k < op.Cantidad; k++)
                                {
                                    table.Rows[op.Indice + k].Height = op.Medida.Value;
                                }
                            }
                        }
                        else
                        {
                            table.InsertRows(op.Indice, op.Medida ?? plan.DefaultRowHeight, op.Cantidad);
                        }
                    }

                    foreach (TableStructuralOp op in plan.OrderedDeleteRows)
                    {
                        table.DeleteRows(op.Indice, op.Cantidad);
                    }

                    // 2. Columnas (en orden descendente de índice)
                    foreach (TableStructuralOp op in plan.OrderedInsertCols)
                    {
                        if (op.HeredarDe.HasValue)
                        {
                            table.InsertColumnsAndInherit(op.Indice, op.HeredarDe.Value, op.Cantidad);
                            if (op.Medida.HasValue)
                            {
                                for (int k = 0; k < op.Cantidad; k++)
                                {
                                    table.Columns[op.Indice + k].Width = op.Medida.Value;
                                }
                            }
                        }
                        else
                        {
                            table.InsertColumns(op.Indice, op.Medida ?? plan.DefaultColWidth, op.Cantidad);
                        }
                    }

                    foreach (TableStructuralOp op in plan.OrderedDeleteCols)
                    {
                        table.DeleteColumns(op.Indice, op.Cantidad);
                    }

                    // 3. Separar celdas combinadas
                    foreach ((int r, int c) in p.Unmerges)
                    {
                        Cell cell = table.Cells[r, c];
                        if (cell.IsMerged == true)
                        {
                            CellRange mr = cell.GetMergeRange();
                            if (mr != null && !mr.IsNull && !mr.IsSingleCell)
                            {
                                table.UnmergeCells(mr);
                            }
                        }
                    }

                    // 4. Combinar nuevos rangos
                    foreach (TableMerge m in p.Merges)
                    {
                        if (!m.IsTrivial)
                        {
                            table.MergeCells(CellRange.Create(table, m.Fila, m.Columna, m.BottomRow, m.RightColumn));
                        }
                    }

                    // 5. Anchos de columna y altos de fila
                    foreach ((int col, double ancho) in p.ColWidths)
                    {
                        table.Columns[col].Width = ancho;
                    }

                    foreach ((int row, double alto) in p.RowHeights)
                    {
                        table.Rows[row].Height = alto;
                    }

                    // 6. Celdas (protegiendo celdas secundarias de merges vigentes, DataLink y FIELD)
                    for (int i = 0; i < p.Celdas.Count; i++)
                    {
                        TableCellSpec cs = p.Celdas[i];
                        Cell cell = table.Cells[cs.Fila, cs.Columna];
                        if (cell.IsMerged == true)
                        {
                            CellRange mr = cell.GetMergeRange();
                            if (mr != null && !mr.IsNull && (mr.TopRow != cs.Fila || mr.LeftColumn != cs.Columna))
                            {
                                throw ToolException.InvalidParams(string.Format(
                                    CultureInfo.InvariantCulture,
                                    "celdas[{0}]: la celda ({1}, {2}) está cubierta por la combinación ({3}, {4}..{5}, {6})",
                                    i, cs.Fila, cs.Columna, mr.TopRow, mr.LeftColumn, mr.BottomRow, mr.RightColumn));
                            }
                        }

                        bool changingText = cs.Texto != null || cs.TextoFormato != null;
                        bool changingFmt = cs.Alineacion != null || cs.AlturaTexto.HasValue || cs.EstiloTexto != null
                            || cs.ColorTexto.HasValue || cs.ColorFondo.HasValue;
                        TableSupport.CheckCellEditable(
                            table, cs.Fila, cs.Columna, changingText, changingFmt, p.AceptarPerdidaCampos,
                            string.Format(CultureInfo.InvariantCulture, "celdas[{0}]", i));

                        ObjectId tsId = cs.EstiloTexto != null ? cellTextStyles[cs.EstiloTexto] : ObjectId.Null;
                        TableSupport.ApplyCellFormatting(cell, cs.Alineacion, cs.AlturaTexto, tsId, cs.ColorTexto, cs.ColorFondo);
                        TableSupport.ApplyCellContent(cell, cs.Texto, cs.TextoFormato);
                    }

                    // 7. Estilo, capa y posición
                    if (!newTableStyleId.IsNull)
                    {
                        table.TableStyle = newTableStyleId;
                    }

                    if (newLayer != null)
                    {
                        table.LayerId = newLayer.Id;
                    }

                    if (p.Posicion != null)
                    {
                        table.Position = AcadUtil.ToPoint3d(p.Posicion.Value);
                    }
                }
                finally
                {
                    table.SuppressRegenerateTable(false);
                }

                table.GenerateLayout();
                table.RecomputeTableBlock(true);
                double[][]? bbox = PrimitiveSupport.Box2D(table);
                plan.CapaFinal = table.Layer;
                plan.EstiloFinal = table.TableStyleName;
                tr.Commit();

                return BuildResponse(pid, CleanWrite.Mode.Write, ins, warnings, p, plan, bbox);
            }
        }

        private static Dictionary<string, object?> BuildResponse(
            int pid,
            CleanWrite.Mode mode,
            int ins,
            List<string> warnings,
            Params p,
            Plan plan,
            double[][]? bbox)
        {
            var resumen = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["filas_insertadas"] = SumCount(p.InsertRows),
                ["filas_borradas"] = SumCount(p.DeleteRows),
                ["columnas_insertadas"] = SumCount(p.InsertCols),
                ["columnas_borradas"] = SumCount(p.DeleteCols),
                ["celdas_separadas"] = p.Unmerges.Count,
                ["rangos_combinados"] = p.Merges.Count,
                ["anchos_modificados"] = p.ColWidths.Count,
                ["altos_modificados"] = p.RowHeights.Count,
                ["celdas_editadas"] = p.Celdas.Count,
                ["capa_cambiada"] = p.Capa != null && !string.Equals(plan.CapaActual, plan.CapaFinal, StringComparison.OrdinalIgnoreCase),
                ["estilo_cambiado"] = p.Estilo != null,
                ["posicion_cambiada"] = p.Posicion != null,
            };

            Dictionary<string, object?> res = CleanWrite.Response(pid, mode, ins, warnings);
            res["handle"] = p.Handle;
            res["espacio"] = plan.Espacio;
            res["capa"] = plan.CapaFinal;
            res["n_usuario"] = plan.NUsuario;
            res["filas_antes"] = plan.FilasAntes;
            res["columnas_antes"] = plan.ColsAntes;
            res["filas_despues"] = plan.FilasDespues;
            res["columnas_despues"] = plan.ColsDespues;
            res["resumen_cambios"] = resumen;
            res["bbox"] = bbox;
            res["origen_texto"] = "contenido_del_plano";
            return res;
        }

        private static int SumCount(List<TableStructuralOp>? ops)
        {
            if (ops == null)
            {
                return 0;
            }

            int total = 0;
            foreach (TableStructuralOp op in ops)
            {
                total += op.Cantidad;
            }

            return total;
        }

        private static double[][]? PrimitiveBoxArray(BBox2? b) => b?.ToArray2D();

        private static List<TableStructuralOp>? ParseInsertOps(JsonElement p, string propName, string[] allowed, string measureProp)
        {
            if (!p.TryGetProperty(propName, out JsonElement arr))
            {
                return null;
            }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > TableRules.MaxStructuralOps)
            {
                throw ToolException.InvalidParams(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: debe ser un array de 1 a {1} objetos",
                    propName, TableRules.MaxStructuralOps));
            }

            var list = new List<TableStructuralOp>(arr.GetArrayLength());
            int i = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = propName + "[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                if (el.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto");
                }

                ParamReader.RejectUnknown(el, allowed, prefix);
                int indice = TableSupport.OptionalRequiredInt(el, "indice", 0, TableRules.MaxRows, prefix);
                int cantidad = TableSupport.OptionalRequiredInt(el, "cantidad", 1, TableRules.MaxRows, prefix);
                double? medida = TableSupport.OptionalNumberPrefixed(el, measureProp, 0, double.PositiveInfinity, true, prefix);
                int? heredar = null;
                if (el.TryGetProperty("heredar_de", out _))
                {
                    heredar = TableSupport.OptionalRequiredInt(el, "heredar_de", 0, TableRules.MaxRows - 1, prefix);
                }

                list.Add(new TableStructuralOp(indice, cantidad, medida, heredar));
                i++;
            }

            return list;
        }

        private static List<TableStructuralOp>? ParseDeleteOps(JsonElement p, string propName)
        {
            if (!p.TryGetProperty(propName, out JsonElement arr))
            {
                return null;
            }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > TableRules.MaxStructuralOps)
            {
                throw ToolException.InvalidParams(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: debe ser un array de 1 a {1} objetos",
                    propName, TableRules.MaxStructuralOps));
            }

            var list = new List<TableStructuralOp>(arr.GetArrayLength());
            int i = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = propName + "[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                if (el.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto");
                }

                ParamReader.RejectUnknown(el, DeleteAllowed, prefix);
                int indice = TableSupport.OptionalRequiredInt(el, "indice", 0, TableRules.MaxRows - 1, prefix);
                int cantidad = TableSupport.OptionalRequiredInt(el, "cantidad", 1, TableRules.MaxRows, prefix);
                list.Add(new TableStructuralOp(indice, cantidad));
                i++;
            }

            return list;
        }

        private static List<(int Fila, int Columna)> ParseUnmerges(JsonElement p)
        {
            var list = new List<(int, int)>();
            if (!p.TryGetProperty("separar_celdas", out JsonElement arr))
            {
                return list;
            }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > 500)
            {
                throw ToolException.InvalidParams("separar_celdas: debe ser un array de 1 a 500 objetos");
            }

            int i = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = "separar_celdas[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                if (el.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {fila, columna}");
                }

                ParamReader.RejectUnknown(el, UnmergeAllowed, prefix);
                int fila = TableSupport.OptionalRequiredInt(el, "fila", 0, TableRules.MaxRows - 1, prefix);
                int col = TableSupport.OptionalRequiredInt(el, "columna", 0, TableRules.MaxColumns - 1, prefix);
                list.Add((fila, col));
                i++;
            }

            return list;
        }

        private static List<TableMerge> ParseMerges(JsonElement p)
        {
            var list = new List<TableMerge>();
            if (!p.TryGetProperty("combinar_celdas", out JsonElement arr))
            {
                return list;
            }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > 500)
            {
                throw ToolException.InvalidParams("combinar_celdas: debe ser un array de 1 a 500 objetos");
            }

            int i = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = "combinar_celdas[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                list.Add(TableSupport.ParseFullMerge(el, prefix));
                i++;
            }

            return list;
        }

        private static List<(int Columna, double Ancho)> ParseColWidths(JsonElement p)
        {
            var list = new List<(int, double)>();
            if (!p.TryGetProperty("anchos_columnas", out JsonElement arr))
            {
                return list;
            }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > TableRules.MaxColumns)
            {
                throw ToolException.InvalidParams("anchos_columnas: debe ser un array de 1 a 50 objetos");
            }

            int i = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = "anchos_columnas[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                if (el.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {columna, ancho}");
                }

                ParamReader.RejectUnknown(el, ColWidthAllowed, prefix);
                int col = TableSupport.OptionalRequiredInt(el, "columna", 0, TableRules.MaxColumns - 1, prefix);
                double ancho = TableSupport.RequiredNumberPrefixed(el, "ancho", 0, double.PositiveInfinity, true, prefix);
                list.Add((col, ancho));
                i++;
            }

            return list;
        }

        private static List<(int Fila, double Alto)> ParseRowHeights(JsonElement p)
        {
            var list = new List<(int, double)>();
            if (!p.TryGetProperty("altos_filas", out JsonElement arr))
            {
                return list;
            }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > TableRules.MaxRows)
            {
                throw ToolException.InvalidParams("altos_filas: debe ser un array de 1 a 200 objetos");
            }

            int i = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = "altos_filas[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                if (el.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {fila, alto}");
                }

                ParamReader.RejectUnknown(el, RowHeightAllowed, prefix);
                int fila = TableSupport.OptionalRequiredInt(el, "fila", 0, TableRules.MaxRows - 1, prefix);
                double alto = TableSupport.RequiredNumberPrefixed(el, "alto", 0, double.PositiveInfinity, true, prefix);
                list.Add((fila, alto));
                i++;
            }

            return list;
        }

        private static List<TableCellSpec> ParseCells(JsonElement p)
        {
            var list = new List<TableCellSpec>();
            if (!p.TryGetProperty("celdas", out JsonElement arr))
            {
                return list;
            }

            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > TableRules.MaxTotalCells)
            {
                throw ToolException.InvalidParams("celdas: debe ser un array de 1 a 2500 objetos");
            }

            int i = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = "celdas[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                list.Add(TableSupport.ParseCellSpec(el, prefix, allowCombinar: false, requireAnyChange: true));
                i++;
            }

            return list;
        }
    }
}
