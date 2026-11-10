using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Layouts;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// Piezas comunes de las escrituras de layouts y viewports (plugin 0.9.0, fase M2). Todas siguen el modo de
    /// <see cref="Clean.CleanWrite"/>: sin confirmar: true solo previsualizan (sin estado, sin paso de deshacer); con
    /// confirmar: true escriben en UNA Transaction dentro de <see cref="UndoGroup.Run"/> (un CTRL+Z). Nunca tocan
    /// entidades del espacio modelo ni cambian la pestaña activa.
    /// </summary>
    internal static class LayoutWrite
    {
        public const string SchemaLayout =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un layout de papel existente (sin distinguir mayúsculas). 'Model' no es un layout de papel."" }";

        public const string SchemaNuevoNombre =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre nuevo: sin espacios al inicio o al final, sin < > / \\ \"" : ; ? * | , = ` ni caracteres de control, distinto de 'Model' y de los demás layouts."" }";

        public const string SchemaEscala =
            @"{ ""type"": ""string"", ""minLength"": 3, ""maxLength"": 20, ""description"": ""Escala '1:N' o '1/N' (N > 0, admite decimales con punto), p. ej. '1:50'. Exige unidades de modelo conocidas (INSUNITS o unidades_modelo) y un layout que no imprima ajustado al papel."" }";

        public const string SchemaUnidadesModelo =
            @"{ ""type"": ""string"", ""enum"": [""m"", ""cm"", ""mm""], ""description"": ""Unidades del espacio modelo cuando el dibujo no las declara (INSUNITS = 0). Si el dibujo sí las declara y no coinciden → INVALID_PARAMS."" }";

        public const string SchemaVentanaModelo =
            @"{ ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }, ""description"": ""Zona del modelo a mostrar [[xmin, ymin], [xmax, ymax]] (WCS). El viewport mide exactamente esta zona a la escala pedida."" }";

        public const string SchemaCentroPapel =
            @"{ ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Centro del viewport en el papel [x, y] (unidades del espacio papel del layout, normalmente mm)."" }";

        /// <summary>
        /// Concepto de lps.json de la capa por defecto de los viewports (capas_preferidas.viewports = "Viewports", no
        /// imprimible). No es una capa MCP-*: los viewports son parte del plano entregable, no marcas del puente.
        /// </summary>
        public const string ViewportConcept = "viewports";

        /// <summary>Layout de papel encontrado: el objeto (abierto para lectura en la transacción) y su nombre real.</summary>
        internal sealed class Found
        {
            public Found(Layout layout, string name)
            {
                Layout = layout;
                Name = name;
            }

            public Layout Layout { get; }

            public string Name { get; }
        }

        /// <summary>Lee y valida un nombre de layout (sin AutoCAD).</summary>
        public static string ReadLayoutName(JsonElement p, string field)
        {
            string name = ParamReader.RequiredText(p, field, 1, LayoutRules.MaxNameLength);
            if (string.Equals(name, "Model", StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidParams(field + ": 'Model' es el espacio modelo, no un layout de papel");
            }

            return name;
        }

        /// <summary>Lee un nombre nuevo de layout y aplica <see cref="LayoutRules.NameProblem"/> (sin AutoCAD).</summary>
        public static string ReadNewName(JsonElement p, string field)
        {
            string name = ParamReader.RequiredText(p, field, 1, LayoutRules.MaxNameLength);
            string? problem = LayoutRules.NameProblem(name);
            if (problem != null)
            {
                throw ToolException.InvalidParams(field + ": " + problem);
            }

            return name;
        }

        /// <summary>Lee 'escala' (1:N) si está; devuelve el denominador o null.</summary>
        public static double? ReadScale(JsonElement p)
        {
            string? text = ParamReader.OptionalString(p, "escala", 3, 20);
            if (text == null)
            {
                return null;
            }

            if (!LayoutRules.TryParseScale(text, out double n))
            {
                throw ToolException.InvalidParams("escala: debe tener la forma '1:N' o '1/N' con 0 < N ≤ 100000 (p. ej. '1:50')");
            }

            return n;
        }

        /// <summary>Lee 'unidades_modelo' si está.</summary>
        public static string? ReadModelUnits(JsonElement p) => ParamReader.OptionalEnum(p, "unidades_modelo", ViewportPlanner.ModelUnitNames);

        /// <summary>Metros por unidad de modelo (INSUNITS o unidades_modelo) o INVALID_PARAMS con el motivo.</summary>
        public static double ModelMeters(Database db, string? unidadesModelo)
        {
            int ins = AcadUtil.InsUnits(db);
            double? fromIns = ins == 0 ? null : UnitsScale.MetersPerUnit(ins);
            double? m = ViewportPlanner.ResolveModelMeters(fromIns, unidadesModelo, out string? problem);
            if (m == null)
            {
                throw ToolException.InvalidParams(problem ?? "unidades_modelo: no se pueden determinar las unidades del modelo");
            }

            return m.Value;
        }

        /// <summary>mm reales de papel por unidad de espacio papel del layout, o INVALID_PARAMS si no se puede saber.</summary>
        public static double PaperMm(Layout layout)
        {
            (double? mm, string descripcion) = ListarLayoutsHandler.PlotScale(layout);
            if (mm == null)
            {
                throw ToolException.InvalidParams(
                    "escala: el layout '" + layout.LayoutName + "' imprime con escala '" + descripcion
                    + "' o en píxeles; configure en su configuración de página una escala fija (p. ej. 1:1, en mm) para poder calcular 1:N");
            }

            return mm.Value;
        }

        /// <summary>Layout de papel por nombre (sin mayúsculas) o NOT_FOUND.</summary>
        public static Found FindLayout(Database db, Transaction tr, string name)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (!string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase) || entry.Value.IsNull || entry.Value.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                {
                    return new Found(layout, layout.LayoutName);
                }
            }

            throw ToolException.LayoutNotFound(name, SpaceResolver.AvailableSpaces(db, tr, includeModel: false));
        }

        /// <summary>Layouts de papel en orden de pestaña: (nombre, id, orden).</summary>
        public static List<(string name, ObjectId id, int order)> PaperLayouts(Database db, Transaction tr)
        {
            var list = new List<(string name, ObjectId id, int order)>();
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (entry.Value.IsNull || entry.Value.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                {
                    list.Add((layout.LayoutName, entry.Value, layout.TabOrder));
                }
            }

            list.Sort((a, b) => a.order.CompareTo(b.order));
            return list;
        }

        /// <summary>Nombre del layout activo (CTAB), "Model" en el espacio modelo, o null si no se puede leer.</summary>
        public static string? CurrentTab()
        {
            try
            {
                return AcadUtil.GetStringSysVar("CTAB");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>true si <paramref name="name"/> es la pestaña activa.</summary>
        public static bool IsCurrent(string name, string? current) =>
            current != null && string.Equals(name, current, StringComparison.OrdinalIgnoreCase);

        /// <summary>Otro layout (distinto de <paramref name="except"/>) con el nombre <paramref name="name"/>, o null.</summary>
        public static string? Clash(Database db, Transaction tr, string name, string? except)
        {
            foreach ((string n, ObjectId _, int _) in PaperLayouts(db, tr))
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                    && (except == null || !string.Equals(n, except, StringComparison.OrdinalIgnoreCase)))
                {
                    return n;
                }
            }

            return null;
        }

        /// <summary>Entidades y viewports del espacio papel de un layout, sin contar el viewport general del papel.</summary>
        public static (int entidades, int viewports) Count(Transaction tr, Layout layout)
        {
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            ObjectId paper = Audit.LayoutViewports.PaperViewportId(tr, btr);
            int entities = 0, vps = 0;
            foreach (ObjectId id in btr)
            {
                if (id.IsErased || id == paper)
                {
                    continue;
                }

                entities++;
                if (id.ObjectClass.DxfName == "VIEWPORT")
                {
                    vps++;
                }
            }

            return (entities, vps);
        }

        /// <summary>
        /// Configuración de página de un layout o de unos PlotSettings: impresora, papel (nombre canónico), tamaño en mm
        /// (según la rotación), estilo de impresión, rotación, escala de impresión y unidades de papel.
        /// </summary>
        public static Dictionary<string, object?> DescribePage(PlotSettings ps)
        {
            Autodesk.AutoCAD.Geometry.Point2d size = ps.PlotPaperSize; // siempre en mm
            bool rotated = ps.PlotRotation == PlotRotation.Degrees090 || ps.PlotRotation == PlotRotation.Degrees270;
            (double? _, string escala) = ListarLayoutsHandler.PlotScale(ps);
            return new Dictionary<string, object?>
            {
                ["impresora"] = ps.PlotConfigurationName,
                ["papel"] = ps.CanonicalMediaName,
                ["tamano_papel_mm"] = new[] { AcadUtil.NumberOrNull(rotated ? size.Y : size.X), AcadUtil.NumberOrNull(rotated ? size.X : size.Y) },
                ["estilo_impresion"] = ps.CurrentStyleSheet,
                ["rotacion"] = ps.PlotRotation switch
                {
                    PlotRotation.Degrees090 => 90,
                    PlotRotation.Degrees180 => 180,
                    PlotRotation.Degrees270 => 270,
                    _ => 0,
                },
                ["escala_impresion"] = escala,
                ["unidades_papel"] = ps.PlotPaperUnits switch
                {
                    PlotPaperUnit.Millimeters => "mm",
                    PlotPaperUnit.Inches => "pulgadas",
                    _ => "pixeles",
                },
                ["centrado"] = ps.PlotCentered,
                ["area_impresion"] = ps.PlotType switch
                {
                    PlotType.Layout => "presentacion",
                    PlotType.Extents => "extension",
                    PlotType.Limits => "limites",
                    PlotType.Window => "ventana",
                    PlotType.View => "vista",
                    _ => "pantalla",
                },
                ["desfase_mm"] = new[] { AcadUtil.NumberOrNull(ps.PlotOrigin.X), AcadUtil.NumberOrNull(ps.PlotOrigin.Y) },
                ["margenes_mm"] = new[]
                {
                    AcadUtil.NumberOrNull(ps.PlotPaperMargins.MinPoint.X), AcadUtil.NumberOrNull(ps.PlotPaperMargins.MinPoint.Y),
                    AcadUtil.NumberOrNull(ps.PlotPaperMargins.MaxPoint.X), AcadUtil.NumberOrNull(ps.PlotPaperMargins.MaxPoint.Y),
                },
            };
        }

        /// <summary>Convierte ArgumentException de GeometryCore en INVALID_PARAMS.</summary>
        public static T Checked<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }
    }
}
