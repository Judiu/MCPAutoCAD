using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>Hallazgo de auditar_bloques con la sección que lo produjo.</summary>
    internal sealed class BlockFinding
    {
        public BlockFinding(string seccion, Finding finding)
        {
            Seccion = seccion;
            Finding = finding;
        }

        /// <summary>"unidades_distintas" | "nombres_sospechosos" | "sin_uso".</summary>
        public string Seccion { get; }

        public Finding Finding { get; }
    }

    /// <summary>Resultado de <see cref="BloquesCheck.Analyze"/>.</summary>
    internal sealed class BlockAuditResult
    {
        public int NDefiniciones { get; set; }
        public int NAnonimos { get; set; }
        public int NXrefs { get; set; }
        public int UnidadesDistintas { get; set; }
        public int NombresSospechosos { get; set; }
        public int SinUso { get; set; }

        /// <summary>Hallazgos de las tres secciones: (a) unidades, (b) nombres, (c) sin uso, cada una por nombre de bloque.</summary>
        public List<BlockFinding> Hallazgos { get; } = new List<BlockFinding>();

        public List<string> Advertencias { get; } = new List<string>();

        public bool Completo { get; set; } = true;

        public Dictionary<string, object?> Resumen() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["n_definiciones"] = NDefiniciones,
            ["n_anonimos"] = NAnonimos,
            ["n_xrefs"] = NXrefs,
            ["unidades_distintas"] = UnidadesDistintas,
            ["nombres_sospechosos"] = NombresSospechosos,
            ["sin_uso"] = SinUso,
        };
    }

    /// <summary>
    /// Regla 3 de auditar_dibujo, <c>auditar_bloques</c> (protocol.md §5C.3): bloques definidos en otras unidades
    /// (factor de escala de GeometryCore <see cref="UnitsScale"/>), nombres sospechosos (<see cref="BlockNameHeuristics"/>)
    /// y bloques sin inserciones. Lee las definiciones del BlockTable y sus referencias; nunca abre las entidades del
    /// interior de una definición.
    /// </summary>
    internal sealed class BloquesCheck : IAuditCheck
    {
        public const string RuleName = "auditar_bloques";

        public const string SeccionUnidades = "unidades_distintas";
        public const string SeccionNombres = "nombres_sospechosos";
        public const string SeccionSinUso = "sin_uso";

        public const string WarningUnitsZero = "INSUNITS=0: no se comparan las unidades de los bloques.";

        public static readonly string[] Secciones = { SeccionUnidades, SeccionNombres, SeccionSinUso };

        public string Regla => RuleName;

        public int Orden => 3;

        private sealed class Definition
        {
            public Definition(BlockTableRecord btr, string name)
            {
                Btr = btr;
                Name = name;
            }

            public BlockTableRecord Btr { get; }
            public string Name { get; }
        }

        public BlockAuditResult Analyze(AuditContext ctx)
        {
            Transaction tr = ctx.Tr;
            int ins = AcadUtil.InsUnits(ctx.Db);
            var result = new BlockAuditResult();
            var definitions = new List<Definition>();

            var bt = (BlockTable)tr.GetObject(ctx.Db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                BlockTableRecord? btr;
                try
                {
                    btr = tr.GetObject(id, OpenMode.ForRead, false) as BlockTableRecord;
                }
                catch (Exception)
                {
                    btr = null;
                }

                if (btr == null || btr.IsLayout)
                {
                    continue;
                }

                string name = btr.Name ?? "";
                if (btr.IsFromExternalReference)
                {
                    result.NXrefs++;
                    continue;
                }

                if (btr.IsDependent)
                {
                    continue;
                }

                if (name.StartsWith("*", StringComparison.Ordinal))
                {
                    result.NAnonimos++;
                    continue;
                }

                definitions.Add(new Definition(btr, name));
            }

            result.NDefiniciones = definitions.Count;
            definitions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            bool compareUnits = ins != 0;
            if (!compareUnits)
            {
                result.Advertencias.Add(WarningUnitsZero);
            }

            // Ids de los espacios: la caja de un hallazgo solo usa inserciones que viven en modelo o layouts.
            var spaceIds = new HashSet<ObjectId>();
            foreach (SpaceResolver.ResolvedSpace sp in AuditSupport.AllSpaces(ctx.Db, tr))
            {
                spaceIds.Add(sp.Btr.ObjectId);
            }

            var unidades = new List<BlockFinding>();
            var nombres = new List<BlockFinding>();
            var sinUso = new List<BlockFinding>();
            for (int i = 0; i < definitions.Count; i++)
            {
                if (i % 25 == 24 && ctx.TimeExceeded)
                {
                    result.Completo = false;
                    break;
                }

                Definition def = definitions[i];
                var inserts = new List<ObjectId>();
                AcadUtil.CollectInsertions(def.Btr, tr, inserts);
                inserts.Sort((a, b) => a.Handle.Value.CompareTo(b.Handle.Value));
                int n = inserts.Count;
                int units = SafeUnits(def.Btr);

                // (a) unidades distintas, solo con inserciones.
                if (compareUnits && units != 0 && units != ins && n > 0)
                {
                    result.UnidadesDistintas++;
                    unidades.Add(new BlockFinding(
                        SeccionUnidades, UnitsFinding(ctx, def.Name, units, ins, inserts, spaceIds, tr, out bool sinEvaluar)));
                    if (sinEvaluar)
                    {
                        // §3.10: el plazo cortó el muestreo antes de evaluar ninguna inserción de este bloque.
                        result.Completo = false;
                    }
                }

                // (b) nombre sospechoso (la heurística ya excluye anónimos y A$C…).
                string? motivo = BlockNameHeuristics.Suspicion(def.Name);
                if (motivo != null)
                {
                    result.NombresSospechosos++;
                    var f = new Finding(RuleName, n == 0 ? Severity.Media : Severity.Baja,
                        "El nombre de bloque '" + def.Name + "' parece arbitrario (" + motivo + "); "
                        + Insertions(n) + ".");
                    f.Datos["bloque"] = def.Name;
                    f.Datos["motivo"] = motivo;
                    f.Datos["n_inserciones"] = n;
                    nombres.Add(new BlockFinding(SeccionNombres, f));
                }

                // (c) sin uso (incluidas las A$C…).
                if (n == 0)
                {
                    result.SinUso++;
                    var f = new Finding(RuleName, Severity.Baja, "El bloque '" + def.Name + "' no tiene inserciones.");
                    f.Datos["bloque"] = def.Name;
                    f.Datos["n_entidades"] = CountIds(def.Btr);
                    f.Datos["unidades_bloque"] = units;
                    sinUso.Add(new BlockFinding(SeccionSinUso, f));
                }
            }

            result.Hallazgos.AddRange(unidades);
            result.Hallazgos.AddRange(nombres);
            result.Hallazgos.AddRange(sinUso);
            return result;
        }

        public AuditCheckResult Run(AuditContext ctx)
        {
            BlockAuditResult analysis = Analyze(ctx);
            var result = new AuditCheckResult { Completo = analysis.Completo };
            foreach (BlockFinding bf in analysis.Hallazgos)
            {
                result.Hallazgos.Add(bf.Finding);
            }

            result.Advertencias.AddRange(analysis.Advertencias);
            foreach (KeyValuePair<string, object?> kv in analysis.Resumen())
            {
                result.Resumen[kv.Key] = kv.Value;
            }

            return result;
        }

        /// <summary>Máximo de inserciones por bloque cuya escala se lee (§5C.3, corrección F4).</summary>
        internal const int MaxEvaluatedInsertions = 500;

        /// <summary>
        /// Hallazgo (a) unidades_distintas con la compensación por inserción de §5C.3 (corrección F4 del gate de Fase 2):
        /// la severidad ya no depende del número de inserciones sino de si cada una lleva aplicado el factor implícito
        /// (|ScaleFactors.X| ≈ factor, tolerancia 1 %, <see cref="UnitsScale.ClassifyInsertions"/>). Se leen como mucho
        /// <see cref="MaxEvaluatedInsertions"/> inserciones (orden de handle) y se comprueba el plazo cada 50.
        /// <paramref name="sinEvaluar"/> = true si el plazo impidió evaluar ninguna inserción.
        /// </summary>
        private static Finding UnitsFinding(
            AuditContext ctx, string name, int units, int ins, List<ObjectId> inserts, HashSet<ObjectId> spaceIds, Transaction tr,
            out bool sinEvaluar)
        {
            int n = inserts.Count;
            double? factor = UnitsScale.Factor(units, ins);
            string prefix = "El bloque '" + name + "' está definido en " + UnitsTable.SpanishName(units) + " (INSUNITS="
                + AuditSupport.Int(units) + ") y el dibujo en " + UnitsTable.SpanishName(ins) + " (INSUNITS="
                + AuditSupport.Int(ins) + "); ";

            // Muestreo: escala real de cada inserción (las anidadas llevan su escala relativa al bloque contenedor).
            var evaluated = new List<(string handle, double escala)>();
            var idByHandle = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
            double[]? tamanoEjemplo = null;
            // Caja (unidades de dibujo) de la primera inserción NO compensada que vive en un espacio: base del tamaño
            // real que decide entre "media" (metadato de unidades incorrecto) y "alta" (§5C.3, decisión F4).
            double[]? tamanoNoCompensada = null;
            bool parcial = false;
            bool timeCut = false;
            // 0.3.2: inserciones no compensadas ANIDADAS (no viven en un espacio), candidatas a medirse componiendo los
            // BlockTransform de sus contenedores si ninguna no compensada vive directamente en un espacio.
            var nestedCandidates = new List<ObjectId>();
            int nAnidadas = 0;
            for (int i = 0; i < n; i++)
            {
                if (i >= MaxEvaluatedInsertions)
                {
                    parcial = true;
                    break;
                }

                if (i % 50 == 49 && ctx.TimeExceeded)
                {
                    parcial = true;
                    timeCut = true;
                    break;
                }

                try
                {
                    if (AuditSupport.Open(tr, inserts[i]) is not BlockReference br)
                    {
                        continue;
                    }

                    // §5C.3: escala = |ScaleFactors.X| (un espejo no cambia las unidades).
                    double escala = Math.Abs(br.ScaleFactors.X);
                    if (!double.IsFinite(escala))
                    {
                        continue;
                    }

                    string h = AuditSupport.Handle(inserts[i]);
                    evaluated.Add((h, escala));
                    idByHandle[h] = inserts[i];
                    bool enEspacio = spaceIds.Contains(br.OwnerId);
                    bool noCompensada = factor != null && !UnitsScale.IsCompensated(escala, factor.Value);
                    if (!enEspacio)
                    {
                        nAnidadas++;
                        if (noCompensada && nestedCandidates.Count < MaxNestedMeasureAttempts)
                        {
                            nestedCandidates.Add(inserts[i]);
                        }
                    }

                    if (enEspacio && (tamanoEjemplo == null || (noCompensada && tamanoNoCompensada == null)))
                    {
                        BBox2? b = AcadUtil.TryBBox2(br);
                        if (b != null)
                        {
                            var size = new[] { b.Value.Width, b.Value.Height };
                            tamanoEjemplo ??= size;
                            if (noCompensada && tamanoNoCompensada == null)
                            {
                                tamanoNoCompensada = size;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Inserción ilegible: se salta y cuenta como no evaluada.
                }
            }

            sinEvaluar = timeCut && evaluated.Count == 0 && n > 0;
            (List<(string handle, double escala)> comp, List<(string handle, double escala)> noComp) =
                UnitsScale.ClassifyInsertions(evaluated, factor);

            // 0.3.2 (gate en vivo 0.3.1, bloque 'C': 5 464 inserciones TODAS anidadas en bloques anónimos a escala 0.045,
            // ninguna en un espacio → no se medía nada y salía "alta"): si ninguna no compensada vive en un espacio, se
            // mide una anidada llevando su caja al espacio a través de la cadena de contenedores (MeasureNested).
            bool medidaAnidada = false;
            if (factor != null && noComp.Count > 0 && tamanoNoCompensada == null)
            {
                foreach (ObjectId id in nestedCandidates)
                {
                    if (ctx.TimeExceeded)
                    {
                        break;
                    }

                    BBox2? b = MeasureNested(tr, id, spaceIds);
                    if (b != null)
                    {
                        tamanoNoCompensada = new[] { b.Value.Width, b.Value.Height };
                        tamanoEjemplo ??= tamanoNoCompensada;
                        medidaAnidada = true;
                        break;
                    }
                }
            }

            bool todasAnidadas = noComp.Count > 0 && nAnidadas > 0 && !medidaAnidada && tamanoNoCompensada == null;

            Finding f;
            List<ObjectId> boxIds;
            double[]? tamanoRealM = tamanoNoCompensada == null
                ? null
                : UnitsScale.RealSizeMeters(tamanoNoCompensada[0], tamanoNoCompensada[1], ins);
            bool metadatoIncorrecto = false;
            if (factor == null || evaluated.Count == 0)
            {
                // Sin factor métrico (o sin ninguna inserción legible): no se puede evaluar la compensación → media.
                string why = factor == null ? "sin factor de escala métrico" : "no se pudo leer la escala de sus inserciones";
                f = new Finding(RuleName, Severity.Media, prefix + Insertions(n) + ", " + why + ".");
                foreach (ObjectId id in inserts)
                {
                    f.Handles.Add(AuditSupport.Handle(id));
                }

                boxIds = inserts;
            }
            else if (noComp.Count == 0)
            {
                // Todas las evaluadas compensadas → baja.
                string quien = parcial
                    ? "las " + AuditSupport.Int(evaluated.Count) + " inserciones evaluadas de sus " + AuditSupport.Int(n) + " están escaladas"
                    : (n == 1 ? "su única inserción está escalada" : "sus " + Insertions(n) + " están escaladas");
                f = new Finding(RuleName, Severity.Baja, prefix + quien + " a " + AuditSupport.Num(factor.Value)
                    + ": compensado; riesgo solo al redefinir el bloque o insertarlo sin escala automática.");
                boxIds = new List<ObjectId>();
                foreach ((string handle, double _) in comp)
                {
                    f.Handles.Add(handle);
                    boxIds.Add(idByHandle[handle]);
                }
            }
            else
            {
                // Alguna sin compensar (decisión F4 del gate, ratificada): si el tamaño real de la primera no
                // compensada (en un espacio o, desde 0.3.2, anidada llevada al espacio) es plausible (0,02–200 m) la
                // geometría ya está en las unidades del dibujo y lo erróneo es el metadato → media. "alta" SOLO si se
                // MIDE un tamaño implausible; sin medida → media "no medible". handles = solo las no compensadas (máx. 50).
                List<double> escalasMsg = DistinctScales(noComp, 3);
                string escalas = (escalasMsg.Count == 1 ? "escala " : "escalas ")
                    + string.Join(", ", escalasMsg.ConvertAll(AuditSupport.Num));
                if (tamanoRealM == null)
                {
                    f = new Finding(RuleName, Severity.Media,
                        UnitsScale.UnmeasurableMessage(name, UnitsTable.Name(units), noComp.Count, escalas, todasAnidadas));
                }
                else if (UnitsScale.NonCompensatedSeverity(tamanoRealM) == Severity.Media)
                {
                    metadatoIncorrecto = true;
                    f = new Finding(RuleName, Severity.Media,
                        UnitsScale.MetadataMessage(name, UnitsTable.Name(units), tamanoRealM, factor.Value));
                }
                else
                {
                    string deCuantas = parcial
                        ? " de las " + AuditSupport.Int(evaluated.Count) + " inserciones evaluadas (de " + AuditSupport.Int(n) + ")"
                        : " de sus " + Insertions(n);
                    f = new Finding(RuleName, Severity.Alta, prefix + "factor de escala implícito " + AuditSupport.Num(factor.Value)
                        + ", pero " + AuditSupport.Int(noComp.Count) + deCuantas + " tienen " + escalas
                        + " (no compensadas; p. ej. " + noComp[0].handle + ").");
                }

                boxIds = new List<ObjectId>();
                foreach ((string handle, double _) in noComp)
                {
                    f.Handles.Add(handle);
                    boxIds.Add(idByHandle[handle]);
                }
            }

            f.Bbox = SpaceBox(tr, boxIds, spaceIds);
            f.Datos["bloque"] = name;
            f.Datos["unidades_bloque"] = units;
            f.Datos["unidades_bloque_nombre"] = UnitsTable.Name(units);
            f.Datos["insunits_dibujo"] = ins;
            f.Datos["n_inserciones"] = n;
            f.Datos["factor_escala"] = factor;
            f.Datos["n_compensadas"] = factor == null ? 0 : comp.Count;
            f.Datos["n_no_compensadas"] = factor == null ? 0 : noComp.Count;
            f.Datos["escalas_no_compensadas"] = factor == null ? new List<double>() : DistinctScales(noComp, 5);
            f.Datos["inserciones_evaluadas"] = evaluated.Count;
            f.Datos["muestra_parcial"] = parcial;
            f.Datos["tamano_ejemplo"] = tamanoEjemplo;
            f.Datos["tamano_real_m"] = tamanoRealM;
            f.Datos["metadato_unidades_incorrecto"] = metadatoIncorrecto;
            f.Datos["tamano_por_anidada"] = medidaAnidada;
            return f;
        }

        /// <summary>Máximo de inserciones anidadas no compensadas que se intentan medir por bloque (0.3.2).</summary>
        internal const int MaxNestedMeasureAttempts = 10;

        /// <summary>Profundidad máxima de contenedores que se sube para llegar a un espacio (0.3.2).</summary>
        internal const int MaxNestingDepth = 4;

        /// <summary>Referencias de un contenedor que se examinan como mucho por nivel (0.3.2).</summary>
        private const int MaxContainerRefs = 50;

        /// <summary>
        /// Caja (unidades de dibujo) de una inserción ANIDADA llevada al espacio (§5C.3, 0.3.2). Su GeometricExtents está
        /// en coordenadas de la definición que la contiene; se sube por la cadena de propietarios: en cada nivel se toma
        /// una referencia directa del bloque contenedor (BlockTableRecord.GetBlockReferenceIds(true, false), prefiriendo
        /// una que viva en un espacio) y se compone su BlockTransform (parte XY, GeometryCore <see cref="Affine2"/>), hasta
        /// llegar a modelo/layout o a <see cref="MaxNestingDepth"/> niveles. Los bloques de cota (*D…) no tienen
        /// BlockReference (los referencia la cota) y su geometría ya está en coordenadas del espacio: se terminan con la
        /// transformación acumulada. null si no se llega a un espacio. Todo ForRead.
        /// </summary>
        private static BBox2? MeasureNested(Transaction tr, ObjectId insertId, HashSet<ObjectId> spaceIds)
        {
            try
            {
                if (AuditSupport.Open(tr, insertId) is not BlockReference br)
                {
                    return null;
                }

                BBox2? local = AcadUtil.TryBBox2(br);
                if (local == null)
                {
                    return null;
                }

                Affine2 acc = Affine2.Identity;
                ObjectId owner = br.OwnerId;
                for (int depth = 0; depth <= MaxNestingDepth; depth++)
                {
                    if (spaceIds.Contains(owner))
                    {
                        return acc.TransformBox(local.Value);
                    }

                    if (depth == MaxNestingDepth || !(tr.GetObject(owner, OpenMode.ForRead, false) is BlockTableRecord ownerBtr))
                    {
                        return null;
                    }

                    if ((ownerBtr.Name ?? "").StartsWith("*D", StringComparison.OrdinalIgnoreCase))
                    {
                        // Bloque de cota: geometría ya en coordenadas del espacio (sin BlockReference que componer).
                        return acc.TransformBox(local.Value);
                    }

                    BlockReference? container = null;
                    ObjectIdCollection refs = ownerBtr.GetBlockReferenceIds(true, false);
                    int seen = 0;
                    foreach (ObjectId rid in refs)
                    {
                        if (++seen > MaxContainerRefs)
                        {
                            break;
                        }

                        if (rid.IsNull || rid.IsErased || !(AuditSupport.Open(tr, rid) is BlockReference cand))
                        {
                            continue;
                        }

                        container ??= cand;
                        if (spaceIds.Contains(cand.OwnerId))
                        {
                            container = cand;
                            break;
                        }
                    }

                    if (container == null)
                    {
                        return null;
                    }

                    Autodesk.AutoCAD.Geometry.Matrix3d m = container.BlockTransform;
                    acc = new Affine2(m[0, 0], m[0, 1], m[1, 0], m[1, 1], m[0, 3], m[1, 3]).Compose(acc);
                    owner = container.OwnerId;
                }
            }
            catch (Exception)
            {
                // Cadena ilegible: no medible.
            }

            return null;
        }

        /// <summary>Hasta <paramref name="max"/> escalas distintas, redondeadas a 6 decimales, en orden de aparición.</summary>
        private static List<double> DistinctScales(List<(string handle, double escala)> items, int max)
        {
            var list = new List<double>();
            foreach ((string _, double escala) in items)
            {
                double r = Math.Round(escala, 6);
                if (!list.Contains(r))
                {
                    list.Add(r);
                    if (list.Count >= max)
                    {
                        break;
                    }
                }
            }

            return list;
        }

        /// <summary>Unión de las cajas de las primeras 50 inserciones que están en un espacio (no anidadas en bloques).</summary>
        private static BBox2? SpaceBox(Transaction tr, List<ObjectId> inserts, HashSet<ObjectId> spaceIds)
        {
            BBox2? box = null;
            for (int i = 0; i < inserts.Count && i < Finding.MaxHandles; i++)
            {
                Entity? e = AuditSupport.Open(tr, inserts[i]);
                if (e == null || !spaceIds.Contains(e.OwnerId))
                {
                    continue;
                }

                BBox2? b = AcadUtil.TryBBox2(e);
                if (b != null)
                {
                    box = box == null ? b : box.Value.Union(b.Value);
                }
            }

            return box;
        }

        private static string Insertions(int n) => AuditSupport.Int(n) + (n == 1 ? " inserción" : " inserciones");

        private static int SafeUnits(BlockTableRecord btr)
        {
            try
            {
                return (int)btr.Units;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Número de entidades de la definición (datos.n_entidades de §5C.3 c): solo se cuentan los ids del registro,
        /// sin abrir ninguna entidad del interior.
        /// </summary>
        private static int CountIds(BlockTableRecord btr)
        {
            int n = 0;
            try
            {
                foreach (ObjectId id in btr)
                {
                    if (!id.IsErased)
                    {
                        n++;
                    }
                }
            }
            catch (Exception)
            {
                // Definición ilegible: se informa lo contado.
            }

            return n;
        }
    }
}
