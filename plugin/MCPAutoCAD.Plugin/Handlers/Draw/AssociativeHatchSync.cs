using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Sincroniza los sombreados asociativos con su contorno después de mover/rotar/escalar (gate en vivo de Fase 3).
    /// Hecho verificado: con TransformBy del contorno desde la API (transacción en contexto de comando) AutoCAD NO
    /// recalcula el HATCH asociativo, ni tras REGEN; con el MOVE nativo sí. Por eso, en la MISMA transacción (mismo
    /// paso de deshacer), cada HATCH asociativo que reacciona a una entidad transformada se reconstruye desde su
    /// contorno: se quitan sus bucles y se vuelven a añadir con los mismos ObjectId de contorno, y EvaluateHatch(true).
    /// Es idempotente (si AutoCAD lo hubiera actualizado, el resultado es el mismo). No se toca un HATCH en capa
    /// bloqueada ni uno con algún bucle sin objetos asociados: se advierte.
    /// </summary>
    internal static class AssociativeHatchSync
    {
        private static readonly RXClass HatchClass = RXObject.GetClass(typeof(Hatch));

        public static void AfterTransform(Transaction tr, IEnumerable<ObjectId> transformed, List<string> warnings)
        {
            var hatchIds = new List<ObjectId>();
            var seen = new HashSet<ObjectId>();
            foreach (ObjectId id in transformed)
            {
                var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                ObjectIdCollection? reactors = ent.GetPersistentReactorIds();
                if (reactors == null)
                {
                    continue;
                }

                foreach (ObjectId r in reactors)
                {
                    if (!r.IsNull && !r.IsErased && r.ObjectClass.IsDerivedFrom(HatchClass) && seen.Add(r))
                    {
                        hatchIds.Add(r);
                    }
                }
            }

            foreach (ObjectId hid in hatchIds)
            {
                Rebuild(tr, hid, warnings);
            }
        }

        private static void Rebuild(Transaction tr, ObjectId hatchId, List<string> warnings)
        {
            var hatch = (Hatch)tr.GetObject(hatchId, OpenMode.ForRead);
            if (!hatch.Associative)
            {
                return;
            }

            string handle = AcadUtil.HandleToString(hatch.Handle);
            var layer = (LayerTableRecord)tr.GetObject(hatch.LayerId, OpenMode.ForRead);
            if (layer.IsLocked)
            {
                warnings.Add("El sombreado asociativo " + handle + " está en la capa bloqueada '" + layer.Name
                    + "' y no se actualizó con su contorno.");
                return;
            }

            int n = hatch.NumberOfLoops;
            var loops = new List<(HatchLoopTypes type, ObjectIdCollection ids)>(n);
            for (int i = 0; i < n; i++)
            {
                ObjectIdCollection ids = hatch.GetAssociatedObjectIdsAt(i);
                if (ids == null || ids.Count == 0)
                {
                    warnings.Add("El sombreado asociativo " + handle + " tiene un bucle sin objetos de contorno y no se actualizó.");
                    return;
                }

                // Solo los bits de clase de bucle; los de forma (Polyline, Derived, ...) no se admiten con ObjectIds.
                HatchLoopTypes type = hatch.LoopTypeAt(i) & (HatchLoopTypes.External | HatchLoopTypes.Outermost);
                loops.Add((type == 0 ? HatchLoopTypes.External : type, ids));
            }

            hatch.UpgradeOpen();
            for (int i = n - 1; i >= 0; i--)
            {
                hatch.RemoveLoopAt(i);
            }

            foreach ((HatchLoopTypes type, ObjectIdCollection ids) in loops)
            {
                hatch.AppendLoop(type, ids);
            }

            hatch.EvaluateHatch(true);
        }
    }
}
