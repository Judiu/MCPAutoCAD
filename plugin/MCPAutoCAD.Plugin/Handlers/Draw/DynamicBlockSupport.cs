using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Propiedades dinámicas de un INSERT (protocol.md §5G.6; <c>BlockReference.DynamicBlockReferencePropertyCollection</c>).
    /// Lo usan insertar_bloque y GeometryDrawer.Blocks (vano). Solo hilo principal, dentro de la transacción del llamador.
    /// La lógica pura (propiedad de ancho, forma JSON, trato de los valores) está en <see cref="DynamicPropertyRules"/>.
    /// Hecho que fija el gate (no verificable sin AutoCAD): un INSERT creado por el API de una definición dinámica importada
    /// con Database.Insert conserva sus propiedades dinámicas (<c>IsDynamicBlock</c> = true) y
    /// <c>DynamicBlockReferenceProperty.Value</c> admite escritura cuando <c>ReadOnly</c> = false; tras fijar valores,
    /// AutoCAD crea la representación anónima (*U…).
    /// Decisiones documentadas: un nombre repetido en la colección (AutoCAD puede dar una copia de solo lectura) se resuelve
    /// a la primera entrada escribible; un valor numérico se escribe con el tipo del valor actual (short o int si cabe,
    /// double si no) para que la escritura case con el tipo de la propiedad; si el número no cabe (1,5 en un short) decide
    /// AutoCAD ("AutoCAD rechazó el valor").
    /// </summary>
    internal static class DynamicBlockSupport
    {
        /// <summary>
        /// true si la referencia es de un bloque dinámico. En el gate 0.7 un INSERT escalado por ancho respondió false con
        /// <c>BlockReference.IsDynamicBlock</c>: se comprueba además la definición (<c>DynamicBlockTableRecord</c>), con la
        /// transacción del llamador: <c>TopTransaction</c> no ve una StartOpenCloseTransaction ni sirve en una referencia temporal.
        /// </summary>
        public static bool IsDynamic(BlockReference br, Transaction tr)
        {
            if (br.IsDynamicBlock)
            {
                return true;
            }

            ObjectId definicion = br.DynamicBlockTableRecord;
            return !definicion.IsNull && tr.GetObject(definicion, OpenMode.ForRead) is BlockTableRecord btr && btr.IsDynamicBlock;
        }

        /// <summary>
        /// Lee todas las propiedades dinámicas en el orden de la colección: nombre, valor (double, string, int o double[]
        /// para puntos), ReadOnly, UnitsType (→ "ninguna" | "angular" | "distancia" | "area"), GetAllowedValues() (null si
        /// vacío) y Description. Lista vacía si no es dinámico.
        /// </summary>
        public static List<DynamicPropertyInfo> Read(BlockReference br, Transaction tr)
        {
            var list = new List<DynamicPropertyInfo>();
            if (!IsDynamic(br, tr))
            {
                return list;
            }

            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
            {
                list.Add(new DynamicPropertyInfo(
                    p.PropertyName,
                    Normalize(p.Value),
                    p.ReadOnly,
                    UnitsName(p.UnitsType),
                    AllowedValues(p),
                    p.Description ?? ""));
            }

            return list;
        }

        /// <summary>
        /// Fija <paramref name="valores"/> (nombre sin distinguir mayúsculas → número o cadena) en la referencia abierta
        /// ForWrite. Devuelve los nombres reales aplicados en orden. Lanza INVALID_PARAMS con detalle exacto:
        /// "propiedades_dinamicas: el bloque '&lt;b&gt;' no es dinámico", "propiedades_dinamicas: propiedad desconocida '&lt;p&gt;'
        /// (propiedades del bloque: A, B, …)", "propiedades_dinamicas.&lt;p&gt;: es de solo lectura",
        /// "propiedades_dinamicas.&lt;p&gt;: el valor &lt;v&gt; no está entre los admitidos (…)", "propiedades_dinamicas.&lt;p&gt;: debe ser
        /// un número" / "… debe ser una cadena". Un valor que AutoCAD rechaza (excepción) → INVALID_PARAMS
        /// "propiedades_dinamicas.&lt;p&gt;: AutoCAD rechazó el valor (&lt;ErrorStatus&gt;)". Si AutoCAD deja un valor distinto del
        /// pedido (p. ej. lo ajusta a su incremento) se avisa en <paramref name="advertencias"/>.
        /// </summary>
        public static List<string> Apply(BlockReference br, Transaction tr, string nombreBloque, IReadOnlyDictionary<string, object> valores, List<string> advertencias)
        {
            if (!IsDynamic(br, tr))
            {
                throw ToolException.InvalidParams("propiedades_dinamicas: el bloque '" + nombreBloque + "' no es dinámico");
            }

            var aplicadas = new List<string>(valores.Count);
            foreach (KeyValuePair<string, object> item in valores)
            {
                // La colección se vuelve a leer en cada paso: fijar una propiedad puede cambiar las demás (visibilidad).
                DynamicBlockReferenceProperty p = Find(br, item.Key);
                string prefix = "propiedades_dinamicas." + p.PropertyName + ": ";
                if (p.ReadOnly)
                {
                    throw ToolException.InvalidParams(prefix + "es de solo lectura");
                }

                object valor = ToWritable(p, item.Value, prefix);
                try
                {
                    p.Value = valor;
                }
                catch (AcRuntimeException ex)
                {
                    throw ToolException.InvalidParams(prefix + "AutoCAD rechazó el valor (" + ex.ErrorStatus + ")");
                }

                object? final = Normalize(p.Value);
                if (!DynamicPropertyRules.SameValue(final, valor))
                {
                    advertencias.Add(prefix + "AutoCAD dejó el valor " + DynamicPropertyRules.Format(final) + " en lugar de " + DynamicPropertyRules.Format(valor) + ".");
                }

                aplicadas.Add(p.PropertyName);
            }

            return aplicadas;
        }

        /// <summary>Primera entrada escribible con ese nombre; si todas son de solo lectura, la primera. Si no hay, INVALID_PARAMS.</summary>
        private static DynamicBlockReferenceProperty Find(BlockReference br, string nombre)
        {
            DynamicBlockReferenceProperty? first = null;
            var names = new List<string>();
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
            {
                names.Add(p.PropertyName);
                if (!string.Equals(p.PropertyName, nombre, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!p.ReadOnly)
                {
                    return p;
                }

                first ??= p;
            }

            return first ?? throw ToolException.InvalidParams(
                "propiedades_dinamicas: propiedad desconocida '" + nombre + "' (propiedades del bloque: " + string.Join(", ", names) + ")",
                new Dictionary<string, object?> { ["propiedades"] = names });
        }

        /// <summary>Comprueba tipo y valores admitidos, y devuelve el objeto que se escribe en <c>Value</c>.</summary>
        private static object ToWritable(DynamicBlockReferenceProperty p, object pedido, string prefix)
        {
            object actual = p.Value;
            if (actual is string && pedido is not string)
            {
                throw ToolException.InvalidParams(prefix + "debe ser una cadena");
            }

            if (DynamicPropertyRules.TryNumber(actual, out _) && pedido is string)
            {
                throw ToolException.InvalidParams(prefix + "debe ser un número");
            }

            object[] admitidos = p.GetAllowedValues() ?? Array.Empty<object>();
            if (admitidos.Length > 0)
            {
                // Se escribe el valor admitido, no el pedido: conserva el tipo y la caja exactos que espera AutoCAD.
                foreach (object admitido in admitidos)
                {
                    if (DynamicPropertyRules.SameValue(admitido, pedido))
                    {
                        return admitido;
                    }
                }

                throw ToolException.InvalidParams(
                    prefix + "el valor " + DynamicPropertyRules.Format(pedido) + " no está entre los admitidos (" + string.Join(", ", admitidos.Select(a => DynamicPropertyRules.Format(Normalize(a)))) + ")");
            }

            return DynamicPropertyRules.TryNumber(pedido, out double number) ? DynamicPropertyRules.ConvertNumber(number, actual) : pedido;
        }

        /// <summary>Los puntos de AutoCAD pasan a double[]; el resto lo resuelve <see cref="DynamicPropertyRules.NormalizeValue"/>.</summary>
        private static object? Normalize(object? value)
        {
            switch (value)
            {
                case Point3d p3:
                    return new[] { p3.X, p3.Y, p3.Z };
                case Point2d p2:
                    return new[] { p2.X, p2.Y };
                default:
                    return DynamicPropertyRules.NormalizeValue(value);
            }
        }

        private static IReadOnlyList<object?>? AllowedValues(DynamicBlockReferenceProperty p)
        {
            object[]? allowed = p.GetAllowedValues();
            return allowed == null || allowed.Length == 0 ? null : allowed.Select(Normalize).ToList();
        }

        private static string UnitsName(DynamicBlockReferencePropertyUnitsType units)
        {
            switch (units)
            {
                case DynamicBlockReferencePropertyUnitsType.Angular:
                    return DynamicPropertyRules.UnidadesAngular;
                case DynamicBlockReferencePropertyUnitsType.Distance:
                    return DynamicPropertyRules.UnidadesDistancia;
                case DynamicBlockReferencePropertyUnitsType.Area:
                    return DynamicPropertyRules.UnidadesArea;
                default:
                    return DynamicPropertyRules.UnidadesNinguna;
            }
        }
    }
}
