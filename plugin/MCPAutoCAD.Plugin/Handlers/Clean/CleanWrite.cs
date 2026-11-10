using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Clean
{
    /// <summary>
    /// Piezas comunes de las cuatro herramientas de limpieza de la Fase L (protocol.md §5F.0): el modo
    /// previsualización SIN estado (sin confirmar: true NUNCA se escribe), los fragmentos de schema de
    /// <c>confirmar</c>/<c>preview</c>, L3 (unidades: solo corregir_unidades_bloques rechaza INSUNITS = 0 al
    /// escribir, con CheckUnits; las demás usan ReadUnits y solo avisan; la previsualización nunca rechaza) y la cabecera común de la respuesta. Flujo obligatorio de un handler:
    /// <code>
    ///   Params p = Parse(parameters);                       // hilo del pipe y otra vez en el principal (sin AutoCAD)
    ///   CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Confirmar, p.Preview);   // Preview | Write (o INVALID_PARAMS)
    ///   Document doc = ctx.RequireDocument();
    ///   int ins = CleanWrite.ReadUnits(db);   // o CheckUnits(db, mode, warnings) si el resultado depende de INSUNITS (L3)
    ///   // lectura (StartOpenCloseTransaction): inventario exacto de lo que se haría
    ///   if (mode == Mode.Preview) return CleanWrite.Response(...) + inventario;   // sin UndoGroup, sin DocumentLock
    ///   return UndoGroup.Run(doc, d => Write(...));                               // R1: UNA Transaction, un CTRL+Z
    /// </code>
    /// </summary>
    internal static class CleanWrite
    {
        /// <summary>Modo resuelto de la llamada (§5F.0).</summary>
        internal enum Mode
        {
            /// <summary>Solo lectura: inventario de lo que se haría; ningún paso de deshacer.</summary>
            Preview,

            /// <summary>Escritura en un paso de deshacer.</summary>
            Write,
        }

        // ---- Fragmentos de schema (texto fijado en protocol.md §5F.0) ----

        public const string SchemaConfirmar =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = ESCRIBIR (las entidades afectadas son del usuario: sin confirmar: true la herramienta solo previsualiza y no modifica nada)."" }";

        public const string SchemaPreview =
            @"{ ""type"": ""boolean"", ""description"": ""true = solo previsualizar (inventario exacto de lo que se haría, sin escribir) aunque se pase confirmar: true. Por defecto, true si confirmar no es true. preview: false sin confirmar: true → INVALID_PARAMS."" }";

        /// <summary>Parámetros comunes a las cuatro herramientas (añadir a la lista Allowed de cada handler).</summary>
        public static readonly string[] CommonParams = { "confirmar", "preview" };

        /// <summary>data.detalle exacto cuando se pide preview: false sin confirmar: true.</summary>
        public const string PreviewFalseWithoutConfirm =
            "confirmar: debe ser true para escribir; omita preview (o pase preview: true) para solo previsualizar";

        /// <summary>Parámetros comunes ya leídos (sin AutoCAD).</summary>
        internal sealed class Common
        {
            public Common(bool confirmar, bool? preview)
            {
                Confirmar = confirmar;
                Preview = preview;
            }

            public bool Confirmar { get; }

            /// <summary>null si el cliente no lo pasó.</summary>
            public bool? Preview { get; }
        }

        /// <summary>Lee confirmar/preview (sin AutoCAD) y valida la combinación (<see cref="ResolveMode"/>).</summary>
        public static Common ReadCommon(JsonElement p)
        {
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            bool? preview = ParamReader.OptionalBool(p, "preview");
            ResolveMode(confirmar, preview);
            return new Common(confirmar, preview);
        }

        /// <summary>
        /// §5F.0: confirmar ≠ true → Preview (preview ausente o true); confirmar ≠ true con preview: false →
        /// INVALID_PARAMS <see cref="PreviewFalseWithoutConfirm"/>; confirmar: true → Write salvo preview: true
        /// explícito (la previsualización siempre gana: así se puede inspeccionar con los mismos parámetros).
        /// </summary>
        public static Mode ResolveMode(bool confirmar, bool? preview)
        {
            if (!confirmar)
            {
                if (preview == false)
                {
                    throw ToolException.InvalidParams(PreviewFalseWithoutConfirm);
                }

                return Mode.Preview;
            }

            return preview == true ? Mode.Preview : Mode.Write;
        }

        /// <summary>
        /// L3 (§5F.0), solo para herramientas cuyo resultado depende de INSUNITS (corregir_unidades_bloques): en
        /// escritura, INSUNITS = 0 → UNITS_UNKNOWN sin escape; en previsualización no se rechaza (es una lectura) y basta
        /// la advertencia de §2.1 que ya añade <see cref="AcadUtil.UnitWarnings"/>. Devuelve INSUNITS.
        /// </summary>
        public static int CheckUnits(Database db, Mode mode, List<string> advertencias)
        {
            int ins = AcadUtil.InsUnits(db);
            if (mode == Mode.Write && UnitsGate.MustReject(ins, aceptarSinUnidades: false))
            {
                throw ToolException.UnitsUnknown();
            }

            return ins;
        }

        /// <summary>
        /// L3 (§5F.0, desde 0.8.3), para herramientas que no interpretan unidades (borrar_fuera_de, borrar_excepto_seleccion,
        /// purgar, normalizar_capas): deciden por handles, tablas de símbolos o geometría del propio dibujo, así que
        /// INSUNITS = 0 no cambia qué se escribe. No rechaza; la advertencia de §2.1 ya la añade
        /// <see cref="AcadUtil.UnitWarnings"/>. Devuelve INSUNITS.
        /// </summary>
        public static int ReadUnits(Database db) => AcadUtil.InsUnits(db);

        /// <summary>
        /// Cabecera común de la respuesta (§5F.0): pid, preview, escrito, insunits, unidades, advertencias. El handler
        /// añade después sus campos propios sobre el mismo diccionario (el inventario es el mismo en ambos modos).
        /// </summary>
        public static Dictionary<string, object?> Response(int pid, Mode mode, int insunits, List<string> advertencias)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["preview"] = mode == Mode.Preview,
                ["escrito"] = mode == Mode.Write,
                ["insunits"] = insunits,
                ["unidades"] = UnitsTable.Name(insunits),
                ["advertencias"] = advertencias,
            };
        }

        /// <summary>Fragmento del outputSchema con la cabecera común (para concatenar con las propiedades propias).</summary>
        public const string OutputHeader =
            @"""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""escrito"": { ""type"": ""boolean"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }";

        /// <summary>Conteo "tipo → n" ordenado por n descendente y nombre (como por_tipo de §5C.2).</summary>
        public static Dictionary<string, object?> SortedCounts(Dictionary<string, int> counts)
        {
            var pairs = new List<KeyValuePair<string, int>>(counts);
            pairs.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
            });

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in pairs)
            {
                result[kv.Key] = kv.Value;
            }

            return result;
        }

        /// <summary>Incrementa un contador por clave.</summary>
        public static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }
    }
}
