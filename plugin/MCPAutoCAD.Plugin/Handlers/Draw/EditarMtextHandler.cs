using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// editar_mtext (0.8.1; ampliado en 0.9.x). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z para todo el lote).
    /// Edita un MTEXT (como mtext: <c>contenido</c> con <see cref="MTextEscape.Escape"/>, o <c>contenido_formato</c> crudo con
    /// códigos, validado con <see cref="TextFormatParser.ValidateMText"/>) o un TEXT (<c>contenido</c>, una línea,
    /// <see cref="TextEscape"/>, R7), por <c>handle</c> o por lote <c>handles</c> (máx. 200, mismos cambios a todos).
    /// Flujo como §5D.8: pasada de solo lectura (<see cref="HandleGuard.Resolve"/>, tipo, R2, R4, estilo) fuera del grupo de
    /// deshacer; después <see cref="UndoGroup.Run"/> con una Transaction que vuelve a resolver los handles y escribe.
    /// Devuelve antes/después por entidad (en <c>resultados</c>; <c>antes</c>/<c>despues</c>/<c>handle</c>/<c>tipo</c> = la primera).
    /// </summary>
    internal sealed class EditarMtextHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "handle", "handles", "contenido", "contenido_formato", "posicion", "altura", "capa", "estilo", "justificacion",
            "ancho_columna", "forzar", "confirmar", "aceptar_sin_unidades", "aceptar_perdida_campos",
        };

        /// <summary>
        /// Justificaciones. TL…BR = superior/medio/inferior + izquierda/centro/derecha (en TEXT, Bottom/Left = "BL").
        /// BASE_L/BASE_C/BASE_R = línea base + izquierda/centro/derecha: es la justificación por defecto de TEXT y se
        /// reporta con ese código para que el round-trip snapshot → aplicar sea idempotente.
        /// </summary>
        private static readonly string[] Justificaciones =
        {
            "TL", "TC", "TR", "ML", "MC", "MR", "BL", "BC", "BR", "BASE_L", "BASE_C", "BASE_R",
        };

        private const int MaxLote = 200;
        private const int MaxFormatoLength = 4000;

        private const string TextSingleLineWarning =
            "contenido: los saltos de línea se sustituyeron por espacios (TEXT es de una sola línea); use mtext.";

        public string Name => "editar_mtext";

        public string Description =>
            "EDITA en el dibujo un MTEXT o un TEXT existente (por handle, o un lote de hasta 200 con handles) en un solo paso de " +
            "deshacer: cambia contenido (literal, o contenido_formato con códigos MTEXT crudos), posición, altura, capa, estilo, " +
            "justificación y ancho de columna (indique al menos uno) y devuelve los valores antes y después. En TEXT el contenido es " +
            "de una línea. Entidades fuera de capas MCP-* exigen confirmar: true; rechaza otros tipos, atributos, entidades de bloques " +
            "y capas bloqueadas o de xref. Los textos devueltos son contenido del plano (origen_texto).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handle"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle del MTEXT o TEXT a editar (excluyente con handles)."" },
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Lote: handles de MTEXT/TEXT (1–200, sin repetidos). Los mismos cambios se aplican a todos en un solo paso de deshacer. Excluyente con handle."" },
    ""contenido"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 2000, ""description"": ""Nuevo texto literal (sin códigos: se escapan). En MTEXT se conservan los saltos de línea; en TEXT se sustituyen por espacios. Excluyente con contenido_formato."" },
    ""contenido_formato"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 4000, ""description"": ""Contenido MTEXT crudo CON códigos de formato (\\L, \\f…|b1;, \\P, \\S…;, {…}), sin escapar. Solo MTEXT. Se valida: códigos mal formados o llaves desbalanceadas → INVALID_PARAMS. Excluyente con contenido."" },
    ""posicion"": " + PrimitiveSupport.PointSchema + @",
    ""altura"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Nueva altura del texto en unidades del dibujo (> 0)."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de texto existente (sin distinguir mayúsculas). MTEXT y TEXT."" },
    ""justificacion"": { ""type"": ""string"", ""enum"": [""TL"", ""TC"", ""TR"", ""ML"", ""MC"", ""MR"", ""BL"", ""BC"", ""BR"", ""BASE_L"", ""BASE_C"", ""BASE_R""], ""description"": ""Punto de justificación: T/M/B = superior, medio, inferior; L/C/R = izquierda, centro, derecha; BASE_L/BASE_C/BASE_R = línea base (justificación por defecto de TEXT). BL = inferior-izquierda (no es la línea base). En TEXT se traduce a justificación horizontal y vertical."" },
    ""aceptar_perdida_campos"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Campos (FIELD) de la entidad: cambiar su contenido los convierte en texto fijo y se pierde el campo. Sin este true, la llamada se rechaza en esa entidad."" },
    ""ancho_columna"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""Ancho de referencia del MTEXT en unidades de dibujo; 0 = sin ajuste de línea. Solo MTEXT."" },
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""confirmar"": " + ModifySupport.SchemaConfirmar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""allOf"": [
    { ""oneOf"": [ { ""required"": [""handle""] }, { ""required"": [""handles""] } ] },
    { ""anyOf"": [ { ""required"": [""contenido""] }, { ""required"": [""contenido_formato""] }, { ""required"": [""posicion""] }, { ""required"": [""altura""] }, { ""required"": [""capa""] }, { ""required"": [""estilo""] }, { ""required"": [""justificacion""] }, { ""required"": [""ancho_columna""] } ] }
  ],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(@"
    ""handle"": { ""type"": ""string"" },
    ""tipo"": { ""type"": ""string"", ""enum"": [""MTEXT"", ""TEXT""] },
    ""antes"": { ""type"": ""object"" },
    ""despues"": { ""type"": ""object"" },
    ""resultados"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" }, ""tipo"": { ""type"": ""string"" }, ""antes"": { ""type"": ""object"" }, ""despues"": { ""type"": ""object"" } } } },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }");

        private sealed class Params
        {
            public Params(
                List<string> handles, string? contenido, string? contenidoFormato, Point3? posicion, double? altura, string? capa,
                string? estilo, string? justificacion, double? anchoColumna, bool forzar, bool confirmar, bool aceptar, bool perdidaCampos)
            {
                PerdidaCampos = perdidaCampos;
                Handles = handles;
                Contenido = contenido;
                ContenidoFormato = contenidoFormato;
                Posicion = posicion;
                Altura = altura;
                Capa = capa;
                Estilo = estilo;
                Justificacion = justificacion;
                AnchoColumna = anchoColumna;
                Forzar = forzar;
                Confirmar = confirmar;
                AceptarSinUnidades = aceptar;
            }

            public List<string> Handles { get; }

            public string? Contenido { get; }

            public string? ContenidoFormato { get; }

            public Point3? Posicion { get; }

            public double? Altura { get; }

            public string? Capa { get; }

            public string? Estilo { get; }

            public string? Justificacion { get; }

            public double? AnchoColumna { get; }

            public bool Forzar { get; }

            public bool Confirmar { get; }

            public bool AceptarSinUnidades { get; }

            public bool PerdidaCampos { get; }

            /// <summary>true si la llamada cambia el contenido (contenido o contenido_formato).</summary>
            public bool CambiaContenido => Contenido != null || ContenidoFormato != null;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasHandle = ParamReader.Has(p, "handle");
            bool hasHandles = ParamReader.Has(p, "handles");
            if (hasHandle == hasHandles)
            {
                throw ToolException.InvalidParams("indique exactamente uno: handle (una entidad) o handles (lote)");
            }

            List<string> handles;
            if (hasHandle)
            {
                string h = ParamReader.OptionalHandle(p, "handle")?.ToUpperInvariant()
                    ?? throw ToolException.InvalidParams("handle: obligatorio");
                handles = new List<string> { h };
            }
            else
            {
                handles = ParamReader.RequiredHandles(p, "handles", 1, MaxLote);
            }

            string? contenido = ParamReader.OptionalString(p, "contenido", 1, PluginInfo.MaxUserTextLength);
            string? formato = ParamReader.OptionalString(p, "contenido_formato", 1, MaxFormatoLength);
            if (contenido != null && formato != null)
            {
                throw ToolException.InvalidParams("contenido y contenido_formato son excluyentes");
            }

            if (formato != null)
            {
                string? motivo = TextFormatParser.ValidateMText(formato);
                if (motivo != null)
                {
                    throw ToolException.InvalidParams("contenido_formato: " + motivo);
                }
            }

            Point3? posicion = ParamReader.OptionalPoint(p, "posicion");
            if (posicion != null && handles.Count > 1)
            {
                throw ToolException.InvalidParams("posicion solo con handle (no en lote de handles)");
            }

            double? altura = ParamReader.OptionalNumber(p, "altura", 0, double.PositiveInfinity, exclusiveMin: true);
            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, null);
            }

            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            string? justificacion = ParamReader.OptionalEnum(p, "justificacion", Justificaciones);
            double? ancho = ParamReader.OptionalNumber(p, "ancho_columna", 0, double.PositiveInfinity);

            if (contenido == null && formato == null && posicion == null && altura == null && capa == null
                && estilo == null && justificacion == null && ancho == null)
            {
                throw ToolException.InvalidParams(
                    "debe indicar al menos uno de: contenido, contenido_formato, posicion, altura, capa, estilo, justificacion, ancho_columna");
            }

            bool forzar = ParamReader.OptionalBool(p, "forzar") ?? false;
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            bool aceptar = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            bool perdida = ParamReader.OptionalBool(p, "aceptar_perdida_campos") ?? false;
            return new Params(handles, contenido, formato, posicion, altura, capa, estilo, justificacion, ancho, forzar, confirmar, aceptar, perdida);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.AceptarSinUnidades, warnings);

            // Pasada de solo lectura: handles, tipos, capas protegidas, estilo y R4 (posición). Ningún rechazo abre el grupo de deshacer.
            string espacio;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, p.Confirmar, warnings);
                for (int i = 0; i < res.Items.Count; i++)
                {
                    Entity e = res.Items[i].Entity;
                    string tipo = RequireTextType(e, p.Handles[i]);
                    CheckTipoYCampos(e, tipo, p, p.Handles[i]);
                }

                espacio = res.Espacio;
                if (p.Estilo != null)
                {
                    ResolveTextStyleId(db, tr, p.Estilo);
                }

                if (p.Posicion != null)
                {
                    PlacementCheck.Run(db, espacio, new List<BBox2> { PlacementCheck.PointBox(p.Posicion.Value) }, p.Forzar, "entidades", warnings);
                }

                tr.Commit();
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Segunda resolución: si algo cambió desde la pasada de lectura, el guard lo rechaza antes de escribir.
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, p.Confirmar, new List<string>());
                var resultados = new List<Dictionary<string, object?>>(res.Items.Count);
                for (int i = 0; i < res.Items.Count; i++)
                {
                    Entity e = res.Items[i].Entity;
                    string handle = p.Handles[i];
                    string tipo = RequireTextType(e, handle);
                    CheckTipoYCampos(e, tipo, p, handle);
                    Dictionary<string, object?> antes = Snapshot(e, tipo);

                    e.UpgradeOpen();
                    if (p.Capa != null)
                    {
                        DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Capa, null, warnings);
                        e.LayerId = layer.Id;
                    }

                    if (e is MText mt)
                    {
                        ApplyMText(db, tr, mt, p);
                    }
                    else if (e is DBText dt)
                    {
                        ApplyDbText(db, tr, dt, p, warnings);
                    }

                    resultados.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = handle,
                        ["tipo"] = tipo,
                        ["antes"] = antes,
                        ["despues"] = Snapshot(e, tipo),
                    });
                }

                tr.Commit();

                Dictionary<string, object?> primera = resultados[0];
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["pid"] = pid,
                    ["handles"] = p.Handles,
                    ["handle"] = primera["handle"],
                    ["tipo"] = primera["tipo"],
                    ["n"] = resultados.Count,
                    ["n_usuario"] = res.NUsuario,
                    ["espacio"] = res.Espacio,
                    ["capas"] = res.Capas,
                    ["insunits"] = ins,
                    ["unidades"] = UnitsTable.Name(ins),
                    ["advertencias"] = warnings,
                    ["antes"] = primera["antes"],
                    ["despues"] = primera["despues"],
                    ["resultados"] = resultados,
                    ["origen_texto"] = "contenido_del_plano",
                };
            }
        }

        /// <summary>
        /// Reglas por entidad, en la pasada de lectura y otra vez al escribir: contenido_formato y ancho_columna solo en MTEXT;
        /// una entidad con campos (FIELD) no cambia su contenido salvo con aceptar_perdida_campos: true.
        /// </summary>
        private static void CheckTipoYCampos(Entity e, string tipo, Params p, string handle)
        {
            if (tipo == "TEXT" && (p.ContenidoFormato != null || p.AnchoColumna != null))
            {
                throw ToolException.InvalidParams(
                    "contenido_formato y ancho_columna solo aplican a MTEXT: el handle " + handle + " es TEXT",
                    new Dictionary<string, object?> { ["handle"] = handle, ["tipo"] = tipo });
            }

            if (tipo == "MTEXT" && p.Justificacion != null && p.Justificacion.StartsWith("BASE_", StringComparison.Ordinal))
            {
                throw ToolException.InvalidParams(
                    "justificacion " + p.Justificacion + " solo aplica a TEXT (en MTEXT use TL…BR)",
                    new Dictionary<string, object?> { ["handle"] = handle, ["tipo"] = tipo });
            }

            if (p.CambiaContenido && e.HasFields && !p.PerdidaCampos)
            {
                throw ToolException.InvalidParams(
                    "el handle " + handle + " contiene campos (FIELD): cambiar su contenido los convierte en texto fijo. "
                    + "Pase aceptar_perdida_campos: true para aceptarlo.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["tipo"] = tipo, ["campos"] = true });
            }
        }

        private static void ApplyMText(Database db, Transaction tr, MText mt, Params p)
        {
            if (p.Contenido != null)
            {
                mt.Contents = MTextEscape.Escape(p.Contenido);
            }

            if (p.ContenidoFormato != null)
            {
                mt.Contents = p.ContenidoFormato;
            }

            if (p.Estilo != null)
            {
                mt.TextStyleId = ResolveTextStyleId(db, tr, p.Estilo);
            }

            if (p.Justificacion != null)
            {
                mt.Attachment = AttachmentOf(p.Justificacion);
            }

            if (p.AnchoColumna != null)
            {
                mt.Width = p.AnchoColumna.Value;
            }

            if (p.Altura != null)
            {
                mt.TextHeight = p.Altura.Value;
            }

            if (p.Posicion != null)
            {
                mt.Location = AcadUtil.ToPoint3d(p.Posicion.Value);
            }
        }

        private static void ApplyDbText(Database db, Transaction tr, DBText dt, Params p, List<string> warnings)
        {
            if (p.Contenido != null)
            {
                string single = TextEscape.CollapseNewlines(p.Contenido, out bool had);
                if (had)
                {
                    warnings.Add(TextSingleLineWarning);
                }

                dt.TextString = TextEscape.EscapeDbText(single);
            }

            if (p.Estilo != null)
            {
                dt.TextStyleId = ResolveTextStyleId(db, tr, p.Estilo);
            }

            if (p.Justificacion != null)
            {
                // El punto de anclaje actual pasa a ser el de alineación: la justificación no lo desplaza.
                Point3d anchor = dt.HorizontalMode == TextHorizontalMode.TextLeft && dt.VerticalMode == TextVerticalMode.TextBase
                    ? dt.Position
                    : dt.AlignmentPoint;
                (TextHorizontalMode h, TextVerticalMode v) = DbModesOf(p.Justificacion);
                dt.HorizontalMode = h;
                dt.VerticalMode = v;
                if (h == TextHorizontalMode.TextLeft && v == TextVerticalMode.TextBase)
                {
                    dt.Position = anchor;
                }
                else
                {
                    dt.AlignmentPoint = anchor;
                    dt.AdjustAlignment(db);
                }
            }

            if (p.Altura != null)
            {
                dt.Height = p.Altura.Value;
            }

            if (p.Posicion != null)
            {
                Point3d pt = AcadUtil.ToPoint3d(p.Posicion.Value);
                if (dt.HorizontalMode == TextHorizontalMode.TextLeft && dt.VerticalMode == TextVerticalMode.TextBase)
                {
                    dt.Position = pt;
                }
                else
                {
                    dt.AlignmentPoint = pt;
                    dt.AdjustAlignment(db);
                }
            }
        }

        /// <summary>Código TL…BR → punto de adjunción de MTEXT.</summary>
        private static AttachmentPoint AttachmentOf(string code)
        {
            switch (code)
            {
                case "TL": return AttachmentPoint.TopLeft;
                case "TC": return AttachmentPoint.TopCenter;
                case "TR": return AttachmentPoint.TopRight;
                case "ML": return AttachmentPoint.MiddleLeft;
                case "MC": return AttachmentPoint.MiddleCenter;
                case "MR": return AttachmentPoint.MiddleRight;
                case "BL": return AttachmentPoint.BottomLeft;
                case "BC": return AttachmentPoint.BottomCenter;
                case "BR": return AttachmentPoint.BottomRight;
                default: throw ToolException.InvalidParams("justificacion: valor inválido");
            }
        }

        /// <summary>Código de justificación → modos horizontal y vertical de TEXT (inverso de <see cref="CodeOfModes"/>).</summary>
        private static (TextHorizontalMode, TextVerticalMode) DbModesOf(string code)
        {
            if (code.StartsWith("BASE_", StringComparison.Ordinal))
            {
                return (HorizontalOf(code[5]), TextVerticalMode.TextBase);
            }

            TextVerticalMode v;
            switch (code[0])
            {
                case 'M': v = TextVerticalMode.TextVerticalMid; break;
                case 'B': v = TextVerticalMode.TextBottom; break;
                default: v = TextVerticalMode.TextTop; break;
            }

            return (HorizontalOf(code[1]), v);
        }

        private static TextHorizontalMode HorizontalOf(char c)
        {
            switch (c)
            {
                case 'C': return TextHorizontalMode.TextCenter;
                case 'R': return TextHorizontalMode.TextRight;
                default: return TextHorizontalMode.TextLeft;
            }
        }

        /// <summary>Estilo de texto por nombre (sin mayúsculas). Inexistente → NOT_FOUND data.estilo.</summary>
        private static ObjectId ResolveTextStyleId(Database db, Transaction tr, string name)
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId candidate in table)
            {
                if (candidate.IsErased)
                {
                    continue;
                }

                var rec = (TextStyleTableRecord)tr.GetObject(candidate, OpenMode.ForRead);
                if (string.Equals(rec.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            throw new ToolException(
                ErrorCodes.NotFound,
                "El estilo de texto '" + name + "' no existe en el dibujo.",
                new Dictionary<string, object?> { ["estilo"] = name });
        }

        /// <summary>MTEXT o TEXT; cualquier otro tipo → INVALID_PARAMS. Devuelve "MTEXT" o "TEXT".</summary>
        private static string RequireTextType(Entity e, string handle)
        {
            if (e is MText)
            {
                return "MTEXT";
            }

            if (e is DBText)
            {
                return "TEXT";
            }

            throw ToolException.InvalidParams(
                "handle: el tipo " + AcadUtil.DxfName(e) + " no es MTEXT ni TEXT",
                new Dictionary<string, object?> { ["handle"] = handle, ["tipo"] = AcadUtil.DxfName(e) });
        }

        /// <summary>Contenido, posición, altura, capa, estilo y justificación de la entidad (contenido tal como lo guarda AutoCAD).</summary>
        private static Dictionary<string, object?> Snapshot(Entity e, string tipo)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tipo"] = tipo, ["capa"] = e.Layer };
            if (e is MText mt)
            {
                d["contenido"] = mt.Contents;
                d["posicion"] = AcadUtil.ToArray(mt.Location);
                d["altura"] = mt.TextHeight;
                d["estilo"] = mt.TextStyleName;
                d["justificacion"] = CodeOfAttachment(mt.Attachment);
                d["ancho_columna"] = mt.Width;
            }
            else if (e is DBText dt)
            {
                d["contenido"] = dt.TextString;
                // Mismo ancla que ApplyDbText: así snapshot → aplicar no desplaza el texto.
                bool base0 = dt.HorizontalMode == TextHorizontalMode.TextLeft && dt.VerticalMode == TextVerticalMode.TextBase;
                d["posicion"] = AcadUtil.ToArray(base0 ? dt.Position : dt.AlignmentPoint);
                d["altura"] = dt.Height;
                d["estilo"] = dt.TextStyleName;
                d["justificacion"] = CodeOfModes(dt.HorizontalMode, dt.VerticalMode);
            }

            return d;
        }

        private static string CodeOfAttachment(AttachmentPoint a)
        {
            switch (a)
            {
                case AttachmentPoint.TopLeft: return "TL";
                case AttachmentPoint.TopCenter: return "TC";
                case AttachmentPoint.TopRight: return "TR";
                case AttachmentPoint.MiddleLeft: return "ML";
                case AttachmentPoint.MiddleCenter: return "MC";
                case AttachmentPoint.MiddleRight: return "MR";
                case AttachmentPoint.BottomLeft: return "BL";
                case AttachmentPoint.BottomCenter: return "BC";
                case AttachmentPoint.BottomRight: return "BR";
                case AttachmentPoint.BaseLeft: return "BASE_L";
                case AttachmentPoint.BaseCenter: return "BASE_C";
                case AttachmentPoint.BaseRight: return "BASE_R";
                default: return "OTRO";
            }
        }

        /// <summary>
        /// Modos de TEXT → código. Inverso exacto de <see cref="DbModesOf"/> para las combinaciones que escribe la herramienta;
        /// cualquier otra combinación (alineada, ajustada, media) se reporta como "OTRO" (no se reaplica).
        /// </summary>
        private static string CodeOfModes(TextHorizontalMode h, TextVerticalMode v)
        {
            string horizontal;
            if (h == TextHorizontalMode.TextLeft) horizontal = "L";
            else if (h == TextHorizontalMode.TextCenter) horizontal = "C";
            else if (h == TextHorizontalMode.TextRight) horizontal = "R";
            else return "OTRO";

            if (v == TextVerticalMode.TextBase)
            {
                return "BASE_" + horizontal;
            }

            if (v == TextVerticalMode.TextTop) return "T" + horizontal;
            if (v == TextVerticalMode.TextVerticalMid) return "M" + horizontal;
            if (v == TextVerticalMode.TextBottom) return "B" + horizontal;
            return "OTRO";
        }
    }
}
