using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Escritura común de mover/copiar/rotar/escalar/borrar (protocol.md §5D.8). Flujo:
    /// <list type="number">
    /// <item>R2: <see cref="DrawWrite.CheckUnits"/> (INSUNITS = 0 → UNITS_UNKNOWN salvo aceptar_sin_unidades).</item>
    /// <item>Pasada de SOLO LECTURA (StartOpenCloseTransaction, fuera del UndoGroup): <see cref="HandleGuard.Resolve"/>
    /// (pasos 1–5), espacio común y cajas transformadas; después R4 con <see cref="PlacementCheck.Run"/> sobre ese
    /// espacio (etiqueta "entidades"; los índices citados son los de handles). Así ningún rechazo crea un paso de deshacer.</item>
    /// <item>R1: <see cref="UndoGroup.Run"/> con UNA Transaction que vuelve a resolver los handles (el guard de esta
    /// segunda pasada manda si algo cambió) y escribe: TransformBy (mover/rotar/escalar), DeepCloneObjects + TransformBy
    /// (copiar) o Erase (borrar).</item>
    /// </list>
    /// Solo hilo principal salvo los métodos de lectura de parámetros (sin AutoCAD).
    /// </summary>
    internal static class ModifySupport
    {
        /// <summary>Parámetros comunes a los cinco métodos (§5D.8): sin capa ni espacio.</summary>
        public static readonly string[] CommonParams = { "handles", "confirmar", "forzar", "aceptar_sin_unidades" };

        /// <summary>
        /// Caja "siempre dentro" para las entidades sin caja calculable: §5D.8 las omite de R4, pero los índices que
        /// cita PlacementCheck deben ser los de handles, así que se sustituyen por una caja que interseca cualquier
        /// extensión en vez de quitarlas de la lista.
        /// </summary>
        private static readonly BBox2 AlwaysInside = BBox2.FromCorners(-1e300, -1e300, 1e300, 1e300);

        /// <summary>Qué hace la escritura con cada entidad.</summary>
        internal enum Operation
        {
            /// <summary>mover / rotar / escalar: UpgradeOpen + TransformBy.</summary>
            Transform,

            /// <summary>copiar: clon en el mismo espacio + TransformBy.</summary>
            Copy,

            /// <summary>borrar: UpgradeOpen + Erase.</summary>
            Erase,
        }

        /// <summary>Parámetros comunes leídos.</summary>
        internal sealed class Common
        {
            public Common(List<string> handles, bool confirmar, bool forzar, bool aceptarSinUnidades, bool permitirOrigenBloqueado = false)
            {
                Handles = handles;
                Confirmar = confirmar;
                Forzar = forzar;
                AceptarSinUnidades = aceptarSinUnidades;
                PermitirOrigenBloqueado = permitirOrigenBloqueado;
            }

            public List<string> Handles { get; }
            public bool Confirmar { get; }
            public bool Forzar { get; }
            public bool AceptarSinUnidades { get; }

            /// <summary>Fallo 43: solo copiar. Admite entidades de origen en capa bloqueada (no se modifican).</summary>
            public bool PermitirOrigenBloqueado { get; }
        }

        /// <summary>
        /// JSON Schema completo de uno de los cinco métodos: <paramref name="ownProperties"/> = propiedades propias
        /// (fragmento JSON sin llaves, terminado sin coma, o "" si no hay), <paramref name="ownRequired"/> = obligatorias
        /// propias (handles siempre lo es).
        /// </summary>
        public static JsonElement BuildSchema(string ownProperties, params string[] ownRequired) =>
            BuildSchemaConHandles(SchemaHandles, ownProperties, ownRequired);

        /// <summary>Como <see cref="BuildSchema"/> con una descripción propia de handles (copiar, fallo 43).</summary>
        public static JsonElement BuildSchemaConHandles(string schemaHandles, string ownProperties, params string[] ownRequired)
        {
            var required = new List<string> { "\"handles\"" };
            foreach (string r in ownRequired)
            {
                required.Add("\"" + r + "\"");
            }

            string own = string.IsNullOrWhiteSpace(ownProperties) ? "" : ownProperties + ",\n";
            string json = "{\n  \"type\": \"object\",\n  \"properties\": {\n" + own
                + "    \"handles\": " + schemaHandles + ",\n"
                + "    \"confirmar\": " + SchemaConfirmar + ",\n"
                + "    \"forzar\": " + DrawWrite.SchemaForzar + ",\n"
                + "    \"aceptar_sin_unidades\": " + DrawWrite.SchemaAceptarSinUnidades + "\n"
                + "  },\n  \"required\": [" + string.Join(", ", required) + "],\n  \"additionalProperties\": false\n}";
            return Schema.Parse(json);
        }

        public const string SchemaHandles =
            @"{ ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Handles de las entidades (1–500, sin repetidos), todas en el mismo espacio (modelo o un mismo layout). No se admiten atributos (use el INSERT), entidades dentro de bloques, viewports de papel ni capas bloqueadas o de xref."" }";

        public const string SchemaConfirmar =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = modificar también entidades del usuario (capas que no empiezan por MCP-). Sin él, si alguna entidad no está en una capa MCP-*, se rechaza con INVALID_PARAMS indicando cuántas y cuáles."" }";

        public const string SchemaDesplazamiento =
            @"{ ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Vector [dx, dy] o [dx, dy, dz] en unidades de dibujo (WCS)."" }";

        public const string SchemaBase =
            @"{ ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Punto base [x, y] o [x, y, z] en WCS."" }";

        /// <summary>JSON Schema informativo del resultado (campos comunes + <paramref name="ownProperties"/>).</summary>
        public static JsonElement BuildOutputSchema(string ownProperties)
        {
            string own = string.IsNullOrWhiteSpace(ownProperties) ? "" : ",\n" + ownProperties;
            string json = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n"": { ""type"": ""integer"" },
    ""n_usuario"": { ""type"": ""integer"" },
    ""espacio"": { ""type"": ""string"" },
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }" + own + @"
  },
  ""required"": [""pid"", ""handles"", ""n"", ""n_usuario"", ""espacio"", ""capas"", ""insunits"", ""unidades"", ""advertencias""]
}";
            return Schema.Parse(json);
        }

        /// <summary>Lee handles/confirmar/forzar/aceptar_sin_unidades (sin AutoCAD).</summary>
        public static Common ReadCommon(JsonElement p)
        {
            List<string> handles = ParamReader.RequiredHandles(p, "handles", 1, PluginInfo.MaxHandlesPerCall);
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            bool forzar = ParamReader.OptionalBool(p, "forzar") ?? false;
            bool aceptar = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            return new Common(handles, confirmar, forzar, aceptar);
        }

        /// <summary>
        /// desplazamiento: 2 o 3 números finitos (z = 0 si falta). <paramref name="allowZero"/> = false (mover) →
        /// todo cero → "desplazamiento: no puede ser cero".
        /// </summary>
        public static Point3 ReadDisplacement(JsonElement p, bool allowZero)
        {
            Point3 v = ParamReader.RequiredPoint(p, "desplazamiento");
            if (!allowZero && v.X == 0 && v.Y == 0 && v.Z == 0)
            {
                throw ToolException.InvalidParams("desplazamiento: no puede ser cero");
            }

            return v;
        }

        /// <summary>Número obligatorio finito (cualquier valor): "&lt;name&gt;: debe ser un número finito".</summary>
        public static double RequiredFinite(JsonElement p, string name)
        {
            if (!p.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v) || !double.IsFinite(v))
            {
                throw ToolException.InvalidParams(name + ": debe ser un número finito");
            }

            return v;
        }

        /// <summary>
        /// Ejecuta la operación. <paramref name="boxTransform"/> = transformación XY para R4 (null en borrar: sin R4).
        /// <paramref name="matrix"/> = matriz de AutoCAD (null en borrar). <paramref name="addFields"/> añade los campos
        /// propios del método a la respuesta.
        /// </summary>
        public static object Run(
            ExecutionContext ctx,
            Common c,
            Operation op,
            Affine2? boxTransform,
            Matrix3d? matrix,
            Action<Dictionary<string, object?>> addFields)
        {
            if (op != Operation.Erase && matrix == null)
            {
                throw new ArgumentException("matriz obligatoria salvo en borrar", nameof(matrix));
            }

            if (c.PermitirOrigenBloqueado && op != Operation.Copy)
            {
                throw new ArgumentException("permitir_origen_bloqueado solo aplica a copiar", nameof(c));
            }

            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, c.AceptarSinUnidades, warnings);

            // Pasada de solo lectura (§3.9): pasos 1–5 de §5D.8 y cajas transformadas para R4. Ningún rechazo llega a
            // abrir el UndoGroup.
            string espacio;
            var boxes = new List<BBox2>(c.Handles.Count);
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                HandleGuard.Resolved first = HandleGuard.Resolve(db, tr, c.Handles, c.Confirmar, warnings, c.PermitirOrigenBloqueado);
                espacio = first.Espacio;
                AvisoOrigenBloqueado(tr, first, c, warnings);
                if (boxTransform != null)
                {
                    foreach (HandleGuard.Item item in first.Items)
                    {
                        BBox2? b = AcadUtil.TryBBox2(item.Entity);
                        boxes.Add(b == null ? AlwaysInside : Transform2D.TransformBox(b.Value, boxTransform.Value));
                    }
                }

                tr.Commit();
            }

            // R4 (salvo borrar): contra la extensión del espacio común; NOT_FOUND imposible (el espacio sale del dibujo).
            if (boxTransform != null)
            {
                PlacementCheck.Run(db, espacio, boxes, c.Forzar, "entidades", warnings);
            }

            Matrix3d m = matrix ?? Matrix3d.Identity;
            return UndoGroup.Run(doc, d => Write(d, c, op, m, ctx.Pid, ins, warnings, addFields));
        }

        /// <summary>
        /// Escritura dentro de UNA transacción (R1): cualquier excepción la descarta (Dispose sin Commit) y no se
        /// modifica nada. Las comprobaciones se repiten aquí antes de tocar la primera entidad (todo o nada).
        /// </summary>
        private static object Write(
            Document doc,
            Common c,
            Operation op,
            Matrix3d m,
            int pid,
            int ins,
            List<string> warnings,
            Action<Dictionary<string, object?>> addFields)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Segunda resolución: sus advertencias ya se dieron en la pasada de lectura (lista desechable).
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, c.Handles, c.Confirmar, new List<string>(), c.PermitirOrigenBloqueado);

                var handles = new List<string>(res.Items.Count);
                List<string>? origen = null;
                switch (op)
                {
                    case Operation.Transform:
                        foreach (HandleGuard.Item item in res.Items)
                        {
                            item.Entity.UpgradeOpen();
                            item.Entity.TransformBy(m);
                            handles.Add(item.Info.Handle);
                        }

                        // Gate en vivo de Fase 3: AutoCAD no recalcula los HATCH asociativos tras TransformBy por API.
                        var transformed = new List<ObjectId>(res.Items.Count);
                        foreach (HandleGuard.Item item in res.Items)
                        {
                            transformed.Add(item.Entity.ObjectId);
                        }

                        AssociativeHatchSync.AfterTransform(tr, transformed, warnings);
                        break;

                    case Operation.Erase:
                        foreach (HandleGuard.Item item in res.Items)
                        {
                            item.Entity.UpgradeOpen();
                            item.Entity.Erase();
                            handles.Add(item.Info.Handle);
                        }

                        break;

                    case Operation.Copy:
                        origen = new List<string>(res.Items.Count);
                        handles.AddRange(CopyAll(db, tr, res, m, origen));
                        break;
                }

                tr.Commit();

                var result = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["pid"] = pid,
                    ["handles"] = handles,
                    ["n"] = res.Items.Count,
                    ["n_usuario"] = res.NUsuario,
                    ["espacio"] = res.Espacio,
                    ["capas"] = res.Capas,
                    ["insunits"] = ins,
                    ["unidades"] = UnitsTable.Name(ins),
                    ["advertencias"] = warnings,
                };
                addFields(result);
                if (origen != null)
                {
                    result["handles_origen"] = origen;
                }

                return result;
            }
        }

        /// <summary>
        /// copiar: clona TODAS las entidades en su mismo espacio con <c>Database.DeepCloneObjects</c> (propietario común
        /// = el BlockTableRecord del espacio, exigido por el paso 4) y aplica la matriz a cada clon. DeepCloneObjects copia
        /// también los objetos que posee cada entidad —los ATTRIB de un INSERT, su diccionario de extensión—, que un
        /// <c>Entity.Clone()</c> superficial no garantiza. Devuelve los handles nuevos en el orden de handles y rellena
        /// <paramref name="origen"/> con los originales.
        /// </summary>
        private static List<string> CopyAll(Database db, Transaction tr, HandleGuard.Resolved res, Matrix3d m, List<string> origen)
        {
            var ids = new ObjectIdCollection();
            foreach (HandleGuard.Item item in res.Items)
            {
                ids.Add(item.Id);
            }

            var mapping = new IdMapping();
            db.DeepCloneObjects(ids, res.OwnerId, mapping, false);

            var created = new List<string>(res.Items.Count);
            foreach (HandleGuard.Item item in res.Items)
            {
                IdPair pair = mapping[item.Id];
                if (!pair.IsCloned || pair.Value.IsNull)
                {
                    throw new InvalidOperationException("copiar: AutoCAD no clonó la entidad " + item.Info.Handle + ".");
                }

                // forceOpenOnLockedLayer: con permitir_origen_bloqueado el clon nace en la capa bloqueada del origen
                // (fallo 43); sin él no hay capas bloqueadas aquí (HandleGuard las rechazó) y el parámetro no cambia nada.
                var clone = (Entity)tr.GetObject(pair.Value, OpenMode.ForWrite, false, true);
                clone.TransformBy(m);
                created.Add(AcadUtil.HandleToString(clone.Handle));
                origen.Add(item.Info.Handle);
            }

            return created;
        }

        /// <summary>
        /// Fallo 43: con permitir_origen_bloqueado, avisa de cuántas copias quedarán en capas bloqueadas y cuáles; después
        /// el puente no puede moverlas ni borrarlas hasta desbloquear la capa (estado_capas bloqueada: false).
        /// </summary>
        private static void AvisoOrigenBloqueado(Transaction tr, HandleGuard.Resolved res, Common c, List<string> warnings)
        {
            if (!c.PermitirOrigenBloqueado)
            {
                return;
            }

            int n = 0;
            var capas = new List<string>();
            var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HandleGuard.Item item in res.Items)
            {
                if (tr.GetObject(item.Entity.LayerId, OpenMode.ForRead, false) is LayerTableRecord ltr && ltr.IsLocked)
                {
                    n++;
                    if (vistas.Add(ltr.Name) && capas.Count < 5)
                    {
                        capas.Add(ltr.Name);
                    }
                }
            }

            if (n > 0)
            {
                warnings.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " copia(s) quedarán en capa(s) bloqueada(s) (" + string.Join(", ", capas) + (vistas.Count > capas.Count ? ", ..." : "")
                    + "): para moverlas o borrarlas después, desbloquee con estado_capas (bloqueada: false).");
            }
        }

        /// <summary>Vector de AutoCAD a partir de un Point3 de GeometryCore.</summary>
        public static Vector3d ToVector(Point3 v) => new Vector3d(v.X, v.Y, v.Z);
    }
}
