using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// hatch (protocol.md §5D.7). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Contorno por handles
    /// (asociativo; cada handle = un bucle External) XOR por vértices (polígono implícito; no asociativo).
    /// Capa por defecto: concepto 'achurados' (Achurados).
    ///
    /// PATRÓN (desde 0.9): (a) 'patron' predefinido (SOLID o un nombre de acad.pat/acadiso.pat); (b) si ese nombre no es
    /// predefinido, un patrón ya DEFINIDO EN EL DIBUJO (p. ej. FP_1, FP_43, FP_44 exportados de Revit): se busca un HATCH
    /// del dibujo que lo use y se clona; (c) 'patron_de': handle de un HATCH existente, clonado tal cual. El clon conserva
    /// las líneas de definición del patrón (<c>Hatch.GetPatternDefinitionAt</c> es de solo lectura en la API: no hay otra
    /// forma de reproducirlas), el tipo, el origen, la escala, el ángulo y el color; escala/angulo/color, si se pasan,
    /// lo sustituyen. Por qué fallaba FP_1: <c>SetHatchPattern(PreDefined, …)</c> solo busca en acad.pat/acadiso.pat, y
    /// esos patrones existen únicamente como definición dentro de los HATCH del dibujo.
    ///
    /// Orden de API del caso predefinido (gate de Fase 3): AppendEntity → SetDatabaseDefaults (vía <see cref="DrawWrite.ApplyByLayer"/>)
    /// → PatternScale → SetHatchPattern(PreDefined) → [PatternAngle → SetHatchPattern] → Associative (solo handles) → AppendLoop → EvaluateHatch(true).
    ///
    /// Bucle por vértices: AppendLoop(HatchLoopTypes.Polyline, Point2dCollection, DoubleCollection) con el primer
    /// vértice repetido al final para cerrar el bucle y bulges = 0. Las comprobaciones de los handles (existencia,
    /// cierre, espacio, capa no bloqueada ni dependiente de xref) son una lectura previa sin efectos, ANTES del UndoGroup.
    /// R4: UNA caja = unión de las cajas del contorno (etiqueta 'entidades' con handles, 'vertices' con vértices).
    /// </summary>
    internal sealed class HatchHandler : IToolHandler
    {
        private const string DefaultConcept = "achurados";
        private const string Solid = "SOLID";

        /// <summary>Límite de contornos por handles (sin lote: un HATCH por llamada).</summary>
        public const int MaxContourHandles = 50;

        private static readonly string[] Own = { "contorno", "patron", "patron_de", "escala", "angulo" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            PrimitiveSupport.Concat(Own, EntityColor.ParamNames),
            DrawWrite.CommonParams);
        private static readonly string[] AllowedContour = { "handles", "vertices" };

        public string Name => "hatch";

        public string Description =>
            "ESCRIBE en el dibujo: crea un sombreado (HATCH) dentro de un contorno dado por handles de entidades cerradas " +
            "(LWPOLYLINE/POLYLINE 2D cerradas, CIRCLE, ELLIPSE; asociativo) o por vértices [x, y] (no asociativo), en un solo paso de deshacer. " +
            "El patrón es predefinido (SOLID, ANSI31, AR-CONC, …), uno ya definido en el dibujo (p. ej. FP_1) o el de un HATCH existente (patron_de: handle), " +
            "con color opcional. Capa por defecto Achurados (concepto 'achurados').";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""contorno"": {
      ""type"": ""object"",
      ""description"": ""Exactamente uno: {handles: [...]} con 1 a 50 entidades cerradas del espacio destino (un bucle por entidad), o {vertices: [...]} con 3 a 1000 puntos de un polígono (sin repetir el primero al final)."",
      ""properties"": {
        ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": " + ParamReader.SchemaHandle + @" },
        ""vertices"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 1000, ""items"": " + PrimitiveSupport.PointSchema + @" }
      },
      ""additionalProperties"": false
    },
    ""patron"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""SOLID, un patrón predefinido de AutoCAD (ANSI31, AR-CONC, …) o un patrón ya definido en el dibujo (p. ej. FP_1), que se copia de un HATCH del dibujo que lo use. Exclusivo con patron_de."" },
    ""patron_de"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle de un HATCH existente del dibujo: copia su patrón (también los definidos en el dibujo), tipo, origen, escala, ángulo y color (si no se pasa color). escala y angulo, si se pasan, lo sustituyen. Exclusivo con patron."" },
    ""escala"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Escala del patrón (> 0; se ignora con SOLID). Por defecto 1, o la del HATCH copiado (patron_de o patrón del dibujo)."" },
    ""angulo"": { ""type"": ""number"", ""description"": ""Grados. Por defecto 0, o el del HATCH copiado. Cualquier valor finito (se normaliza a [0, 360))."" },
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @",
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""contorno""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = PrimitiveSupport.OutputSchema(@"{
      ""type"": ""object"",
      ""properties"": {
        ""handle"": { ""type"": ""string"" },
        ""patron"": { ""type"": ""string"" },
        ""tipo_patron"": { ""type"": ""string"", ""description"": ""PreDefined, UserDefined o CustomDefined (AutoCAD)."" },
        ""patron_de"": { ""type"": [""string"", ""null""], ""description"": ""Handle del HATCH copiado, si lo hubo."" },
        ""escala"": { ""type"": ""number"" },
        ""angulo"": { ""type"": ""number"" },
        ""area"": { ""type"": [""number"", ""null""] },
        ""n_bucles"": { ""type"": ""integer"" },
        ""asociativo"": { ""type"": ""boolean"" },
        ""contorno_handles"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""string"" } },
        ""bbox"": { ""type"": [""array"", ""null""] }
      }
    }");

        /// <summary>Parámetros ya validados. Pattern/PatronDe: exactamente uno (no nulo).</summary>
        private sealed class Params
        {
            public Params(
                List<string>? handles,
                List<(double x, double y)>? vertices,
                string? pattern,
                string? patronDe,
                double? scale,
                double? angle,
                ColorSpec? color,
                DrawWrite.Common common)
            {
                Handles = handles;
                Vertices = vertices;
                Pattern = pattern;
                PatronDe = patronDe;
                Scale = scale;
                Angle = angle;
                Color = color;
                Common = common;
            }

            /// <summary>Handles en mayúsculas (o null si el contorno es por vértices).</summary>
            public List<string>? Handles { get; }

            public List<(double x, double y)>? Vertices { get; }

            /// <summary>Nombre del patrón (null con patron_de).</summary>
            public string? Pattern { get; }

            /// <summary>Handle (mayúsculas) del HATCH a copiar (null sin patron_de).</summary>
            public string? PatronDe { get; }

            /// <summary>Escala pedida (null = por defecto o la del HATCH copiado).</summary>
            public double? Scale { get; }

            /// <summary>Grados normalizados a [0, 360) o null (por defecto o el del HATCH copiado).</summary>
            public double? Angle { get; }

            public ColorSpec? Color { get; }
            public DrawWrite.Common Common { get; }

            public bool IsSolid => Pattern != null && string.Equals(Pattern, Solid, StringComparison.OrdinalIgnoreCase);
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            if (!p.TryGetProperty("contorno", out JsonElement contour))
            {
                throw ToolException.InvalidParams("contorno: obligatorio");
            }

            if (contour.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams(HatchBoundary.ContourExclusive);
            }

            ParamReader.RejectUnknown(contour, AllowedContour, "contorno.");
            bool hasHandles = contour.TryGetProperty("handles", out JsonElement handlesEl);
            bool hasVertices = contour.TryGetProperty("vertices", out JsonElement verticesEl);
            if (hasHandles == hasVertices)
            {
                throw ToolException.InvalidParams(HatchBoundary.ContourExclusive);
            }

            List<string>? handles = null;
            List<(double x, double y)>? vertices = null;
            if (hasHandles)
            {
                handles = ReadHandles(handlesEl);
            }
            else
            {
                vertices = ReadVertices(verticesEl);
                IReadOnlyList<string> errors = HatchBoundary.ValidateVertices(vertices);
                if (errors.Count > 0)
                {
                    throw ToolException.InvalidParams(errors[0]);
                }
            }

            bool hasPattern = ParamReader.Has(p, "patron");
            bool hasDe = ParamReader.Has(p, "patron_de");
            if (hasPattern == hasDe)
            {
                throw ToolException.InvalidParams("patron: indique patron o patron_de (solo uno)");
            }

            string? pattern = null;
            string? patronDe = null;
            if (hasPattern)
            {
                pattern = (ParamReader.OptionalString(p, "patron", 1, 255) ?? "").Trim();
                if (pattern.Length == 0)
                {
                    throw ToolException.InvalidParams("patron: debe tener al menos 1 carácter(es)");
                }
            }
            else
            {
                patronDe = (ParamReader.OptionalHandle(p, "patron_de") ?? "").ToUpperInvariant();
            }

            double? scale = ParamReader.OptionalNumber(p, "escala", 0, double.PositiveInfinity, exclusiveMin: true);
            double? angleRaw = PrimitiveSupport.OptionalFinite(p, "angulo");
            double? angle = angleRaw == null ? null : Angles.Normalize360(angleRaw.Value);
            ColorSpec? color = EntityColor.Read(p);
            DrawWrite.Common common = DrawWrite.ReadCommon(p, DefaultConcept);
            return new Params(handles, vertices, pattern, patronDe, scale, angle, color, common);
        }

        /// <summary>contorno.handles: 1–50 handles hexadecimales sin repetidos (mayúsculas).</summary>
        private static List<string> ReadHandles(JsonElement el)
        {
            const string name = "contorno.handles";
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 1 || el.GetArrayLength() > MaxContourHandles)
            {
                throw ToolException.InvalidParams(name + ": debe ser un array de 1 a 50 handles");
            }

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                string path = name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                string value = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
                bool ok = value.Length >= 1 && value.Length <= 16;
                foreach (char c in value)
                {
                    ok &= (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
                }

                if (!ok)
                {
                    throw ToolException.InvalidParams(path + ": formato inválido");
                }

                string upper = value.ToUpperInvariant();
                if (!seen.Add(upper))
                {
                    throw ToolException.InvalidParams(path + ": handle repetido");
                }

                list.Add(upper);
                i++;
            }

            return list;
        }

        /// <summary>contorno.vertices: 3–1000 puntos (z ignorada).</summary>
        private static List<(double x, double y)> ReadVertices(JsonElement el)
        {
            const string name = "contorno.vertices";
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 3 || el.GetArrayLength() > 1000)
            {
                throw ToolException.InvalidParams(name + ": debe ser un array de 3 a 1000 puntos");
            }

            var list = new List<(double x, double y)>();
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                Point3 pt = ParamReader.ReadPoint(item, name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                list.Add((pt.X, pt.Y));
                i++;
            }

            return list;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);

            var boxes = new List<BBox2>(1);
            List<ObjectId>? ids = null;
            string etiqueta;
            if (p.Handles != null)
            {
                ids = CheckHandles(db, p.Handles, p.Common.Espacio, out BBox2? union);
                if (union != null)
                {
                    boxes.Add(union.Value);
                }

                etiqueta = "entidades";
            }
            else
            {
                boxes.Add(PrimitiveSupport.BoxOf(p.Vertices!));
                etiqueta = "vertices";
            }

            // Todo lo que pueda fallar por el patrón se decide AQUÍ, antes del UndoGroup (sin paso de deshacer vacío).
            ObjectId templateId = ObjectId.Null;
            if (p.PatronDe != null)
            {
                templateId = CheckTemplate(db, p.PatronDe, p);
            }
            else if (!p.IsSolid)
            {
                templateId = PlanPatternTemplate(db, p);
            }

            PlacementCheck.Run(db, p.Common.Espacio, boxes, p.Common.Forzar, etiqueta, warnings);

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, ids, templateId, pid, ins, warnings));
        }

        /// <summary>
        /// Lectura previa sin efectos: cada handle existe (NOT_FOUND), es un contorno cerrado admitido (INVALID_PARAMS
        /// con su tipo) y vive en el espacio destino. Devuelve los ObjectId y la unión de sus cajas.
        /// </summary>
        private static List<ObjectId> CheckHandles(Database db, List<string> handles, string? espacio, out BBox2? union)
        {
            union = null;
            var ids = new List<ObjectId>(handles.Count);
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, espacio);
                for (int i = 0; i < handles.Count; i++)
                {
                    Entity e = AcadUtil.TryGetEntityByHandle(db, handles[i], tr) ?? throw ToolException.EntityNotFound(handles[i]);
                    if (!IsClosedContour(e))
                    {
                        throw ToolException.InvalidParams(
                            HatchBoundary.NotClosed(i, AcadUtil.DxfName(e)), new Dictionary<string, object?> { ["handle"] = handles[i] });
                    }

                    string? owner = SpaceResolver.OwnerName(e, tr);
                    if (!string.Equals(owner, resolved.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        throw ToolException.InvalidParams(
                            HatchBoundary.OtherSpace(i, resolved.Name), new Dictionary<string, object?> { ["handle"] = handles[i] });
                    }

                    // Capa de la entidad de contorno: con el sombreado asociativo AutoCAD abre el contorno para
                    // escribir su reactor, y en una capa bloqueada lanza eOnLockedLayer dentro de la escritura
                    // (→ INTERNAL). Se rechaza aquí, antes de escribir nada (corrección 0.4.0).
                    RequireBoundaryLayer(e, i, handles[i], tr);

                    BBox2? b = AcadUtil.TryBBox2(e);
                    if (b != null)
                    {
                        union = union == null ? b : union.Value.Union(b.Value);
                    }

                    ids.Add(e.ObjectId);
                }

                tr.Commit();
            }

            return ids;
        }

        /// <summary>Clasificación de un nombre de patrón frente a acad.pat / acadiso.pat (lectura del fichero, sin AutoCAD).</summary>
        private enum PatternKind
        {
            /// <summary>No se pudo leer el fichero de patrones: se decide al escribir (comportamiento anterior).</summary>
            Unknown,

            /// <summary>Está en acad.pat o acadiso.pat.</summary>
            Predefined,

            /// <summary>Se leyeron ambos ficheros y no está en ninguno.</summary>
            NotPredefined,
        }

        /// <summary>
        /// ¿El nombre es un patrón predefinido? Busca las líneas "*NOMBRE," de acad.pat y acadiso.pat (FindFile con
        /// FindFileHint.PatternFile). Sin efectos sobre el dibujo: así el NOT_FOUND sale antes del UndoGroup.
        /// </summary>
        private static PatternKind ClassifyPredefined(Database db, string name)
        {
            int filesRead = 0;
            foreach (string file in new[] { "acad.pat", "acadiso.pat" })
            {
                string? path;
                try
                {
                    path = HostApplicationServices.Current.FindFile(file, db, FindFileHint.PatternFile);
                }
                catch (Exception)
                {
                    path = null;
                }

                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    continue;
                }

                try
                {
                    foreach (string raw in System.IO.File.ReadLines(path))
                    {
                        string line = raw.TrimStart();
                        if (line.Length < 2 || line[0] != '*')
                        {
                            continue;
                        }

                        int comma = line.IndexOf(',');
                        string n = (comma < 0 ? line.Substring(1) : line.Substring(1, comma - 1)).Trim();
                        if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return PatternKind.Predefined;
                        }
                    }

                    filesRead++;
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    // Fichero ilegible (E/S o permisos): no cuenta como leído → Unknown, se decide al escribir.
                }
            }

            return filesRead == 2 ? PatternKind.NotPredefined : PatternKind.Unknown;
        }

        /// <summary>
        /// Lectura previa (antes del UndoGroup) para un patrón por nombre que no es SOLID: si es predefinido → Null (la
        /// escritura usa SetHatchPattern); si no, un HATCH del dibujo que lo use (<see cref="FindTemplateByName"/> +
        /// <see cref="TemplateProblem"/>); si no hay ninguno y el fichero se leyó → NOT_FOUND sin paso de deshacer; con
        /// CustomDefined no se admiten escala ni ángulo. Si el fichero no se pudo leer (Unknown) y no hay plantilla, se
        /// decide al escribir como antes.
        /// </summary>
        private static ObjectId PlanPatternTemplate(Database db, Params p)
        {
            string name = p.Pattern!;
            PatternKind kind = ClassifyPredefined(db, name);
            if (kind == PatternKind.Predefined)
            {
                return ObjectId.Null;
            }

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                ObjectId tpl = FindTemplateByName(db, tr, name);
                if (tpl.IsNull)
                {
                    if (kind == PatternKind.NotPredefined)
                    {
                        throw new ToolException(
                            ErrorCodes.NotFound,
                            "El patrón de sombreado '" + name + "' no existe.",
                            new Dictionary<string, object?>
                            {
                                ["patron"] = name,
                                ["detalle"] = "no está en acad.pat ni en acadiso.pat, ni en un HATCH del dibujo que se pueda copiar",
                                ["consejo"] = "use patron_de con el handle de un HATCH del modelo o de un layout que lo use.",
                            });
                    }

                    tr.Commit();
                    return ObjectId.Null;
                }

                var h = (Hatch)tr.GetObject(tpl, OpenMode.ForRead);
                if (h.PatternType == HatchPatternType.CustomDefined && (p.Scale != null || p.Angle != null))
                {
                    throw ToolException.InvalidParams(
                        CustomScaleAngle, new Dictionary<string, object?> { ["patron"] = name, ["handle"] = AcadUtil.HandleToString(h.Handle) });
                }

                tr.Commit();
                return tpl;
            }
        }

        /// <summary>Detalle de escala/angulo con un patrón CustomDefined (las líneas de definición ya lo llevan).</summary>
        public const string CustomScaleAngle =
            "escala/angulo: el patrón es CustomDefined (definido en el dibujo): su escala y ángulo están en las líneas de definición y se conservan del HATCH de origen; no se pueden cambiar";

        /// <summary>
        /// Motivo por el que un HATCH no puede servir de plantilla de copia segura, o null. Se rechaza: gradiente; dueño que
        /// no es el modelo ni un layout (definición de bloque o referencia externa: la copia a otro espacio cambiaría el
        /// sistema de coordenadas); capa bloqueada o de xref; contorno asociado en capa bloqueada/xref o dentro de bloque
        /// (el clon quita y reasocia bucles, y eso abriría objetos del origen).
        /// </summary>
        private static string? TemplateProblem(Transaction tr, Hatch h)
        {
            if (h.IsGradient)
            {
                return "es un sombreado de gradiente";
            }

            if (!IsModelOrLayoutOwner(tr, h.OwnerId))
            {
                return "está dentro de una definición de bloque o de una referencia externa; use un HATCH del espacio modelo o de un layout";
            }

            if (!h.LayerId.IsNull)
            {
                var layer = (LayerTableRecord)tr.GetObject(h.LayerId, OpenMode.ForRead, false);
                if (layer.IsDependent || layer.IsLocked || layer.Name.IndexOf('|') >= 0)
                {
                    return "está en una capa bloqueada o de referencia externa";
                }
            }

            if (h.Associative)
            {
                foreach (ObjectId bid in h.GetAssociatedObjectIds())
                {
                    if (bid.IsNull || bid.IsErased)
                    {
                        continue;
                    }

                    var boundary = tr.GetObject(bid, OpenMode.ForRead, false) as Entity;
                    if (boundary == null)
                    {
                        continue;
                    }

                    if (!IsModelOrLayoutOwner(tr, boundary.OwnerId))
                    {
                        return "tiene un contorno asociado dentro de un bloque o de una referencia externa";
                    }

                    if (!boundary.LayerId.IsNull)
                    {
                        var bl = (LayerTableRecord)tr.GetObject(boundary.LayerId, OpenMode.ForRead, false);
                        if (bl.IsLocked || bl.IsDependent)
                        {
                            return "tiene un contorno asociado en una capa bloqueada o de referencia externa";
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>true si el dueño es el espacio modelo o un layout (no un bloque ni una referencia externa).</summary>
        private static bool IsModelOrLayoutOwner(Transaction tr, ObjectId owner)
        {
            if (owner.IsNull || owner.IsErased)
            {
                return false;
            }

            var btr = tr.GetObject(owner, OpenMode.ForRead, false) as BlockTableRecord;
            if (btr == null || btr.IsFromExternalReference)
            {
                return false;
            }

            return btr.IsLayout || string.Equals(btr.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// patron_de: el handle existe (NOT_FOUND), es un HATCH y puede copiarse con seguridad (INVALID_PARAMS con el motivo),
        /// y con un patrón CustomDefined no se pide escala ni ángulo. Lectura sin efectos.
        /// </summary>
        private static ObjectId CheckTemplate(Database db, string handle, Params p)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                Entity e = AcadUtil.TryGetEntityByHandle(db, handle, tr) ?? throw ToolException.EntityNotFound(handle);
                var data = new Dictionary<string, object?> { ["handle"] = handle };
                if (!(e is Hatch h))
                {
                    throw ToolException.InvalidParams("patron_de: la entidad " + handle + " no es un HATCH", data);
                }

                string? motivo = TemplateProblem(tr, h);
                if (motivo != null)
                {
                    throw ToolException.InvalidParams("patron_de: el HATCH " + handle + " " + motivo, data);
                }

                if (h.PatternType == HatchPatternType.CustomDefined && (p.Scale != null || p.Angle != null))
                {
                    throw ToolException.InvalidParams(CustomScaleAngle, data);
                }

                ObjectId id = e.ObjectId;
                tr.Commit();
                return id;
            }
        }

        /// <summary>
        /// Capa de una entidad de contorno: bloqueada → INVALID_PARAMS <see cref="HatchBoundary.LockedLayer"/>;
        /// dependiente de xref → <see cref="HatchBoundary.XrefLayer"/> (data.handle, data.capa). Congelada o apagada
        /// no impide asociar el sombreado: no se rechaza.
        /// </summary>
        private static void RequireBoundaryLayer(Entity e, int i, string handle, Transaction tr)
        {
            if (e.LayerId.IsNull)
            {
                return;
            }

            var layer = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
            var data = new Dictionary<string, object?> { ["handle"] = handle, ["capa"] = layer.Name };
            if (layer.IsDependent || layer.Name.IndexOf('|') >= 0)
            {
                throw ToolException.InvalidParams(HatchBoundary.XrefLayer(i), data);
            }

            if (layer.IsLocked)
            {
                throw ToolException.InvalidParams(HatchBoundary.LockedLayer(i), data);
            }
        }

        /// <summary>
        /// LWPOLYLINE (criterio <see cref="HatchBoundary.IsClosedPolyline"/>), POLYLINE 2D (criterio de
        /// <see cref="AuditSupport.TryArea"/>: Closed o primero = último con ≥ 3 vértices), CIRCLE, ELLIPSE cerrada.
        /// </summary>
        private static bool IsClosedContour(Entity e)
        {
            try
            {
                switch (e)
                {
                    case Polyline pl:
                        var verts = new List<(double x, double y)>(pl.NumberOfVertices);
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            Point2d v = pl.GetPoint2dAt(i);
                            verts.Add((v.X, v.Y));
                        }

                        return HatchBoundary.IsClosedPolyline(pl.Closed, verts);
                    case Polyline2d p2:
                        return AuditSupport.TryArea(p2, out _);
                    case Circle _:
                        return true;
                    case Ellipse el:
                        return el.Closed;
                    default:
                        return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Último paso de la API de Hatch alcanzado en Write; se registra si AutoCAD lanza (diagnóstico del gate).
        private static string Paso = "";

        private static object Write(Document doc, Params p, List<ObjectId>? ids, ObjectId templateId, int pid, int ins, List<string> warnings)
        {
            Paso = "inicio";
            try
            {
                return WriteCore(doc, p, ids, templateId, pid, ins, warnings);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                PluginLog.Error("hatch: AutoCAD falló en el paso '" + Paso + "'", ex);
                throw;
            }
        }

        private static object WriteCore(Document doc, Params p, List<ObjectId>? ids, ObjectId templateId, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Common.Capa, DefaultConcept, warnings);
                BlockTableRecord space = PrimitiveSupport.OpenForWrite(tr, resolved);

                Hatch hatch;
                string? copiadoDe = p.PatronDe;
                if (!templateId.IsNull)
                {
                    Paso = "CloneTemplate";
                    hatch = CloneFromTemplate(tr, space, db, layer, p, templateId, out copiadoDe);
                }
                else
                {
                    Paso = "AppendEntity";
                    var created = new Hatch();
                    DrawWrite.Append(space, tr, created);
                    DrawWrite.ApplyByLayer(created, db, layer.Id);   // SetDatabaseDefaults + capa + ByLayer
                    EntityColor.Apply(created, p.Color);

                    // Normal (+Z), Elevation (0) y HatchStyle (Normal) son los valores por defecto de un Hatch nuevo.
                    string patternName = p.IsSolid ? Solid : p.Pattern!.ToUpperInvariant();
                    double scale = p.Scale ?? 1.0;
                    if (!p.IsSolid)
                    {
                        Paso = "PatternScale";
                        created.PatternScale = scale;
                    }

                    Paso = "SetHatchPattern";
                    if (TrySetPredefined(created, patternName, out string detail))
                    {
                        // Gate de Fase 3: PatternAngle ANTES de SetHatchPattern lanza eInvalidInput en AutoCAD 2026
                        // (PatternScale no). El ángulo se fija con el patrón ya asignado y se vuelve a llamar a
                        // SetHatchPattern para que las líneas del patrón se reconstruyan con él. SOLID no tiene ángulo.
                        if (!p.IsSolid && (p.Angle ?? 0.0) != 0.0)
                        {
                            Paso = "PatternAngle";
                            created.PatternAngle = Angles.ToRadians(p.Angle!.Value);
                            created.SetHatchPattern(HatchPatternType.PreDefined, patternName);
                        }

                        hatch = created;
                    }
                    else
                    {
                        // No es predefinido: ¿lo define algún HATCH del dibujo? Si no, NOT_FOUND (y nada se escribe).
                        ObjectId tpl = p.IsSolid ? ObjectId.Null : FindTemplateByName(db, tr, p.Pattern!);
                        if (tpl.IsNull)
                        {
                            throw new ToolException(
                                ErrorCodes.NotFound,
                                "El patrón de sombreado '" + p.Pattern + "' no existe.",
                                new Dictionary<string, object?>
                                {
                                    ["patron"] = p.Pattern,
                                    ["detalle"] = detail,
                                    ["consejo"] = "No está en acad.pat/acadiso.pat ni en un HATCH del dibujo; use patron_de con el handle de un HATCH que lo use.",
                                });
                        }

                        // Descartar el HATCH provisional (no predefinido) y clonar el del dibujo. Un objeto recién
                        // añadido ya está abierto para escritura: UpgradeOpen solo si hace falta (si no, eWasOpenForWrite).
                        Paso = "EraseProvisional";
                        if (!created.IsWriteEnabled)
                        {
                            created.UpgradeOpen();
                        }

                        created.Erase();
                        Paso = "CloneTemplate";
                        hatch = CloneFromTemplate(tr, space, db, layer, p, tpl, out copiadoDe);
                    }
                }

                if (hatch.Associative != (ids != null))
                {
                    hatch.Associative = ids != null;
                }

                if (ids != null)
                {
                    for (int i = 0; i < ids.Count; i++)
                    {
                        ObjectId id = ids[i];
                        if (id.IsNull || id.IsErased)
                        {
                            throw ToolException.EntityNotFound(p.Handles![i]);
                        }

                        Paso = "AppendLoop(handles)";
                        hatch.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { id });
                    }
                }
                else
                {
                    List<(double x, double y)> v = p.Vertices!;
                    var pts = new Point2dCollection();
                    var bulges = new DoubleCollection();
                    foreach ((double x, double y) in v)
                    {
                        pts.Add(new Point2d(x, y));
                        bulges.Add(0.0);
                    }

                    // Cierre explícito del bucle: se repite el primer vértice.
                    pts.Add(new Point2d(v[0].x, v[0].y));
                    bulges.Add(0.0);
                    Paso = "AppendLoop(vertices)";
                    hatch.AppendLoop(HatchLoopTypes.External | HatchLoopTypes.Polyline, pts, bulges);
                }

                Paso = "EvaluateHatch";
                hatch.EvaluateHatch(true);
                Paso = "respuesta";

                string handle = AcadUtil.HandleToString(hatch.Handle);
                bool solid = hatch.PatternType == HatchPatternType.PreDefined && string.Equals(hatch.PatternName, Solid, StringComparison.OrdinalIgnoreCase);
                var entidad = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = handle,
                    ["patron"] = SafePatternName(hatch, p.Pattern ?? Solid),
                    ["tipo_patron"] = hatch.PatternType.ToString(),
                    ["patron_de"] = copiadoDe,
                    ["escala"] = solid ? (p.Scale ?? 1.0) : PrimitiveSupport.TryNumber(() => hatch.PatternScale),
                    ["angulo"] = solid ? 0.0 : PrimitiveSupport.TryNumber(() => AcadUtil.ToDegrees360(hatch.PatternAngle)),
                    ["area"] = PrimitiveSupport.TryNumber(() => hatch.Area),
                    ["n_bucles"] = hatch.NumberOfLoops,
                    ["asociativo"] = ids != null,
                    ["contorno_handles"] = p.Handles,
                    ["bbox"] = PrimitiveSupport.Box2D(hatch),
                };

                tr.Commit();
                Dictionary<string, object?> r = DrawWrite.Response(pid, new List<string> { handle }, layer, resolved.Name, ins, warnings);
                r["entidad"] = entidad;
                return r;
            }
        }

        /// <summary>
        /// Intenta SetHatchPattern(PreDefined, name) (acad.pat / acadiso.pat). false con el detalle de la excepción si no
        /// existe. No lanza: el llamador decide si hay alternativa (patrón del dibujo) o NOT_FOUND.
        /// </summary>
        private static bool TrySetPredefined(Hatch h, string name, out string detail)
        {
            try
            {
                h.SetHatchPattern(HatchPatternType.PreDefined, name);
                detail = "";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Busca el primer HATCH del dibujo, en el modelo o en un layout (no en definiciones de bloque ni referencias
        /// externas), cuyo patrón se llama <paramref name="name"/> (sin distinguir mayúsculas), no es sólido ni gradiente
        /// y pasa <see cref="TemplateProblem"/>. Salta los ids borrados. Devuelve su ObjectId o Null.
        /// </summary>
        private static ObjectId FindTemplateByName(Database db, Transaction tr, string name)
        {
            Autodesk.AutoCAD.Runtime.RXClass hatchClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Hatch));
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (!(btr.IsLayout || string.Equals(btr.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                    || btr.IsFromExternalReference)
                {
                    continue;
                }

                foreach (ObjectId id in btr)
                {
                    if (id.IsErased || !id.ObjectClass.IsDerivedFrom(hatchClass))
                    {
                        continue;
                    }

                    var h = (Hatch)tr.GetObject(id, OpenMode.ForRead, false);
                    if (!h.IsSolidFill && string.Equals(h.PatternName, name, StringComparison.OrdinalIgnoreCase)
                        && TemplateProblem(tr, h) == null)
                    {
                        return id;
                    }
                }
            }

            return ObjectId.Null;
        }

        /// <summary>
        /// Clona un HATCH existente (copia sus líneas de definición del patrón, tipo, origen, escala, ángulo y color),
        /// quita sus bucles, le pone la capa de la llamada y, si se pasan, sustituye escala/ángulo/color. El color
        /// copiado se conserva salvo que se pase color/color_rgb.
        /// </summary>
        /// <remarks>
        /// Copia con DeepCloneObjects al espacio destino (la maquinaria de COPY, como ModifySupport.CopyAll), NO con
        /// Entity.Clone(): el clon no hereda contorno ajeno. Después: Normal = +Z y Elevation = 0 (el origen podría tener
        /// normal −Z o elevación, y el clon quedaría espejado/desplazado), Associative = false ANTES de quitar los bucles
        /// (así no se abre el contorno del origen), capa y color. Gate en vivo pendiente: origen asociativo con contorno en
        /// capa bloqueada (rechazado antes), origen dentro de bloque (rechazado), origen no asociativo, y CTRL+Z deja 0
        /// entidades si falla. Con <c>DrawWrite.ApplyByLayer</c> el clon vuelve a tipo de línea, grosor y capa ByLayer (PorCapa);
        /// el color sí se conserva del origen salvo que se pase color/color_rgb.
        /// </remarks>
        private static Hatch CloneFromTemplate(
            Transaction tr, BlockTableRecord space, Database db, DrawLayerFactory.Result layer, Params p, ObjectId templateId, out string copiadoDe)
        {
            var tpl = (Hatch)tr.GetObject(templateId, OpenMode.ForRead);
            copiadoDe = AcadUtil.HandleToString(tpl.Handle);
            Color tplColor = tpl.Color;
            bool custom = tpl.PatternType == HatchPatternType.CustomDefined;
            if (custom && (p.Scale != null || p.Angle != null))
            {
                throw ToolException.InvalidParams(CustomScaleAngle, new Dictionary<string, object?> { ["handle"] = p.PatronDe });
            }

            Paso = "DeepCloneObjects";
            var mapping = new IdMapping();
            db.DeepCloneObjects(new ObjectIdCollection { templateId }, space.ObjectId, mapping, false);
            IdPair pair = mapping[templateId];
            if (!pair.IsCloned || pair.Value.IsNull)
            {
                throw new InvalidOperationException("patron_de: AutoCAD no copió el HATCH " + AcadUtil.HandleToString(tpl.Handle) + ".");
            }

            var hatch = (Hatch)tr.GetObject(pair.Value, OpenMode.ForWrite);

            Paso = "CloneNormalElevation";
            hatch.Normal = Vector3d.ZAxis;
            hatch.Elevation = 0.0;

            Paso = "CloneDisassociate";
            if (hatch.Associative)
            {
                hatch.Associative = false;
            }

            // Si el clon aún conserva objetos asociados (los del origen), se quitan antes de tocar sus bucles.
            Paso = "CloneRemoveAssociated";
            if (hatch.GetAssociatedObjectIds().Count > 0)
            {
                hatch.RemoveAssociatedObjectIds();
            }

            Paso = "CloneLoops";
            for (int i = hatch.NumberOfLoops - 1; i >= 0; i--)
            {
                hatch.RemoveLoopAt(i);
            }

            DrawWrite.ApplyByLayer(hatch, db, layer.Id);
            if (p.Color != null)
            {
                EntityColor.Apply(hatch, p.Color);
            }
            else
            {
                hatch.Color = tplColor;
            }

            if (!hatch.IsSolidFill && !custom)
            {
                if (p.Scale != null)
                {
                    Paso = "PatternScale(clone)";
                    hatch.PatternScale = p.Scale.Value;
                }

                if (p.Angle != null)
                {
                    Paso = "PatternAngle(clone)";
                    hatch.PatternAngle = Angles.ToRadians(p.Angle.Value);
                    if (hatch.PatternType == HatchPatternType.PreDefined || hatch.PatternType == HatchPatternType.UserDefined)
                    {
                        // Como en el caso predefinido: el ángulo se aplica con el patrón asignado y se reconstruyen sus líneas.
                        hatch.SetHatchPattern(hatch.PatternType, hatch.PatternName);
                    }
                }
            }

            return hatch;
        }

        private static string SafePatternName(Hatch h, string fallback)
        {
            try
            {
                string n = h.PatternName;
                return string.IsNullOrEmpty(n) ? fallback : n;
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
