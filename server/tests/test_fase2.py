"""Pruebas de la Fase 2 (protocolo 0.3, protocol.md §5C) a través del cliente MCP.

El servidor es genérico: estas pruebas comprueban que las 10 herramientas de
auditoría se publican y se llaman sin código propio en server.py, con el
plugin simulado de fake_plugin.py (FakeTransport en memoria, sin pipes reales).
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from fake_plugin import (
    CLAVES_HALLAZGO,
    ETIQUETA_CONTORNO_RE,
    HERRAMIENTAS_FASE3,
    REGLAS_AUDITORIA,
    SEVERIDADES,
    SUFIJO_FUERA_DE_LAYOUT,
    manifest_tools,
)
from mcp import Client
from test_server import MANIFEST_ORDER, World, _guardar_manifest, run, text_of

import mcp_autocad.server as server_module
from mcp_autocad.client import PipeClient
from mcp_autocad.discovery import Instance
from mcp_autocad.errors import PluginError

FASE2 = [
    "auditar_capas",
    "entidades_en_capa_0",
    "auditar_bloques",
    "areas_por_capa",
    "verificar_cuadro_areas",
    "duplicados",
    "textos_fuera_de_escala",
    "auditar_dibujo",
    "marcar_hallazgos",
    "limpiar_observaciones",
]
ESCRITURA = ["linea", "marcar_hallazgos", "limpiar_observaciones"]
UNIDADES = ("pid", "insunits", "unidades", "advertencias")
NO_INSTANCE = "AutoCAD no está abierto o el plugin no cargó."
MARCA = {"bbox": [[0, 0], [5, 3]], "texto": "Revisar muro", "severidad": "alta"}

# Argumentos mínimos y campos obligatorios del result según los ejemplos de §5C.
CASOS: dict[str, tuple[dict[str, Any], list[str]]] = {
    "auditar_capas": ({}, ["capas", "total", "cursor_siguiente", "resumen", "estandar_disponible"]),
    "entidades_en_capa_0": ({}, ["total", "por_tipo", "por_espacio", "entidades", "cursor_siguiente"]),
    "auditar_bloques": ({}, ["resumen", "hallazgos", "total", "cursor_siguiente"]),
    "areas_por_capa": ({}, ["espacio", "capas", "capas_truncadas", "unidad_area"]),
    "verificar_cuadro_areas": ({}, ["espacio", "cuadros", "n_textos_leidos", "hallazgos", "total"]),
    "duplicados": ({}, ["espacio", "tolerancia", "resumen", "grupos", "total", "cursor_siguiente"]),
    "textos_fuera_de_escala": (
        {},
        ["espacio", "mediana", "n_textos", "resumen", "textos", "total", "cursor_siguiente"],
    ),
    "auditar_dibujo": (
        {},
        [
            "espacio",
            "resumen",
            "reglas_omitidas",
            "reglas_con_error",
            "duracion_ms",
            "hallazgos",
            "total",
            "cursor_siguiente",
        ],
    ),
    "marcar_hallazgos": (
        {"marcas": [MARCA]},
        ["marcas", "handles", "n_marcas", "capa", "capa_creada", "espacio"],
    ),
    "limpiar_observaciones": ({"confirmar": True, "capa": "MCP-DIBUJO"}, ["capa", "n_borradas", "por_espacio"]),
}


async def _ok(c: Client, name: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
    res = await c.call_tool(name, args or {})
    assert not res.is_error, text_of(res)
    data = json.loads(text_of(res))
    assert res.structured_content == data
    return data


def _assert_hallazgo(h: dict[str, Any], regla: str | None = None) -> None:
    assert list(h) == CLAVES_HALLAZGO
    assert h["severidad"] in SEVERIDADES
    assert h["regla"] in REGLAS_AUDITORIA
    if regla is not None:
        assert h["regla"] == regla
    prefijo, n = h["id"].rsplit("-", 1)
    assert prefijo == h["regla"] and n.isdigit() and int(n) >= 1
    assert isinstance(h["handles"], list) and len(h["handles"]) <= 50
    assert isinstance(h["datos"], dict)
    assert h["bbox"] is None or (len(h["bbox"]) == 2 and all(len(p) == 2 for p in h["bbox"]))


# ---------------------------------------------------------------------------
# Publicación (tools/list) y manifest
# ---------------------------------------------------------------------------


def test_tools_list_expone_las_10_de_fase2() -> None:
    world = World(18244)
    esperado = {t["name"]: t for t in manifest_tools()}

    async def body(c: Client) -> None:
        tools = {t.name: t for t in (await c.list_tools()).tools}
        for name in FASE2:
            tool = tools[name]
            assert tool.input_schema["type"] == "object", name
            assert tool.input_schema["additionalProperties"] is False, name
            assert tool.input_schema == esperado[name]["inputSchema"], name
            assert tool.description == esperado[name]["description"], name

    run(world.server(), body)


def test_manifest_orden_y_escritura() -> None:
    tools = manifest_tools()
    assert [t["name"] for t in tools] == MANIFEST_ORDER
    # Desde 0.4 las 14 de Fase 3 (§5D) también son de escritura, después de las 3 anteriores (y desde 0.5,
    # detrás, las 8 de escritura de la Fase 4A, §5E, y desde 0.6 las 4 de la Fase L, §5F: se comprueban en
    # test_fase4a.py y test_faseL.py).
    assert [t["name"] for t in tools if t["escritura"]][: len(ESCRITURA) + 14] == ESCRITURA + HERRAMIENTAS_FASE3
    for t in tools:
        if t["name"] in FASE2:
            assert t["requiere_documento"] is True, t["name"]


def test_schemas_fase2_estrictos() -> None:
    schemas = {t["name"]: t["inputSchema"] for t in manifest_tools()}
    marcar = schemas["marcar_hallazgos"]
    assert marcar["required"] == ["marcas"]
    assert (marcar["properties"]["marcas"]["minItems"], marcar["properties"]["marcas"]["maxItems"]) == (1, 100)
    item = marcar["properties"]["marcas"]["items"]
    assert item["additionalProperties"] is False
    assert item["properties"]["severidad"]["enum"] == ["alta", "media", "baja"]
    assert (item["properties"]["texto"]["minLength"], item["properties"]["texto"]["maxLength"]) == (1, 500)
    assert marcar["properties"]["capa"]["default"] == "MCP-OBSERVACIONES"
    assert schemas["auditar_dibujo"]["properties"]["reglas"]["items"]["enum"] == REGLAS_AUDITORIA
    dup = schemas["duplicados"]["properties"]["tolerancia"]
    assert (dup["minimum"], dup["maximum"], dup["default"]) == (1e-12, 1, 1e-6)
    assert schemas["limpiar_observaciones"]["properties"]["confirmar"]["type"] == "boolean"
    for name in FASE2:
        assert schemas[name]["additionalProperties"] is False


# ---------------------------------------------------------------------------
# Una llamada por herramienta: forma del result
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", FASE2)
def test_call_tool_forma_del_result(name: str) -> None:
    world = World(18244)
    args, campos = CASOS[name]

    async def body(c: Client) -> None:
        data = await _ok(c, name, args)
        for campo in (*UNIDADES, *campos):
            assert campo in data, campo
        assert data["pid"] == 18244
        assert data["insunits"] == 6 and data["unidades"] == "m"
        if name in ESCRITURA:
            assert "origen_texto" not in data
        else:
            assert data["origen_texto"] == "contenido_del_plano"

    run(world.server(), body)
    assert [r["params"] for r in world.states[18244].requests if r["method"] == name] == [args]


# ---------------------------------------------------------------------------
# Idempotencia: las escrituras no se reintentan
# ---------------------------------------------------------------------------


class _SpyWorld(World):
    """World que registra el argumento idempotent con que el servidor llama al PipeClient."""

    def __init__(self, *pids: int) -> None:
        super().__init__(*pids)
        self.calls: list[tuple[str, bool]] = []

    def client_factory(self, inst: Instance) -> PipeClient:
        client = super().client_factory(inst)
        original = client.call

        def spy(method: str, params: Any = None, *args: Any, **kw: Any) -> Any:
            self.calls.append((method, bool(kw.get("idempotent", False))))
            return original(method, params, *args, **kw)

        client.call = spy  # type: ignore[method-assign]
        return client


def test_escrituras_no_idempotentes() -> None:
    world = _SpyWorld(18244)

    async def body(c: Client) -> None:
        await _ok(c, "auditar_dibujo")
        await _ok(c, "marcar_hallazgos", {"marcas": [MARCA]})
        await _ok(c, "limpiar_observaciones", {"confirmar": True})
        await _ok(c, "linea", {"inicio": [0, 0], "fin": [1, 0]})
        await _ok(c, "duplicados")

    run(world.server(), body)
    por_metodo = {m: i for m, i in world.calls if m != "tools/manifest"}
    assert por_metodo == {
        "auditar_dibujo": True,
        "marcar_hallazgos": False,
        "limpiar_observaciones": False,
        "linea": False,
        "duplicados": True,
    }


def test_servidor_sin_ramas_por_herramienta_de_fase2() -> None:
    fuente = Path(server_module.__file__).read_text(encoding="utf-8")
    # Desde 0.8 el servidor sí nombra auditar_archivo y auditar_carpeta (§5H.3); las de Fase 2 siguen sin código propio.
    for name in FASE2:
        assert name not in fuente, name


# ---------------------------------------------------------------------------
# Herramientas de lectura
# ---------------------------------------------------------------------------


def test_auditar_capas_clasificacion_y_paginacion() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "auditar_capas")
        clasif = {r["nombre"]: r["clasificacion"] for r in data["capas"]}
        assert clasif == {
            "0": "estandar",
            "A-WALL-FULL": "ajena_revit_aia",
            "Layer9": "generica",
            "MCP-DIBUJO": "mcp",
            "MUROS": "estandar",
            "PUERTAS": "estandar",
            "Rotulo": "estandar",
            "TEXTOS": "estandar",
        }
        sugerencias = {r["nombre"]: r["sugerencia"] for r in data["capas"]}
        # Como LayerClassifier: sin palabra clave en español no hay 'sugerencia' (WALL → MUROS es sugerencia_patron).
        assert sugerencias["A-WALL-FULL"] is None and sugerencias["Layer9"] is None
        assert list(data["resumen"]["por_clasificacion"]) == [
            "estandar",
            "estandar_con_diferencias",
            "cype",
            "mcp",
            "ajena_revit_aia",
            "ajena_exportacion",
            "generica",
            "fuera_de_estandar",
        ]
        assert data["total"] == data["resumen"]["n_capas"] == 8
        p1 = await _ok(c, "auditar_capas", {"limit": 3})
        assert [r["nombre"] for r in p1["capas"]] == ["0", "A-WALL-FULL", "Layer9"]
        assert p1["cursor_siguiente"] == "3"
        p2 = await _ok(c, "auditar_capas", {"limit": 3, "cursor": "3"})
        assert p2["capas"][0]["nombre"] == "MCP-DIBUJO"
        solo = await _ok(c, "auditar_capas", {"clasificacion": ["generica", "ajena_revit_aia"]})
        assert [r["nombre"] for r in solo["capas"]] == ["A-WALL-FULL", "Layer9"]
        assert solo["resumen"]["n_capas"] == 8

    run(world.server(), body)


def test_entidades_en_capa_0() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "entidades_en_capa_0")
        assert data["total"] == 2
        assert data["por_tipo"] == {"INSERT": 1, "LWPOLYLINE": 1}
        assert data["por_espacio"] == {"modelo": 2}
        assert [e["handle"] for e in data["entidades"]] == ["1A3", "1AC"]
        assert set(data["entidades"][0]) == {"handle", "tipo", "espacio", "bbox"}
        pag = await _ok(c, "entidades_en_capa_0", {"limit": 1})
        assert pag["cursor_siguiente"] == "1" and len(pag["entidades"]) == 1

    run(world.server(), body)


def test_auditar_bloques_resumen_y_hallazgos() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "auditar_bloques")
        assert data["resumen"] == {
            "n_definiciones": 2,
            "n_anonimos": 1,
            "n_xrefs": 1,
            "unidades_distintas": 1,
            "nombres_sospechosos": 1,
            "sin_uso": 1,
        }
        assert data["total"] == 3
        for h in data["hallazgos"]:
            _assert_hallazgo(h, "auditar_bloques")
        sev = [h["severidad"] for h in data["hallazgos"]]
        assert sev == sorted(sev, key=SEVERIDADES.index)
        dp = next(h for h in data["hallazgos"] if h["datos"].get("unidades_bloque") == 4)
        assert dp["datos"]["factor_escala"] == pytest.approx(0.001)
        assert dp["handles"] == ["1A4", "1A7"]
        basura = [h for h in data["hallazgos"] if h["datos"]["bloque"] == "BASURA_jgytfyuh"]
        assert {h["datos"].get("motivo") for h in basura} >= {"sin_vocales"}
        solo = await _ok(c, "auditar_bloques", {"seccion": ["sin_uso"]})
        assert solo["total"] == 1 and solo["resumen"]["unidades_distintas"] == 1

    run(world.server(), body)


def test_areas_por_capa() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "areas_por_capa")
        assert data["capas"] == [
            {"capa": "0", "n": 1, "area_total": 15.0, "n_sin_area": 0, "por_tipo": {"LWPOLYLINE": 1}}
        ]
        assert data["unidad_area"] == "m²"
        assert "entidades" not in data and "total" not in data and "cursor_siguiente" not in data
        det = await _ok(c, "areas_por_capa", {"detalle": True})
        assert det["entidades"][0]["handle"] == "1A3" and det["entidades"][0]["area"] == 15.0
        assert det["total"] == 1 and det["cursor_siguiente"] is None
        malo = await c.call_tool("areas_por_capa", {"cursor": "0"})
        assert malo.is_error
        assert text_of(malo) == "[INVALID_PARAMS] Parámetros inválidos: cursor: solo aplica con detalle"

    run(world.server(), body)


def test_verificar_cuadro_areas() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "verificar_cuadro_areas")
        assert len(data["cuadros"]) == 1
        cuadro = data["cuadros"][0]
        assert cuadro["id"] == "cuadro-1"
        assert cuadro["valores"] == {"LOTE": 72.0, "AREA PISO 1": 52.5, "TOTAL CONSTRUIDO": 52.5, "OCUPACION": 0.73}
        assert data["hallazgos"] == [] and data["total"] == 0
        # 0.3.1 (F2/F3): campos de cuadro siempre presentes; sin viewports evaluables → en_layout true.
        assert cuadro["en_layout"] is True
        assert cuadro["reglas_evaluadas"] == ["suma_pisos", "ocupacion"]
        assert cuadro["reglas_no_evaluables"] == [
            {"regla": "libre", "motivo": "falta LIBRE"},
            {"regla": "construccion", "motivo": "falta CONSTRUCCION"},
        ]
        con = await _ok(c, "verificar_cuadro_areas", {"contornos": {"LOTE": "1A3"}})
        assert con["total"] == 1
        h = con["hallazgos"][0]
        _assert_hallazgo(h, "verificar_cuadro_areas")
        assert h["severidad"] == "alta" and h["datos"]["regla"] == "contorno_vs_declarado"
        assert (h["datos"]["esperado"], h["datos"]["declarado"]) == (15.0, 72.0)
        assert "1A3" in h["handles"] and h["espacio"] == "modelo"
        malo = await c.call_tool("verificar_cuadro_areas", {"contornos": {"LOTE": "FFFF"}})
        assert malo.is_error
        assert text_of(malo) == "[NOT_FOUND] No existe ninguna entidad con handle 'FFFF'."

    run(world.server(), body)


# ---------------------------------------------------------------------------
# Correcciones 0.3.1 (gate de Fase 2: F2, F3, F4, F8 y desviaciones documentadas)
# ---------------------------------------------------------------------------


def test_contornos_claves_validas_del_contrato() -> None:
    """§5C.5: solo LOTE, LIBRE, TOTAL CONSTRUIDO, REMATE PUNTO FIJO y AREA PISO n (1–999)."""
    assert ETIQUETA_CONTORNO_RE == r"^(LOTE|LIBRE|TOTAL CONSTRUIDO|REMATE PUNTO FIJO|AREA PISO [1-9][0-9]{0,2})$"
    schema = next(t for t in manifest_tools() if t["name"] == "verificar_cuadro_areas")["inputSchema"]
    assert schema["properties"]["contornos"]["propertyNames"]["pattern"] == ETIQUETA_CONTORNO_RE
    world = World(18244)

    async def body(c: Client) -> None:
        for etiqueta in ("OCUPACION", "CONSTRUCCION", "TITULO", "AREA PISO 01"):
            res = await c.call_tool("verificar_cuadro_areas", {"contornos": {etiqueta: "1A3"}})
            assert res.is_error
            assert text_of(res) == (
                f"[INVALID_PARAMS] Parámetros inválidos: contornos: etiqueta no reconocida '{etiqueta}'"
            )

    run(world.server(), body)


def test_verificar_cuadro_areas_fuera_de_layout() -> None:
    """F3: dos pasadas de cuadros_discrepantes (alta en layout, media con un cuadro fuera)."""
    world = World(18244, cuadros_fuera_de_layout=True)

    async def body(c: Client) -> None:
        data = await _ok(c, "verificar_cuadro_areas", {"espacio": "modelo"})
        cuadros = {c_["valores"]["LOTE"]: c_ for c_ in data["cuadros"]}
        assert set(cuadros) == {72.0, 73.11, 72.10}
        for c_ in data["cuadros"]:
            assert isinstance(c_["en_layout"], bool)
            assert isinstance(c_["reglas_evaluadas"], list)
            assert all(set(r) == {"regla", "motivo"} for r in c_["reglas_no_evaluables"])
        assert cuadros[72.0]["en_layout"] is True and cuadros[73.11]["en_layout"] is True
        resto = cuadros[72.10]
        assert resto["en_layout"] is False
        assert resto["reglas_evaluadas"] == []
        assert resto["reglas_no_evaluables"][0] == {"regla": "suma_pisos", "motivo": "falta AREA PISO n"}
        assert any(a.startswith("1 cuadro(s) fuera de los viewports de los layouts (") for a in data["advertencias"])
        discrepantes = [h for h in data["hallazgos"] if h["datos"]["regla"] == "cuadros_discrepantes"]
        por_clave = {(h["datos"]["etiqueta"], h["datos"]["ambito"]): h for h in discrepantes}
        assert set(por_clave) == {
            ("LOTE", "en_layout"),
            ("OCUPACION", "en_layout"),
            ("LOTE", "con_fuera_de_layout"),
            ("TOTAL CONSTRUIDO", "con_fuera_de_layout"),
        }
        for (_etiqueta, ambito), h in por_clave.items():
            _assert_hallazgo(h, "verificar_cuadro_areas")
            if ambito == "en_layout":
                assert h["severidad"] == "alta"
                assert "cuadros_fuera_de_layout" not in h["datos"]
                assert not h["mensaje"].endswith(SUFIJO_FUERA_DE_LAYOUT)
            else:
                assert h["severidad"] == "media"
                assert h["mensaje"].endswith(SUFIJO_FUERA_DE_LAYOUT)
                assert h["datos"]["cuadros_fuera_de_layout"] == [resto["id"]]
        # Sin hallazgos internos: los cuadros en layout son coherentes y el resto no permite evaluar reglas.
        assert {h["datos"]["regla"] for h in data["hallazgos"]} == {"cuadros_discrepantes"}
        # auditar_dibujo usa la misma detección: 2 alta + 2 media en la regla.
        audit = await _ok(c, "auditar_dibujo", {"espacio": "modelo", "reglas": ["verificar_cuadro_areas"]})
        fila = audit["resumen"]["por_regla"]["verificar_cuadro_areas"]
        assert (fila["alta"], fila["media"], fila["n"]) == (2, 2, 4)

    run(world.server(), body)


def test_auditar_dibujo_resumen_n_cuadros_fuera_de_layout() -> None:
    """§5C.8: resumen.n_cuadros_fuera_de_layout (entero ≥ 0; 0 si la regla del cuadro no se ejecuta)."""
    world = World(18244, cuadros_fuera_de_layout=True)

    async def body(c: Client) -> None:
        todo = await _ok(c, "auditar_dibujo", {"espacio": "modelo"})
        assert todo["resumen"]["n_cuadros_fuera_de_layout"] == 1
        solo = await _ok(c, "auditar_dibujo", {"espacio": "modelo", "reglas": ["verificar_cuadro_areas"]})
        assert solo["resumen"]["n_cuadros_fuera_de_layout"] == 1
        sin_regla = await _ok(c, "auditar_dibujo", {"espacio": "modelo", "reglas": ["unidades"]})
        assert sin_regla["resumen"]["n_cuadros_fuera_de_layout"] == 0

    run(world.server(), body)

    base = World(18244)

    async def body_base(c: Client) -> None:
        data = await _ok(c, "auditar_dibujo")
        n = data["resumen"]["n_cuadros_fuera_de_layout"]
        assert isinstance(n, int) and n == 0

    run(base.server(), body_base)


def test_auditar_capas_resumen_sobre_todas_las_capas() -> None:
    """F8: el filtro solo afecta a capas/total; resumen siempre sobre todas las capas."""
    world = World(18244)

    async def body(c: Client) -> None:
        todo = await _ok(c, "auditar_capas")
        filtro = await _ok(c, "auditar_capas", {"clasificacion": ["generica"]})
        assert filtro["total"] == 1 < todo["total"]
        assert filtro["resumen"] == todo["resumen"]
        por = filtro["resumen"]["por_clasificacion"]
        assert sum(por.values()) == filtro["resumen"]["n_capas"] == 8
        assert filtro["total"] == por["generica"]
        sin_vacias = await _ok(c, "auditar_capas", {"incluir_vacias": False, "limit": 1})
        assert sin_vacias["resumen"]["n_vacias"] == todo["resumen"]["n_vacias"] > 0
        assert sin_vacias["total"] == todo["total"] - todo["resumen"]["n_vacias"]

    run(world.server(), body)


def test_auditar_bloques_compensacion_por_insercion() -> None:
    """F4: compensadas → baja; no compensadas con tamaño real plausible → media (metadato); si no → alta."""
    world = World(18244, bloques_compensacion=True)

    async def body(c: Client) -> None:
        data = await _ok(c, "auditar_bloques", {"seccion": ["unidades_distintas"]})
        por_bloque = {h["datos"]["bloque"]: h for h in data["hallazgos"]}
        assert set(por_bloque) == {"DP", "VENTANA_MM", "LAV_MM", "MESA_MM", "C_MM", "FLECHA_MM"}
        nuevos = (
            "n_compensadas",
            "n_no_compensadas",
            "escalas_no_compensadas",
            "inserciones_evaluadas",
            "muestra_parcial",
            "tamano_ejemplo",
            "tamano_real_m",
            "metadato_unidades_incorrecto",
        )
        for h in por_bloque.values():
            _assert_hallazgo(h, "auditar_bloques")
            for clave in nuevos:
                assert clave in h["datos"], clave
        ventana = por_bloque["VENTANA_MM"]
        assert ventana["severidad"] == "baja"
        assert ventana["handles"] == []
        assert (ventana["datos"]["n_compensadas"], ventana["datos"]["n_no_compensadas"]) == (2, 0)
        assert ventana["datos"]["escalas_no_compensadas"] == []
        assert "compensado" in ventana["mensaje"] and "0.001" in ventana["mensaje"]
        assert ventana["datos"]["metadato_unidades_incorrecto"] is False
        assert ventana["datos"]["tamano_real_m"] is None
        lav = por_bloque["LAV_MM"]
        assert lav["severidad"] == "media"
        assert lav["handles"] == ["1F3"]
        assert lav["datos"]["metadato_unidades_incorrecto"] is True
        assert lav["datos"]["tamano_real_m"] == pytest.approx([0.45, 0.51])
        assert lav["mensaje"] == (
            "El bloque 'LAV_MM' declara unidades mm pero su geometría ya está en las unidades del dibujo "
            "(tamaño real ≈ 0.45×0.51 m): el metadato de unidades es incorrecto; si se reinserta con escala "
            "automática quedará 1000× más pequeño."
        )
        assert (lav["datos"]["n_compensadas"], lav["datos"]["n_no_compensadas"]) == (1, 1)
        assert lav["datos"]["escalas_no_compensadas"] == [1.0]
        assert lav["datos"]["inserciones_evaluadas"] == 2 and lav["datos"]["muestra_parcial"] is False
        assert lav["datos"]["tamano_ejemplo"] == pytest.approx([0.45, 0.51])
        mesa = por_bloque["MESA_MM"]
        assert mesa["severidad"] == "alta" and mesa["handles"] == ["1F4"]
        assert mesa["datos"]["metadato_unidades_incorrecto"] is False
        assert mesa["datos"]["tamano_real_m"] == pytest.approx([450.0, 510.0])
        assert "factor de escala implícito 0.001" in mesa["mensaje"] and "1F4" in mesa["mensaje"]
        dp = por_bloque["DP"]
        assert dp["severidad"] == "media" and dp["handles"] == ["1A4", "1A7"]
        assert dp["datos"]["n_no_compensadas"] == 2 and dp["datos"]["metadato_unidades_incorrecto"] is True
        # 0.3.2 (bloque 'C' real): anidadas medidas a través del contenedor → media por metadato, no alta.
        c_mm = por_bloque["C_MM"]
        assert c_mm["severidad"] == "media"
        assert c_mm["datos"]["metadato_unidades_incorrecto"] is True
        assert c_mm["datos"]["tamano_por_anidada"] is True
        assert c_mm["datos"]["tamano_real_m"] == pytest.approx([0.045, 0.045])
        assert c_mm["datos"]["escalas_no_compensadas"] == [0.045]
        # Anidadas sin ningún contenedor medible → media "no medible (inserciones anidadas)", nunca alta.
        flecha = por_bloque["FLECHA_MM"]
        assert flecha["severidad"] == "media"
        assert flecha["datos"]["tamano_real_m"] is None
        assert flecha["datos"]["metadato_unidades_incorrecto"] is False
        assert "no medible (inserciones anidadas)" in flecha["mensaje"]
        assert flecha["mensaje"] == (
            "El bloque 'FLECHA_MM' declara unidades mm y 1 de sus inserciones evaluadas no están compensadas "
            "(escala 0.045), pero su tamaño real es no medible (inserciones anidadas): revise el metadato de "
            "unidades del bloque."
        )
        assert lav["datos"]["tamano_por_anidada"] is False
        # Orden §5C.0: alta → media → baja.
        assert [h["severidad"] for h in data["hallazgos"]] == ["alta", "media", "media", "media", "media", "baja"]

    run(world.server(), body)


def test_duplicados_handles_acotados_a_50() -> None:
    """§5C.6 (desviación aceptada): grupos con n > 50 llevan 50 handles, handles_truncados y n_handles."""
    world = World(18244, duplicados_masivos=True)

    async def body(c: Client) -> None:
        data = await _ok(c, "duplicados")
        grande = data["grupos"][0]
        assert grande["n"] == 51
        assert len(grande["handles"]) == 50
        assert grande["handles_truncados"] is True and grande["n_handles"] == 51
        pequeno = data["grupos"][1]
        assert pequeno["n"] == 3 and "handles_truncados" not in pequeno
        audit = await _ok(c, "auditar_dibujo", {"reglas": ["duplicados"]})
        h = next(x for x in audit["hallazgos"] if x["datos"]["n"] == 51)
        assert h["severidad"] == "media" and len(h["handles"]) == 50
        assert h["datos"]["handles_truncados"] is True and h["datos"]["n_handles"] == 51

    run(world.server(), body)


def test_limpiar_observaciones_conserva_viewports() -> None:
    """§5C.10 (desviación aceptada): los VIEWPORT de la capa no se borran ni cuentan."""
    world = World(18244, viewport_observaciones=True)

    async def body(c: Client) -> None:
        marcado = await _ok(c, "marcar_hallazgos", {"marcas": [MARCA]})
        assert marcado["capa_creada"] is False
        limpio = await _ok(c, "limpiar_observaciones", {"confirmar": True})
        assert limpio["n_borradas"] == 2 and limpio["por_espacio"] == {"modelo": 2}
        assert limpio["advertencias"] == [
            "Se conservaron 1 viewport(s) de la capa MCP-OBSERVACIONES: limpiar_observaciones no borra viewports."
        ]
        papel = await _ok(c, "consultar_entidades", {"espacio": "Layout1"})
        assert [e["tipo"] for e in papel["entidades"]] == ["VIEWPORT"]

    run(world.server(), body)


def test_errores_del_plugin_simulado_llevan_data() -> None:
    """data.handle / data.capa de NOT_FOUND según §5C.5 y §5C.10 (vía PipeClient)."""
    client = World(18244).client_factory(Instance(pid=18244, pipe="x", user="usuario"))
    with pytest.raises(PluginError) as exc:
        client.call("verificar_cuadro_areas", {"contornos": {"LOTE": "ABC"}})
    assert (exc.value.code, exc.value.rpc_code, exc.value.data["handle"]) == ("NOT_FOUND", -32003, "ABC")
    with pytest.raises(PluginError) as exc:
        client.call("limpiar_observaciones", {"confirmar": True, "capa": "MCP-NADA"})
    assert (exc.value.code, exc.value.data["capa"]) == ("NOT_FOUND", "MCP-NADA")
    with pytest.raises(PluginError) as exc:
        client.call("limpiar_observaciones", {})
    assert (exc.value.code, exc.value.rpc_code, exc.value.data["detalle"]) == (
        "INVALID_PARAMS",
        -32602,
        "confirmar: debe ser true",
    )


def test_duplicados() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "duplicados")
        assert data["tolerancia"] == 1e-6
        assert data["grupos"] == [
            {"tipo": "LINE", "n": 3, "handles": ["1A0", "1B0", "1B1"], "capas": ["MUROS"], "bbox": [[0.0, 0.0], [5.0, 0.0]]}
        ]
        assert data["resumen"] == {"n_grupos": 1, "n_entidades_sobrantes": 2, "por_tipo": {"LINE": 1}}
        nada = await _ok(c, "duplicados", {"tipos": ["CIRCLE"]})
        assert nada["grupos"] == [] and nada["total"] == 0
        malo = await c.call_tool("duplicados", {"tolerancia": 5})
        assert malo.is_error
        assert text_of(malo) == "[INVALID_PARAMS] Parámetros inválidos: tolerancia: debe ser un número entre 1e-12 y 1"

    run(world.server(), body)


def test_textos_fuera_de_escala() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "textos_fuera_de_escala")
        assert data["mediana"] == pytest.approx(0.18)
        assert data["n_textos"] == 11
        assert data["resumen"] == {"menor": 0, "mayor": 1, "no_positiva": 0}
        (t,) = data["textos"]
        assert (t["handle"], t["motivo"], t["altura"]) == ("1D0", "mayor", 2.5)
        assert t["ratio"] == pytest.approx(2.5 / 0.18)
        assert set(t) == {"handle", "tipo", "capa", "altura", "ratio", "motivo", "texto", "posicion"}

    run(world.server(), body)


# ---------------------------------------------------------------------------
# auditar_dibujo
# ---------------------------------------------------------------------------


def test_auditar_dibujo_resumen_y_orden() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "auditar_dibujo")
        por_regla = data["resumen"]["por_regla"]
        assert list(por_regla) == REGLAS_AUDITORIA
        for regla, fila in por_regla.items():
            assert set(fila) == {"n", "alta", "media", "baja", "completo"}, regla
            assert fila["completo"] is True
            assert fila["n"] == fila["alta"] + fila["media"] + fila["baja"]
        assert data["reglas_omitidas"] == [] and data["reglas_con_error"] == []
        assert isinstance(data["duracion_ms"], int)
        hallazgos = data["hallazgos"]
        assert data["total"] == data["resumen"]["total_hallazgos"] == len(hallazgos)
        assert sum(data["resumen"]["por_severidad"].values()) == data["total"]
        for h in hallazgos:
            _assert_hallazgo(h)
        claves = [(SEVERIDADES.index(h["severidad"]), REGLAS_AUDITORIA.index(h["regla"])) for h in hallazgos]
        assert claves == sorted(claves)
        reglas = {h["regla"] for h in hallazgos}
        assert {"auditar_capas", "entidades_en_capa_0", "auditar_bloques", "duplicados", "textos_fuera_de_escala"} <= reglas
        assert len({h["id"] for h in hallazgos}) == len(hallazgos)

    run(world.server(), body)


def test_auditar_dibujo_paginacion() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        completo = await _ok(c, "auditar_dibujo")
        vistos: list[str] = []
        cursor: str | None = None
        while True:
            args: dict[str, Any] = {"limit": 1}
            if cursor is not None:
                args["cursor"] = cursor
            page = await _ok(c, "auditar_dibujo", args)
            assert len(page["hallazgos"]) == 1
            assert page["total"] == completo["total"]
            vistos.append(page["hallazgos"][0]["id"])
            cursor = page["cursor_siguiente"]
            if cursor is None:
                break
            assert cursor == str(len(vistos))
        assert vistos == [h["id"] for h in completo["hallazgos"]]
        fuera = await c.call_tool("auditar_dibujo", {"cursor": str(completo["total"])})
        assert fuera.is_error
        assert text_of(fuera) == "[INVALID_PARAMS] Parámetros inválidos: cursor: fuera de rango"

    run(world.server(), body)


def test_auditar_dibujo_reglas_filtradas() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "auditar_dibujo", {"reglas": ["unidades", "duplicados"]})
        assert list(data["resumen"]["por_regla"]) == ["duplicados", "unidades"]
        assert {h["regla"] for h in data["hallazgos"]} == {"duplicados"}
        assert data["hallazgos"][0]["id"] == "duplicados-1"

    run(world.server(), body)


# ---------------------------------------------------------------------------
# Escritura: marcar_hallazgos y limpiar_observaciones
# ---------------------------------------------------------------------------


def test_marcar_y_limpiar_observaciones() -> None:
    world = World(18244)
    marcas = [MARCA, {"bbox": [[10, 10], [10, 10]], "texto": "Punto"}, {"bbox": [[1, 1], [2, 2]], "texto": "x"}]

    async def body(c: Client) -> None:
        marcado = await _ok(c, "marcar_hallazgos", {"marcas": marcas})
        n = marcado["n_marcas"]
        assert n == 3 and len(marcado["marcas"]) == 3
        assert marcado["capa"] == "MCP-OBSERVACIONES" and marcado["capa_creada"] is True
        assert marcado["espacio"] == "modelo"
        esperados = [h for m in marcado["marcas"] for h in (m["nube"], m["texto"])]
        assert marcado["handles"] == esperados and len(set(esperados)) == 2 * n
        for m in marcado["marcas"]:
            # Σ n por lado = 2·(nx + ny), con n ≥ 4 por lado (§5C.9).
            assert m["n_vertices"] >= 16 and m["n_vertices"] % 2 == 0
            (x0, y0), (x1, y1) = m["bbox_nube"]
            assert x0 < x1 and y0 < y1
        otra = await _ok(c, "marcar_hallazgos", {"marcas": [MARCA]})
        assert otra["capa_creada"] is False
        limpio = await _ok(c, "limpiar_observaciones", {"confirmar": True})
        assert limpio["capa"] == "MCP-OBSERVACIONES"
        assert limpio["n_borradas"] == 2 * (n + otra["n_marcas"])
        assert limpio["por_espacio"] == {"modelo": limpio["n_borradas"]}
        de_nuevo = await _ok(c, "limpiar_observaciones", {"confirmar": True})
        assert de_nuevo["n_borradas"] == 0 and de_nuevo["por_espacio"] == {}

    run(world.server(), body)


def test_marcar_hallazgos_crea_entidades_en_la_capa() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        marcado = await _ok(c, "marcar_hallazgos", {"marcas": [MARCA]})
        nube, texto = marcado["handles"]
        p_nube = await _ok(c, "propiedades", {"handle": nube})
        p_texto = await _ok(c, "propiedades", {"handle": texto})
        assert (p_nube["tipo"], p_nube["capa"], p_nube["cerrada"]) == ("LWPOLYLINE", "MCP-OBSERVACIONES", True)
        assert (p_texto["tipo"], p_texto["capa"]) == ("MTEXT", "MCP-OBSERVACIONES")
        assert p_texto["texto"] == "[ALTA] Revisar muro"
        capas = await _ok(c, "auditar_capas", {"clasificacion": ["mcp"]})
        assert [r["nombre"] for r in capas["capas"]] == ["MCP-DIBUJO", "MCP-OBSERVACIONES"]

    run(world.server(), body)


@pytest.mark.parametrize(
    ("args", "detalle"),
    [
        ({"marcas": [MARCA], "capa": "MUROS"}, "capa: solo se admiten capas MCP-*"),
        ({"marcas": []}, "marcas: debe tener entre 1 y 100 elementos"),
        ({"marcas": [MARCA, {"bbox": [[0, 0], [1, 1]], "texto": ""}]}, "marcas[1].texto: debe tener entre 1 y 500 caracteres"),
        ({"marcas": [{"bbox": [[0, 0], [1, 1]], "texto": "x" * 501}]}, "marcas[0].texto: debe tener entre 1 y 500 caracteres"),
        ({"marcas": [MARCA], "altura_texto": 0}, "altura_texto: debe ser un número mayor que 0"),
    ],
)
def test_marcar_hallazgos_invalid_params(args: dict[str, Any], detalle: str) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        res = await c.call_tool("marcar_hallazgos", args)
        assert res.is_error
        assert text_of(res) == f"[INVALID_PARAMS] Parámetros inválidos: {detalle}"
        # Atómico: no se creó nada.
        assert not any(e["capa"] == "MCP-OBSERVACIONES" for e in world.states[18244].entities)

    run(world.server(), body)


# 0.3.2 (F9): marcas fuera de la extensión del espacio destino.
MARCA_LEJANA = {"bbox": [[3273, 3989], [3278, 3994]], "texto": "Coordenadas de otro espacio", "severidad": "alta"}


def test_marcar_hallazgos_fuera_de_extension_invalid_params_sin_escribir() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        res = await c.call_tool("marcar_hallazgos", {"marcas": [MARCA, MARCA_LEJANA]})
        assert res.is_error
        texto = text_of(res)
        assert texto.startswith(
            "[INVALID_PARAMS] Parámetros inválidos: marcas[1] [[3273, 3989], [3278, 3994]] fuera de la extensión "
            "del espacio destino 'modelo' [["
        )
        assert "(ampliada un 10 %)" in texto and "indique en 'espacio' el layout correcto" in texto
        assert texto.endswith("No se escribió nada; pase forzar: true para escribir igualmente.")
        assert "marcas[0]" not in texto
        # Atómico: no se creó nada (tampoco la capa).
        assert not any(e["capa"] == "MCP-OBSERVACIONES" for e in world.states[18244].entities)
        capas = await _ok(c, "auditar_capas", {"clasificacion": ["mcp"]})
        assert [r["nombre"] for r in capas["capas"]] == ["MCP-DIBUJO"]

    run(world.server(), body)


def test_marcar_hallazgos_en_layout_con_coordenadas_de_modelo() -> None:
    """Caso del gate: layout con contenido pequeño en papel y marca con coordenadas de modelo."""
    world = World(18244)
    world.states[18244].layout_entities["Layout1"].append(
        {"handle": "1FB", "tipo": "LINE", "capa": "MARCO", "bbox": [[319.0, 59.0, 0.0], [321.0, 60.0, 0.0]]}
    )

    async def body(c: Client) -> None:
        res = await c.call_tool("marcar_hallazgos", {"marcas": [MARCA_LEJANA], "espacio": "Layout1"})
        assert res.is_error
        assert text_of(res) == (
            "[INVALID_PARAMS] Parámetros inválidos: marcas[0] [[3273, 3989], [3278, 3994]] fuera de la extensión del "
            "espacio destino 'Layout1' [[319, 59], [321, 60]] (ampliada un 10 %): probablemente las coordenadas son "
            "del modelo: use espacio:\"modelo\" (o el layout correcto). No se escribió nada; pase forzar: true para "
            "escribir igualmente."
        )
        forzado = await _ok(c, "marcar_hallazgos", {"marcas": [MARCA_LEJANA], "espacio": "Layout1", "forzar": True})
        assert forzado["n_marcas"] == 1 and forzado["espacio"] == "Layout1"
        assert any(a.startswith("forzar: true: se escribieron marcas fuera") for a in forzado["advertencias"])

    run(world.server(), body)


def test_marcar_hallazgos_espacio_vacio_no_bloquea() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        # Layout1 del simulador está vacío (sin extensión): no se bloquea.
        marcado = await _ok(c, "marcar_hallazgos", {"marcas": [MARCA_LEJANA], "espacio": "Layout1"})
        assert marcado["n_marcas"] == 1 and marcado["advertencias"] == []

    run(world.server(), body)


def test_marcar_hallazgos_schema_forzar() -> None:
    tool = next(t for t in manifest_tools() if t["name"] == "marcar_hallazgos")
    assert tool["inputSchema"]["properties"]["forzar"]["type"] == "boolean"
    assert tool["inputSchema"]["properties"]["forzar"]["default"] is False


@pytest.mark.parametrize(
    ("args", "texto"),
    [
        ({}, "[INVALID_PARAMS] Parámetros inválidos: confirmar: debe ser true"),
        ({"confirmar": False}, "[INVALID_PARAMS] Parámetros inválidos: confirmar: debe ser true"),
        ({"confirmar": True, "capa": "0"}, "[INVALID_PARAMS] Parámetros inválidos: capa: solo se admiten capas MCP-*"),
        ({"confirmar": True}, "[NOT_FOUND] La capa 'MCP-OBSERVACIONES' no existe en el dibujo."),
        ({"confirmar": True, "capa": "MCP-NADA"}, "[NOT_FOUND] La capa 'MCP-NADA' no existe en el dibujo."),
    ],
)
def test_limpiar_observaciones_errores(args: dict[str, Any], texto: str) -> None:
    world = World(18244)
    antes = len(world.states[18244].entities)

    async def body(c: Client) -> None:
        res = await c.call_tool("limpiar_observaciones", args)
        assert res.is_error is True
        assert text_of(res) == texto
        assert res.structured_content is None

    run(world.server(), body)
    assert len(world.states[18244].entities) == antes


def test_error_programado_confirmar_se_reenvia() -> None:
    world = World(18244)
    world.states[18244].program_error("limpiar_observaciones", "INVALID_PARAMS_CONFIRMAR")

    async def body(c: Client) -> None:
        res = await c.call_tool("limpiar_observaciones", {"confirmar": True})
        assert res.is_error
        assert text_of(res) == "[INVALID_PARAMS] Parámetros inválidos: confirmar: debe ser true"

    run(world.server(), body)


# ---------------------------------------------------------------------------
# Sin instancia: manifest guardado en disco (§4.1) y NO_INSTANCE (§6.1)
# ---------------------------------------------------------------------------


def test_manifest_en_disco_con_autocad_cerrado(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.json"
    _guardar_manifest(World(18244), manifest)
    guardado = json.loads(manifest.read_text(encoding="utf-8"))
    assert guardado["protocol_version"] == "0.8" and len(guardado["tools"]) == 56
    # La instancia sigue listada pero AutoCAD "se cerró": el pipe no conecta.
    world = World(18244, alive=False)

    async def body(c: Client) -> None:
        tools = [t.name for t in (await c.list_tools()).tools]
        assert tools[: len(MANIFEST_ORDER)] == MANIFEST_ORDER
        for name in ("auditar_dibujo", "marcar_hallazgos", "limpiar_observaciones"):
            args = CASOS[name][0]
            res = await c.call_tool(name, args)
            assert res.is_error is True, name
            assert text_of(res) == NO_INSTANCE, name

    run(world.server(manifest_path=manifest), body)
    assert world.states[18244].requests == []


def test_manifest_en_disco_sin_ninguna_instancia(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.json"
    _guardar_manifest(World(18244), manifest)

    async def body(c: Client) -> None:
        names = {t.name for t in (await c.list_tools()).tools}
        assert set(FASE2) <= names
        res = await c.call_tool("auditar_dibujo", {})
        assert res.is_error and text_of(res) == NO_INSTANCE

    run(World().server(manifest_path=manifest), body)
