using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Piezas comunes de array_rectangular y array_polar (protocol.md §5E.3.5): lectura de handles/confirmar/capa,
    /// resolución con las protecciones de §5D.8 (<see cref="HandleGuard.Resolve"/>), cajas de las entidades y construcción
    /// de las <see cref="PlannedCopy"/> (orden: posición, luego handle) con sus números. No escribe nunca: el dibujo lo
    /// hace <see cref="GeometryDrawer"/> (preview en MCP-PREVIEW; definitivo en la capa del original salvo 'capa').
    /// </summary>
    internal static class ArraySupport
    {
        /// <summary>Parámetros comunes de los dos arrays (además de los propios y de GenSchema.CommonParams).</summary>
        public static readonly string[] SharedParams = { "handles", "confirmar", "capa" };

        /// <summary>Máximo de transformaciones listadas en la respuesta.</summary>
        public const int MaxListedTransforms = 20;

        public const string SchemaHandles =
            @"{ ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Handles de las entidades a copiar (1–100, sin repetidos), todas en el mismo espacio. No se admiten atributos (use el INSERT), entidades dentro de bloques, viewports de papel ni capas bloqueadas o de xref."" }";

        public const string SchemaConfirmar =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = copiar también entidades del usuario (capas que no empiezan por MCP-). Sin él, si alguna entidad no está en una capa MCP-*, se rechaza con INVALID_PARAMS indicando cuántas y cuáles."" }";

        public const string SchemaCapa =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Capa de las copias definitivas (concepto de lps.json, capa existente o capa del estándar / MCP-* que se crea si falta). Por defecto cada copia conserva la capa de su original; la previsualización va siempre a MCP-PREVIEW."" }";

        /// <summary>Texto JSON de las tres propiedades compartidas (sin coma final).</summary>
        public const string SharedProperties =
            "    \"handles\": " + SchemaHandles + ",\n"
            + "    \"confirmar\": " + SchemaConfirmar + ",\n"
            + "    \"capa\": " + SchemaCapa;

        /// <summary>Propiedades de 'numeros' comunes a los dos arrays (sin coma final).</summary>
        public const string NumerosProperties =
            @"        ""n_copias"": { ""type"": ""integer"", ""description"": ""Posiciones de copia (sin contar la del original)."" },
        ""n_posiciones"": { ""type"": ""integer"" },
        ""n_usuario"": { ""type"": ""integer"" },
        ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
        ""transformaciones"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } }";

        /// <summary>Parámetros compartidos ya leídos.</summary>
        internal sealed class Shared
        {
            public Shared(List<string> handles, bool confirmar, string? capa, string? espacio)
            {
                Handles = handles;
                Confirmar = confirmar;
                Capa = capa;
                Espacio = espacio;
            }

            /// <summary>Handles en mayúsculas, sin repetidos.</summary>
            public List<string> Handles { get; }

            public bool Confirmar { get; }

            public string? Capa { get; }

            /// <summary>Parámetro común 'espacio' (solo para avisar si destino y originales no coinciden).</summary>
            public string? Espacio { get; }
        }

        /// <summary>Lee handles (1–100), confirmar y capa. Sin AutoCAD.</summary>
        public static Shared Read(JsonElement p)
        {
            List<string> handles = ParamReader.RequiredHandles(p, "handles", 1, 100);
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, null);
            }

            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            return new Shared(handles, confirmar, capa, espacio);
        }

        /// <summary>Entidades resueltas y sus cajas (lectura).</summary>
        internal sealed class Sources
        {
            public Sources(HandleGuard.Resolved resolved, List<BBox2?> cajas, BBox2? union)
            {
                Resolved = resolved;
                Cajas = cajas;
                Union = union;
            }

            public HandleGuard.Resolved Resolved { get; }

            /// <summary>Caja de cada entidad en el orden de handles (null si no es calculable).</summary>
            public List<BBox2?> Cajas { get; }

            /// <summary>Unión de las cajas conocidas, o null si ninguna lo es.</summary>
            public BBox2? Union { get; }
        }

        /// <summary>
        /// Pasos 1–5 de §5D.8 (NOT_FOUND, atributos/bloques/viewports, capas bloqueadas o de xref, mismo espacio, confirmar)
        /// y cajas de cada entidad. Si el espacio destino no es el de los originales se avisa (las copias van al destino).
        /// </summary>
        public static Sources Resolve(Shared s, Database db, Transaction tr, List<string> advertencias)
        {
            HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, s.Handles, s.Confirmar, advertencias);
            var cajas = new List<BBox2?>(res.Items.Count);
            BBox2? union = null;
            foreach (HandleGuard.Item item in res.Items)
            {
                BBox2? b = AcadUtil.TryBBox2(item.Entity);
                cajas.Add(b);
                if (b != null)
                {
                    union = union == null ? b : union.Value.Union(b.Value);
                }
            }

            string destino = SpaceResolver.Resolve(db, tr, s.Espacio).Name;
            if (!string.Equals(destino, res.Espacio, StringComparison.OrdinalIgnoreCase))
            {
                advertencias.Add("array: las entidades originales están en '" + res.Espacio + "' y las copias se crean en '" + destino + "' (parámetro espacio).");
            }

            return new Sources(res, cajas, union);
        }

        /// <summary>Caja unión obligatoria para el punto base por defecto.</summary>
        public static BBox2 RequireUnion(Sources src) =>
            src.Union ?? throw ToolException.InvalidParams("handles: las entidades no tienen caja calculable; indique base");

        /// <summary>
        /// Una <see cref="PlannedCopy"/> por posición × handle (orden: posición, luego handle) y los números comunes:
        /// n_copias, n_posiciones, n_entidades, n_usuario, capas, transformaciones (hasta 20).
        /// El tope de 500 se aplica también al total de entidades copiadas (posiciones × handles).
        /// </summary>
        public static GeneratedGeometry Build(Shared s, Sources src, IReadOnlyList<ArrayLayout.Placement> placements, bool polar)
        {
            long total = (long)placements.Count * src.Resolved.Items.Count;
            if (total > ArrayLayout.MaxCopies)
            {
                throw ToolException.InvalidParams(
                    "array: el número de copias (" + total.ToString(CultureInfo.InvariantCulture) + ") supera el tope de " + ArrayLayout.MaxCopies.ToString(CultureInfo.InvariantCulture),
                    new Dictionary<string, object?> { ["n_posiciones"] = placements.Count + 1, ["n_handles"] = src.Resolved.Items.Count });
            }

            var entidades = new List<PlannedEntity>((int)total);
            foreach (ArrayLayout.Placement pl in placements)
            {
                for (int i = 0; i < src.Resolved.Items.Count; i++)
                {
                    BBox2? caja = src.Cajas[i];
                    BBox2? transformada = caja == null ? (BBox2?)null : Transform2D.TransformBox(caja.Value, pl.Transformacion);
                    entidades.Add(new PlannedCopy(src.Resolved.Items[i].Info.Handle, pl.Transformacion, transformada, null, s.Capa));
                }
            }

            var transformaciones = new List<Dictionary<string, object?>>();
            for (int k = 0; k < placements.Count && k < MaxListedTransforms; k++)
            {
                ArrayLayout.Placement pl = placements[k];
                object? posicion = polar || pl.Fila == null || pl.Columna == null
                    ? pl.Indice
                    : new[] { pl.Fila.Value, pl.Columna.Value };
                transformaciones.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["posicion"] = posicion,
                    ["indice"] = pl.Indice,
                    ["fila"] = pl.Fila,
                    ["columna"] = pl.Columna,
                    ["desplazamiento"] = new[] { pl.Dx, pl.Dy },
                    ["rotacion"] = pl.RotacionGrados,
                });
            }

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["n_copias"] = placements.Count,
                ["n_posiciones"] = placements.Count + 1,
                ["n_entidades"] = entidades.Count,
                ["n_usuario"] = src.Resolved.NUsuario,
                ["capas"] = src.Resolved.Capas,
                ["transformaciones"] = transformaciones,
            };
            var geo = new GeneratedGeometry(entidades, numeros);

            // Huella de cada original (§5E.5): confirmar se niega a copiar si alguno cambió o se borró desde la preview.
            foreach (HandleGuard.Item item in src.Resolved.Items)
            {
                geo.AnclasExtra.Add(PointRefResolver.Fingerprint(item.Entity, item.Info.Handle));
            }

            return geo;
        }

        /// <summary>Llama a ArrayLayout traduciendo ArgumentException → INVALID_PARAMS (ex.Message tal cual).</summary>
        public static IReadOnlyList<ArrayLayout.Placement> Layout(Func<IReadOnlyList<ArrayLayout.Placement>> build)
        {
            try
            {
                return build();
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>Número finito obligatorio distinto de 0: "&lt;name&gt;: no puede ser 0".</summary>
        public static double RequiredNonZero(JsonElement p, string name)
        {
            double v = PrimitiveSupport.RequiredFinite(p, name);
            if (v == 0.0)
            {
                throw ToolException.InvalidParams(name + ": no puede ser 0");
            }

            return v;
        }

        /// <summary>Entero obligatorio en [min, max] ("&lt;name&gt;: debe ser un entero entre a y b").</summary>
        public static int RequiredInt(JsonElement p, string name, int min, int max) =>
            ParamReader.OptionalInt(p, name, min, max) ?? throw ToolException.InvalidParams(name + ": obligatorio");

        /// <summary>Lista Allowed de un array: propios + compartidos + comunes de generadora.</summary>
        public static string[] AllowedWith(params string[] own)
        {
            var list = new List<string>(own);
            list.AddRange(SharedParams);
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }
    }
}
