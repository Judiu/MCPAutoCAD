using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Layouts;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// configurar_layout (plugin 0.9.0, fase M). Cambia la configuración de página de un layout (impresora, papel, estilo de
    /// impresión, rotación, escala de impresión, unidades de papel, centrado) con PlotSettingsValidator sobre una copia
    /// (PlotSettings) que solo se aplica al layout (Layout.CopyFrom) con confirmar: true, en un paso de deshacer. La
    /// previsualización hace exactamente las mismas validaciones sobre la copia y la descarta. Solo admite impresoras,
    /// papeles y estilos que existan en este PC: si no, INVALID_PARAMS con la lista de los disponibles.
    /// </summary>
    internal sealed class ConfigurarLayoutHandler : IToolHandler
    {
        private const int MaxListed = 60;

        private static readonly string[] Allowed =
        {
            "nombre", "impresora", "papel", "estilo_impresion", "rotacion", "escala_impresion", "unidades_papel", "centrar", "area_impresion", "desfase", "confirmar", "preview",
        };

        public string Name => "configurar_layout";

        public string Description =>
            "Cambia la configuración de página de un layout: impresora (.pc3 o dispositivo del sistema), papel (nombre canónico o " +
            "el que muestra AutoCAD), estilo de impresión (.ctb/.stb), rotación, área de impresión (presentacion, extension, limites), " +
            "escala de impresión ('1:1', 'metros' para espacio papel dibujado en metros, '1:N' o 'ajustar'), unidades de papel, " +
            "desfase [x, y] en mm y centrado. Solo acepta lo que exista en este PC; si no, devuelve la lista de opciones. Sin " +
            "confirmar: true solo previsualiza (antes/después); con confirmar: true lo aplica en un paso de deshacer.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        // Enumerar impresoras y papeles carga los controladores: puede tardar.
        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": " + LayoutWrite.SchemaLayout + @",
    ""impresora"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Dispositivo de impresión (p. ej. 'DWG To PDF.pc3'), sin distinguir mayúsculas. Al cambiarlo, si el papel actual no existe en el nuevo hay que indicar 'papel'."" },
    ""papel"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Tamaño de papel del dispositivo: nombre canónico (p. ej. 'ISO_full_bleed_A1_(841.00_x_594.00_MM)') o el nombre que muestra AutoCAD, sin distinguir mayúsculas. Los tamaños personalizados se crean en el .pc3, no aquí."" },
    ""estilo_impresion"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Tabla de estilos de impresión (.ctb o .stb) instalada, p. ej. 'monochrome.ctb'."" },
    ""rotacion"": { ""type"": ""integer"", ""enum"": [0, 90, 180, 270], ""description"": ""Rotación de la impresión en grados."" },
    ""escala_impresion"": { ""type"": ""string"", ""minLength"": 3, ""maxLength"": 20, ""description"": ""'1:1' (espacio papel dibujado en mm: 1 mm = 1 unidad), 'metros' (espacio papel dibujado en metros, como las hojas LPS: 1 mm = 0,001 unidades; fija también unidades_papel mm), '1:N' (1 unidad de papel = N de dibujo) o 'ajustar' (ajustada al papel; la escala 1:N de los viewports deja de ser exacta y calculable)."" },
    ""unidades_papel"": { ""type"": ""string"", ""enum"": [""mm"", ""pulgadas""] },
    ""centrar"": { ""type"": ""boolean"", ""description"": ""Centrar la impresión en el papel. AutoCAD no lo admite con area_impresion 'presentacion' (error): ahí use desfase."" },
    ""area_impresion"": { ""type"": ""string"", ""enum"": [""presentacion"", ""extension"", ""limites""], ""description"": ""Qué se imprime: 'presentacion' (la hoja del layout: lo necesario para que la escala de los viewports sea exacta), 'extension' o 'limites'. 'ventana' no se fija aquí (necesita la ventana)."" },
    ""desfase"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""Desfase del trazado [x, y] en mm desde el origen del área imprimible (|x|, |y| ≤ 2000). Con un marco más pequeño que el papel, centrarlo = ((papel − marco)/2 − margen). Incompatible con centrar: true."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""nombre"": { ""type"": ""string"" },
    ""antes"": { ""type"": ""object"", ""description"": ""impresora, papel, tamano_papel_mm, estilo_impresion, rotacion, escala_impresion, unidades_papel, centrado, area_impresion, desfase_mm, margenes_mm [izq, inf, der, sup]."" },
    ""despues"": { ""type"": ""object"", ""description"": ""Los mismos campos con la configuración nueva."" },
    ""papel_mostrado"": { ""type"": [""string"", ""null""], ""description"": ""Nombre del papel tal como lo muestra AutoCAD (localizado)."" },
    ""cambia"": { ""type"": ""boolean"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""nombre"", ""antes"", ""despues"", ""papel_mostrado"", ""cambia""]
}");

        private sealed class Params
        {
            public string Nombre = "";
            public string? Impresora;
            public string? Papel;
            public string? Estilo;
            public int? Rotacion;
            public string? Escala;
            public double? EscalaN;
            public string? Unidades;
            public bool? Centrar;
            public string? Area;
            public double[]? Desfase;
            public bool Metros;
            public CleanWrite.Common Common = new CleanWrite.Common(false, null);
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var r = new Params
            {
                Nombre = LayoutWrite.ReadLayoutName(p, "nombre"),
                Impresora = ParamReader.OptionalString(p, "impresora", 1, 255),
                Papel = ParamReader.OptionalString(p, "papel", 1, 255),
                Estilo = ParamReader.OptionalString(p, "estilo_impresion", 1, 255),
                Rotacion = ParamReader.OptionalInt(p, "rotacion", 0, 270),
                Escala = ParamReader.OptionalString(p, "escala_impresion", 3, 20),
                Unidades = ParamReader.OptionalEnum(p, "unidades_papel", new[] { "mm", "pulgadas" }),
                Centrar = ParamReader.OptionalBool(p, "centrar"),
                Area = ParamReader.OptionalEnum(p, "area_impresion", new[] { "presentacion", "extension", "limites" }),
                Common = CleanWrite.ReadCommon(p),
            };
            if (r.Rotacion is int rot && rot % 90 != 0)
            {
                throw ToolException.InvalidParams("rotacion: debe ser 0, 90, 180 o 270");
            }

            if (r.Escala != null && string.Equals(r.Escala.Trim(), "metros", StringComparison.OrdinalIgnoreCase))
            {
                r.Metros = true;
                r.EscalaN = 0.001;
                if (r.Unidades == "pulgadas")
                {
                    throw ToolException.InvalidParams("escala_impresion: 'metros' exige unidades_papel mm");
                }
            }
            else if (r.Escala != null && !string.Equals(r.Escala.Trim(), "ajustar", StringComparison.OrdinalIgnoreCase))
            {
                if (!LayoutRules.TryParseScale(r.Escala, out double n))
                {
                    throw ToolException.InvalidParams("escala_impresion: debe ser '1:1', 'metros', '1:N' (0 < N ≤ 100000) o 'ajustar'");
                }

                r.EscalaN = n;
            }

            if (p.TryGetProperty("desfase", out JsonElement des))
            {
                if (des.ValueKind != JsonValueKind.Array || des.GetArrayLength() != 2)
                {
                    throw ToolException.InvalidParams("desfase: debe ser [x, y] en mm");
                }

                var v = new double[2];
                int i = 0;
                foreach (JsonElement item in des.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double x) || !double.IsFinite(x) || Math.Abs(x) > 2000)
                    {
                        throw ToolException.InvalidParams("desfase: cada valor debe ser un número finito con |valor| ≤ 2000 mm");
                    }

                    v[i++] = x;
                }

                r.Desfase = v;
                if (r.Centrar == true)
                {
                    throw ToolException.InvalidParams("desfase: no se puede combinar con centrar: true");
                }
            }

            if (r.Impresora == null && r.Papel == null && r.Estilo == null && r.Rotacion == null && r.Escala == null
                && r.Unidades == null && r.Centrar == null && r.Area == null && r.Desfase == null)
            {
                throw ToolException.InvalidParams("indique al menos uno de impresora, papel, estilo_impresion, rotacion, escala_impresion, unidades_papel, centrar, area_impresion o desfase");
            }

            return r;
        }

        private static List<string> ToList(StringCollection? items)
        {
            var list = new List<string>();
            if (items != null)
            {
                foreach (string? s in items)
                {
                    if (!string.IsNullOrEmpty(s))
                    {
                        list.Add(s);
                    }
                }
            }

            return list;
        }

        private static string Options(List<string> items) =>
            items.Count == 0 ? "ninguna disponible"
            : items.Count <= MaxListed ? string.Join(" | ", items)
            : string.Join(" | ", items.GetRange(0, MaxListed)) + " | … (" + (items.Count - MaxListed) + " más)";

        private static string? Find(List<string> items, string wanted)
        {
            foreach (string s in items)
            {
                if (string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>
        /// Aplica los parámetros a <paramref name="ps"/> (copia del layout) con el validador. Devuelve el nombre localizado
        /// del papel resultante. Errores de valores inexistentes → INVALID_PARAMS con la lista de opciones.
        /// </summary>
        private static string? Apply(PlotSettings ps, Params p, List<string> warnings)
        {
            PlotSettingsValidator psv = PlotSettingsValidator.Current;
            string currentDevice = ps.PlotConfigurationName;
            string originalMedia = ps.CanonicalMediaName;
            bool changeDevice = p.Impresora != null && !string.Equals(p.Impresora, currentDevice, StringComparison.OrdinalIgnoreCase);
            if (changeDevice || p.Papel != null)
            {
                // Solo se toca el dispositivo si se pide cambiar impresora o papel: SetPlotConfigurationName puede
                // normalizar otros campos y no debe ejecutarse para pedir solo, por ejemplo, la rotación.
                List<string> devices = ToList(psv.GetPlotDeviceList());
                string wantedDevice = p.Impresora ?? currentDevice;
                string device = Find(devices, wantedDevice)
                    ?? throw ToolException.InvalidParams(
                        (p.Impresora != null ? "impresora: '" + p.Impresora + "' no está instalada en este PC" : "impresora: la impresora actual '" + currentDevice + "' no está instalada en este PC; indique impresora")
                        + ". Disponibles: " + Options(devices));
                if (string.Equals(device, "None", StringComparison.OrdinalIgnoreCase))
                {
                    psv.SetPlotConfigurationName(ps, device, null);
                }
                else
                {
                    psv.SetPlotConfigurationName(ps, device, null);
                    psv.RefreshLists(ps);
                    List<string> canonical = ToList(psv.GetCanonicalMediaNameList(ps));
                    // Sin 'papel' se busca el papel ORIGINAL (no el que el validador haya puesto al cambiar de dispositivo).
                    string wantedMedia = p.Papel ?? originalMedia;
                    string? media = string.IsNullOrEmpty(wantedMedia) ? null : Find(canonical, wantedMedia);
                    if (media == null && !string.IsNullOrEmpty(wantedMedia))
                    {
                        foreach (string c in canonical)
                        {
                            string local;
                            try
                            {
                                local = psv.GetLocaleMediaName(ps, c);
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                continue;
                            }

                            if (string.Equals(local, wantedMedia, StringComparison.OrdinalIgnoreCase))
                            {
                                media = c;
                                break;
                            }
                        }
                    }

                    if (media == null && p.Papel == null && string.IsNullOrEmpty(originalMedia))
                    {
                        // El layout no tenía papel: se acepta el que haya puesto el validador para el dispositivo nuevo.
                        media = ps.CanonicalMediaName;
                    }

                    if (string.IsNullOrEmpty(media))
                    {
                        string que = p.Papel != null ? "papel: '" + p.Papel + "' no existe" : "papel: el papel actual '" + originalMedia + "' no existe";
                        throw ToolException.InvalidParams(que + " en '" + device + "'; indique uno de: " + Options(canonical));
                    }

                    psv.SetPlotConfigurationName(ps, device, media);
                }
            }

            try
            {
                psv.RefreshLists(ps);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Dispositivo actual no instalado: las demás propiedades se fijan igual.
            }

            if (p.Estilo != null)
            {
                List<string> styles = ToList(psv.GetPlotStyleSheetList());
                string style = Find(styles, p.Estilo)
                    ?? throw ToolException.InvalidParams("estilo_impresion: '" + p.Estilo + "' no está instalado. Disponibles: " + Options(styles));
                psv.SetCurrentStyleSheet(ps, style);
            }

            if (p.Area != null)
            {
                PlotType newType = p.Area switch
                {
                    "extension" => PlotType.Extents,
                    "limites" => PlotType.Limits,
                    _ => PlotType.Layout,
                };
                // Con Layout ya no se podrá descentrar (SetPlotCentered da eInvalidInput): se hace antes de cambiar el tipo.
                if (newType == PlotType.Layout && ps.PlotCentered && ps.PlotType != PlotType.Layout)
                {
                    psv.SetPlotCentered(ps, false);
                }

                psv.SetPlotType(ps, newType);
            }

            if (p.Metros)
            {
                psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
            }

            if (p.Unidades != null)
            {
                psv.SetPlotPaperUnits(ps, p.Unidades == "mm" ? PlotPaperUnit.Millimeters : PlotPaperUnit.Inches);
            }

            if (p.Rotacion is int rot)
            {
                psv.SetPlotRotation(ps, rot switch
                {
                    90 => PlotRotation.Degrees090,
                    180 => PlotRotation.Degrees180,
                    270 => PlotRotation.Degrees270,
                    _ => PlotRotation.Degrees000,
                });
            }

            if (p.Escala != null)
            {
                if (p.EscalaN == null && ps.PlotType == PlotType.Layout)
                {
                    throw ToolException.InvalidParams("escala_impresion: 'ajustar' no se admite con el área 'presentacion'; use area_impresion extension o limites, o una escala fija");
                }

                if (p.EscalaN == null)
                {
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    warnings.Add("Escala de impresión 'ajustada al papel': la escala 1:N de los viewports de este layout deja de ser calculable (listar_layouts la dará como null).");
                }
                else if (Math.Abs(p.EscalaN.Value - 1) < 1e-12)
                {
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.StdScale1To1);
                }
                else
                {
                    psv.SetUseStandardScale(ps, false);
                    psv.SetCustomPrintScale(ps, new CustomScale(1, p.EscalaN.Value));
                    if (p.Metros)
                    {
                        warnings.Add("Escala 'metros': el espacio papel de este layout debe estar dibujado en metros (hoja de 105 × 72 cm ≈ 1,05 × 0,72 unidades); si está dibujado en mm use '1:1' o saldrá ampliado ×1000.");
                    }
                    else
                    {
                        warnings.Add("Escala de impresión distinta de 1:1: la escala real de cada viewport depende también de ella (crear_viewport y listar_layouts ya la tienen en cuenta).");
                    }
                }
            }

            if (p.Desfase != null)
            {
                // AutoCAD rechaza SetPlotCentered (incluso false) con PlotType.Layout (eInvalidInput) y lo ignora al
                // trazar ese tipo; solo se descentra en los demás.
                if (ps.PlotCentered && ps.PlotType != PlotType.Layout)
                {
                    psv.SetPlotCentered(ps, false);
                }

                psv.SetPlotOrigin(ps, new Autodesk.AutoCAD.Geometry.Point2d(p.Desfase[0], p.Desfase[1]));
                if (ps.PlotCentered)
                {
                    warnings.Add(CentradoHeredado);
                }
                else if (Math.Abs(ps.PlotOrigin.X - p.Desfase[0]) > 1e-6 || Math.Abs(ps.PlotOrigin.Y - p.Desfase[1]) > 1e-6)
                {
                    warnings.Add("desfase: AutoCAD guardó [" + ps.PlotOrigin.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", "
                        + ps.PlotOrigin.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "] mm en vez de lo pedido (posible conversión de unidades de papel); revise la vista previa.");
                }
            }

            if (ps.PlotType == PlotType.Window || (ps.UseStandardScale && ps.StdScaleType == StdScaleType.ScaleToFit))
            {
                warnings.Add("La página queda con área 'ventana' o escala 'ajustada al papel': la impresión no sale a la escala exacta de los viewports. Para escala exacta: area_impresion 'presentacion' y escala_impresion '1:1' o 'metros'.");
            }

            if (p.Centrar is bool c2)
            {
                if (ps.PlotType == PlotType.Layout)
                {
                    // AutoCAD rechaza SetPlotCentered con PlotType.Layout (eInvalidInput).
                    if (c2)
                    {
                        throw ToolException.InvalidParams("centrar: AutoCAD no admite centrar con el área de impresión 'presentacion'; use desfase [x, y] en mm o area_impresion extension/limites");
                    }

                    if (ps.PlotCentered && p.Desfase == null)
                    {
                        warnings.Add(CentradoHeredado);
                    }
                }
                else
                {
                    psv.SetPlotCentered(ps, c2);
                }
            }

            try
            {
                return string.IsNullOrEmpty(ps.CanonicalMediaName) ? null : psv.GetLocaleMediaName(ps, ps.CanonicalMediaName);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
        }

        private const string CentradoHeredado =
            "La página tiene 'centrado' activo aunque el área sea 'presentacion' (AutoCAD lo ignora al trazar y no deja quitarlo con ese área). " +
            "Para limpiarlo: area_impresion 'extension' + centrar: false y después volver a 'presentacion'.";

        private static bool Same(Dictionary<string, object?> a, Dictionary<string, object?> b) =>
            JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

        /// <summary>Plan: estado antes/después sobre una copia descartable. Lectura pura del dibujo.</summary>
        private static (string nombre, Dictionary<string, object?> antes, Dictionary<string, object?> despues, string? local) Plan(
            Database db, Transaction tr, Params p, List<string> warnings, Action<Layout, PlotSettings>? write)
        {
            LayoutWrite.Found found = LayoutWrite.FindLayout(db, tr, p.Nombre);
            Dictionary<string, object?> antes = LayoutWrite.DescribePage(found.Layout);
            using (var ps = new PlotSettings(false))
            {
                ps.CopyFrom(found.Layout);
                string? local;
                try
                {
                    local = Apply(ps, p, warnings);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    throw ToolException.InvalidParams(
                        "AutoCAD rechazó la configuración (" + ex.ErrorStatus + "): compruebe que la impresora del layout está instalada en este PC (indique 'impresora') y que el valor pedido aplica a ese dispositivo");
                }
                Dictionary<string, object?> despues = LayoutWrite.DescribePage(ps);
                write?.Invoke(found.Layout, ps);
                return (found.Name, antes, despues, local);
            }
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var baseWarnings = new List<string>(AcadUtil.UnitWarnings(db));

            Dictionary<string, object?> Build(CleanWrite.Mode m, List<string> warnings, string nombre, Dictionary<string, object?> antes, Dictionary<string, object?> despues, string? local, bool cambia, bool escrito)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["escrito"] = escrito;
                r["nombre"] = nombre;
                r["antes"] = antes;
                r["despues"] = despues;
                r["papel_mostrado"] = local;
                r["cambia"] = cambia;
                return r;
            }

            var preWarnings = new List<string>(baseWarnings);
            (string nombre, Dictionary<string, object?> antes, Dictionary<string, object?> despues, string? local) pre;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                pre = Plan(db, tr, p, preWarnings, null);
                tr.Commit();
            }

            bool cambia = !Same(pre.antes, pre.despues);
            if (mode == CleanWrite.Mode.Preview || !cambia)
            {
                if (!cambia)
                {
                    preWarnings.Add("La configuración pedida ya es la del layout; no se escribe nada.");
                }

                return Build(mode, preWarnings, pre.nombre, pre.antes, pre.despues, pre.local, cambia, false);
            }

            return UndoGroup.Run(doc, d =>
            {
                var warnings = new List<string>(baseWarnings);
                using (Transaction tr = d.Database.TransactionManager.StartTransaction())
                {
                    var plan = Plan(d.Database, tr, p, warnings, (layout, ps) =>
                    {
                        layout.UpgradeOpen();
                        layout.CopyFrom(ps);
                    });
                    tr.Commit();
                    return Build(CleanWrite.Mode.Write, warnings, plan.nombre, plan.antes, plan.despues, plan.local, true, true);
                }
            });
        }
    }
}
