using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Resolución y comprobaciones de los handles de mover/copiar/rotar/escalar/borrar (protocol.md §5D.8, pasos 1–5),
    /// TODAS antes de modificar la primera entidad (todo o nada). Lee de AutoCAD y delega las reglas en
    /// <see cref="EntityProtection"/> (GeometryCore). Solo hilo principal; no abre nada para escritura.
    /// </summary>
    internal static class HandleGuard
    {
        /// <summary>Entidad resuelta: id, entidad abierta ForRead en la transacción del llamador y sus datos.</summary>
        internal sealed class Item
        {
            public Item(ObjectId id, Entity entity, EntityInfo info)
            {
                Id = id;
                Entity = entity;
                Info = info;
            }

            public ObjectId Id { get; }

            public Entity Entity { get; }

            public EntityInfo Info { get; }
        }

        /// <summary>Resultado de <see cref="Resolve"/>.</summary>
        internal sealed class Resolved
        {
            public Resolved(List<Item> items, string espacio, ObjectId ownerId, int nUsuario, List<string> capas)
            {
                Items = items;
                Espacio = espacio;
                OwnerId = ownerId;
                NUsuario = nUsuario;
                Capas = capas;
            }

            /// <summary>Entidades en el orden de <c>handles</c>.</summary>
            public List<Item> Items { get; }

            /// <summary>Espacio común: "modelo" o nombre del layout.</summary>
            public string Espacio { get; }

            /// <summary>BlockTableRecord propietario común (modelo o layout).</summary>
            public ObjectId OwnerId { get; }

            public int NUsuario { get; }

            /// <summary>Capas distintas implicadas, en orden de aparición (nombre real del dibujo).</summary>
            public List<string> Capas { get; }
        }

        /// <summary>
        /// Pasos 1–5 de §5D.8 sobre <paramref name="handles"/> (ya validados por formato y sin repetidos):
        /// 1 NOT_FOUND (data.handle); 2 atributo / dentro de bloque / viewport de papel; 3 capa bloqueada o de xref
        /// (congelada o apagada → advertencia en <paramref name="advertencias"/>); 4 mismo espacio; 5 confirmación si hay
        /// entidades fuera de capas MCP-*. La primera comprobación que falla lanza <see cref="ToolException"/>.
        /// <paramref name="permitirCapaBloqueada"/> (fallo 43): solo copiar con permitir_origen_bloqueado; el origen no se
        /// modifica.
        /// </summary>
        public static Resolved Resolve(
            Database db, Transaction tr, IReadOnlyList<string> handles, bool confirmar, List<string> advertencias,
            bool permitirCapaBloqueada = false)
        {
            // Paso 1: todos los handles existen y son entidades.
            var entities = new List<(ObjectId id, Entity e)>(handles.Count);
            foreach (string h in handles)
            {
                Entity? e = AcadUtil.TryGetEntityByHandle(db, h, tr);
                if (e == null)
                {
                    throw ToolException.EntityNotFound(h);
                }

                entities.Add((e.ObjectId, e));
            }

            // Lectura de los datos de cada entidad (capa, tipo, propietario).
            var items = new List<Item>(entities.Count);
            var infos = new List<EntityInfo>(entities.Count);
            for (int i = 0; i < entities.Count; i++)
            {
                (ObjectId id, Entity e) = entities[i];
                EntityInfo info = BuildInfo(e, handles[i], tr);
                infos.Add(info);
                items.Add(new Item(id, e, info));
            }

            // Pasos 2–4 (lógica pura).
            ProtectionError? error = EntityProtection.Classify(infos, permitirCapaBloqueada);
            if (error != null)
            {
                var data = new Dictionary<string, object?>();
                if (error.Handle != null)
                {
                    data["handle"] = error.Handle;
                }

                if (error.Capa != null)
                {
                    data["capa"] = error.Capa;
                }

                if (error.Handles != null)
                {
                    data["handles"] = error.Handles;
                }

                throw ToolException.InvalidParams(error.Detalle, data);
            }

            // Paso 3 (advertencia): capas congeladas o apagadas.
            int frozen = EntityProtection.CountFrozenOrOff(infos);
            if (frozen > 0)
            {
                advertencias.Add(EntityProtection.FrozenOrOffWarning(frozen));
            }

            // Paso 5: protección de las entidades del usuario.
            int nUsuario = EntityProtection.CountUser(infos);
            string? confirm = EntityProtection.RequireConfirmation(infos, confirmar);
            if (confirm != null)
            {
                var ejemplos = new List<Dictionary<string, object?>>();
                foreach ((string handle, string capa) in EntityProtection.UserExamples(infos))
                {
                    ejemplos.Add(new Dictionary<string, object?> { ["handle"] = handle, ["capa"] = capa });
                }

                throw ToolException.InvalidParams(
                    confirm,
                    new Dictionary<string, object?> { ["n_usuario"] = nUsuario, ["ejemplos"] = ejemplos });
            }

            var capas = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EntityInfo info in infos)
            {
                if (seen.Add(info.Capa))
                {
                    capas.Add(info.Capa);
                }
            }

            ObjectId ownerId = items.Count > 0 ? items[0].Entity.OwnerId : ObjectId.Null;
            string espacio = infos.Count > 0 ? infos[0].Espacio : SpaceResolver.ModelName;
            return new Resolved(items, espacio, ownerId, nUsuario, capas);
        }

        /// <summary>Datos de §5D.8 de una entidad: capa (bloqueada / xref / congelada o apagada), tipo y propietario.</summary>
        private static EntityInfo BuildInfo(Entity e, string handle, Transaction tr)
        {
            string capa = e.Layer;
            bool locked = false;
            bool xref = capa.IndexOf('|') >= 0;
            bool hidden = false;
            if (!e.LayerId.IsNull && tr.GetObject(e.LayerId, OpenMode.ForRead, false) is LayerTableRecord layer)
            {
                capa = layer.Name;
                locked = layer.IsLocked;
                xref = xref || layer.IsDependent || layer.Name.IndexOf('|') >= 0;
                hidden = layer.IsFrozen || layer.IsOff;
            }

            bool isAttribute = e is AttributeReference;

            // Propietario directo: debe ser el modelo o un layout. Un atributo vive bajo su INSERT (paso 2 lo rechaza
            // antes), cualquier otro propietario que no sea modelo/layout es una definición de bloque.
            BlockTableRecord? owner = null;
            try
            {
                if (!e.OwnerId.IsNull)
                {
                    owner = tr.GetObject(e.OwnerId, OpenMode.ForRead, false) as BlockTableRecord;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                owner = null;
            }

            string espacio;
            bool inBlock;
            bool paperViewport = false;
            if (owner != null)
            {
                espacio = SpaceResolver.SpaceName(owner, tr);
                inBlock = espacio.StartsWith(SpaceResolver.BlockPrefix, StringComparison.Ordinal);
                bool ownerIsLayout = owner.IsLayout
                    && !string.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
                paperViewport = e is Viewport && ownerIsLayout;
            }
            else
            {
                // Sin BlockTableRecord propietario directo (p. ej. atributo → INSERT): el espacio informativo sale del
                // contenedor; si no es un atributo, la entidad no está en un espacio y no se puede modificar aquí.
                espacio = SpaceResolver.OwnerName(e, tr) ?? "?";
                inBlock = !isAttribute;
            }

            return new EntityInfo(handle, capa, locked, xref, hidden, isAttribute, paperViewport, inBlock, espacio);
        }
    }
}
