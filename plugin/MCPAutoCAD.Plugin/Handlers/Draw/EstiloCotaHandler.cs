using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// estilo_cota (0.10.x). Hilo principal; requiere documento. Crea un estilo de cota copiando otro existente (basado_en; por
    /// defecto el actual) con overrides acotados, o modifica uno existente (modificar: true).
    /// <list type="bullet">
    /// <item>Crear: escribe directamente, como crear_capa, en un paso de deshacer. preview: true solo previsualiza. Un nombre ya
    /// existente → INVALID_PARAMS (use modificar: true).</item>
    /// <item>Modificar: afecta a TODAS las cotas que usan el estilo: se recalculan (RecomputeDimensionBlock). Sin confirmar: true
    /// solo previsualiza (antes/después y cotas_afectadas); con confirmar: true escribe. Alcance: cotas del modelo y de los
    /// layouts; NO se tocan las cotas dentro de definiciones de bloque.</item>
    /// <item>activar: hace el estilo el actual. Si el estilo es el actual (antes o después), se propagan sus valores a las
    /// variables del dibujo (SetDimstyleData).</item>
    /// <item>Toda validación (nombre, base, estilo de texto, xref, estilo actual) se hace ANTES del paso de deshacer.</item>
    /// <item>Anotativo: si el estilo es anotativo, escala_global se ignora y se avisa.</item>
    /// </list>
    /// Overrides: escala_global (DIMSCALE), altura_texto (DIMTXT), tamano_flecha (DIMASZ), estilo_texto (DIMTXSTY, estilo de
    /// texto existente), decimales (DIMDEC 0–8), factor_medida (DIMLFAC > 0), extension (DIMEXE ≥ 0), desfase (DIMEXO ≥ 0),
    /// color_texto (DIMCLRT, índice ACI 1–255).
    /// </summary>
    internal sealed class EstiloCotaHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "nombre", "modificar", "basado_en", "escala_global", "altura_texto", "tamano_flecha", "estilo_texto", "decimales",
            "factor_medida", "extension", "desfase", "color_texto", "activar", "confirmar", "preview",
        };

        private const string EscalaAnotativa = "escala_global ignorada: el estilo es anotativo (la escala la fija la escala de anotación).";

        public string Name => "estilo_cota";

        public string Description =>
            "CREA un estilo de cota copiando otro existente (basado_en, por defecto el actual) con overrides acotados (escala_global, " +
            "altura_texto, tamano_flecha, estilo_texto, decimales, factor_medida, extension, desfase, color_texto), o MODIFICA uno " +
            "existente con modificar: true (recalcula todas sus cotas del modelo y layouts; sin confirmar: true solo previsualiza). " +
            "activar: true lo hace el estilo actual. Un paso de deshacer.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre del estilo (válido para AutoCAD). Con modificar: true debe existir; sin él, no debe existir. No puede ser dependiente de una xref."" },
    ""modificar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = cambiar el estilo existente 'nombre' y recalcular sus cotas (modelo y layouts; no las de bloques). Exige confirmar: true para escribir."" },
    ""basado_en"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Estilo existente del que se copian todos los valores al crear. Por defecto, el estilo actual. No se admite con modificar: true."" },
    ""escala_global"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""DIMSCALE del estilo. Ignorado (con aviso) si el estilo es anotativo."" },
    ""altura_texto"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""DIMTXT: altura del texto de cota (unidades de dibujo)."" },
    ""tamano_flecha"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""DIMASZ: tamaño de las flechas."" },
    ""estilo_texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""DIMTXSTY: nombre de un estilo de texto existente (no dependiente de xref)."" },
    ""decimales"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 8, ""description"": ""DIMDEC: decimales de la medida."" },
    ""factor_medida"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""DIMLFAC: factor de escala lineal de la medida."" },
    ""extension"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""DIMEXE: extensión de las líneas de referencia más allá de la línea de cota."" },
    ""desfase"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""DIMEXO: desfase de las líneas de referencia desde el origen."" },
    ""color_texto"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255, ""description"": ""DIMCLRT: índice ACI (1–255) del color del texto de cota."" },
    ""activar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = hacer este estilo el actual del dibujo (y aplicar sus valores a las variables de cota)."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = escribir una modificación (modificar: true). Crear no lo necesita."" },
    ""preview"": { ""type"": ""boolean"", ""description"": ""true = solo previsualizar aunque se pase confirmar: true (y, al crear, no escribir)."" }
  },
  ""required"": [""nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""estilo"": { ""type"": ""string"" },
    ""creado"": { ""type"": ""boolean"" },
    ""modificado"": { ""type"": ""boolean"" },
    ""basado_en"": { ""type"": [""string"", ""null""] },
    ""activo"": { ""type"": ""boolean"", ""description"": ""true si el estilo es el actual tras la llamada."" },
    ""cotas_afectadas"": { ""type"": ""integer"", ""description"": ""Cotas del modelo y de los layouts que usan el estilo y se recalculan (no las de bloques)."" },
    ""cambios"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""campo"": { ""type"": ""string"" }, ""antes"": { ""type"": [""string"", ""number"", ""null""] }, ""despues"": { ""type"": [""string"", ""number"", ""null""] } } } }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""estilo"", ""creado"", ""modificado"", ""activo"", ""cotas_afectadas"", ""cambios""]
}");

        private sealed class Params
        {
            public string Nombre = "";
            public bool Modificar;
            public string? BasadoEn;
            public double? Escala;
            public double? AlturaTexto;
            public double? TamanoFlecha;
            public string? EstiloTexto;
            public int? Decimales;
            public double? FactorMedida;
            public double? Extension;
            public double? Desfase;
            public int? ColorTexto;
            public bool Activar;
            public bool Confirmar;
            public bool? Preview;

            /// <summary>Modo calculado en <see cref="Parse"/> (sin AutoCAD): modificar usa ResolveMode(confirmar, preview); crear escribe salvo preview: true.</summary>
            public CleanWrite.Mode Mode;

            public bool HayOverrides =>
                Escala != null || AlturaTexto != null || TamanoFlecha != null || EstiloTexto != null || Decimales != null
                || FactorMedida != null || Extension != null || Desfase != null || ColorTexto != null;
        }

        /// <summary>Resultado de la validación previa (lectura): ids válidos en cualquier transacción posterior.</summary>
        private sealed class Plan
        {
            public ObjectId Existing = ObjectId.Null;
            public ObjectId BaseId = ObjectId.Null;
            public ObjectId TextStyle = ObjectId.Null;
            public string? TextStyleName;
            public List<ObjectId> Usando = new List<ObjectId>();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var r = new Params
            {
                Nombre = ParamReader.RequiredText(p, "nombre", 1, 255),
                Modificar = ParamReader.OptionalBool(p, "modificar") ?? false,
                BasadoEn = ParamReader.OptionalString(p, "basado_en", 1, 255),
                Escala = ParamReader.OptionalNumber(p, "escala_global", 0, double.PositiveInfinity, exclusiveMin: true),
                AlturaTexto = ParamReader.OptionalNumber(p, "altura_texto", 0, double.PositiveInfinity, exclusiveMin: true),
                TamanoFlecha = ParamReader.OptionalNumber(p, "tamano_flecha", 0, double.PositiveInfinity, exclusiveMin: true),
                EstiloTexto = ParamReader.OptionalString(p, "estilo_texto", 1, 255),
                Decimales = ParamReader.OptionalInt(p, "decimales", 0, 8),
                FactorMedida = ParamReader.OptionalNumber(p, "factor_medida", 0, double.PositiveInfinity, exclusiveMin: true),
                Extension = ParamReader.OptionalNumber(p, "extension", 0, double.PositiveInfinity),
                Desfase = ParamReader.OptionalNumber(p, "desfase", 0, double.PositiveInfinity),
                ColorTexto = ParamReader.OptionalInt(p, "color_texto", 1, 255),
                Activar = ParamReader.OptionalBool(p, "activar") ?? false,
                Confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false,
                Preview = ParamReader.OptionalBool(p, "preview"),
            };

            if (r.Modificar && r.BasadoEn != null)
            {
                throw ToolException.InvalidParams("basado_en no se admite con modificar: true");
            }

            if (r.Modificar && !r.HayOverrides && !r.Activar)
            {
                throw ToolException.InvalidParams("modificar: indique al menos un valor a cambiar o activar: true");
            }

            r.Mode = r.Modificar
                ? CleanWrite.ResolveMode(r.Confirmar, r.Preview)
                : CleanWrite.ResolveMode(true, r.Preview);
            return r;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            try
            {
                // Devuelve void y lanza si el nombre contiene caracteres prohibidos o no es un símbolo válido.
                SymbolUtilityServices.ValidateSymbolName(p.Nombre, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw ToolException.InvalidParams("nombre: no es un nombre válido para un estilo de cota",
                    new Dictionary<string, object?> { ["nombre"] = p.Nombre });
            }

            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db);
            int pid = ctx.Pid;

            // Validación previa FUERA del grupo de deshacer (MEDIA-3); en previsualización también es la respuesta.
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                Plan plan = Resolve(db, tr, p);
                if (p.Mode == CleanWrite.Mode.Preview)
                {
                    ObjectId recId = p.Modificar ? plan.Existing : plan.BaseId;
                    var rec = (DimStyleTableRecord)tr.GetObject(recId, OpenMode.ForRead);
                    Dictionary<string, object?> antes = Snapshot(tr, rec);
                    Dictionary<string, object?> despues = Proposed(p, antes, rec, plan.TextStyleName, warnings);
                    if (p.Modificar && plan.Usando.Count > 0)
                    {
                        warnings.Add("Esta modificación recalculará " + plan.Usando.Count + " cota(s) que usan el estilo '" + p.Nombre + "'.");
                        int bloqueadas = CountOnLockedLayer(tr, plan.Usando);
                        if (bloqueadas > 0)
                        {
                            warnings.Add(bloqueadas + " cota(s) en capas bloqueadas no se recalcularán.");
                        }
                    }

                    tr.Commit();
                    // Estado actual (antes de escribir): el estilo nuevo aún no existe al previsualizar una creación.
                    bool activoAhora = p.Modificar && !plan.Existing.IsNull && db.Dimstyle == plan.Existing;
                    return Response(pid, CleanWrite.Mode.Preview, ins, warnings, p, p.Nombre, antes, despues,
                        creado: false, modificado: false, activo: activoAhora,
                        cotas: p.Modificar ? plan.Usando.Count : 0, baseName: p.Modificar ? null : ((DimStyleTableRecord)tr.GetObject(plan.BaseId, OpenMode.ForRead)).Name);
                }

                tr.Commit();
            }

            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        /// <summary>
        /// Validación completa en lectura: existencia (crear/modificar), xref (nombre, base, estilo de texto), base o estilo actual
        /// válido, estilo de texto y cotas que usan el estilo. Lanza ToolException antes de cualquier escritura.
        /// </summary>
        private static Plan Resolve(Database db, Transaction tr, Params p)
        {
            var plan = new Plan();
            plan.Existing = FindStyle(db, tr, p.Nombre);
            CheckExistence(db, tr, p, plan.Existing);
            if (p.Modificar)
            {
                CotaHandler.RequireNoXref((DimStyleTableRecord)tr.GetObject(plan.Existing, OpenMode.ForRead));
                plan.Usando = CollectUsing(db, tr, plan.Existing);
            }
            else
            {
                plan.BaseId = ResolveBase(db, tr, p.BasadoEn);
            }

            if (p.EstiloTexto != null)
            {
                plan.TextStyle = ResolveTextStyleId(db, tr, p.EstiloTexto);
                plan.TextStyleName = ((TextStyleTableRecord)tr.GetObject(plan.TextStyle, OpenMode.ForRead)).Name;
            }

            return plan;
        }

        /// <summary>Escritura en UNA transacción: crea (clon de la base) o modifica el estilo y recalcula sus cotas.</summary>
        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Plan plan = Resolve(db, tr, p);
                DimStyleTableRecord rec;
                Dictionary<string, object?> antes;
                string? baseName = null;
                if (p.Modificar)
                {
                    rec = (DimStyleTableRecord)tr.GetObject(plan.Existing, OpenMode.ForWrite);
                    antes = Snapshot(tr, rec);
                }
                else
                {
                    var origen = (DimStyleTableRecord)tr.GetObject(plan.BaseId, OpenMode.ForRead);
                    baseName = origen.Name;
                    antes = Snapshot(tr, origen);
                    rec = (DimStyleTableRecord)origen.Clone();
                    rec.Name = p.Nombre;
                    var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForWrite);
                    table.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }

                bool anotativo = IsAnnotative(rec);
                if (p.Escala.HasValue && anotativo)
                {
                    warnings.Add(EscalaAnotativa);
                }

                ApplyOverrides(rec, p, plan.TextStyle, anotativo);
                if (p.Activar)
                {
                    db.Dimstyle = rec.ObjectId;
                }

                if (db.Dimstyle == rec.ObjectId)
                {
                    db.SetDimstyleData(rec);   // el estilo es el actual: sus valores pasan a las variables del dibujo
                }

                int recalculadas = 0;
                int bloqueadas = 0;
                foreach (ObjectId id in plan.Usando)
                {
                    // Capa bloqueada: no se fuerza la apertura (eOnLockedLayer); se cuenta y se avisa.
                    if (IsOnLockedLayer(tr, id))
                    {
                        bloqueadas++;
                        continue;
                    }

                    try
                    {
                        var dim = (Dimension)tr.GetObject(id, OpenMode.ForWrite);
                        dim.RecomputeDimensionBlock(true);
                        recalculadas++;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Cota ilegible: no se recalcula; el resto sí.
                    }
                }

                if (p.Modificar && recalculadas > 0)
                {
                    warnings.Add("Se recalcularon " + recalculadas + " cota(s) que usan el estilo '" + p.Nombre + "' (no se tocan las de bloques).");
                }

                if (bloqueadas > 0)
                {
                    warnings.Add(bloqueadas + " cota(s) en capas bloqueadas no se recalcularon.");
                }

                Dictionary<string, object?> despues = Snapshot(tr, rec);
                bool activo = IsCurrent(db, rec.ObjectId, p);
                string nombre = rec.Name;
                tr.Commit();
                return Response(pid, CleanWrite.Mode.Write, ins, warnings, p, nombre, antes, despues,
                    creado: !p.Modificar, modificado: p.Modificar, activo: activo, cotas: recalculadas, baseName: baseName);
            }
        }

        /// <summary>Crear exige que no exista; modificar exige que exista (NOT_FOUND con la lista de estilos disponibles).</summary>
        private static void CheckExistence(Database db, Transaction tr, Params p, ObjectId existing)
        {
            if (p.Modificar)
            {
                if (existing.IsNull)
                {
                    throw NotFound(db, tr, "El estilo de cota '" + p.Nombre + "' no existe; para crearlo quite modificar.", p.Nombre);
                }

                return;
            }

            if (!existing.IsNull)
            {
                throw ToolException.InvalidParams(
                    "ya existe el estilo de cota '" + p.Nombre + "'; use modificar: true para cambiarlo",
                    new Dictionary<string, object?> { ["estilo"] = p.Nombre });
            }
        }

        /// <summary>Estilo base: el pedido o el actual. NOT_FOUND si el actual es nulo/borrado o el pedido no existe; INVALID_PARAMS si es de xref.</summary>
        private static ObjectId ResolveBase(Database db, Transaction tr, string? basadoEn)
        {
            if (basadoEn == null)
            {
                ObjectId cur = db.Dimstyle;
                if (cur.IsNull || cur.IsErased)
                {
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        "El estilo de cota actual del dibujo no es válido (nulo o borrado); indique 'basado_en'.",
                        new Dictionary<string, object?> { ["estilo"] = null });
                }

                CotaHandler.RequireNoXref((DimStyleTableRecord)tr.GetObject(cur, OpenMode.ForRead));
                return cur;
            }

            ObjectId id = FindStyle(db, tr, basadoEn);
            if (id.IsNull)
            {
                throw NotFound(db, tr, "El estilo base '" + basadoEn + "' no existe en el dibujo.", basadoEn);
            }

            CotaHandler.RequireNoXref((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead));
            return id;
        }

        private static void ApplyOverrides(DimStyleTableRecord rec, Params p, ObjectId textStyle, bool anotativo)
        {
            if (p.Escala.HasValue && !anotativo) rec.Dimscale = p.Escala.Value;
            if (p.AlturaTexto.HasValue) rec.Dimtxt = p.AlturaTexto.Value;
            if (p.TamanoFlecha.HasValue) rec.Dimasz = p.TamanoFlecha.Value;
            if (!textStyle.IsNull) rec.Dimtxsty = textStyle;
            if (p.Decimales.HasValue) rec.Dimdec = p.Decimales.Value;
            if (p.FactorMedida.HasValue) rec.Dimlfac = p.FactorMedida.Value;
            if (p.Extension.HasValue) rec.Dimexe = p.Extension.Value;
            if (p.Desfase.HasValue) rec.Dimexo = p.Desfase.Value;
            if (p.ColorTexto.HasValue) rec.Dimclrt = Color.FromColorIndex(ColorMethod.ByAci, (short)p.ColorTexto.Value);
        }

        private static bool IsAnnotative(DimStyleTableRecord rec) => rec.Annotative == AnnotativeStates.True;

        /// <summary>Valores de un estilo (los que admiten override).</summary>
        private static Dictionary<string, object?> Snapshot(Transaction tr, DimStyleTableRecord rec)
        {
            string textStyle = "";
            if (!rec.Dimtxsty.IsNull && !rec.Dimtxsty.IsErased)
            {
                textStyle = ((TextStyleTableRecord)tr.GetObject(rec.Dimtxsty, OpenMode.ForRead)).Name;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["escala_global"] = rec.Dimscale,
                ["altura_texto"] = rec.Dimtxt,
                ["tamano_flecha"] = rec.Dimasz,
                ["estilo_texto"] = textStyle,
                ["decimales"] = rec.Dimdec,
                ["factor_medida"] = rec.Dimlfac,
                ["extension"] = rec.Dimexe,
                ["desfase"] = rec.Dimexo,
                ["color_texto"] = (int)rec.Dimclrt.ColorIndex,
            };
        }

        /// <summary>Valores que quedarían tras los overrides pedidos (previsualización; sin escala si el estilo es anotativo).</summary>
        private static Dictionary<string, object?> Proposed(
            Params p, Dictionary<string, object?> antes, DimStyleTableRecord rec, string? textStyleName, List<string> warnings)
        {
            var d = new Dictionary<string, object?>(antes, StringComparer.Ordinal);
            bool anotativo = IsAnnotative(rec);
            if (p.Escala.HasValue)
            {
                if (anotativo)
                {
                    warnings.Add(EscalaAnotativa);
                }
                else
                {
                    d["escala_global"] = p.Escala.Value;
                }
            }

            if (p.AlturaTexto.HasValue) d["altura_texto"] = p.AlturaTexto.Value;
            if (p.TamanoFlecha.HasValue) d["tamano_flecha"] = p.TamanoFlecha.Value;
            if (textStyleName != null) d["estilo_texto"] = textStyleName;
            if (p.Decimales.HasValue) d["decimales"] = p.Decimales.Value;
            if (p.FactorMedida.HasValue) d["factor_medida"] = p.FactorMedida.Value;
            if (p.Extension.HasValue) d["extension"] = p.Extension.Value;
            if (p.Desfase.HasValue) d["desfase"] = p.Desfase.Value;
            if (p.ColorTexto.HasValue) d["color_texto"] = p.ColorTexto.Value;
            return d;
        }

        /// <summary>Si el estilo queda como actual: activar, o (modificar) si ya lo era. Un estilo nuevo sin activar no lo es.</summary>
        private static bool IsCurrent(Database db, ObjectId id, Params p)
        {
            if (p.Activar)
            {
                return true;
            }

            return p.Modificar && !id.IsNull && db.Dimstyle == id;
        }

        private static Dictionary<string, object?> Response(
            int pid, CleanWrite.Mode mode, int ins, List<string> warnings, Params p, string estilo,
            Dictionary<string, object?> antes, Dictionary<string, object?> despues, bool creado, bool modificado, bool activo,
            int cotas, string? baseName)
        {
            Dictionary<string, object?> d = CleanWrite.Response(pid, mode, ins, warnings);
            d["estilo"] = estilo;
            d["creado"] = creado && mode == CleanWrite.Mode.Write;
            d["modificado"] = modificado && mode == CleanWrite.Mode.Write;
            d["basado_en"] = p.Modificar ? null : baseName;
            d["activo"] = activo;
            d["cotas_afectadas"] = cotas;
            var cambios = new List<Dictionary<string, object?>>();
            foreach (string campo in despues.Keys)
            {
                object? a = antes.TryGetValue(campo, out object? av) ? av : null;
                object? b = despues[campo];
                if (!Equals(a, b))
                {
                    cambios.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["campo"] = campo, ["antes"] = a, ["despues"] = b });
                }
            }

            if (p.Activar)
            {
                cambios.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["campo"] = "activo", ["antes"] = null, ["despues"] = estilo });
            }

            d["cambios"] = cambios;
            return d;
        }

        /// <summary>Nombre → id, sin distinguir mayúsculas; ObjectId.Null si no existe.</summary>
        private static ObjectId FindStyle(Database db, Transaction tr, string name)
        {
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId candidate in table)
            {
                if (candidate.IsErased)
                {
                    continue;
                }

                var rec = (DimStyleTableRecord)tr.GetObject(candidate, OpenMode.ForRead);
                if (string.Equals(rec.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return ObjectId.Null;
        }

        private static ToolException NotFound(Database db, Transaction tr, string mensaje, string estilo)
        {
            var disponibles = new List<string>();
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId candidate in table)
            {
                if (!candidate.IsErased)
                {
                    disponibles.Add(((DimStyleTableRecord)tr.GetObject(candidate, OpenMode.ForRead)).Name);
                }
            }

            return new ToolException(
                ErrorCodes.NotFound,
                mensaje + " Disponibles: " + string.Join(", ", disponibles) + ".",
                new Dictionary<string, object?> { ["estilo"] = estilo, ["disponibles"] = disponibles });
        }

        /// <summary>Estilo de texto por nombre (sin mayúsculas). Inexistente → NOT_FOUND data.estilo_texto; de xref → INVALID_PARAMS.</summary>
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
                    if (rec.IsDependent || rec.Name.IndexOf('|') >= 0)
                    {
                        throw ToolException.InvalidParams(
                            "el estilo de texto '" + rec.Name + "' es dependiente de una xref; use un estilo del dibujo.",
                            new Dictionary<string, object?> { ["estilo_texto"] = rec.Name, ["xref"] = true });
                    }

                    return candidate;
                }
            }

            throw new ToolException(
                ErrorCodes.NotFound,
                "El estilo de texto '" + name + "' no existe en el dibujo.",
                new Dictionary<string, object?> { ["estilo_texto"] = name });
        }

        /// <summary>true si la entidad está en una capa bloqueada (no se puede abrir para escritura).</summary>
        private static bool IsOnLockedLayer(Transaction tr, ObjectId id)
        {
            try
            {
                var e = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (e == null || e.LayerId.IsNull)
                {
                    return false;
                }

                return tr.GetObject(e.LayerId, OpenMode.ForRead, false) is LayerTableRecord layer && layer.IsLocked;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }

        private static int CountOnLockedLayer(Transaction tr, List<ObjectId> ids)
        {
            int n = 0;
            foreach (ObjectId id in ids)
            {
                if (IsOnLockedLayer(tr, id))
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// Cotas (Dimension) del modelo y de los layouts que usan el estilo. No incluye las de definiciones de bloque
        /// (documentado: esas no se recalculan).
        /// </summary>
        private static List<ObjectId> CollectUsing(Database db, Transaction tr, ObjectId styleId)
        {
            var spaces = new List<ObjectId> { SymbolUtilityServices.GetBlockModelSpaceId(db) };
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (entry.Value.IsNull || entry.Value.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType && !layout.BlockTableRecordId.IsNull)
                {
                    spaces.Add(layout.BlockTableRecordId);
                }
            }

            var result = new List<ObjectId>();
            foreach (ObjectId spaceId in spaces)
            {
                var btr = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    try
                    {
                        if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead, false) is Dimension dim && dim.DimensionStyle == styleId)
                        {
                            result.Add(id);
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Entidad ilegible: no cuenta.
                    }
                }
            }

            return result;
        }
    }
}
