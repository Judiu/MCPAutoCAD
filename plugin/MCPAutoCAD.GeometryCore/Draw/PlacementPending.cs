using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Cajas de R4 (protocol.md §5D.0) que aún no caben en la extensión PARCIAL que se va construyendo al recorrer el
    /// espacio destino. Sirve solo para cortar la pasada en cuanto todas caben; el veredicto final lo da siempre
    /// <see cref="MarkPlacement.OutsideIndices"/> con la extensión final. Correcto porque la extensión solo crece y la
    /// ampliación (10 % del lado mayor) crece con ella: una caja que cabe ya no deja de caber, así que se retira del
    /// conjunto y no se vuelve a mirar. El llamador refresca solo cuando la extensión cambió y como mucho una vez
    /// cada <see cref="PlacementBudget.CheckEvery"/> entidades: el coste deja de ser O(entidades × cajas). Puro.
    /// </summary>
    public sealed class PlacementPending
    {
        private readonly IReadOnlyList<BBox2> _boxes;
        private readonly List<int> _pending;
        private readonly double _fraction;

        public PlacementPending(IReadOnlyList<BBox2> boxes, double fraction = MarkPlacement.DefaultMarginFraction)
        {
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _fraction = fraction;
            _pending = new List<int>(boxes.Count);
            for (int i = 0; i < boxes.Count; i++)
            {
                _pending.Add(i);
            }
        }

        /// <summary>Cajas que aún no caben en la última extensión refrescada.</summary>
        public int Count => _pending.Count;

        /// <summary>
        /// Retira las cajas que caben en <paramref name="extents"/> (ampliada) y devuelve true si ya no queda ninguna.
        /// Sin extensión (null) no retira nada y devuelve false: un espacio sin extensión no permite cortar la pasada.
        /// </summary>
        public bool Refresh(BBox2? extents)
        {
            if (extents == null)
            {
                return false;
            }

            BBox2 expanded = MarkPlacement.Expand(extents.Value, _fraction);
            _pending.RemoveAll(i => _boxes[i].Intersects(expanded));
            return _pending.Count == 0;
        }
    }
}
