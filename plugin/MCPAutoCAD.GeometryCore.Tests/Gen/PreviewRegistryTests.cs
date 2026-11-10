using System;
using System.Collections.Generic;
using System.Globalization;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>PreviewRegistry / PreviewRecord (protocol.md §5E.5) con reloj inyectable. Puro, sin AutoCAD.</summary>
    public class PreviewRegistryTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(-5));
        private static readonly IReadOnlyList<AnchorFingerprint> NoAnchors = Array.Empty<AnchorFingerprint>();

        private DateTimeOffset _now = T0;
        private int _seq;

        private PreviewRegistry NewRegistry(TimeSpan? ttl = null, int maxPerDoc = PreviewRegistry.DefaultMaxPerDoc) =>
            new PreviewRegistry(() => _now, ttl, maxPerDoc, () => PreviewRegistry.IdPrefix + (++_seq).ToString(CultureInfo.InvariantCulture));

        private static PreviewRecord Add(PreviewRegistry reg, string doc, params string[] handles) =>
            reg.Add(doc, "muro", "{\"eje\":[[0,0],[1,0]]}", NoAnchors, handles, "modelo", out _);

        [Fact]
        public void Defaults()
        {
            var reg = new PreviewRegistry(() => T0);
            Assert.Equal(TimeSpan.FromMinutes(15), reg.Ttl);
            Assert.Equal(20, reg.MaxPerDoc);
            PreviewRecord r = Add(reg, "doc");
            Assert.StartsWith("prev-", r.Id);
        }

        [Fact]
        public void Add_IdPrefixAndExpiry()
        {
            PreviewRegistry reg = NewRegistry(TimeSpan.FromMinutes(15));
            PreviewRecord r = reg.Add("doc", "muro", "{}", NoAnchors, new[] { "1A" }, "modelo", out PreviewRecord? evicted);
            Assert.Null(evicted);
            Assert.StartsWith(PreviewRegistry.IdPrefix, r.Id);
            Assert.Equal("prev-1", r.Id);
            Assert.Equal(T0, r.Creado);
            Assert.Equal(T0 + TimeSpan.FromMinutes(15), r.Caduca);
            Assert.Equal("doc", r.DocKey);
            Assert.Equal("muro", r.Herramienta);
            Assert.Equal("modelo", r.Espacio);
            Assert.Equal(new[] { "1A" }, r.HandlesPreview);
            Assert.Equal("2026-10-26T12:15:00.000-05:00", r.CaducaIso);
        }

        [Fact]
        public void Get_BeforeExpiry_ReturnsRecord()
        {
            PreviewRegistry reg = NewRegistry(TimeSpan.FromMinutes(15));
            PreviewRecord r = Add(reg, "doc");
            _now = T0 + TimeSpan.FromMinutes(14.99);
            Assert.Same(r, reg.Get(r.Id));
        }

        [Fact]
        public void Get_AfterExpiry_NullAndPurgeReturnsIt()
        {
            PreviewRegistry reg = NewRegistry(TimeSpan.FromMinutes(15));
            PreviewRecord r = Add(reg, "doc");
            _now = T0 + TimeSpan.FromMinutes(15);
            List<PreviewRecord> purged = reg.Purge();
            Assert.Single(purged);
            Assert.Same(r, purged[0]);
            Assert.Null(reg.Get(r.Id));
            Assert.Empty(reg.Purge());
        }

        [Fact]
        public void Get_AfterExpiry_IsNullEvenWithoutExplicitPurge()
        {
            PreviewRegistry reg = NewRegistry(TimeSpan.FromSeconds(10));
            PreviewRecord r = Add(reg, "doc");
            _now = T0 + TimeSpan.FromSeconds(11);
            Assert.Null(reg.Get(r.Id));
            Assert.Equal(0, reg.Count("doc"));
        }

        [Fact]
        public void Get_UnknownId_Null()
        {
            Assert.Null(NewRegistry().Get("prev-nada"));
        }

        [Fact]
        public void Remove_TrueThenFalse()
        {
            PreviewRegistry reg = NewRegistry();
            PreviewRecord r = Add(reg, "doc");
            Assert.True(reg.Remove(r.Id));
            Assert.False(reg.Remove(r.Id));
            Assert.Null(reg.Get(r.Id));
            Assert.False(reg.Remove("prev-otro"));
        }

        [Fact]
        public void Remove_Expired_False()
        {
            PreviewRegistry reg = NewRegistry(TimeSpan.FromMinutes(1));
            PreviewRecord r = Add(reg, "doc");
            _now = T0 + TimeSpan.FromMinutes(2);
            Assert.False(reg.Remove(r.Id));
        }

        [Fact]
        public void Cap_EvictsOldest()
        {
            PreviewRegistry reg = NewRegistry(maxPerDoc: 2);
            PreviewRecord a = Add(reg, "doc");
            _now = T0 + TimeSpan.FromSeconds(1);
            PreviewRecord b = Add(reg, "doc");
            _now = T0 + TimeSpan.FromSeconds(2);
            PreviewRecord c = reg.Add("doc", "muro", "{}", NoAnchors, Array.Empty<string>(), "modelo", out PreviewRecord? evicted);
            Assert.Same(a, evicted);
            Assert.Equal(2, reg.Count("doc"));
            Assert.Null(reg.Get(a.Id));
            Assert.Same(b, reg.Get(b.Id));
            Assert.Same(c, reg.Get(c.Id));
        }

        [Fact]
        public void Cap_IsPerDocument()
        {
            PreviewRegistry reg = NewRegistry(maxPerDoc: 1);
            Add(reg, "doc1");
            reg.Add("doc2", "muro", "{}", NoAnchors, Array.Empty<string>(), "modelo", out PreviewRecord? evicted);
            Assert.Null(evicted);
            Assert.Equal(1, reg.Count("doc1"));
            Assert.Equal(1, reg.Count("doc2"));
        }

        [Fact]
        public void Cap_ExpiredAreNotEvictedButPurged()
        {
            PreviewRegistry reg = NewRegistry(TimeSpan.FromMinutes(1), maxPerDoc: 1);
            Add(reg, "doc");
            _now = T0 + TimeSpan.FromMinutes(2);
            reg.Add("doc", "muro", "{}", NoAnchors, Array.Empty<string>(), "modelo", out PreviewRecord? evicted);
            Assert.Null(evicted);
            Assert.Equal(1, reg.Count("doc"));
        }

        [Fact]
        public void List_OrderedByCreation()
        {
            PreviewRegistry reg = NewRegistry();
            _now = T0 + TimeSpan.FromSeconds(5);
            PreviewRecord late = Add(reg, "doc");
            _now = T0 + TimeSpan.FromSeconds(6);
            PreviewRecord later = Add(reg, "doc");
            IReadOnlyList<PreviewRecord> list = reg.List("doc");
            Assert.Equal(new[] { late.Id, later.Id }, new[] { list[0].Id, list[1].Id });
        }

        [Fact]
        public void TrackedHandles_UnionOfLiveExcludingExpired()
        {
            PreviewRegistry reg = NewRegistry(TimeSpan.FromMinutes(10));
            Add(reg, "doc", "1A", "1B");
            _now = T0 + TimeSpan.FromMinutes(5);
            Add(reg, "doc", "2a", "1B");
            HashSet<string> tracked = reg.TrackedHandles("doc");
            Assert.Equal(3, tracked.Count);
            Assert.Contains("1A", tracked);
            Assert.Contains("2A", tracked); // sin distinguir mayúsculas
            Assert.Contains("1b", tracked);

            _now = T0 + TimeSpan.FromMinutes(11); // caduca el primero
            tracked = reg.TrackedHandles("doc");
            Assert.Equal(2, tracked.Count);
            Assert.DoesNotContain("1A", tracked);
            Assert.Contains("1B", tracked);
        }

        [Fact]
        public void Documents_DoNotMix()
        {
            PreviewRegistry reg = NewRegistry();
            PreviewRecord a = Add(reg, "doc1", "AA");
            PreviewRecord b = Add(reg, "doc2", "BB");
            Assert.Single(reg.List("doc1"));
            Assert.Same(a, reg.List("doc1")[0]);
            Assert.Same(b, reg.List("doc2")[0]);
            Assert.DoesNotContain("BB", reg.TrackedHandles("doc1"));
            Assert.Empty(reg.List("doc3"));
            Assert.Equal(0, reg.Count("doc3"));
            // Get no filtra por documento: lo comprueba el handler (OtherDocumentDetail).
            Assert.Equal("doc2", reg.Get(b.Id)!.DocKey);
        }

        [Fact]
        public void IdFactory_CollisionRetries()
        {
            int n = 0;
            var reg = new PreviewRegistry(() => T0, null, 5, () => n++ < 2 ? "prev-x" : "prev-y");
            PreviewRecord first = Add(reg, "doc");
            PreviewRecord second = Add(reg, "doc");
            Assert.Equal("prev-x", first.Id);
            Assert.Equal("prev-y", second.Id);
        }

        [Fact]
        public void Messages_ContainIdHandleCount()
        {
            Assert.Equal("No existe ninguna previsualización con id 'prev-9' (caducada, descartada, confirmada o de otra sesión).",
                PreviewRegistry.NotFoundMessage("prev-9"));
            Assert.Equal("preview_id: la previsualización 'prev-9' pertenece a otro documento; active ese documento o vuelva a previsualizar",
                PreviewRegistry.OtherDocumentDetail("prev-9"));
            Assert.Equal("preview_id: la entidad ancla 2A3F cambió o ya no existe desde la previsualización; no se escribió nada; vuelva a previsualizar",
                PreviewRegistry.AnchorChangedDetail("2A3F"));
            Assert.Contains("'prev-7'", PreviewRegistry.EvictedWarning("prev-7"));
            Assert.Contains("MCP-PREVIEW", PreviewRegistry.EvictedWarning("prev-7"));
            Assert.StartsWith("Hay 12 entidad(es) en MCP-PREVIEW", PreviewRegistry.OrphansWarning(12));
            Assert.Contains("{\"confirmar\": true, \"capa\": \"MCP-PREVIEW\"}", PreviewRegistry.OrphansWarning(1));
            Assert.StartsWith("Hay al menos 3 entidad(es) en MCP-PREVIEW", PreviewRegistry.OrphansWarningPartial(3));
            Assert.Contains("tope de coste", PreviewRegistry.OrphansWarningPartial(3));
            Assert.Contains("{\"confirmar\": true, \"capa\": \"MCP-PREVIEW\"}", PreviewRegistry.OrphansWarningPartial(3));
        }

        [Fact]
        public void Ctor_InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewRegistry(() => T0, TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewRegistry(() => T0, TimeSpan.FromSeconds(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewRegistry(() => T0, null, 0));
            Assert.Throws<ArgumentNullException>(() => new PreviewRegistry(null!));
        }
    }
}
