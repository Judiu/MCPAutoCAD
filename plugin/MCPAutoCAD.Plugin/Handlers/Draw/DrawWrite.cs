using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Piezas comunes de las escrituras de Fase 3 (protocol.md §5D.0): R2 (unidades), R5 (espacio), R6 (respuesta)
    /// y los fragmentos de JSON Schema de los parámetros comunes. Flujo obligatorio de un handler de escritura:
    /// <code>
    ///   Params p = Parse(parameters);                       // hilo del pipe y otra vez en el principal (sin AutoCAD)
    ///   Document doc = ctx.RequireDocument();
    ///   int ins = DrawWrite.CheckUnits(db, p.AceptarSinUnidades, warnings);   // R2 → UNITS_UNKNOWN
    ///   PlacementCheck.Result place = PlacementCheck.Run(db, p.Espacio, cajas, p.Forzar, "entidades", warnings); // R4
    ///   return UndoGroup.Run(doc, d => Write(...));          // R1: dentro, UNA Transaction, SpaceResolver.Resolve +
    ///                                                        // DrawLayerFactory.Ensure (R3) + AppendEntity + ApplyByLayer
    /// </code>
    /// </summary>
    internal static class DrawWrite
    {
        // ---- Fragmentos de schema comunes (texto fijado en protocol.md §5D.0) ----

        public const string SchemaCapa =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Capa destino: un concepto de lps.json (muros, cotas, textos, ejes, columnas, vigas, puertas, ventanas, filos, achurados, mobiliario_fijo, sanitarios, estructura, mamposteria, niveles, escaleras, proyeccion, rotulo), el nombre de una capa existente, o una capa del estándar LPS / MCP-* que se crea si falta. Otro nombre inexistente → NOT_FOUND. Bloqueada, congelada o de xref → INVALID_PARAMS."" }";

        public const string SchemaEspacio =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""'modelo' o nombre de un layout. Por defecto, el espacio activo. Nunca cambia la pestaña activa ni la vista."" }";

        public const string SchemaForzar =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = escribir aunque la geometría quede fuera de la extensión (ampliada un 10 %) del espacio destino; por defecto se rechaza con INVALID_PARAMS sin escribir nada."" }";

        public const string SchemaAceptarSinUnidades =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = dibujar aunque el dibujo no declare unidades (INSUNITS=0); por defecto se rechaza con UNITS_UNKNOWN."" }";

        /// <summary>Parámetros comunes a toda escritura de §5D (añadir a la lista Allowed de cada handler).</summary>
        public static readonly string[] CommonParams = { "capa", "espacio", "forzar", "aceptar_sin_unidades" };

        /// <summary>Parámetros comunes ya leídos.</summary>
        internal sealed class Common
        {
            public Common(string? capa, string? espacio, bool forzar, bool aceptarSinUnidades)
            {
                Capa = capa;
                Espacio = espacio;
                Forzar = forzar;
                AceptarSinUnidades = aceptarSinUnidades;
            }

            public string? Capa { get; }
            public string? Espacio { get; }
            public bool Forzar { get; }
            public bool AceptarSinUnidades { get; }
        }

        /// <summary>Lee capa/espacio/forzar/aceptar_sin_unidades (sin AutoCAD) y valida el plan de capa.</summary>
        public static Common ReadCommon(System.Text.Json.JsonElement p, string? conceptoPorDefecto)
        {
            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            bool forzar = ParamReader.OptionalBool(p, "forzar") ?? false;
            bool aceptar = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            DrawLayerFactory.Plan(capa, conceptoPorDefecto);
            return new Common(capa, espacio, forzar, aceptar);
        }

        /// <summary>R2: INSUNITS del dibujo; 0 sin aceptar_sin_unidades → UNITS_UNKNOWN. Añade la advertencia de aceptación.</summary>
        public static int CheckUnits(Database db, bool aceptarSinUnidades, List<string> advertencias)
        {
            int ins = AcadUtil.InsUnits(db);
            if (UnitsGate.MustReject(ins, aceptarSinUnidades))
            {
                throw ToolException.UnitsUnknown();
            }

            if (UnitsGate.AcceptedUnknown(ins, aceptarSinUnidades))
            {
                advertencias.Add(UnitsGate.AcceptedWithoutUnits);
            }

            return ins;
        }

        /// <summary>Color, tipo de línea y grosor ByLayer sobre la capa indicada (todas las entidades creadas por el MCP).</summary>
        public static void ApplyByLayer(Entity e, Database db, ObjectId layerId)
        {
            e.SetDatabaseDefaults(db);
            e.LayerId = layerId;
            e.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            e.Linetype = "ByLayer";
            e.LineWeight = LineWeight.ByLayer;
        }

        /// <summary>Añade la entidad al espacio, la registra en la transacción y devuelve su handle (§2.1).</summary>
        public static string Append(BlockTableRecord space, Transaction tr, Entity e)
        {
            space.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
            return AcadUtil.HandleToString(e.Handle);
        }

        /// <summary>
        /// Respuesta común R6: pid, handles, capa, capa_creada, espacio, insunits, unidades, advertencias. El handler
        /// añade después sus campos propios (entidades, medida, …) sobre el mismo diccionario.
        /// </summary>
        public static Dictionary<string, object?> Response(
            int pid, IList<string> handles, DrawLayerFactory.Result capa, string espacio, int insunits, List<string> advertencias)
        {
            return new Dictionary<string, object?>(System.StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["handles"] = handles,
                ["capa"] = capa.Nombre,
                ["capa_creada"] = capa.Creada,
                ["espacio"] = espacio,
                ["insunits"] = insunits,
                ["unidades"] = UnitsTable.Name(insunits),
                ["advertencias"] = advertencias,
            };
        }
    }
}
