using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>Clase de referencia de punto (protocol.md §5E.1).</summary>
    public enum PointRefKind
    {
        /// <summary>[x, y] o [x, y, z] literal en WCS.</summary>
        Coordenada,

        /// <summary>{handle, punto, indice?}: un punto característico de una entidad existente.</summary>
        PuntoDeEntidad,

        /// <summary>{handle, interseccion_con, cerca_de?}: intersección de dos curvas existentes.</summary>
        Interseccion,
    }

    /// <summary>
    /// Referencia de punto "PuntoRef" (protocol.md §5E.1): lo que el cliente escribe en TODO parámetro de punto de las
    /// herramientas de la Fase 4A. Parsing PURO (sin AutoCAD); la resolución a coordenadas la hace el plugin
    /// (<c>PointRefResolver</c>). Los textos de error forman parte del contrato: no cambiarlos.
    /// </summary>
    public sealed class PointRef
    {
        public const string PuntoInicio = "inicio";
        public const string PuntoFin = "fin";
        public const string PuntoMedio = "medio";
        public const string PuntoCentro = "centro";
        public const string PuntoVertice = "vertice";

        /// <summary>Valores admitidos de <c>punto</c>, en el orden del contrato.</summary>
        public static readonly string[] Puntos = { PuntoInicio, PuntoFin, PuntoMedio, PuntoCentro, PuntoVertice };

        /// <summary>Fragmento de JSON Schema (draft 2020-12) de un PuntoRef; se inserta tal cual en los schemas.</summary>
        public const string Schema =
            @"{ ""description"": ""PuntoRef: [x, y] en WCS, o {\""handle\"": \""2A3F\"", \""punto\"": \""inicio|fin|medio|centro|vertice\"", \""indice\"": n} (punto de una entidad existente; indice solo con 'vertice', desde 0), o {\""handle\"": \""2A3F\"", \""interseccion_con\"": \""2A40\"", \""cerca_de\"": [x, y]} (intersección de dos curvas; cerca_de elige si hay varias)."", ""oneOf"": [
      { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
      { ""type"": ""object"", ""properties"": { ""handle"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""punto"": { ""type"": ""string"", ""enum"": [""inicio"", ""fin"", ""medio"", ""centro"", ""vertice""] }, ""indice"": { ""type"": ""integer"", ""minimum"": 0 } }, ""required"": [""handle"", ""punto""], ""additionalProperties"": false },
      { ""type"": ""object"", ""properties"": { ""handle"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""interseccion_con"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""cerca_de"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } }, ""required"": [""handle"", ""interseccion_con""], ""additionalProperties"": false }
    ] }";

        private PointRef(PointRefKind kind, Point3? coord, string? handle, string? punto, int? indice, string? handle2, Point3? cercaDe)
        {
            Kind = kind;
            Coordenada = coord;
            Handle = handle;
            Punto = punto;
            Indice = indice;
            Handle2 = handle2;
            CercaDe = cercaDe;
        }

        public PointRefKind Kind { get; }

        /// <summary>Solo <see cref="PointRefKind.Coordenada"/>.</summary>
        public Point3? Coordenada { get; }

        /// <summary>Handle en MAYÚSCULAS (sin prefijo) de la entidad ancla; null en coordenadas.</summary>
        public string? Handle { get; }

        /// <summary>inicio | fin | medio | centro | vertice (solo PuntoDeEntidad).</summary>
        public string? Punto { get; }

        /// <summary>Índice de vértice (solo punto = vertice; null = no se dio).</summary>
        public int? Indice { get; }

        /// <summary>Handle de la segunda curva (solo Interseccion).</summary>
        public string? Handle2 { get; }

        /// <summary>Punto de desambiguación (solo Interseccion; null = no se dio).</summary>
        public Point3? CercaDe { get; }

        /// <summary>true si depende de alguna entidad existente (ancla).</summary>
        public bool TieneAncla => Kind != PointRefKind.Coordenada;

        public static PointRef FromPoint(Point3 p) => new PointRef(PointRefKind.Coordenada, p, null, null, null, null, null);

        public static PointRef FromEntity(string handle, string punto, int? indice = null) =>
            new PointRef(PointRefKind.PuntoDeEntidad, null, handle.ToUpperInvariant(), punto, indice, null, null);

        public static PointRef FromIntersection(string handle, string handle2, Point3? cercaDe = null) =>
            new PointRef(PointRefKind.Interseccion, null, handle.ToUpperInvariant(), null, null, handle2.ToUpperInvariant(), cercaDe);

        // ---- Mensajes de error (data.detalle exacto; protocol.md §5E.1) ----

        public static string ShapeError(string path) =>
            path + ": debe ser [x, y] o un objeto {handle, punto} / {handle, interseccion_con}";

        public static string PuntoError(string path) => path + ".punto: debe ser uno de inicio, fin, medio, centro, vertice";

        public static string IndiceSoloVertice(string path) => path + ".indice: solo aplica con punto 'vertice'";

        public static string IndiceError(string path) => path + ".indice: debe ser un entero mayor o igual que 0";

        public static string HandleError(string path, string campo) => path + "." + campo + ": formato inválido";

        public static string MixedError(string path) => path + ": no puede llevar punto e interseccion_con a la vez";

        public static string UnknownProperty(string path, string prop) => path + "." + prop + ": propiedad no permitida";

        public static string SameHandleError(string path) => path + ": interseccion_con debe ser una entidad distinta de handle";

        /// <summary>
        /// Parsea un PuntoRef. <paramref name="path"/> = ruta del campo para los mensajes ("eje[2]", "origen").
        /// Lanza <see cref="ArgumentException"/> con el detalle exacto; el plugin lo convierte en INVALID_PARAMS.
        /// </summary>
        public static PointRef Parse(JsonElement el, string path)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                return FromPoint(ReadPoint(el, path));
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(ShapeError(path));
            }

            string? handle = null, punto = null, inter = null;
            int? indice = null;
            Point3? cercaDe = null;
            bool hasIndice = false, hasCerca = false;
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "handle":
                        handle = ReadHandle(prop.Value, path, "handle");
                        break;
                    case "punto":
                        punto = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                        if (punto == null || Array.IndexOf(Puntos, punto) < 0)
                        {
                            throw new ArgumentException(PuntoError(path));
                        }

                        break;
                    case "indice":
                        hasIndice = true;
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double d)
                            || !double.IsFinite(d) || Math.Floor(d) != d || d < 0 || d > int.MaxValue)
                        {
                            throw new ArgumentException(IndiceError(path));
                        }

                        indice = (int)d;
                        break;
                    case "interseccion_con":
                        inter = ReadHandle(prop.Value, path, "interseccion_con");
                        break;
                    case "cerca_de":
                        hasCerca = true;
                        cercaDe = ReadPoint(prop.Value, path + ".cerca_de");
                        break;
                    default:
                        throw new ArgumentException(UnknownProperty(path, prop.Name));
                }
            }

            if (handle == null)
            {
                throw new ArgumentException(ShapeError(path));
            }

            if (punto != null && inter != null)
            {
                throw new ArgumentException(MixedError(path));
            }

            if (inter != null)
            {
                if (hasIndice)
                {
                    throw new ArgumentException(IndiceSoloVertice(path));
                }

                if (string.Equals(inter, handle, StringComparison.Ordinal))
                {
                    throw new ArgumentException(SameHandleError(path));
                }

                return new PointRef(PointRefKind.Interseccion, null, handle, null, null, inter, cercaDe);
            }

            if (punto == null)
            {
                throw new ArgumentException(ShapeError(path));
            }

            if (hasCerca)
            {
                throw new ArgumentException(UnknownProperty(path, "cerca_de"));
            }

            if (hasIndice && punto != PuntoVertice)
            {
                throw new ArgumentException(IndiceSoloVertice(path));
            }

            return new PointRef(PointRefKind.PuntoDeEntidad, null, handle, punto, indice, null, null);
        }

        /// <summary>Array de PuntoRef con [minItems, maxItems] elementos: "&lt;path&gt;: debe ser un array de a a b puntos".</summary>
        public static List<PointRef> ParseList(JsonElement el, string path, int minItems, int maxItems)
        {
            string shape = string.Format(CultureInfo.InvariantCulture, "{0}: debe ser un array de {1} a {2} puntos", path, minItems, maxItems);
            if (el.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException(shape);
            }

            int n = el.GetArrayLength();
            if (n < minItems || n > maxItems)
            {
                throw new ArgumentException(shape);
            }

            var list = new List<PointRef>(n);
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                list.Add(Parse(item, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"));
                i++;
            }

            return list;
        }

        /// <summary>Forma JSON de la referencia tal como la escribió el cliente (para 'puntos_resueltos.ref').</summary>
        public object ToJson()
        {
            switch (Kind)
            {
                case PointRefKind.Coordenada:
                    return Coordenada!.Value.ToArray();
                case PointRefKind.PuntoDeEntidad:
                    var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["handle"] = Handle, ["punto"] = Punto };
                    if (Indice != null)
                    {
                        d["indice"] = Indice;
                    }

                    return d;
                default:
                    var i = new Dictionary<string, object?>(StringComparer.Ordinal) { ["handle"] = Handle, ["interseccion_con"] = Handle2 };
                    if (CercaDe != null)
                    {
                        i["cerca_de"] = CercaDe.Value.ToArray();
                    }

                    return i;
            }
        }

        private static string ReadHandle(JsonElement el, string path, string campo)
        {
            string invalid = HandleError(path, campo);
            if (el.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(invalid);
            }

            string value = el.GetString() ?? "";
            if (value.Length < 1 || value.Length > 16)
            {
                throw new ArgumentException(invalid);
            }

            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
                if (!hex)
                {
                    throw new ArgumentException(invalid);
                }
            }

            return value.ToUpperInvariant();
        }

        private static Point3 ReadPoint(JsonElement el, string path)
        {
            if (el.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException(ShapeError(path));
            }

            int count = el.GetArrayLength();
            if (count != 2 && count != 3)
            {
                throw new ArgumentException(path + ": debe ser un array de 2 o 3 números");
            }

            var comps = new double[count];
            int i = 0;
            foreach (JsonElement c in el.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Number || !c.TryGetDouble(out double v))
                {
                    throw new ArgumentException(path + ": debe ser un array de 2 o 3 números");
                }

                if (!double.IsFinite(v))
                {
                    throw new ArgumentException(path + ": coordenada no finita");
                }

                comps[i++] = v;
            }

            return Point3.FromArray(comps);
        }
    }

    /// <summary>
    /// Huella de la entidad ancla de un PuntoRef en el momento de resolverlo (protocol.md §5E.4): sirve para que
    /// <c>confirmar</c> detecte que el ancla cambió (movida, borrada, editada) y se niegue a escribir. Puro.
    /// </summary>
    public sealed record AnchorFingerprint(string Handle, string Tipo, string Firma)
    {
        /// <summary>
        /// Firma a partir de la caja, los extremos, la longitud de la curva (todo redondeado a 1e-6) y el número de
        /// vértices (polilíneas), invariante entre lecturas: <c>caja|inicio|fin|longitud|n_vertices</c> ("-" si no aplica).
        /// La longitud y el número de vértices detectan la edición de un vértice interior que no cambia ni la caja ni los
        /// extremos (corrección de auditoría 2026-10-26). Sin notación exponencial (nunca contiene '+', que separa las
        /// huellas de un ancla compuesta) y sin "-0".
        /// </summary>
        public static string BuildFirma(BBox2? caja, Point3? inicio, Point3? fin, double? longitud = null, int? nVertices = null)
        {
            string F(double v) => (Math.Round(v, 6) + 0.0).ToString("0.######", CultureInfo.InvariantCulture);
            string c = caja == null ? "-" : F(caja.Value.Xmin) + "," + F(caja.Value.Ymin) + "," + F(caja.Value.Xmax) + "," + F(caja.Value.Ymax);
            string s = inicio == null ? "-" : F(inicio.Value.X) + "," + F(inicio.Value.Y) + "," + F(inicio.Value.Z);
            string e = fin == null ? "-" : F(fin.Value.X) + "," + F(fin.Value.Y) + "," + F(fin.Value.Z);
            string l = longitud == null || !double.IsFinite(longitud.Value) ? "-" : F(longitud.Value);
            string nv = nVertices == null ? "-" : nVertices.Value.ToString(CultureInfo.InvariantCulture);
            return c + "|" + s + "|" + e + "|" + l + "|" + nv;
        }

        public Dictionary<string, object?> ToJson() =>
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["handle"] = Handle, ["tipo"] = Tipo, ["firma"] = Firma };

        public static AnchorFingerprint FromJson(JsonElement el) =>
            new AnchorFingerprint(el.GetProperty("handle").GetString()!, el.GetProperty("tipo").GetString()!, el.GetProperty("firma").GetString()!);
    }

    /// <summary>PuntoRef ya resuelto a WCS por el plugin, con la huella de su ancla (null en coordenadas literales).</summary>
    public sealed record ResolvedPoint(PointRef Ref, Point3 Punto, AnchorFingerprint? Ancla)
    {
        /// <summary>Entrada de 'puntos_resueltos' de la respuesta: {ref, punto, ancla}.</summary>
        public Dictionary<string, object?> ToJson() =>
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ref"] = Ref.ToJson(),
                ["punto"] = Punto.ToArray(),
                ["ancla"] = Ancla?.Handle,
            };
    }
}
