using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// offset (protocol.md §5E.3.4): curva paralela a una existente a una distancia, del lado de un punto (PuntoRef) o a
    /// la izquierda/derecha del sentido de la curva. El plan es un <see cref="PlannedOffset"/>; el dibujante prueba
    /// GetOffsetCurves(±distancia) y elige el resultado más cercano al punto de lado. Capa: la del original salvo 'capa'.
    /// </summary>
    internal sealed class OffsetHandler : GeneratorBase
    {
        private const string Izquierda = "izquierda";
        private const string Derecha = "derecha";

        private static readonly string[] AllowedNames = BuildAllowed();

        public override string Name => "offset";

        public override string Description =>
            "ESCRIBE una curva paralela (offset) a una entidad existente (handle) a una distancia, del lado indicado (PuntoRef o izquierda|derecha), en la capa de la original o la indicada. Con preview: true solo calcula y devuelve números + preview_id.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            @"    ""handle"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle de la curva existente (LINE, LWPOLYLINE, POLYLINE, ARC, CIRCLE, ELLIPSE, SPLINE)."" },
    ""distancia"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Distancia de la paralela (> 0)."" },
    ""lado"": { ""description"": ""Lado del resultado: un PuntoRef (queda del lado más cercano a él) o 'izquierda' / 'derecha' respecto al sentido de la curva (del inicio al fin)."", ""oneOf"": [
      " + GenSchema.SchemaPuntoRef + @",
      { ""type"": ""string"", ""enum"": [""izquierda"", ""derecha""] }
    ] },
    ""capa"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Capa del resultado definitivo (concepto de lps.json, capa existente o capa del estándar / MCP-*). Por defecto, la capa de la curva original."" }",
            "handle", "distancia", "lado");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(
            @"        ""distancia"": { ""type"": ""number"" },
        ""longitud_original"": { ""type"": [""number"", ""null""] },
        ""lado_punto"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""tipo_entidad"": { ""type"": ""string"" }");

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => null;

        private static string[] BuildAllowed()
        {
            var list = new List<string> { "handle", "distancia", "lado", "capa" };
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }

        private sealed class Params
        {
            public Params(string handle, double distancia, PointRef? ladoRef, string? ladoNombre, string? capa, string? espacio)
            {
                Espacio = espacio;
                Handle = handle;
                Distancia = distancia;
                LadoRef = ladoRef;
                LadoNombre = ladoNombre;
                Capa = capa;
            }

            /// <summary>Handle en mayúsculas.</summary>
            public string Handle { get; }
            public double Distancia { get; }

            /// <summary>Lado como PuntoRef (excluyente con <see cref="LadoNombre"/>).</summary>
            public PointRef? LadoRef { get; }

            /// <summary>"izquierda" | "derecha" (excluyente con <see cref="LadoRef"/>).</summary>
            public string? LadoNombre { get; }
            public string? Capa { get; }

            /// <summary>Parámetro común 'espacio' (solo para avisar si destino y original no coinciden).</summary>
            public string? Espacio { get; }
        }

        protected override object ParseOwn(JsonElement p)
        {
            string handle = (ParamReader.OptionalHandle(p, "handle") ?? throw ToolException.InvalidParams("handle: obligatorio")).ToUpperInvariant();
            double distancia = ParamReader.RequiredNumber(p, "distancia", 0, double.PositiveInfinity, exclusiveMin: true);
            if (!p.TryGetProperty("lado", out JsonElement lado))
            {
                throw ToolException.InvalidParams("lado: obligatorio");
            }

            PointRef? ladoRef = null;
            string? ladoNombre = null;
            if (lado.ValueKind == JsonValueKind.String)
            {
                string v = lado.GetString() ?? "";
                if (v != Izquierda && v != Derecha)
                {
                    throw ToolException.InvalidParams("lado: debe ser un PuntoRef o uno de izquierda, derecha");
                }

                ladoNombre = v;
            }
            else
            {
                ladoRef = GenSchema.ReadPointRef(lado, "lado");
            }

            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, null);
            }

            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            return new Params(handle, distancia, ladoRef, ladoNombre, capa, espacio);
        }

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            var list = new List<(PointRef Ref, string Path)>(1);
            if (p.LadoRef != null)
            {
                list.Add((p.LadoRef, "lado"));
            }

            return list;
        }

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;
            Entity e = PointRefResolver.Require(db, tr, p.Handle);
            string tipo = AcadUtil.DxfName(e);
            if (e is not Curve curve)
            {
                throw ToolException.InvalidParams(
                    "handle: la entidad " + p.Handle + " (" + tipo + ") no es una curva: no admite offset",
                    new Dictionary<string, object?> { ["handle"] = p.Handle });
            }

            double? longitud = null;
            try
            {
                double len = curve.GetDistanceAtParameter(curve.EndParam);
                if (double.IsFinite(len))
                {
                    longitud = len;
                }
            }
            catch (Exception)
            {
                longitud = null;
            }

            Point3 ladoPunto = p.LadoRef != null
                ? puntos[0].Punto
                : SidePoint(curve, p.Handle, longitud, p.Distancia, p.LadoNombre == Izquierda);

            // Corrección del gate en vivo de Fase 4A (2026-10-27): la caja es la de la curva ELEGIDA, construida aquí con el
            // mismo criterio que el dibujante (GeometryDrawer.BuildOffset → OffsetChoice), no la de la original ampliada la
            // distancia (que abarcaba las DOS paralelas candidatas). Sirve para R4 y para numeros.bbox (= entidades[0].bbox
            // tras dibujar). Si AutoCAD no construye la paralela se rechaza ya en la lectura (nada escrito, tampoco la preview).
            BBox2? cajaResultado = ChosenBox(curve, p.Handle, p.Distancia, ladoPunto);

            // El resultado se añade al espacio destino; si la original vive en otro, se avisa (no es error, §5E.0 G5).
            string? origen = SpaceResolver.OwnerName(e, tr);
            string destino = SpaceResolver.Resolve(db, tr, p.Espacio).Name;
            if (origen != null && !string.Equals(origen, destino, StringComparison.OrdinalIgnoreCase))
            {
                advertencias.Add("offset: la curva " + p.Handle + " está en '" + origen + "' y la paralela se crea en '" + destino + "' (parámetro espacio).");
            }

            // Sin 'capa' la paralela HEREDA la capa del original: se le aplica R3 (§5D.0) como a cualquier capa destino,
            // ya en la lectura (también en la preview, para que confirmar no falle después).
            if (p.Capa == null)
            {
                CheckInheritedLayer(e, p.Handle, tr, advertencias);
            }

            var entidades = new List<PlannedEntity> { new PlannedOffset(p.Handle, p.Distancia, ladoPunto, cajaResultado, null, p.Capa) };
            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["distancia"] = p.Distancia,
                ["longitud_original"] = longitud,
                ["lado_punto"] = ladoPunto.ToArray(),
                ["tipo_entidad"] = tipo,
                ["bbox"] = cajaResultado?.ToArray2D(),
            };
            if (p.LadoNombre != null)
            {
                numeros["lado"] = p.LadoNombre;
            }

            var geo = new GeneratedGeometry(entidades, numeros);
            geo.AnclasExtra.Add(PointRefResolver.Fingerprint(e, p.Handle));
            return geo;
        }

        /// <summary>
        /// Caja de la paralela que se dibujará (unión de sus piezas): se construye con
        /// <see cref="GeometryDrawer.BuildOffset"/> (objetos NO residentes, liberados aquí). Sin resultado →
        /// INVALID_GEOMETRY de §5E.3.4. null solo si ninguna pieza tiene extensión legible (R4 no la comprobará).
        /// </summary>
        private static BBox2? ChosenBox(Curve curve, string handle, double distancia, Point3 lado)
        {
            DBObjectCollection? elegida = GeometryDrawer.BuildOffset(curve, handle, distancia, AcadUtil.ToPoint3d(lado));
            if (elegida == null)
            {
                throw GeometryDrawer.OffsetNotBuilt(handle, distancia);
            }

            try
            {
                var cajas = new List<BBox2?>(elegida.Count);
                foreach (DBObject o in elegida)
                {
                    if (o is Entity pieza)
                    {
                        cajas.Add(AcadUtil.TryBBox2(pieza));
                    }
                }

                return OffsetChoice.UnionOf(cajas);
            }
            finally
            {
                GeometryDrawer.DisposeAll(elegida);
            }
        }

        /// <summary>
        /// R3 sobre la capa heredada del original (offset sin 'capa'): dependiente de xref, bloqueada o congelada →
        /// INVALID_PARAMS sugiriendo 'capa' (data.capa, data.handle); apagada → advertencia. El original nunca se modifica.
        /// </summary>
        private static void CheckInheritedLayer(Entity e, string handle, Transaction tr, List<string> advertencias)
        {
            if (tr.GetObject(e.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                return;
            }

            string nombre = layer.Name;
            var data = new Dictionary<string, object?> { ["capa"] = nombre, ["handle"] = handle };
            if (layer.IsDependent || nombre.IndexOf('|') >= 0)
            {
                throw ToolException.InvalidParams(InheritedLayerDetail(handle, "dependiente de xref", nombre) + "; indique capa con otra capa de destino", data);
            }

            if (layer.IsLocked)
            {
                throw ToolException.InvalidParams(InheritedLayerDetail(handle, "bloqueada", nombre) + "; indique capa con otra capa de destino o desbloquee '" + nombre + "'", data);
            }

            if (layer.IsFrozen)
            {
                throw ToolException.InvalidParams(InheritedLayerDetail(handle, "congelada", nombre) + "; indique capa con otra capa de destino o descongele '" + nombre + "'", data);
            }

            if (layer.IsOff)
            {
                advertencias.Add("offset: la curva " + handle + " está en la capa apagada '" + nombre + "' y la paralela hereda esa capa: no será visible (indique capa para otra capa de destino).");
            }
        }

        private static string InheritedLayerDetail(string handle, string estado, string capa) =>
            "capa: la curva " + handle + " está en la capa " + estado + " '" + capa + "' y la paralela heredaría esa capa (regla R3)";

        /// <summary>
        /// Punto de lado para 'izquierda'/'derecha': punto medio de la curva (GetPointAtDist(L/2)) desplazado
        /// <paramref name="distancia"/> según la normal izquierda (−ty, tx) o derecha (ty, −tx) de la tangente unitaria.
        /// </summary>
        private static Point3 SidePoint(Curve curve, string handle, double? longitud, double distancia, bool izquierda)
        {
            string error = "lado: no se pudo determinar la tangente de " + handle;
            Point3d mid;
            Vector3d tan;
            try
            {
                mid = longitud != null ? curve.GetPointAtDist(longitud.Value / 2.0) : curve.GetPointAtParameter((curve.StartParam + curve.EndParam) / 2.0);
                tan = curve.GetFirstDerivative(mid);
            }
            catch (Exception)
            {
                throw ToolException.InvalidGeometry(error, new Dictionary<string, object?> { ["handle"] = handle });
            }

            double tx = tan.X, ty = tan.Y;
            double norm = Math.Sqrt((tx * tx) + (ty * ty));
            if (!double.IsFinite(norm) || norm < 1e-12)
            {
                throw ToolException.InvalidGeometry(error, new Dictionary<string, object?> { ["handle"] = handle });
            }

            tx /= norm;
            ty /= norm;
            double nx = izquierda ? -ty : ty;
            double ny = izquierda ? tx : -tx;
            return new Point3(mid.X + (distancia * nx), mid.Y + (distancia * ny), mid.Z);
        }
    }
}
