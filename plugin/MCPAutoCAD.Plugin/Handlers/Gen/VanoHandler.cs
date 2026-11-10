using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;
using DrawLibrary = MCPAutoCAD.Plugin.Handlers.Draw.BlockLibrary;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// vano (protocol.md §5G.1): parte el contorno cerrado de un muro en dos contornos cerrados (<see cref="WallOpening.Cut"/>),
    /// o las dos caras LINE de un muro en cuatro con dos tapas (<see cref="LineWallOpening.Cut"/>), y coloca el bloque de puerta o ventana (<see cref="OpeningPlacement"/>, catálogo <see cref="VanoCatalog"/>).
    /// Database.Insert no se revierte con el Abort de la transacción (§5D.5), así que Plan valida antes de escribir todo lo
    /// que puede: capas de destino y, si la definición viene de la biblioteca, su capa homónima y si es dinámica.
    /// Todas las entidades del plan llevan concepto o capa, de modo que <see cref="DefaultConcept"/> no decide nada aquí:
    /// la propiedad no recibe los parámetros y el INSERT usa el concepto de su entrada del catálogo.
    /// capas_importadas y propiedades_dinamicas van en la raíz de la respuesta (<see cref="AddResponseFields"/>, §5G.1).
    /// </summary>
    internal sealed class VanoHandler : GeneratorBase
    {
        private const int MaxDynamicProperties = 20;
        private const int MaxReferencesProbed = 8;
        private const double ThicknessTol = 1e-3;
        private const string AnchoPendiente = "pendiente";

        /// <summary>Clave del diccionario de extensión que distingue a un DWG exportado de un bloque dinámico (§5G.0).</summary>
        private const string EnhancedBlockKey = "ACAD_ENHANCEDBLOCK";

        private const string SchemaCaraOpuesta =
            @"{ ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Solo si muro es una LINE: handle de la otra cara del muro, también una LINE. Si falta, se busca la LINE paralela más cercana de la misma capa que solape el muro a 0,40 o menos."" }";

        private static readonly string[] Tipos = { VanoCatalog.TipoPuerta, VanoCatalog.TipoVentana, VanoCatalog.TipoHueco };
        private static readonly string[] Caras = { "izquierda", "derecha" };
        private static readonly string[] Bisagras = { "inicio", "fin" };
        private static readonly string[] DistanciaProps = { "distancia", "desde" };
        private static readonly string[] AllowedNames = BuildAllowed();

        public override string Name => "vano";

        public override string Description =>
            "ESCRIBE un vano (puerta, ventana o hueco) en un muro existente dado por el handle de su contorno cerrado (polilínea de 'muro' o rectángulo de la oficina) o de una de sus caras LINE (con cara_opuesta, o buscando la LINE paralela más cercana de la misma capa a 0,40 o menos): parte el contorno en dos contornos cerrados, o las dos líneas en cuatro con dos tapas, con tapa de jamba a cada lado del hueco (rehace sus sombreados asociativos) e inserta el bloque de biblioteca (Puerta Dinamica LM / VENTANA01 por defecto, o 'bloque') centrado y orientado con el muro, con el ancho fijado por su propiedad dinámica (o escalado con advertencia). Rechaza huecos que se salen del tramo, cruzan una esquina u otro vano. El muro es del usuario: exige confirmar: true si no está en una capa MCP-*. Con preview: true solo calcula y devuelve números + preview_id (no modifica el muro hasta confirmar).";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            @"    ""muro"": " + ParamReader.SchemaHandle + @",
    ""cara_opuesta"": " + SchemaCaraOpuesta + @",
    ""posicion"": { ""description"": ""Centro del hueco: un PuntoRef (se proyecta sobre la línea media del tramo recto más cercano) o una distancia sobre el eje, solo en contornos de 4 vértices."", ""oneOf"": [
      " + GenSchema.SchemaPuntoRef + @",
      { ""type"": ""object"", ""properties"": {
        ""distancia"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""Distancia sobre el eje desde el extremo elegido."" },
        ""desde"": { ""type"": ""string"", ""enum"": [""inicio"", ""fin""], ""default"": ""inicio"", ""description"": ""inicio = extremo de menor x (a igual x, menor y); fin = el otro."" }
      }, ""required"": [""distancia""], ""additionalProperties"": false }
    ] },
    ""ancho"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Ancho del hueco a lo largo del muro, de jamba exterior a jamba exterior."" },
    ""tipo"": { ""type"": ""string"", ""enum"": [""puerta"", ""ventana"", ""hueco""], ""description"": ""hueco = solo parte el contorno, sin bloque."" },
    ""bloque"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Bloque del dibujo o de la biblioteca, presente en lps.json biblioteca_vanos. Por defecto, la primera entrada del tipo. No aplica con tipo hueco."" },
    ""cara_apertura"": { ""type"": ""string"", ""enum"": [""izquierda"", ""derecha""], ""default"": ""izquierda"", ""description"": ""Cara del muro hacia la que abre la hoja, respecto a la dirección del tramo (numeros.hueco.direccion); el punto base del bloque va en la cara opuesta. En ventanas solo decide la cara del punto base."" },
    ""bisagra"": { ""type"": ""string"", ""enum"": [""inicio"", ""fin""], ""default"": ""inicio"", ""description"": ""Esquina del hueco donde va el punto base: la más cercana al inicio o al fin del eje."" },
    ""propiedades_dinamicas"": { ""type"": ""object"", ""minProperties"": 1, ""maxProperties"": 20, ""additionalProperties"": { ""type"": [""number"", ""string""] }, ""description"": ""Otras propiedades del bloque dinámico (nombre sin distinguir mayúsculas → número o cadena), p. ej. visibilidad. El ancho no se pasa aquí: lo fija la herramienta."" },
    ""confirmar"": " + WallEntitySupport.SchemaConfirmar + @",
    ""capa"": " + DrawWrite.SchemaCapa,
            "muro", "posicion", "ancho", "tipo");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(
            @"        ""hueco"": { ""type"": ""object"", ""properties"": {
          ""centro"": { ""type"": ""array"" }, ""a1"": { ""type"": ""array"" }, ""a2"": { ""type"": ""array"" }, ""b1"": { ""type"": ""array"" }, ""b2"": { ""type"": ""array"" },
          ""ancho"": { ""type"": ""number"" }, ""espesor"": { ""type"": ""number"" },
          ""direccion"": { ""type"": ""number"", ""description"": ""Grados antihorarios desde +X."" },
          ""distancia_desde_inicio"": { ""type"": ""number"" }, ""longitud_tramo"": { ""type"": ""number"" }
        } },
        ""contornos"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
          ""lado"": { ""type"": ""string"" }, ""n_vertices"": { ""type"": ""integer"" }, ""longitud"": { ""type"": ""number"" }, ""area"": { ""type"": ""number"" }
        } } },
        ""bloque"": { ""type"": [""object"", ""null""], ""description"": ""{nombre, catalogo, posicion, rotacion, escala, espejo, ancho_por, propiedad_ancho, factor_ancho}; null con tipo hueco."" },
        ""muro_original"": { ""type"": ""object"", ""properties"": {
          ""handle"": { ""type"": ""string"" }, ""capa"": { ""type"": ""string"" }, ""area"": { ""type"": ""number"" }, ""n_vertices"": { ""type"": ""integer"" }
        } },
        ""n_hatch_rehechos"": { ""type"": ""integer"" }",
            @"    ""capas_importadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Capas que trajo el bloque; solo en la llamada que lo importó de la biblioteca."" },
    ""propiedades_dinamicas"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""Propiedades de la referencia creada, leídas tras aplicar; vacío si no se creó INSERT o no es dinámico."" },");

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => "muros";

        /// <summary>§5G.1: en la raíz, igual en vano y en confirmar; capas_importadas solo en la llamada que importó la definición.</summary>
        protected override void AddResponseFields(Dictionary<string, object?> response, GeometryDrawer.Result drawn)
        {
            if (drawn.DefinicionImportada)
            {
                response["capas_importadas"] = drawn.CapasImportadas;
            }

            response["propiedades_dinamicas"] = drawn.PropiedadesDinamicas;

            if (response["numeros"] is not Dictionary<string, object?> numeros
                || numeros["bloque"] is not Dictionary<string, object?> bloque
                || !string.Equals(bloque["ancho_por"] as string, AnchoPendiente, StringComparison.Ordinal))
            {
                return;
            }

            GeometryDrawer.ResolvedWidth? resuelto = drawn.AnchoResuelto;
            if (resuelto == null)
            {
                // Previsualización sin dibujar: no hay referencia y la decisión queda para confirmar.
                ((List<string>)response["advertencias"]!).Add(DynamicPropertyRules.PendingWidthWarning((string)bloque["nombre"]!));
                return;
            }

            numeros["bloque"] = new Dictionary<string, object?>(bloque, StringComparer.Ordinal)
            {
                ["ancho_por"] = resuelto.Propiedad != null ? "propiedad" : "escala",
                ["propiedad_ancho"] = resuelto.Propiedad,
                ["factor_ancho"] = resuelto.FactorAncho,
                ["escala"] = resuelto.Escala,
            };
        }

        private static string[] BuildAllowed()
        {
            var list = new List<string> { "muro", "cara_opuesta", "posicion", "ancho", "tipo", "bloque", "cara_apertura", "bisagra", "propiedades_dinamicas", "confirmar", "capa" };
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }

        private sealed record Params(
            string Muro,
            string? CaraOpuesta,
            PointRef? PosicionRef,
            double? Distancia,
            bool DesdeFin,
            double Ancho,
            string Tipo,
            string? Bloque,
            string CaraApertura,
            string Bisagra,
            Dictionary<string, object>? PropiedadesDinamicas,
            bool Confirmar,
            string? Capa,
            string? Espacio);

        /// <summary>Definición del bloque resuelta en la lectura: nombre real, si hay que importarla y si es dinámica.</summary>
        private sealed record Definition(string Nombre, string? Archivo, ObjectId Id, bool Dinamica);

        protected override object ParseOwn(JsonElement p)
        {
            string muro = p.TryGetProperty("muro", out JsonElement muroEl)
                ? AxisReader.ReadHandle(muroEl, "muro")
                : throw ToolException.InvalidParams("muro: obligatorio");
            string? caraOpuesta = p.TryGetProperty("cara_opuesta", out JsonElement caraEl) ? AxisReader.ReadHandle(caraEl, "cara_opuesta") : null;
            if (caraOpuesta != null && string.Equals(caraOpuesta, muro, StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidParams("cara_opuesta: debe ser distinta de muro");
            }

            if (!p.TryGetProperty("posicion", out JsonElement posEl))
            {
                throw ToolException.InvalidParams("posicion: obligatorio");
            }

            PointRef? posRef = null;
            double? distancia = null;
            bool desdeFin = false;
            if (posEl.ValueKind == JsonValueKind.Object && posEl.TryGetProperty("distancia", out JsonElement distEl))
            {
                ParamReader.RejectUnknown(posEl, DistanciaProps, "posicion.");
                if (distEl.ValueKind != JsonValueKind.Number || !distEl.TryGetDouble(out double d) || !double.IsFinite(d) || d < 0)
                {
                    throw ToolException.InvalidParams("posicion.distancia: debe ser un número mayor o igual que 0");
                }

                distancia = d;
                desdeFin = ParamReader.OptionalEnum(posEl, "desde", Bisagras) == "fin";
            }
            else
            {
                posRef = GenSchema.ReadPointRef(posEl, "posicion");
            }

            double ancho = ParamReader.RequiredNumber(p, "ancho", 0, double.PositiveInfinity, exclusiveMin: true);
            string tipo = ParamReader.OptionalEnum(p, "tipo", Tipos) ?? throw ToolException.InvalidParams("tipo: obligatorio");
            bool hueco = tipo == VanoCatalog.TipoHueco;

            string? bloque = ParamReader.OptionalString(p, "bloque", 1, 255);
            if (bloque != null)
            {
                if (hueco)
                {
                    throw ToolException.InvalidParams("bloque: no aplica con tipo hueco");
                }

                string? error = BlockNameRules.Validate(bloque);
                if (error != null)
                {
                    throw ToolException.InvalidParams(error, new Dictionary<string, object?> { ["bloque"] = bloque });
                }
            }

            Dictionary<string, object>? propiedades = ReadDynamicProperties(p);
            if (hueco && propiedades != null)
            {
                throw ToolException.InvalidParams("propiedades_dinamicas: no aplica con tipo hueco");
            }

            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, tipo == VanoCatalog.TipoVentana ? "ventanas" : hueco ? "muros" : "puertas");
            }

            return new Params(
                muro, caraOpuesta, posRef, distancia, desdeFin, ancho, tipo, bloque,
                ParamReader.OptionalEnum(p, "cara_apertura", Caras) ?? "izquierda",
                ParamReader.OptionalEnum(p, "bisagra", Bisagras) ?? "inicio",
                propiedades,
                ParamReader.OptionalBool(p, "confirmar") ?? false,
                capa,
                ParamReader.OptionalString(p, "espacio", 1, 255));
        }

        /// <summary>propiedades_dinamicas: objeto nombre → número finito o cadena, con 1 a 20 entradas; null si no se dio.</summary>
        private static Dictionary<string, object>? ReadDynamicProperties(JsonElement p)
        {
            if (!p.TryGetProperty("propiedades_dinamicas", out JsonElement el))
            {
                return null;
            }

            string forma = "propiedades_dinamicas: debe ser un objeto con 1 a " + MaxDynamicProperties.ToString(CultureInfo.InvariantCulture) + " propiedades";
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams(forma);
            }

            var valores = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                if (prop.Name.Trim().Length == 0)
                {
                    throw ToolException.InvalidParams("propiedades_dinamicas: propiedad con nombre vacío");
                }

                if (valores.ContainsKey(prop.Name))
                {
                    throw ToolException.InvalidParams("propiedades_dinamicas: propiedad repetida '" + prop.Name + "'");
                }

                valores[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number when prop.Value.TryGetDouble(out double d) && double.IsFinite(d) => d,
                    _ => throw ToolException.InvalidParams("propiedades_dinamicas." + prop.Name + ": debe ser un número o una cadena"),
                };
            }

            if (valores.Count < 1 || valores.Count > MaxDynamicProperties)
            {
                throw ToolException.InvalidParams(forma);
            }

            return valores;
        }

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            return p.PosicionRef == null
                ? Array.Empty<(PointRef Ref, string Path)>()
                : new[] { (p.PosicionRef, "posicion") };
        }

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;
            if (PointRefResolver.Require(db, tr, p.Muro) is Line lineaMuro)
            {
                return PlanLineWall(p, lineaMuro, puntos, db, tr, advertencias);
            }

            if (p.CaraOpuesta != null)
            {
                throw ToolException.InvalidParams("cara_opuesta: solo aplica si muro es una LINE");
            }

            HandleGuard.Resolved guard = HandleGuard.Resolve(db, tr, new[] { p.Muro }, p.Confirmar, advertencias);
            WallEntitySupport.CheckSpace(db, tr, guard, p.Espacio);
            WallEntitySupport.ClosedContour muro = WallEntitySupport.ReadClosedContour(db, tr, p.Muro, "muro", advertencias);

            (double x, double y) centro = p.PosicionRef != null
                ? (puntos[0].Punto.X, puntos[0].Punto.Y)
                : WallEntitySupport.Geometry(() => WallOpening.CenterFromDistance(muro.Vertices, p.Distancia!.Value, p.DesdeFin));
            WallOpening.OpeningResult hueco = WallEntitySupport.Geometry(() => WallOpening.Cut(muro.Vertices, centro, p.Ancho));
            (PlannedBlockRef? insert, Dictionary<string, object?>? numerosBloque) = PlanOptionalBlock(p, hueco, db, tr, advertencias);

            // Comprobaciones de R3 antes de escribir: los contornos conservan la capa del muro, que puede estar congelada.
            DrawLayerFactory.Check(db, tr, muro.Capa, "muros", out _);
            WallEntitySupport.HatchRebuild rehacer = WallEntitySupport.PlanHatchRebuild(
                db, tr, WallEntitySupport.AssociatedHatches(tr, muro.Entidad), p.Confirmar, advertencias);
            WallEntitySupport.WarnHoleOverlaps(db, tr, muro.Entidad.BlockId, hueco, rehacer.Hatches.ConvertAll(h => h.Handle), advertencias);

            var entidades = new List<PlannedEntity> { new PlannedErase(muro.Handle, muro.Tipo) };
            entidades.AddRange(rehacer.Borrados);
            int nBorrados = entidades.Count;
            var ladoInicio = new PlannedPolyline(hueco.LadoInicio, true, null, 0.0, "muros", muro.Capa);
            var ladoFin = new PlannedPolyline(hueco.LadoFin, true, null, 0.0, "muros", muro.Capa);
            entidades.Add(ladoInicio);
            entidades.Add(ladoFin);
            foreach (WallEntitySupport.AssociatedHatch h in rehacer.Hatches)
            {
                if (h.SoloEsteContorno)
                {
                    entidades.Add(h.ToPlanned(nBorrados));
                    entidades.Add(h.ToPlanned(nBorrados + 1));
                }
                else
                {
                    // Conserva sus otros bucles en un solo sombreado (§5G.0).
                    entidades.Add(h.ReplacingWallLoops(nBorrados, nBorrados + 1));
                }
            }

            if (insert != null)
            {
                entidades.Add(insert);
            }

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hueco"] = OpeningNumbers(hueco, p.Ancho),
                ["contornos"] = new List<Dictionary<string, object?>> { ContourNumbers("inicio", ladoInicio), ContourNumbers("fin", ladoFin) },
                ["lineas"] = new List<Dictionary<string, object?>>(),
                ["bloque"] = numerosBloque,
                ["muro_original"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = muro.Handle,
                    ["capa"] = muro.Capa,
                    ["area"] = PolygonMetrics.LengthAndArea(muro.Vertices, true).area,
                    ["n_vertices"] = muro.Vertices.Count,
                    ["cara_opuesta"] = null,
                },
                ["n_hatch_rehechos"] = rehacer.Hatches.Count,
                ["n_entidades"] = entidades.Count - nBorrados,   // solo lo creado, como entidades[] (§5G.0)
            };
            var geo = new GeneratedGeometry(entidades, numeros);
            geo.AnclasExtra.Add(muro.Huella);
            geo.AnclasExtra.AddRange(rehacer.Huellas);
            return geo;
        }

        /// <summary>
        /// §5G.1, muro de dos caras LINE: las parte en cuatro, conservando la capa y el aspecto de cada una, y añade dos tapas
        /// en la capa del muro. Mismas garantías que el contorno: confirmar sobre las dos, huella de las dos y un paso de deshacer.
        /// </summary>
        private GeneratedGeometry PlanLineWall(Params p, Line lineaMuro, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            WallEntitySupport.WallLine muro = WallEntitySupport.ReadWallLine(lineaMuro, p.Muro, "muro", advertencias);
            (double x, double y)? centroPedido = p.PosicionRef != null ? (puntos[0].Punto.X, puntos[0].Punto.Y) : null;
            WallEntitySupport.WallLine opuesta = ResolveOppositeFace(p, muro, centroPedido, db, tr, advertencias);

            HandleGuard.Resolved guard = HandleGuard.Resolve(db, tr, new[] { muro.Handle, opuesta.Handle }, p.Confirmar, advertencias);
            WallEntitySupport.CheckSpace(db, tr, guard, p.Espacio);

            LineWallOpening.LineCut corte;
            try
            {
                (double x, double y) centro = centroPedido
                    ?? WallEntitySupport.Geometry(() => LineWallOpening.CenterFromDistance(muro.Cara, opuesta.Cara, p.Distancia!.Value, p.DesdeFin));
                corte = WallEntitySupport.Geometry(() => LineWallOpening.Cut(muro.Cara, opuesta.Cara, centro, p.Ancho));
            }
            catch (ToolException ex) when (p.CaraOpuesta == null)
            {
                // §5G.1: el cliente no eligió esa cara; sin saber cuál fue, el rechazo no se entiende.
                throw new ToolException(ex.Code, ex.Message, new Dictionary<string, object?>(ex.ExtraData) { ["cara_opuesta"] = opuesta.Handle });
            }

            (PlannedBlockRef? insert, Dictionary<string, object?>? numerosBloque) = PlanOptionalBlock(p, corte.Hueco, db, tr, advertencias);

            DrawLayerFactory.Check(db, tr, muro.Capa, "muros", out _);
            DrawLayerFactory.Check(db, tr, opuesta.Capa, "muros", out _);

            // Un bucle que usa las caras solo se reparte si las usa las dos y lo demás lo cierra a cada lado del hueco.
            var caras = new[] { muro.Entidad.ObjectId, opuesta.Entidad.ObjectId };
            var repartos = new Dictionary<WallEntitySupport.HatchLoopInfo, (IReadOnlyList<string> Inicio, IReadOnlyList<string> Fin)>();
            bool Repartible(WallEntitySupport.HatchLoopInfo b)
            {
                List<(string Handle, (double x, double y) P, (double x, double y) Q)>? otras = WallEntitySupport.OtherLoopCurves(tr, b, caras);
                (IReadOnlyList<string> Inicio, IReadOnlyList<string> Fin)? reparto =
                    otras == null || !caras.All(b.Ids.Contains) ? null : LineWallOpening.SplitHatchLoop(corte, otras);
                if (reparto != null)
                {
                    repartos[b] = reparto.Value;
                }

                return reparto != null;
            }

            WallEntitySupport.HatchRebuild rehacer = WallEntitySupport.PlanHatchRebuild(
                db, tr, WallEntitySupport.AssociatedHatches(tr, muro.Entidad, opuesta.Entidad), p.Confirmar, advertencias, Repartible);
            WallEntitySupport.WarnHoleOverlaps(db, tr, muro.Entidad.BlockId, corte.Hueco, rehacer.Hatches.ConvertAll(h => h.Handle), advertencias);

            var entidades = new List<PlannedEntity> { new PlannedErase(muro.Handle, "LINE"), new PlannedErase(opuesta.Handle, "LINE") };
            entidades.AddRange(rehacer.Borrados);
            int n = entidades.Count;
            entidades.Add(Piece(corte.MuroInicio, muro));
            entidades.Add(Piece(corte.MuroFin, muro));
            entidades.Add(Piece(corte.OpuestaInicio, opuesta));
            entidades.Add(Piece(corte.OpuestaFin, opuesta));
            entidades.Add(Tapa(corte.TapaInicio, muro.Capa));
            entidades.Add(Tapa(corte.TapaFin, muro.Capa));
            foreach (WallEntitySupport.AssociatedHatch h in rehacer.Hatches)
            {
                var bucles = new List<PlannedHatchLoop>();
                foreach (WallEntitySupport.HatchLoopInfo b in h.Bucles)
                {
                    if (!b.TocaMuro)
                    {
                        bucles.Add(b.Conservado());
                        continue;
                    }

                    // Cada lado: cara del muro, tapa, cara opuesta y lo que cerraba el bucle, en orden de encadenado.
                    int tipo = WallEntitySupport.AssociatedHatch.BucleExterior;
                    bucles.Add(new PlannedHatchLoop(new[] { n, n + 4, n + 2 }, repartos[b].Inicio, tipo));
                    bucles.Add(new PlannedHatchLoop(new[] { n + 1, n + 5, n + 3 }, repartos[b].Fin, tipo));
                }

                entidades.Add(h.ToPlanned(new[] { n, n + 1, n + 2, n + 3, n + 4, n + 5 }, bucles));
            }

            if (insert != null)
            {
                entidades.Add(insert);
            }

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hueco"] = OpeningNumbers(corte.Hueco, p.Ancho),
                ["contornos"] = new List<Dictionary<string, object?>>(),
                ["lineas"] = new List<Dictionary<string, object?>>
                {
                    LineNumbers("inicio", muro.Handle, corte.MuroInicio),
                    LineNumbers("fin", muro.Handle, corte.MuroFin),
                    LineNumbers("inicio", opuesta.Handle, corte.OpuestaInicio),
                    LineNumbers("fin", opuesta.Handle, corte.OpuestaFin),
                },
                ["bloque"] = numerosBloque,
                ["muro_original"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = muro.Handle,
                    ["capa"] = muro.Capa,
                    ["area"] = null,
                    ["n_vertices"] = 2,
                    ["cara_opuesta"] = opuesta.Handle,
                },
                ["n_hatch_rehechos"] = rehacer.Hatches.Count,
                ["n_entidades"] = entidades.Count - n,
            };
            var geo = new GeneratedGeometry(entidades, numeros);
            geo.AnclasExtra.Add(muro.Huella);
            geo.AnclasExtra.Add(opuesta.Huella);
            geo.AnclasExtra.AddRange(rehacer.Huellas);
            return geo;
        }

        /// <summary>La cara indicada o, si falta, la buscada: rechaza listando las candidatas si no hay una sola (§5G.1).</summary>
        private static WallEntitySupport.WallLine ResolveOppositeFace(
            Params p, WallEntitySupport.WallLine muro, (double x, double y)? centro, Database db, Transaction tr, List<string> advertencias)
        {
            if (p.CaraOpuesta != null)
            {
                Entity e = PointRefResolver.Require(db, tr, p.CaraOpuesta);
                return e is Line l
                    ? WallEntitySupport.ReadWallLine(l, p.CaraOpuesta, "cara_opuesta", advertencias)
                    : throw ToolException.InvalidParams(
                        "cara_opuesta: la entidad " + p.CaraOpuesta + " no es una LINE (" + AcadUtil.DxfName(e) + ")",
                        new Dictionary<string, object?> { ["handle"] = p.CaraOpuesta });
            }

            var lineas = new Dictionary<string, Line>(StringComparer.OrdinalIgnoreCase);
            var caras = new List<LineWallOpening.Face>();
            foreach (Line l in WallEntitySupport.LinesOnLayer(tr, muro.Entidad.BlockId, muro.Capa))
            {
                string handle = AcadUtil.HandleToString(l.Handle);
                lineas[handle] = l;
                caras.Add(new LineWallOpening.Face(handle, (l.StartPoint.X, l.StartPoint.Y), (l.EndPoint.X, l.EndPoint.Y)));
            }

            LineWallOpening.OppositeSearch busqueda = LineWallOpening.FindOpposite(muro.Cara, caras, centro, p.Ancho);
            if (busqueda.Elegida != null)
            {
                return WallEntitySupport.ReadWallLine(lineas[busqueda.Elegida.Handle], busqueda.Elegida.Handle, "cara_opuesta", advertencias);
            }

            var nombres = new List<string>(busqueda.Candidatas.Count);
            foreach (LineWallOpening.Candidate c in busqueda.Candidatas)
            {
                nombres.Add(c.Cara.Handle);
            }

            throw ToolException.InvalidParams(
                busqueda.Candidatas.Count == 0
                    ? LineWallOpening.NoOppositeDetail(muro.Handle, muro.Capa)
                    : LineWallOpening.ManyOppositesDetail(muro.Capa, busqueda.Candidatas),
                new Dictionary<string, object?> { ["handle"] = muro.Handle, ["candidatas"] = nombres });
        }

        private static PlannedLine Piece(LineWallOpening.Piece t, WallEntitySupport.WallLine cara) =>
            new PlannedLine(new Point3(t.Desde.x, t.Desde.y, 0.0), new Point3(t.Hasta.x, t.Hasta.y, 0.0), "muros", cara.Capa) { AspectoOriginal = cara.Aspecto };

        private static PlannedLine Tapa(LineWallOpening.Piece t, string capa) =>
            new PlannedLine(new Point3(t.Desde.x, t.Desde.y, 0.0), new Point3(t.Hasta.x, t.Hasta.y, 0.0), "muros", capa);

        private static Dictionary<string, object?> LineNumbers(string lado, string origen, LineWallOpening.Piece t) =>
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["lado"] = lado,
                ["origen"] = origen,
                ["longitud"] = PolygonMetrics.Dist(t.Desde, t.Hasta),
            };

        private static (PlannedBlockRef? Insert, Dictionary<string, object?>? Numeros) PlanOptionalBlock(
            Params p, WallOpening.OpeningResult hueco, Database db, Transaction tr, List<string> advertencias)
        {
            if (p.Tipo != VanoCatalog.TipoHueco)
            {
                return PlanBlock(p, hueco, db, tr, advertencias);
            }

            if (p.Capa != null)
            {
                advertencias.Add("capa: no aplica con tipo hueco; los contornos conservan la capa del muro.");
            }

            return (null, null);
        }

        /// <summary>Paso (4) de §5G.1: catálogo, definición, modo de ancho, colocación e INSERT planificado.</summary>
        private static (PlannedBlockRef Insert, Dictionary<string, object?> Numeros) PlanBlock(
            Params p, WallOpening.OpeningResult hueco, Database db, Transaction tr, List<string> advertencias)
        {
            IReadOnlyList<VanoBlockSpec> catalogo = VanoCatalog.Parse(LpsStandard.Root, advertencias);
            VanoBlockSpec spec = FindSpec(catalogo, p);
            if (Math.Abs(spec.EspesorNominal - hueco.Espesor) > ThicknessTol)
            {
                advertencias.Add("vano: el bloque '" + spec.Bloque + "' está dibujado para muros de " + Num(spec.EspesorNominal)
                    + "; el muro mide " + Num(hueco.Espesor) + "; las jambas no cubren todo el espesor.");
            }

            // R3 del INSERT antes de una posible importación, que no se revierte.
            (string Capa, bool Existe) destino = CheckInsertLayer(db, tr, p.Capa, spec.Concepto);
            Definition def = ResolveDefinition(spec.Bloque, db, tr, destino);
            if (p.PropiedadesDinamicas != null && !def.Dinamica)
            {
                throw ToolException.InvalidParams("propiedades_dinamicas: el bloque '" + def.Nombre + "' no es dinámico",
                    new Dictionary<string, object?> { ["bloque"] = def.Nombre });
            }

            // Definición dinámica aún por importar y sin nombre configurado: sus propiedades reales solo se ven en la referencia.
            bool pendiente = def.Dinamica && def.Archivo != null && spec.PropiedadAncho == null;
            (string? propiedadAncho, IReadOnlyList<string> candidatas) = def.Dinamica && !pendiente
                ? WidthProperty(spec, def, tr)
                : (null, Array.Empty<string>());
            bool porPropiedad = propiedadAncho != null || pendiente;
            OpeningPlacement.Placement colocacion = WallEntitySupport.Geometry(() => OpeningPlacement.Place(hueco, spec, p.CaraApertura, p.Bisagra, porPropiedad));
            PendingWidth? anchoPendiente = null;
            if (pendiente)
            {
                OpeningPlacement.Placement porEscala = WallEntitySupport.Geometry(() => OpeningPlacement.Place(hueco, spec, p.CaraApertura, p.Bisagra, false));
                anchoPendiente = new PendingWidth(spec.ValorPropiedad(p.Ancho), spec.ValorNominal, porEscala.Escala, porEscala.FactorAncho ?? 1.0);
            }

            BBox2 caja = WallEntitySupport.Geometry(() => OpeningPlacement.EstimatedBox(hueco, p.CaraApertura, p.Tipo == VanoCatalog.TipoPuerta ? p.Ancho : 0.0));

            Dictionary<string, object>? propiedades = p.PropiedadesDinamicas == null ? null : new Dictionary<string, object>(p.PropiedadesDinamicas, StringComparer.OrdinalIgnoreCase);
            if (propiedadAncho != null)
            {
                propiedades ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (propiedades.ContainsKey(propiedadAncho))
                {
                    advertencias.Add(DynamicPropertyRules.WidthIgnoredWarning(propiedadAncho));
                }

                propiedades[propiedadAncho] = spec.ValorPropiedad(p.Ancho);
            }
            else if (!pendiente)
            {
                double factor = colocacion.FactorAncho ?? 1.0;
                advertencias.Add(candidatas.Count > 1
                    ? DynamicPropertyRules.AmbiguousWidthWarning(def.Nombre, candidatas, factor)
                    : DynamicPropertyRules.ScaledWidthWarning(def.Nombre, factor));
            }

            var insert = new PlannedBlockRef(
                def.Nombre,
                def.Archivo != null,
                new Point3(colocacion.Posicion.x, colocacion.Posicion.y, 0.0),
                colocacion.RotacionGrados,
                colocacion.Escala,
                propiedades,
                caja,
                spec.Concepto,
                p.Capa) { AnchoPendiente = anchoPendiente };

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nombre"] = def.Nombre,
                ["catalogo"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tipo"] = spec.Tipo,
                    ["eje_ancho"] = spec.EjeAncho,
                    ["direccion_espesor"] = spec.DireccionEspesor,
                    ["ancho_nominal"] = spec.AnchoNominal,
                    ["ajuste_ancho"] = spec.AjusteAncho,
                    ["espesor_nominal"] = spec.EspesorNominal,
                    ["propiedad_ancho"] = spec.PropiedadAncho,
                    ["capa"] = spec.Concepto,
                },
                ["posicion"] = Pair(colocacion.Posicion),
                ["rotacion"] = colocacion.RotacionGrados,
                ["escala"] = colocacion.Escala,
                ["espejo"] = colocacion.Espejo,
                ["ancho_por"] = pendiente ? AnchoPendiente : porPropiedad ? "propiedad" : "escala",
                ["propiedad_ancho"] = propiedadAncho,
                ["factor_ancho"] = colocacion.FactorAncho,
            };
            return (insert, numeros);
        }

        /// <summary>Entrada del catálogo pedida o la primera del tipo; textos de §5G.1 si no existe.</summary>
        private static VanoBlockSpec FindSpec(IReadOnlyList<VanoBlockSpec> catalogo, Params p)
        {
            if (p.Bloque == null)
            {
                return VanoCatalog.DefaultFor(catalogo, p.Tipo)
                    ?? throw ToolException.InvalidParams("bloque: lps.json biblioteca_vanos no tiene ningún bloque de tipo " + p.Tipo);
            }

            return VanoCatalog.Find(catalogo, p.Bloque)
                ?? throw ToolException.InvalidParams(
                    "bloque: '" + p.Bloque + "' no está en lps.json biblioteca_vanos; no se conoce su orientación ni su ancho nominal",
                    new Dictionary<string, object?> { ["bloque"] = p.Bloque });
        }

        /// <summary>
        /// Definición en el dibujo (no xref, no layout ni anónima) o archivo de la biblioteca (NOT_FOUND de §5D.5 si no existe).
        /// Del archivo se lee, en una base lateral que se descarta, si es dinámico y las propiedades de una capa homónima de
        /// la capa del INSERT que aún no exista, porque Database.Insert la traería tal cual (§5D.5).
        /// </summary>
        private static Definition ResolveDefinition(string nombre, Database db, Transaction tr, (string Capa, bool Existe) destino)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(nombre) && !bt[nombre].IsErased)
            {
                var btr = (BlockTableRecord)tr.GetObject(bt[nombre], OpenMode.ForRead);
                var data = new Dictionary<string, object?> { ["bloque"] = nombre };
                if (btr.IsLayout || btr.IsAnonymous)
                {
                    throw ToolException.InvalidParams(BlockNameRules.AnonymousMessage, data);
                }

                if (btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
                {
                    throw ToolException.InvalidParams(InsertarBloqueHandler.XrefMessage, data);
                }

                return new Definition(btr.Name, null, btr.ObjectId, btr.IsDynamicBlock);
            }

            string archivo = DrawLibrary.FindFile(nombre) ?? throw GeometryDrawer.LibraryNotFound(nombre);
            string nombreReal = System.IO.Path.GetFileNameWithoutExtension(archivo);
            using (Database side = DrawLibrary.ReadSide(archivo))
            {
                if (!destino.Existe)
                {
                    DrawLibrary.CheckSideLayer(side, destino.Capa, l => DrawLayerFactory.RequireWritable(l, new List<string>()));
                }

                return new Definition(nombreReal, archivo, ObjectId.Null, IsDynamicFile(side));
            }
        }

        /// <summary>true si el espacio modelo de la base lateral lleva el diccionario de extensión de un bloque dinámico (§5G.0).</summary>
        private static bool IsDynamicFile(Database side)
        {
            using (Transaction str = side.TransactionManager.StartOpenCloseTransaction())
            {
                var ms = (BlockTableRecord)str.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(side), OpenMode.ForRead);
                bool dinamico = !ms.ExtensionDictionary.IsNull
                    && str.GetObject(ms.ExtensionDictionary, OpenMode.ForRead) is DBDictionary d
                    && d.Contains(EnhancedBlockKey);
                str.Commit();
                return dinamico;
            }
        }

        /// <summary>
        /// Propiedad de ancho de una definición dinámica: la del catálogo o la deducida por valor ≈ ancho nominal, primero en
        /// una referencia temporal NO residente y, si AutoCAD no expone ahí las propiedades, en las primeras referencias
        /// reales. Con varias candidatas no elige: devuelve null y sus nombres. Un fallo aquí no es un rechazo: el vano cae
        /// a escala. Una definición aún por importar no llega aquí: la deduce GeometryDrawer en la referencia creada.
        /// </summary>
        private static (string? Nombre, IReadOnlyList<string> Candidatas) WidthProperty(VanoBlockSpec spec, Definition def, Transaction tr)
        {
            if (spec.PropiedadAncho != null)
            {
                return (spec.PropiedadAncho, Array.Empty<string>());
            }

            try
            {
                using (var temporal = new BlockReference(Point3d.Origin, def.Id))
                {
                    WidthMatch m = DynamicPropertyRules.FindWidthProperty(DynamicBlockSupport.Read(temporal, tr), spec.ValorNominal);
                    if (m.Candidatas.Count > 0)
                    {
                        return (m.Propiedad?.Nombre, m.Candidatas);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("vano: referencia temporal de '" + def.Nombre + "' sin propiedades dinámicas (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            try
            {
                var referencias = new List<ObjectId>();
                AcadUtil.CollectInsertions((BlockTableRecord)tr.GetObject(def.Id, OpenMode.ForRead), tr, referencias);
                for (int i = 0; i < referencias.Count && i < MaxReferencesProbed; i++)
                {
                    if (tr.GetObject(referencias[i], OpenMode.ForRead) is BlockReference br)
                    {
                        WidthMatch m = DynamicPropertyRules.FindWidthProperty(DynamicBlockSupport.Read(br, tr), spec.ValorNominal);
                        if (m.Candidatas.Count > 0)
                        {
                            return (m.Propiedad?.Nombre, m.Candidatas);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("vano: no se pudieron leer referencias de '" + def.Nombre + "' (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            return (null, Array.Empty<string>());
        }

        private static (string Capa, bool Existe) CheckInsertLayer(Database db, Transaction tr, string? capa, string concepto)
        {
            LayerPlan plan = DrawLayerFactory.Check(db, tr, capa, concepto, out bool existe);
            return (plan.Nombre, existe);
        }

        private static Dictionary<string, object?> OpeningNumbers(WallOpening.OpeningResult h, double ancho) =>
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["centro"] = Pair(h.Centro),
                ["a1"] = Pair(h.A1),
                ["a2"] = Pair(h.A2),
                ["b1"] = Pair(h.B1),
                ["b2"] = Pair(h.B2),
                ["ancho"] = ancho,
                ["espesor"] = h.Espesor,
                ["direccion"] = AcadUtil.ToDegrees360(Math.Atan2(h.Direccion.y, h.Direccion.x)),
                ["distancia_desde_inicio"] = h.DistanciaDesdeInicio,
                ["longitud_tramo"] = h.LongitudTramo,
            };

        private static Dictionary<string, object?> ContourNumbers(string lado, PlannedPolyline contorno)
        {
            (double longitud, double? area) = contorno.Metrics();
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["lado"] = lado,
                ["n_vertices"] = contorno.Vertices.Count,
                ["longitud"] = longitud,
                ["area"] = area,
            };
        }

        private static double[] Pair((double x, double y) p) => new[] { p.x, p.y };
    }
}
