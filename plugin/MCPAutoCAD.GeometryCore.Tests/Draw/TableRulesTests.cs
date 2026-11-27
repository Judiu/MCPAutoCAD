using System;
using System.Collections.Generic;
using Xunit;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class TableRulesTests
    {
        [Fact]
        public void ValidateDimensions_AcceptsValidAndRejectsOutOfRange()
        {
            Assert.Null(TableRules.ValidateDimensions(19, 7));
            Assert.Null(TableRules.ValidateDimensions(1, 1));
            Assert.NotNull(TableRules.ValidateDimensions(0, 5));
            Assert.NotNull(TableRules.ValidateDimensions(201, 2));
            Assert.NotNull(TableRules.ValidateDimensions(5, 51));
            Assert.NotNull(TableRules.ValidateDimensions(200, 50)); // 10000 > 2500
        }

        [Fact]
        public void ResolveColumnWidths_SingleAndArray()
        {
            double[] uniform = TableRules.ResolveColumnWidths(3, 2.5, null);
            Assert.Equal(new[] { 2.5, 2.5, 2.5 }, uniform);

            double[] custom = TableRules.ResolveColumnWidths(3, null, new[] { 1.0, 2.0, 3.0 });
            Assert.Equal(new[] { 1.0, 2.0, 3.0 }, custom);

            Assert.Throws<ArgumentException>(() => TableRules.ResolveColumnWidths(3, null, null));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveColumnWidths(3, 2.0, new[] { 1.0, 2.0, 3.0 }));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveColumnWidths(3, null, new[] { 1.0, 2.0 }));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveColumnWidths(3, 0.0, null));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveColumnWidths(3, null, new[] { 1.0, -0.5, 2.0 }));
        }

        [Fact]
        public void ResolveRowHeights_SingleAndArray()
        {
            double[] uniform = TableRules.ResolveRowHeights(2, 0.4, null);
            Assert.Equal(new[] { 0.4, 0.4 }, uniform);

            double[] custom = TableRules.ResolveRowHeights(2, null, new[] { 0.6, 0.35 });
            Assert.Equal(new[] { 0.6, 0.35 }, custom);

            Assert.Throws<ArgumentException>(() => TableRules.ResolveRowHeights(2, null, null));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveRowHeights(2, 0.4, new[] { 0.4, 0.4 }));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveRowHeights(2, null, new[] { 0.4 }));
        }

        [Fact]
        public void ValidateCellsAndMerges_ValidatesMergesCoveredCellsAndFormat()
        {
            var cells = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, Texto: "CUADRO DE AREAS", Alineacion: "MC", Combinar: new TableMerge(0, 0, 1, 3)),
                new TableCellSpec(1, 0, Texto: "PISO", Alineacion: "ML"),
                new TableCellSpec(1, 1, TextoFormato: "\\LArea\\l", Alineacion: "MR"),
            };

            List<TableMerge> merges = TableRules.ValidateCellsAndMerges(3, 3, cells);
            Assert.Single(merges);
            Assert.Equal(new TableMerge(0, 0, 1, 3), merges[0]);
        }

        [Fact]
        public void ValidateCellsAndMerges_RejectsCoveredCellDuplicateOverlapAndBadFormat()
        {
            // Celda cubierta por merge
            var covered = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, Texto: "Titulo", Combinar: new TableMerge(0, 0, 1, 2)),
                new TableCellSpec(0, 1, Texto: "Cubierta"),
            };
            Assert.Throws<ArgumentException>(() => TableRules.ValidateCellsAndMerges(2, 2, covered));

            // Celda duplicada
            var dup = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, Texto: "A"),
                new TableCellSpec(0, 0, Texto: "B"),
            };
            Assert.Throws<ArgumentException>(() => TableRules.ValidateCellsAndMerges(2, 2, dup));

            // Solapamiento entre merges
            var overlap = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, Combinar: new TableMerge(0, 0, 2, 2)),
                new TableCellSpec(1, 2, Combinar: new TableMerge(1, 1, 1, 2)),
            };
            Assert.Throws<ArgumentException>(() => TableRules.ValidateCellsAndMerges(3, 3, overlap));

            // Ambos texto y texto_formato
            var both = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, Texto: "A", TextoFormato: "B"),
            };
            Assert.Throws<ArgumentException>(() => TableRules.ValidateCellsAndMerges(2, 2, both));

            // MTEXT mal formado
            var badFmt = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, TextoFormato: "{sin cerrar"),
            };
            Assert.Throws<ArgumentException>(() => TableRules.ValidateCellsAndMerges(2, 2, badFmt));

            // Alineación inválida
            var badAlign = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, Texto: "A", Alineacion: "XX"),
            };
            Assert.Throws<ArgumentException>(() => TableRules.ValidateCellsAndMerges(2, 2, badAlign));

            // Merge trivial 1x1 no produce error y no se añade
            var trivial = new List<TableCellSpec>
            {
                new TableCellSpec(0, 0, Texto: "A", Combinar: new TableMerge(0, 0, 1, 1)),
            };
            Assert.Empty(TableRules.ValidateCellsAndMerges(2, 2, trivial));
        }

        [Fact]
        public void EstimateBBox_GrowsRightAndDown()
        {
            BBox2 box = TableRules.EstimateBBox(new Point3(10, 20, 0), new[] { 2.0, 3.0 }, new[] { 1.0, 1.5 });
            Assert.Equal(10.0, box.Xmin, 6);
            Assert.Equal(15.0, box.Xmax, 6);
            Assert.Equal(17.5, box.Ymin, 6);
            Assert.Equal(20.0, box.Ymax, 6);
        }

        [Fact]
        public void ComputeSafeMargins_AdjustsLargeStandardMarginsInMeters()
        {
            // Estilo Standard (margen 1.5) frente a fila 0.40 m y texto 0.15 m
            SafeMarginsResult res = TableRules.ComputeSafeMargins(1.5, 1.5, 0.40, 1.0, 0.15, null, null);
            Assert.True(res.AutoAdjusted);
            Assert.NotNull(res.Vertical);
            Assert.NotNull(res.Horizontal);
            Assert.True(res.Vertical!.Value <= 0.12);
            Assert.True(res.Horizontal!.Value <= 0.25);

            // Márgenes pequeños ya válidos no se ajustan
            SafeMarginsResult ok = TableRules.ComputeSafeMargins(0.05, 0.05, 0.40, 1.0, 0.15, null, null);
            Assert.False(ok.AutoAdjusted);
            Assert.Null(ok.Vertical);
            Assert.Null(ok.Horizontal);

            // Explícitos se respetan sin marcar AutoAdjusted
            SafeMarginsResult req = TableRules.ComputeSafeMargins(1.5, 1.5, 0.40, 1.0, 0.15, 0.08, 0.10);
            Assert.False(req.AutoAdjusted);
            Assert.Equal(0.08, req.Vertical);
            Assert.Equal(0.10, req.Horizontal);

            // Fallo 53 (papel en metros: col 0.0325, texto 0.0033): reduce drásticamente el margen 1.5
            SafeMarginsResult paperM = TableRules.ComputeSafeMargins(1.5, 1.5, 0.008, 0.0325, 0.0033, null, null);
            Assert.True(paperM.AutoAdjusted);
            Assert.True(paperM.Horizontal!.Value <= 0.002);
            Assert.True(paperM.Vertical!.Value <= 0.002);

            // Fallo 53 (espacio modelo: col 1.625, texto 0.165): margen 1.5 del estilo Standard también se reduce
            SafeMarginsResult modelM = TableRules.ComputeSafeMargins(1.5, 1.5, 0.40, 1.625, 0.165, null, null);
            Assert.True(modelM.AutoAdjusted);
            Assert.True(modelM.Horizontal!.Value < 0.10);
        }

        [Fact]
        public void PlanInsertAndDelete_OrdersDescendingAndValidatesBounds()
        {
            var insOps = new[]
            {
                new TableStructuralOp(1, 2, 0.4, 0),
                new TableStructuralOp(5, 1, 0.5, null),
            };
            (List<TableStructuralOp> orderedIns, int newRows) = TableRules.PlanInsert(5, 4, TableRules.MaxRows, true, insOps);
            Assert.Equal(8, newRows);
            Assert.Equal(5, orderedIns[0].Indice);
            Assert.Equal(1, orderedIns[1].Indice);

            var delOps = new[]
            {
                new TableStructuralOp(0, 1),
                new TableStructuralOp(3, 2),
            };
            (List<TableStructuralOp> orderedDel, int remRows) = TableRules.PlanDelete(6, true, delOps);
            Assert.Equal(3, remRows);
            Assert.Equal(3, orderedDel[0].Indice);
            Assert.Equal(0, orderedDel[1].Indice);

            // Solapamiento en borrado
            Assert.Throws<ArgumentException>(() =>
                TableRules.PlanDelete(6, true, new[] { new TableStructuralOp(1, 3), new TableStructuralOp(3, 1) }));

            // Borrar todas las filas
            Assert.Throws<ArgumentException>(() =>
                TableRules.PlanDelete(3, true, new[] { new TableStructuralOp(0, 3) }));
        }

        [Fact]
        public void ResolveRowSlice_HandlesDefaultsAndBounds()
        {
            Assert.Equal((0, 9), TableRules.ResolveRowSlice(10, null, null));
            Assert.Equal((2, 5), TableRules.ResolveRowSlice(10, 2, 5));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveRowSlice(10, 5, 2));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveRowSlice(10, -1, 2));
            Assert.Throws<ArgumentException>(() => TableRules.ResolveRowSlice(10, 0, 10));
        }
    }
}
