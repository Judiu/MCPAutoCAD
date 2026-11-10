"""Pruebas de la Fase 4A (protocolo 0.5, protocol.md §5E) a través del cliente MCP y del PipeClient.

El servidor es genérico: estas pruebas comprueban que las 11 herramientas de geometría
paramétrica se publican con su 'escritura' y sus schemas del manifest, que las lecturas
(punto_de, distancia, proyectar) resuelven PuntoRef con los errores exactos de §5E.1, que
las seis generadoras responden con la forma común G6 (§5E.0), aplican G2 estricta
(UNITS_UNKNOWN sin aceptar_sin_unidades) y R4, y que el ciclo de previsualización
(preview → confirmar / descartar_preview, caducidad, huérfanas, anclas, tope 20; §5E.4–§5E.5)
se comporta como el contrato. Se usa el plugin simulado de fake_plugin.py (sin AutoCAD):
simula la FORMA de las respuestas y los errores; la geometría es una aproximación.
"""

from __future__ import annotations

import json
import math
from datetime import timedelta
from pathlib import Path
from typing import Any

import pytest
from fake_plugin import (
    CAPA_PREVIEW,
    CLAVES_G6,
    DESCRIPCIONES_FASE4A,
    HERRAMIENTAS_FASE4A,
    HERRAMIENTAS_GENERADORAS,
    HERRAMIENTAS_LECTURA_4A,
    PREVIEW_ID_RE,
    PUNTO_REF_SCHEMA,
    FakePluginState,
    advertencia_desalojo,
    advertencia_duplicados_colapsados,
    advertencia_eje_duplicados_colapsados,
    advertencia_estilo_ejes,
    advertencia_huerfanas,
    advertencia_preview_movida,
    detalle_ancla_cambiada,
    detalle_capa_heredada,
    detalle_offset_lado,
    detalle_preview_otro_documento,
    manifest_tools,
    mensaje_preview_no_encontrada,
)
from mcp import Client
from test_server import LOCAL_ORDER, MANIFEST_ORDER, World, _guardar_manifest, run, text_of

from mcp_autocad.client import PipeClient
from mcp_autocad.discovery import Instance
from mcp_autocad.errors import RPC_CODE_TO_SYMBOLIC, PluginError

NO_INSTANCE = "AutoCAD no está abierto o el plugin no cargó."
UNITS_UNKNOWN = "[UNITS_UNKNOWN] El dibujo no declara unidades (INSUNITS=0)."
INVALIDO = "[INVALID_PARAMS] Parámetros inválidos: "
GEOMETRIA = "[INVALID_GEOMETRY] Geometría no construible: "
COMUNES_GEN = {"preview", "dibujar_preview", "espacio", "forzar"}
CADUCA_INICIAL = "2026-10-26T10:15:00.000-05:00"
MURO = {"eje": [[0, 0], [4, 0]], "espesor": 0.15}
RECTANGULO_12_35 = [[10, 10], [15, 10], [15, 12.47], [10, 12.47]]

# Argumentos mínimos válidos de cada generadora (sobre el dibujo ficticio) y sus números propios (§5E.3).
GENERADORAS: dict[str, tuple[dict[str, Any], list[str]]] = {
    "muro": (MURO, ["longitud_eje", "longitud_contorno", "area", "espesor", "n_tramos"]),
    "ejes_rejilla": (
        {"origen": [0, 0], "separaciones_x": [4], "separaciones_y": [3], "extension": 1, "radio_burbuja": 0.154, "altura_texto": 0.175},
        ["n_ejes_x", "n_ejes_y", "ancho", "alto", "posiciones_x", "posiciones_y", "n_burbujas"],
    ),
    "recinto": (
        {"contorno": RECTANGULO_12_35, "nombre": "COCINA", "altura_texto": 0.15},
        ["area", "perimetro", "centroide", "punto_etiqueta", "etiqueta_en_centroide", "n_vertices"],
    ),
    "offset": ({"handle": "1A0", "distancia": 0.5, "lado": [2, 1]}, ["distancia", "longitud_original", "lado_punto"]),
    "array_rectangular": (
        {"handles": ["1A0"], "filas": 2, "columnas": 2, "sep_filas": 1, "sep_columnas": 6, "confirmar": True},
        ["n_copias", "n_posiciones", "transformaciones", "n_usuario", "capas"],
    ),
    "array_polar": (
        {"handles": ["1AA"], "centro": [0, 0], "n": 4, "angulo_total": 360, "confirmar": True},
        ["n_copias", "n_posiciones", "transformaciones", "n_usuario", "capas"],
    ),
}


async def _ok(c: Client, name: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
    res = await c.call_tool(name, args or {})
    assert not res.is_error, text_of(res)
    data = json.loads(text_of(res))
    assert res.structured_content == data
    return data


async def _err(c: Client, name: str, args: dict[str, Any]) -> str:
    res = await c.call_tool(name, args)
    assert res.is_error is True, text_of(res)
    assert res.structured_content is None
    return text_of(res)


def _client(world: World, pid: int = 18244) -> PipeClient:
    return world.client_factory(Instance(pid=pid, pipe="x", user="usuario"))


def _plugin_error(world: World, method: str, params: dict[str, Any]) -> PluginError:
    with pytest.raises(PluginError) as exc:
        _client(world).call(method, params)
    return exc.value


def _state(world: World) -> FakePluginState:
    return world.states[18244]


def _entity(world: World, handle: str) -> dict[str, Any] | None:
    st = _state(world)
    for entities in [st.entities, *st.layout_entities.values()]:
        for e in entities:
            if e["handle"] == handle:
                return e
    return None


def _en_capa(world: World, capa: str) -> list[dict[str, Any]]:
    st = _state(world)
    return [e for entities in [st.entities, *st.layout_entities.values()] for e in entities if e["capa"] == capa]


def _snapshot(world: World) -> tuple[int, int, int, int]:
    st = _state(world)
    return (len(st.entities), len(st.capas_creadas), len(st.previews), st.pasos_deshacer)


# ---------------------------------------------------------------------------
# Publicación (tools/list) y manifest
# ---------------------------------------------------------------------------


def test_tools_list_publica_las_11_de_fase4a_con_schema_del_manifest() -> None:
    world = World(18244)
    esperado = {t["name"]: t for t in manifest_tools()}

    async def body(c: Client) -> None:
        tools = (await c.list_tools()).tools
        assert [t.name for t in tools] == MANIFEST_ORDER + LOCAL_ORDER
        assert len(tools) == 61
        publicadas = {t.name: t for t in tools}
        for name in HERRAMIENTAS_FASE4A:
            assert publicadas[name].input_schema == esperado[name]["inputSchema"], name
            assert publicadas[name].input_schema["additionalProperties"] is False, name
            assert publicadas[name].description == esperado[name]["description"], name

    run(world.server(), body)


def test_manifest_fase4a_en_orden_con_escritura_correcta() -> None:
    tools = manifest_tools()
    assert len(tools) == 56
    assert [t["name"] for t in tools][36:47] == HERRAMIENTAS_FASE4A == MANIFEST_ORDER[36:47]
    for t in tools[36:47]:
        assert t["requiere_documento"] is True, t["name"]
        assert t["escritura"] is (t["name"] not in HERRAMIENTAS_LECTURA_4A), t["name"]
    assert sum(1 for t in tools[36:47] if t["escritura"]) == 8


def test_descripciones_literales_de_5e6() -> None:
    descripciones = {t["name"]: t["description"] for t in manifest_tools()}
    for name in HERRAMIENTAS_FASE4A:
        assert descripciones[name] == DESCRIPCIONES_FASE4A[name]
    assert descripciones["punto_de"].startswith("LEE un punto del dibujo: resuelve un PuntoRef")
    assert descripciones["muro"].endswith("No une con muros existentes.")
    assert descripciones["descartar_preview"].startswith("BORRA las entidades de MCP-PREVIEW")


def test_schemas_generadoras_con_comunes_y_sin_aceptar_sin_unidades() -> None:
    schemas = {t["name"]: t["inputSchema"] for t in manifest_tools()}
    for name in HERRAMIENTAS_GENERADORAS:
        props = set(schemas[name]["properties"])
        assert COMUNES_GEN <= props, name
        assert "aceptar_sin_unidades" not in props, name
        assert schemas[name]["additionalProperties"] is False, name
    for name in HERRAMIENTAS_LECTURA_4A + ["confirmar", "descartar_preview"]:
        assert not COMUNES_GEN & set(schemas[name]["properties"]), name
    assert schemas["confirmar"]["required"] == ["preview_id"] == schemas["descartar_preview"]["required"]


def test_schema_puntoref_es_oneof_de_tres_formas() -> None:
    schemas = {t["name"]: t["inputSchema"] for t in manifest_tools()}
    assert len(PUNTO_REF_SCHEMA["oneOf"]) == 3
    assert schemas["punto_de"]["properties"]["ref"] == PUNTO_REF_SCHEMA
    assert schemas["ejes_rejilla"]["properties"]["origen"] == PUNTO_REF_SCHEMA
    assert schemas["muro"]["properties"]["eje"]["oneOf"][0]["items"] == PUNTO_REF_SCHEMA
    entidad, interseccion = PUNTO_REF_SCHEMA["oneOf"][1], PUNTO_REF_SCHEMA["oneOf"][2]
    assert entidad["required"] == ["handle", "punto"] and entidad["additionalProperties"] is False
    assert interseccion["required"] == ["handle", "interseccion_con"] and interseccion["additionalProperties"] is False


def test_manifest_en_disco_publica_55_y_fase4a_sin_instancia(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.json"
    _guardar_manifest(World(18244), manifest)
    guardado = json.loads(manifest.read_text(encoding="utf-8"))
    assert guardado["protocol_version"] == "0.8" and guardado["plugin_version"] == "0.8.0"
    assert [t["name"] for t in guardado["tools"]] == MANIFEST_ORDER

    async def body(c: Client) -> None:
        assert len((await c.list_tools()).tools) == 61
        res = await c.call_tool("muro", MURO)
        assert res.is_error and text_of(res) == NO_INSTANCE

    run(World().server(manifest_path=manifest), body)


# ---------------------------------------------------------------------------
# punto_de / distancia / proyectar (§5E.1, §5E.2)
# ---------------------------------------------------------------------------


def test_punto_de_coordenada_literal() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "punto_de", {"ref": [1.5, 2]})
        assert (r["punto"], r["ref"], r["ancla"], r["tipo_entidad"]) == ([1.5, 2.0, 0.0], [1.5, 2.0, 0.0], None, None)
        assert (r["insunits"], r["unidades"], r["advertencias"]) == (6, "m", [])

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 0


def test_punto_de_extremos_de_una_linea_con_handle_en_minusculas() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        fin = await _ok(c, "punto_de", {"ref": {"handle": "1a0", "punto": "fin"}})
        assert (fin["punto"], fin["ancla"], fin["tipo_entidad"]) == ([5.0, 0.0, 0.0], "1A0", "LINE")
        assert fin["ref"] == {"handle": "1A0", "punto": "fin"}
        medio = await _ok(c, "punto_de", {"ref": {"handle": "1A0", "punto": "medio"}})
        assert medio["punto"] == [2.5, 0.0, 0.0]
        v1 = await _ok(c, "punto_de", {"ref": {"handle": "1A0", "punto": "vertice", "indice": 1}})
        assert v1["punto"] == [5.0, 0.0, 0.0] and v1["ref"]["indice"] == 1

    run(world.server(), body)


def test_punto_de_polilinea_medio_y_vertice() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        # 1A3: rectángulo cerrado 5 × 3 (perímetro 16): la mitad del recorrido cae en (5, 3).
        medio = await _ok(c, "punto_de", {"ref": {"handle": "1A3", "punto": "medio"}})
        assert medio["punto"] == pytest.approx([5.0, 3.0, 0.0])
        v2 = await _ok(c, "punto_de", {"ref": {"handle": "1A3", "punto": "vertice", "indice": 2}})
        assert v2["punto"] == [5.0, 3.0, 0.0] and v2["tipo_entidad"] == "LWPOLYLINE"
        v0 = await _ok(c, "punto_de", {"ref": {"handle": "1A3", "punto": "vertice"}})
        assert v0["punto"] == [0.0, 0.0, 0.0]

    run(world.server(), body)


def test_punto_de_indice_fuera_de_rango() -> None:
    world = World(18244)
    err = _plugin_error(world, "punto_de", {"ref": {"handle": "1A3", "punto": "vertice", "indice": 4}})
    assert err.code == "INVALID_PARAMS"
    assert err.data["detalle"] == "ref.indice: 4 fuera de rango; la entidad 1A3 tiene 4 vértice(s) (índices 0 a 3)"
    assert err.data["n_vertices"] == 4


def test_punto_de_centro_de_entidad_no_circular_avisa() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "punto_de", {"ref": {"handle": "1AA", "punto": "centro"}})
        assert r["punto"] == pytest.approx([1.55, 1.6, 0.0])
        assert r["advertencias"] == ["ref: 1AA (TEXT) no es un círculo ni un arco; se usó el centro de su caja."]

    run(world.server(), body)


def test_punto_de_entidades_sin_vertices_ni_curva() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        sin_vertices = await _err(c, "punto_de", {"ref": {"handle": "1AA", "punto": "vertice"}})
        assert sin_vertices == INVALIDO + "ref: la entidad 1AA (TEXT) no tiene vértices (use inicio/fin/medio/centro)"
        sin_curva = await _err(c, "punto_de", {"ref": {"handle": "1A4", "punto": "inicio"}})
        assert sin_curva == INVALIDO + "ref: la entidad 1A4 (INSERT) no es una curva: no tiene inicio, fin ni medio"

    run(world.server(), body)


def test_punto_de_interseccion_de_dos_lineas() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "punto_de", {"ref": {"handle": "1A0", "interseccion_con": "1A1"}})
        assert r["punto"] == pytest.approx([5.0, 0.0, 0.0])
        assert (r["ancla"], r["tipo_entidad"]) == ("1A0+1A1", "LINE+LINE")
        assert r["ref"] == {"handle": "1A0", "interseccion_con": "1A1"}

    run(world.server(), body)


def test_punto_de_sin_interseccion_es_invalid_geometry() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        texto = await _err(c, "punto_de", {"ref": {"handle": "1A0", "interseccion_con": "1A2"}})
        assert texto == GEOMETRIA + "ref: las entidades 1A0 y 1A2 no se intersecan"

    run(world.server(), body)
    err = _plugin_error(world, "punto_de", {"ref": {"handle": "1A0", "interseccion_con": "1A2"}})
    assert (err.code, err.rpc_code) == ("INVALID_GEOMETRY", -32005)
    assert (err.data["detalle"], err.data["handle"], err.data["interseccion_con"]) == (
        "ref: las entidades 1A0 y 1A2 no se intersecan",
        "1A0",
        "1A2",
    )


def test_punto_de_varias_intersecciones_exige_cerca_de() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        circulo = await _ok(c, "circulo", {"centro": [2, 0], "radio": 1})
        h = circulo["handles"][0]
        texto = await _err(c, "punto_de", {"ref": {"handle": "1A0", "interseccion_con": h}})
        assert texto == INVALIDO + f"ref: hay 2 intersecciones entre 1A0 y {h}; indique cerca_de"
        cerca = await _ok(c, "punto_de", {"ref": {"handle": "1A0", "interseccion_con": h, "cerca_de": [3.2, 0.1]}})
        assert cerca["punto"] == pytest.approx([3.0, 0.0, 0.0])
        assert cerca["ref"]["cerca_de"] == [3.2, 0.1, 0.0]

    run(world.server(), body)
    st = _state(world)
    h = f"{st.next_handle - 1:X}"
    err = _plugin_error(world, "punto_de", {"ref": {"handle": "1A0", "interseccion_con": h}})
    assert err.data["n_intersecciones"] == 2 and len(err.data["intersecciones"]) == 2


def test_punto_de_handle_inexistente_not_found() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        texto = await _err(c, "punto_de", {"ref": {"handle": "FFFF", "punto": "inicio"}})
        assert texto == "[NOT_FOUND] No existe ninguna entidad con handle 'FFFF'."

    run(world.server(), body)


@pytest.mark.parametrize(
    ("ref", "detalle"),
    [
        ("texto", "ref: debe ser [x, y] o un objeto {handle, punto} / {handle, interseccion_con}"),
        ({"handle": "1A0"}, "ref: debe ser [x, y] o un objeto {handle, punto} / {handle, interseccion_con}"),
        ({"handle": "1A0", "punto": "esquina"}, "ref.punto: debe ser uno de inicio, fin, medio, centro, vertice"),
        ({"handle": "1A0", "punto": "fin", "indice": 1}, "ref.indice: solo aplica con punto 'vertice'"),
        ({"handle": "1A0", "punto": "vertice", "indice": -1}, "ref.indice: debe ser un entero mayor o igual que 0"),
        ({"handle": "XYZ", "punto": "fin"}, "ref.handle: formato inválido"),
        ({"handle": "1A0", "interseccion_con": "Z!"}, "ref.interseccion_con: formato inválido"),
        (
            {"handle": "1A0", "punto": "fin", "interseccion_con": "1A1"},
            "ref: no puede llevar punto e interseccion_con a la vez",
        ),
        ({"handle": "1A0", "interseccion_con": "1a0"}, "ref: interseccion_con debe ser una entidad distinta de handle"),
        ({"handle": "1A0", "punto": "fin", "color": 1}, "ref.color: propiedad no permitida"),
        ([1], "ref: debe ser un array de 2 o 3 números"),
    ],
)
def test_punto_ref_errores_de_forma_exactos(ref: Any, detalle: str) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, "punto_de", {"ref": ref}) == INVALIDO + detalle

    run(world.server(), body)


def test_punto_de_propiedades_obligatorias_y_no_permitidas() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, "punto_de", {}) == INVALIDO + "ref: obligatorio"
        assert await _err(c, "punto_de", {"ref": [0, 0], "x": 1}) == INVALIDO + "x: propiedad no permitida"

    run(world.server(), body)


def test_lecturas_responden_sin_unidades_y_no_escriben() -> None:
    world = World(18244, insunits=0)

    async def body(c: Client) -> None:
        r = await _ok(c, "punto_de", {"ref": {"handle": "1A0", "punto": "inicio"}})
        assert r["insunits"] == 0 and r["unidades"] == "desconocida"
        assert r["advertencias"][0].startswith("INSUNITS=0:")
        d = await _ok(c, "distancia", {"a": [0, 0], "b": [3, 4]})
        assert d["distancia"] == pytest.approx(5.0)
        p = await _ok(c, "proyectar", {"punto": [2, 1], "handle": "1A0"})
        assert p["punto_proyectado"] == pytest.approx([2.0, 0.0, 0.0])

    antes = _snapshot(world)
    run(world.server(), body)
    assert _snapshot(world) == antes


def test_distancia_entre_coordenada_y_punto_de_entidad() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "distancia", {"a": [0, 0], "b": {"handle": "1A1", "punto": "fin"}})
        assert r["distancia"] == pytest.approx(math.sqrt(34)) and r["distancia_2d"] == pytest.approx(math.sqrt(34))
        assert (r["dx"], r["dy"], r["dz"]) == pytest.approx((5.0, 3.0, 0.0))
        assert r["angulo"] == pytest.approx(math.degrees(math.atan2(3, 5)))
        assert (r["a"], r["b"]) == ([0.0, 0.0, 0.0], [5.0, 3.0, 0.0])
        assert [p["ancla"] for p in r["puntos_resueltos"]] == [None, "1A1"]

    run(world.server(), body)


def test_distancia_cero_en_planta_angulo_null_y_error_en_b() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "distancia", {"a": [1, 1, 0], "b": [1, 1, 2]})
        assert (r["distancia"], r["distancia_2d"], r["angulo"]) == (2.0, 0.0, None)
        texto = await _err(c, "distancia", {"a": [0, 0], "b": {"handle": "1A0", "punto": "lado"}})
        assert texto == INVALIDO + "b.punto: debe ser uno de inicio, fin, medio, centro, vertice"

    run(world.server(), body)


def test_proyectar_sobre_linea_y_polilinea() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "proyectar", {"punto": [2, 1], "handle": "1A0"})
        assert r["punto_proyectado"] == pytest.approx([2.0, 0.0, 0.0])
        assert (r["distancia"], r["parametro"], r["distancia_desde_inicio"], r["longitud_curva"]) == pytest.approx(
            (1.0, 2.0, 2.0, 5.0)
        )
        assert (r["handle"], r["tipo_entidad"]) == ("1A0", "LINE")
        pl = await _ok(c, "proyectar", {"punto": [6, 1.5], "handle": "1a3"})
        assert pl["punto_proyectado"] == pytest.approx([5.0, 1.5, 0.0])
        assert (pl["parametro"], pl["distancia_desde_inicio"], pl["longitud_curva"]) == pytest.approx((1.5, 6.5, 16.0))
        assert pl["handle"] == "1A3" and pl["puntos_resueltos"][0]["punto"] == [6.0, 1.5, 0.0]

    run(world.server(), body)


def test_proyectar_handle_no_curva_o_inexistente() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        texto = await _err(c, "proyectar", {"punto": [0, 0], "handle": "1AA"})
        assert texto == INVALIDO + "handle: la entidad 1AA (TEXT) no es una curva"
        nf = await _err(c, "proyectar", {"punto": [0, 0], "handle": "FFFF"})
        assert nf == "[NOT_FOUND] No existe ninguna entidad con handle 'FFFF'."

    run(world.server(), body)


# ---------------------------------------------------------------------------
# Generadoras: respuesta G6, G2 estricta y R4 (§5E.0)
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", HERRAMIENTAS_GENERADORAS)
def test_generadora_responde_g6_completa(name: str) -> None:
    world = World(18244)
    args, numeros = GENERADORAS[name]

    async def body(c: Client) -> None:
        r = await _ok(c, name, args)
        assert list(r) == CLAVES_G6
        assert (r["preview"], r["preview_id"], r["preview_dibujada"], r["caduca"]) == (False, None, False, None)
        assert r["espacio"] == "modelo" and (r["insunits"], r["unidades"]) == (6, "m")
        assert len(r["handles"]) == len(r["entidades"]) == r["numeros"]["n_entidades"] > 0
        assert [e["handle"] for e in r["entidades"]] == r["handles"]
        assert r["capa"] == r["capas"][0] == r["entidades"][0]["capa"]
        assert set(numeros) | {"n_entidades", "bbox"} <= set(r["numeros"])
        assert len(r["numeros"]["bbox"]) == 2
        for e in r["entidades"]:
            assert {"handle", "tipo", "capa", "bbox"} <= set(e), e
            assert _entity(world, e["handle"]) is not None

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 1


@pytest.mark.parametrize("name", HERRAMIENTAS_GENERADORAS)
def test_generadora_sin_unidades_units_unknown_sin_escape(name: str) -> None:
    world = World(18244, insunits=0)

    async def body(c: Client) -> None:
        assert await _err(c, name, GENERADORAS[name][0]) == UNITS_UNKNOWN

    antes = _snapshot(world)
    run(world.server(), body)
    assert _snapshot(world) == antes


@pytest.mark.parametrize("name", HERRAMIENTAS_GENERADORAS)
def test_generadora_rechaza_aceptar_sin_unidades(name: str) -> None:
    world = World(18244, insunits=0)

    async def body(c: Client) -> None:
        texto = await _err(c, name, {**GENERADORAS[name][0], "aceptar_sin_unidades": True})
        assert texto == INVALIDO + "aceptar_sin_unidades: propiedad no permitida"

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 0


def test_generadora_r4_fuera_de_la_extension_y_forzar() -> None:
    world = World(18244)
    lejos = {"eje": [[1000, 1000], [1004, 1000]], "espesor": 0.15}

    async def body(c: Client) -> None:
        texto = await _err(c, "muro", lejos)
        assert texto.startswith(INVALIDO + "entidades[0] [[1000, 999.925], [1004, 1000.075]] fuera de la extensión")
        assert texto.endswith("No se escribió nada; pase forzar: true para escribir igualmente.")
        assert await _err(c, "muro", {**lejos, "preview": True}) == texto
        r = await _ok(c, "muro", {**lejos, "forzar": True})
        assert r["advertencias"][-1].startswith("forzar: true: se escribió fuera de la extensión del espacio destino 'modelo'")

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 1


def test_generadora_espacio_inexistente_not_found() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        texto = await _err(c, "muro", {**MURO, "espacio": "NoExiste"})
        assert texto == (
            "[NOT_FOUND] El layout 'NoExiste' no existe en el dibujo. Disponibles: modelo, Layout1"
            " (nombre de la pestaña, no el título del plano; listar_layouts da sus viewports)."
        )

    run(world.server(), body)


# ---------------------------------------------------------------------------
# muro (§5E.3.1)
# ---------------------------------------------------------------------------


def test_muro_recto_centrado_en_muros() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "muro", MURO)
        (e,) = r["entidades"]
        assert (e["tipo"], e["capa"], e["n_vertices"], e["cerrada"]) == ("LWPOLYLINE", "MUROS", 4, True)
        assert e["area"] == pytest.approx(0.6) and [*e["bbox"][0], *e["bbox"][1]] == pytest.approx([0, -0.075, 4, 0.075])
        n = r["numeros"]
        assert (n["longitud_eje"], n["area"], n["espesor"], n["n_tramos"]) == pytest.approx((4.0, 0.6, 0.15, 1))
        assert r["capas_creadas"] == [] and r["capa"] == "MUROS"

    run(world.server(), body)


@pytest.mark.parametrize(("justificacion", "ymin", "ymax"), [("izquierda", 0.0, 0.15), ("derecha", -0.15, 0.0)])
def test_muro_justificacion(justificacion: str, ymin: float, ymax: float) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "muro", {**MURO, "justificacion": justificacion})
        bbox = r["numeros"]["bbox"]
        assert [*bbox[0], *bbox[1]] == pytest.approx([0, ymin, 4, ymax])

    run(world.server(), body)


def test_muro_en_l_con_inglete() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "muro", {"eje": [[0, 0], [4, 0], [4, 3]], "espesor": 0.2})
        (e,) = r["entidades"]
        assert e["n_vertices"] == 6
        pl = _entity(world, e["handle"])
        assert pl is not None
        # Esquina exterior a inglete en (4.1, −0.1) e interior en (3.9, 0.1).
        assert [4.1, -0.1, 0.0] in [pytest.approx(v) for v in pl["vertices"]]
        assert [3.9, 0.1, 0.0] in [pytest.approx(v) for v in pl["vertices"]]
        assert r["numeros"]["area"] == pytest.approx(1.4) and r["numeros"]["longitud_eje"] == pytest.approx(7.0)

    run(world.server(), body)


def test_muro_cerrado_anillo_con_achurado_asociativo() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(
            c,
            "muro",
            {"eje": [[0, 0], [4, 0], [4, 3], [0, 3]], "espesor": 0.2, "cerrado": True, "achurado": {"patron": "solid"}},
        )
        assert [e["tipo"] for e in r["entidades"]] == ["LWPOLYLINE", "LWPOLYLINE", "HATCH"]
        assert r["capas"] == ["MUROS", "Achurados"] and r["capas_creadas"] == ["Achurados"]
        exterior, interior, hatch = r["entidades"]
        assert exterior["area"] == pytest.approx(4.2 * 3.2) and interior["area"] == pytest.approx(3.8 * 2.8)
        assert hatch["contorno_handles"] == [exterior["handle"], interior["handle"]]
        assert (hatch["patron"], hatch["asociativo"]) == ("SOLID", True)
        assert r["numeros"]["area"] == pytest.approx(2.8) and r["numeros"]["longitud_contorno"] == pytest.approx(28.0)

    run(world.server(), body)


def test_muro_representacion_caras_con_tapas() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "muro", {**MURO, "representacion": "caras"})
        assert [e["tipo"] for e in r["entidades"]] == ["LWPOLYLINE", "LWPOLYLINE", "LINE", "LINE"]
        assert all(e["capa"] == "MUROS" for e in r["entidades"])
        assert r["entidades"][2]["longitud"] == pytest.approx(0.15)
        texto = await _err(c, "muro", {**MURO, "representacion": "caras", "achurado": {}})
        assert texto == INVALIDO + "achurado: solo con representacion 'contorno'"

    run(world.server(), body)


def test_muro_eje_por_handle_de_polilinea_cerrada() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "muro", {"eje": "1a3", "espesor": 0.15})
        assert [e["tipo"] for e in r["entidades"]] == ["LWPOLYLINE", "LWPOLYLINE"]
        assert r["numeros"]["longitud_eje"] == pytest.approx(16.0) and r["puntos_resueltos"] == []
        texto = await _err(c, "muro", {"eje": "1AA", "espesor": 0.15})
        assert texto == INVALIDO + "eje: la entidad 1AA (TEXT) no es una línea ni una polilínea"

    run(world.server(), body)


def test_muro_eje_anclado_a_entidades_existentes() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "muro", {"eje": [{"handle": "1A0", "punto": "inicio"}, {"handle": "1A0", "punto": "fin"}], "espesor": 0.15})
        assert [p["ancla"] for p in r["puntos_resueltos"]] == ["1A0", "1A0"]
        assert [p["punto"] for p in r["puntos_resueltos"]] == [[0.0, 0.0, 0.0], [5.0, 0.0, 0.0]]
        assert r["numeros"]["longitud_eje"] == pytest.approx(5.0)

    run(world.server(), body)


@pytest.mark.parametrize(
    ("args", "esperado"),
    [
        ({"eje": [[0, 0], [0.1, 0]], "espesor": 0.15}, GEOMETRIA + "espesor: el tramo 0 (longitud 0.1) es más corto que el espesor 0.15"),
        ({"eje": [[0, 0], [4, 0], [4, 3], [2, -1]], "espesor": 0.15}, GEOMETRIA + "eje: el eje se autointerseca (tramos 0 y 2)"),
        ({"eje": [[0, 0], [4, 0], [4, 0]], "espesor": 0.15}, INVALIDO + "eje[2]: vértice duplicado con el anterior"),
        ({"eje": [[0, 0], [4, 0]], "espesor": 0.15, "cerrado": True}, INVALIDO + "eje: un muro cerrado necesita al menos 3 vértices"),
        ({"eje": [[0, 0]], "espesor": 0.15}, INVALIDO + "eje: debe ser un array de 2 a 200 puntos"),
        ({"eje": "ZZ", "espesor": 0.15}, INVALIDO + "eje: formato de handle inválido"),
        ({"eje": [[0, 0], [4, 0]], "espesor": 0}, INVALIDO + "espesor: debe ser un número mayor que 0"),
    ],
)
def test_muro_rechazos_sin_escribir(args: dict[str, Any], esperado: str) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, "muro", args) == esperado

    antes = _snapshot(world)
    run(world.server(), body)
    assert _snapshot(world) == antes


def test_muro_inglete_imposible_en_angulo_agudo() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        texto = await _err(c, "muro", {"eje": [[0, 0], [4, 0], [0, 0.5]], "espesor": 0.15})
        assert texto.startswith(GEOMETRIA + "eje: el ángulo en el vértice 1 (")
        assert texto.endswith("°) es demasiado agudo para el inglete (límite 4× el espesor)")
        # Con un límite mayor el inglete sí cabe.
        await _ok(c, "muro", {"eje": [[0, 0], [4, 0], [0, 0.5]], "espesor": 0.15, "limite_inglete": 20})

    run(world.server(), body)


def test_muro_invalid_geometry_por_pipeclient() -> None:
    world = World(18244)
    err = _plugin_error(world, "muro", {"eje": [[0, 0], [0.1, 0]], "espesor": 0.15})
    assert (err.code, err.rpc_code) == ("INVALID_GEOMETRY", -32005)
    assert err.plugin_message == "Geometría no construible: espesor: el tramo 0 (longitud 0.1) es más corto que el espesor 0.15"
    assert err.data["detalle"] == "espesor: el tramo 0 (longitud 0.1) es más corto que el espesor 0.15"


# ---------------------------------------------------------------------------
# ejes_rejilla (§5E.3.2)
# ---------------------------------------------------------------------------


def test_ejes_rejilla_lineas_burbujas_y_rotulos() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "ejes_rejilla", GENERADORAS["ejes_rejilla"][0])
        assert [e["tipo"] for e in r["entidades"]] == ["LINE"] * 4 + ["CIRCLE"] * 4 + ["TEXT"] * 4
        assert r["capas"] == ["EJES"] and r["capas_creadas"] == ["EJES"]
        circulos = [e for e in r["entidades"] if e["tipo"] == "CIRCLE"]
        assert circulos[0]["centro"] == pytest.approx([0.0, 4.154, 0.0]) and circulos[0]["radio"] == 0.154
        assert circulos[2]["centro"] == pytest.approx([5.154, 0.0, 0.0])
        textos = [e for e in r["entidades"] if e["tipo"] == "TEXT"]
        assert [t["texto"] for t in textos] == ["1", "2", "A", "B"]
        assert all(t["justificacion"] == "medio_centro" and t["altura"] == 0.175 for t in textos)
        assert textos[0]["posicion"] == circulos[0]["centro"]
        n = r["numeros"]
        assert (n["n_ejes_x"], n["n_ejes_y"], n["ancho"], n["alto"], n["n_burbujas"]) == (2, 2, 4.0, 3.0, 4)
        assert (n["posiciones_x"], n["posiciones_y"]) == ([0.0, 4.0], [0.0, 3.0])

    run(world.server(), body)


def test_ejes_rejilla_burbujas_en_ambos_extremos_y_rotulos_propios() -> None:
    world = World(18244)
    args = {**GENERADORAS["ejes_rejilla"][0], "burbujas": "ambos", "rotulos_x": ["A1", "A2"], "rotulos_y": ["X", "Y"]}

    async def body(c: Client) -> None:
        r = await _ok(c, "ejes_rejilla", args)
        assert r["numeros"]["n_burbujas"] == 8 and r["numeros"]["n_entidades"] == 4 + 8 + 8
        assert [e["texto"] for e in r["entidades"] if e["tipo"] == "TEXT"][:2] == ["A1", "A1"]

    run(world.server(), body)


def test_ejes_rejilla_rotada_conserva_posiciones_y_rotulos_horizontales() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "ejes_rejilla", {**GENERADORAS["ejes_rejilla"][0], "rotacion": 90})
        assert r["numeros"]["posiciones_x"] == [0.0, 4.0]
        linea0 = _entity(world, r["handles"][1])
        assert linea0 is not None and linea0["inicio"] == pytest.approx([1.0, 4.0, 0.0])
        texto = _entity(world, r["handles"][-1])
        assert texto is not None and texto["rotacion"] == 0.0

    run(world.server(), body)


def test_ejes_rejilla_origen_anclado() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "ejes_rejilla", {**GENERADORAS["ejes_rejilla"][0], "origen": {"handle": "1A1", "punto": "inicio"}})
        assert r["puntos_resueltos"] == [{"ref": {"handle": "1A1", "punto": "inicio"}, "punto": [5.0, 0.0, 0.0], "ancla": "1A1"}]
        assert r["numeros"]["posiciones_x"] == [5.0, 9.0]

    run(world.server(), body)


@pytest.mark.parametrize(
    ("cambio", "detalle"),
    [
        ({"rotulos_x": ["1", "1"]}, "rotulos: rótulo repetido '1'"),
        ({"rotulos_x": ["A", "Z"]}, "rotulos: rótulo repetido 'A'"),
        ({"separaciones_x": [], "separaciones_y": []}, "separaciones: se necesita al menos un eje en x o en y"),
        ({"rotulos_x": ["1"]}, "rotulos_x: debe tener 2 elementos (separaciones_x + 1)"),
        ({"separaciones_y": [3, -1]}, "separaciones_y[1]: debe ser un número mayor que 0"),
        ({"rotulos_y": ["A", "DEMASIADO_LARGO"]}, "rotulos_y[1]: debe tener entre 1 y 10 caracteres"),
        ({"extension": -1}, "extension: debe ser un número mayor o igual que 0"),
        ({"altura_texto": 0}, "altura_texto: debe ser un número mayor que 0"),
    ],
)
def test_ejes_rejilla_rechazos(cambio: dict[str, Any], detalle: str) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, "ejes_rejilla", {**GENERADORAS["ejes_rejilla"][0], **cambio}) == INVALIDO + detalle

    run(world.server(), body)


def test_ejes_rejilla_estilo_inexistente_not_found() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        texto = await _err(c, "ejes_rejilla", {**GENERADORAS["ejes_rejilla"][0], "estilo": "75"})
        assert texto == "[NOT_FOUND] El estilo de texto '75' no existe en el dibujo."

    run(world.server(), body)


def test_ejes_rejilla_sin_estilo_usa_el_75_de_la_oficina_si_existe() -> None:
    """Gate en vivo de Fase 4A (2026-10-27): los rótulos de eje de la oficina usan el estilo '75' (859F0)."""
    world = World(18244)
    _state(world).estilos_texto = ["Standard", "Arial", "75"]

    async def body(c: Client) -> None:
        r = await _ok(c, "ejes_rejilla", GENERADORAS["ejes_rejilla"][0])
        textos = [e for e in r["entidades"] if e["tipo"] == "TEXT"]
        assert textos and all(t["estilo"] == "75" for t in textos)
        assert r["advertencias"] == []
        # Con 'estilo' explícito manda el parámetro.
        r2 = await _ok(c, "ejes_rejilla", {**GENERADORAS["ejes_rejilla"][0], "estilo": "Arial"})
        assert {e["estilo"] for e in r2["entidades"] if e["tipo"] == "TEXT"} == {"Arial"}

    run(world.server(), body)


def test_ejes_rejilla_sin_estilo_y_sin_75_usa_el_actual_con_advertencia() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "ejes_rejilla", GENERADORAS["ejes_rejilla"][0])
        assert {e["estilo"] for e in r["entidades"] if e["tipo"] == "TEXT"} == {"Standard"}
        assert r["advertencias"] == [advertencia_estilo_ejes("75", "Standard")]
        assert r["advertencias"][0].startswith("ejes_rejilla: el estilo de texto '75' de los rótulos de eje de la oficina")

    run(world.server(), body)


def test_ejes_rejilla_schema_estilo_describe_el_defecto_75() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        tools = {t.name: t for t in (await c.list_tools()).tools}
        descripcion = tools["ejes_rejilla"].input_schema["properties"]["estilo"]["description"]
        assert "'75'" in descripcion and "actual" in descripcion

    run(world.server(), body)


# ---------------------------------------------------------------------------
# recinto (§5E.3.3)
# ---------------------------------------------------------------------------


def test_recinto_con_vertices_y_etiqueta_con_coma_decimal() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "recinto", GENERADORAS["recinto"][0])
        assert [e["tipo"] for e in r["entidades"]] == ["LWPOLYLINE", "MTEXT"]
        assert r["capas"] == ["MCP-DIBUJO", "TEXTOS"] and r["capa"] == "MCP-DIBUJO"
        mtext = r["entidades"][1]
        assert mtext["texto"] == "COCINA\n12,35 m²"
        assert (mtext["adjunto"], mtext["altura"], mtext["capa"]) == ("medio_centro", 0.15, "TEXTOS")
        n = r["numeros"]
        assert n["area"] == pytest.approx(12.35) and n["perimetro"] == pytest.approx(14.94)
        assert n["centroide"] == pytest.approx([12.5, 11.235, 0.0]) and n["etiqueta_en_centroide"] is True
        assert n["punto_etiqueta"] == n["centroide"] and n["n_vertices"] == 4

    run(world.server(), body)


def test_recinto_vertices_por_interseccion_de_lineas_existentes() -> None:
    world = World(18244)
    contorno = [
        [0, 0],
        {"handle": "1A0", "interseccion_con": "1A1"},
        {"handle": "1A1", "interseccion_con": "1A2"},
        [0, 3],
    ]

    async def body(c: Client) -> None:
        r = await _ok(c, "recinto", {"contorno": contorno, "nombre": "SALA", "altura_texto": 0.15, "capa_contorno": "MCP-RECINTOS"})
        assert [p["ancla"] for p in r["puntos_resueltos"]] == [None, "1A0+1A1", "1A1+1A2", None]
        assert r["puntos_resueltos"][2]["punto"] == pytest.approx([5.0, 3.0, 0.0])
        assert r["entidades"][1]["texto"] == "SALA\n15,00 m²"
        assert r["capas_creadas"] == ["MCP-RECINTOS"]

    run(world.server(), body)


def test_recinto_en_u_etiqueta_fuera_del_centroide() -> None:
    world = World(18244)
    u = [[0, 0], [6, 0], [6, 4], [4, 4], [4, 1], [2, 1], [2, 4], [0, 4]]

    async def body(c: Client) -> None:
        r = await _ok(c, "recinto", {"contorno": u, "nombre": "PATIO", "altura_texto": 0.15, "decimales": 1})
        n = r["numeros"]
        assert n["area"] == pytest.approx(18.0) and n["etiqueta_en_centroide"] is False
        assert n["centroide"] == pytest.approx([3.0, 11 / 6, 0.0])
        x, y, _z = n["punto_etiqueta"]
        assert not (2 < x < 4 and 1 < y <= 4)  # fuera de la muesca de la U
        assert r["entidades"][1]["texto"] == "PATIO\n18,0 m²"

    run(world.server(), body)


def test_recinto_nombre_con_saltos_de_linea_avisa() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "recinto", {**GENERADORAS["recinto"][0], "nombre": "COCINA\nSUR"})
        assert r["entidades"][1]["texto"] == "COCINA SUR\n12,35 m²"
        assert r["advertencias"] == ["nombre: los saltos de línea se sustituyeron por espacios."]

    run(world.server(), body)


def test_recinto_por_handle_solo_etiqueta() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "recinto", {"contorno": "1A3", "nombre": "Baño", "altura_texto": 0.15, "etiqueta_en": [1, 1]})
        assert [e["tipo"] for e in r["entidades"]] == ["MTEXT"]
        assert r["entidades"][0]["texto"] == "Baño\n15,00 m²" and r["entidades"][0]["posicion"] == [1.0, 1.0, 0.0]
        assert r["numeros"]["etiqueta_en_centroide"] is False
        texto = await _err(c, "recinto", {"contorno": "1A0", "nombre": "X", "altura_texto": 0.15})
        assert texto == INVALIDO + "contorno: la entidad 1A0 no es una polilínea cerrada (LINE)"

    run(world.server(), body)


@pytest.mark.parametrize(
    ("contorno", "esperado"),
    [
        ([[0, 0], [1, 0], [2, 0]], GEOMETRIA + "contorno: el área es 0"),
        ([[0, 0], [1, 1], [1, 0], [0, 1]], INVALIDO + "contorno: la polilínea cerrada se autointerseca (tramos 0 y 2)"),
        ([[0, 0], [1, 0]], INVALIDO + "contorno: debe ser un array de 3 a 200 puntos"),
    ],
)
def test_recinto_rechazos(contorno: Any, esperado: str) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, "recinto", {"contorno": contorno, "nombre": "X", "altura_texto": 0.15}) == esperado

    run(world.server(), body)


# ---------------------------------------------------------------------------
# offset (§5E.3.4)
# ---------------------------------------------------------------------------


def test_offset_de_linea_con_lado_punto_en_la_capa_original() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "offset", GENERADORAS["offset"][0])
        (e,) = r["entidades"]
        assert (e["tipo"], e["capa"], e["origen"]) == ("LINE", "MUROS", "1A0")
        nueva = _entity(world, e["handle"])
        assert nueva is not None and (nueva["inicio"], nueva["fin"]) == ([0.0, 0.5, 0.0], [5.0, 0.5, 0.0])
        n = r["numeros"]
        assert (n["distancia"], n["longitud_original"], n["lado_punto"]) == (0.5, 5.0, [2.0, 1.0, 0.0])

    run(world.server(), body)


@pytest.mark.parametrize(("lado", "y"), [("izquierda", 0.5), ("derecha", -0.5)])
def test_offset_izquierda_derecha(lado: str, y: float) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        r = await _ok(c, "offset", {"handle": "1A0", "distancia": 0.5, "lado": lado, "capa": "MCP-OFFSET"})
        nueva = _entity(world, r["handles"][0])
        assert nueva is not None and nueva["inicio"][1] == pytest.approx(y)
        assert r["capa"] == "MCP-OFFSET" and r["capas_creadas"] == ["MCP-OFFSET"]

    run(world.server(), body)


def test_offset_de_circulo_y_polilinea() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        circulo = await _ok(c, "circulo", {"centro": [2, 2], "radio": 1})
        interior = await _ok(c, "offset", {"handle": circulo["handles"][0], "distancia": 0.25, "lado": "izquierda"})
        assert interior["entidades"][0]["radio"] == pytest.approx(0.75)
        exterior = await _ok(c, "offset", {"handle": circulo["handles"][0], "distancia": 0.25, "lado": [5, 5]})
        assert exterior["entidades"][0]["radio"] == pytest.approx(1.25)
        pl = await _ok(c, "offset", {"handle": "1A3", "distancia": 0.5, "lado": [2.5, 1.5]})
        assert pl["entidades"][0]["area"] == pytest.approx(4.0 * 2.0)
        assert pl["entidades"][0]["capa"] == "0"

    run(world.server(), body)


@pytest.mark.parametrize("lado", [[2, 1], "izquierda", "derecha"])
def test_offset_numeros_bbox_es_la_caja_de_la_paralela_elegida(lado: Any) -> None:
    """Gate en vivo de Fase 4A (2026-10-27): numeros.bbox abarcaba las DOS paralelas; debe ser la de la elegida."""
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "offset", {"handle": "1A0", "distancia": 0.5, "lado": lado, "preview": True, "dibujar_preview": False})
        r = await _ok(c, "offset", {"handle": "1A0", "distancia": 0.5, "lado": lado})
        (e,) = r["entidades"]
        assert r["numeros"]["bbox"] == e["bbox"] == p["numeros"]["bbox"]
        (xmin, ymin), (xmax, ymax) = r["numeros"]["bbox"]
        assert ymin == pytest.approx(ymax)  # una sola línea horizontal, no la franja de las dos candidatas
        assert (xmin, xmax) == pytest.approx((0.0, 5.0))
        # lado_punto queda del lado de la paralela elegida.
        lp = r["numeros"]["lado_punto"]
        assert (lp[1] > 0) == (ymin > 0)

    run(world.server(), body)


@pytest.mark.parametrize("lado", ["izquierda", [2.2, 2]])
def test_offset_hacia_dentro_de_un_circulo_mas_alla_del_radio_falla_sin_escribir(lado: Any) -> None:
    """O3 (2026-10-27): antes se elegía en silencio la paralela del otro lado (exterior)."""
    world = World(18244)

    async def body(c: Client) -> None:
        circulo = await _ok(c, "circulo", {"centro": [2, 2], "radio": 1})
        antes = _snapshot(world)
        for preview in (False, True):
            texto = await _err(c, "offset", {"handle": circulo["handles"][0], "distancia": 1.5, "lado": lado, "preview": preview})
            assert texto == GEOMETRIA + detalle_offset_lado(1.5, "la distancia supera o iguala el radio 1")
        assert _snapshot(world) == antes
        # Hacia fuera sí se construye, aunque el punto de lado esté lejos (sin falso positivo).
        fuera = await _ok(c, "offset", {"handle": circulo["handles"][0], "distancia": 1.5, "lado": [9, 9]})
        assert fuera["entidades"][0]["radio"] == pytest.approx(2.5)

    run(world.server(), body)


def test_detalle_offset_lado_formato() -> None:
    assert detalle_offset_lado(0.15, "x") == "lado: no se puede construir la paralela a 0.15 del lado pedido (x)"


def test_offset_de_entidad_que_no_es_curva() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        texto = await _err(c, "offset", {"handle": "1AA", "distancia": 0.5, "lado": "derecha"})
        assert texto == INVALIDO + "handle: la entidad 1AA (TEXT) no es una curva: no admite offset"
        lado = await _err(c, "offset", {"handle": "1A0", "distancia": 0.5, "lado": "arriba"})
        assert lado == INVALIDO + "lado: debe ser un PuntoRef o uno de izquierda, derecha"

    run(world.server(), body)


# ---------------------------------------------------------------------------
# array_rectangular / array_polar (§5E.3.5)
# ---------------------------------------------------------------------------


def test_array_rectangular_de_entidad_mcp_sin_confirmar() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        h = (await _ok(c, "circulo", {"centro": [2, 0], "radio": 1}))["handles"][0]
        r = await _ok(c, "array_rectangular", {"handles": [h], "filas": 2, "columnas": 3, "sep_filas": 2, "sep_columnas": 3})
        assert len(r["handles"]) == 5 and r["capas"] == ["MCP-DIBUJO"]
        assert [e["centro"][:2] for e in r["entidades"]] == [[5.0, 0.0], [8.0, 0.0], [2.0, 2.0], [5.0, 2.0], [8.0, 2.0]]
        assert all(e["origen"] == h for e in r["entidades"])
        n = r["numeros"]
        assert (n["n_copias"], n["n_posiciones"], n["n_entidades"], n["n_usuario"]) == (5, 6, 5, 0)
        assert n["transformaciones"][0] == {"posicion": [0, 1], "desplazamiento": [3.0, 0.0], "rotacion": 0.0}

    run(world.server(), body)


def test_array_de_entidades_del_usuario_exige_confirmar() -> None:
    world = World(18244)
    args = {"handles": ["1A0", "1AA"], "filas": 1, "columnas": 2, "sep_filas": 1, "sep_columnas": 10}

    async def body(c: Client) -> None:
        texto = await _err(c, "array_rectangular", args)
        assert texto == INVALIDO + (
            "confirmar: 2 de las 2 entidades no están en capas MCP-* (p. ej. 1A0 en MUROS, 1AA en TEXTOS); "
            "pase confirmar: true para modificarlas"
        )
        r = await _ok(c, "array_rectangular", {**args, "confirmar": True})
        assert [e["capa"] for e in r["entidades"]] == ["MUROS", "TEXTOS"] and r["numeros"]["n_usuario"] == 2

    run(world.server(), body)
    err = _plugin_error(world, "array_polar", {"handles": ["1A0"], "centro": [0, 0], "n": 3, "angulo_total": 90})
    assert (err.data["n_usuario"], err.data["ejemplos"]) == (1, [{"handle": "1A0", "capa": "MUROS"}])


@pytest.mark.parametrize(
    ("cambio", "detalle"),
    [
        ({"filas": 30, "columnas": 20}, "array: el número de copias (599) supera el tope de 500"),
        ({"filas": 1, "columnas": 1}, "filas/columnas: se necesita al menos 2 posiciones"),
        ({"handles": ["1A0", "1a0"]}, "handles[1]: handle repetido"),
        ({"handles": ["1A5"]}, "handles[0]: es un atributo; use el handle del INSERT"),
    ],
)
def test_array_rectangular_rechazos(cambio: dict[str, Any], detalle: str) -> None:
    world = World(18244)
    base = {"handles": ["1A0"], "filas": 2, "columnas": 2, "sep_filas": 1, "sep_columnas": 1, "confirmar": True}

    async def body(c: Client) -> None:
        assert await _err(c, "array_rectangular", {**base, **cambio}) == INVALIDO + detalle

    antes = _snapshot(world)
    run(world.server(), body)
    assert _snapshot(world) == antes


def test_array_polar_vuelta_completa_con_y_sin_rotacion() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        h = (await _ok(c, "circulo", {"centro": [2, 0], "radio": 1}))["handles"][0]
        r = await _ok(c, "array_polar", {"handles": [h], "centro": [0, 0], "n": 4, "angulo_total": 360})
        assert [e["centro"][:2] for e in r["entidades"]] == [pytest.approx(p) for p in ([0, 2], [-2, 0], [0, -2])]
        assert [t["rotacion"] for t in r["numeros"]["transformaciones"]] == [90.0, 180.0, 270.0]
        t = await _ok(c, "texto", {"posicion": [2, 0], "texto": "T", "altura": 0.2})
        sin = await _ok(
            c,
            "array_polar",
            {"handles": t["handles"], "centro": [0, 0], "n": 3, "angulo_total": 90, "rotar_elementos": False},
        )
        assert [x["rotacion"] for x in sin["numeros"]["transformaciones"]] == [0.0, 0.0]
        assert all(_entity(world, hh)["rotacion"] == 0.0 for hh in sin["handles"])  # type: ignore[index]

    run(world.server(), body)


def test_array_preview_va_a_mcp_preview_y_confirmar_a_la_capa_original() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        h = (await _ok(c, "circulo", {"centro": [2, 0], "radio": 1}))["handles"][0]
        p = await _ok(c, "array_rectangular", {"handles": [h], "filas": 1, "columnas": 3, "sep_filas": 1, "sep_columnas": 3, "preview": True})
        assert p["capas"] == [CAPA_PREVIEW] and len(p["handles"]) == 2
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert r["capas"] == ["MCP-DIBUJO"] and r["n_preview_borradas"] == 2 and r["herramienta"] == "array_rectangular"

    run(world.server(), body)
    assert _en_capa(world, CAPA_PREVIEW) == []


# ---------------------------------------------------------------------------
# Previsualización: preview_id, confirmar, descartar_preview (§5E.4, §5E.5, §5E.7)
# ---------------------------------------------------------------------------


def test_preview_dibujada_confirmar_y_confirmar_de_nuevo() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True})
        assert list(p) == CLAVES_G6
        assert PREVIEW_ID_RE.fullmatch(p["preview_id"]) and p["preview"] is True and p["preview_dibujada"] is True
        assert p["caduca"] == CADUCA_INICIAL
        assert (p["capa"], p["capas"], p["capas_creadas"]) == (CAPA_PREVIEW, [CAPA_PREVIEW], [CAPA_PREVIEW])
        assert [_entity(world, h)["capa"] for h in p["handles"]] == [CAPA_PREVIEW]  # type: ignore[index]
        assert _state(world).pasos_deshacer == 1
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert list(r)[: len(CLAVES_G6)] == CLAVES_G6
        assert (r["confirmado"], r["preview_id"], r["herramienta"], r["n_preview_borradas"]) == (True, p["preview_id"], "muro", 1)
        assert (r["preview"], r["caduca"], r["capa"]) == (False, None, "MUROS")
        assert r["numeros"] == p["numeros"]
        assert _entity(world, p["handles"][0]) is None
        assert _entity(world, r["handles"][0])["capa"] == "MUROS"  # type: ignore[index]
        otra = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert otra == "[NOT_FOUND] " + mensaje_preview_no_encontrada(p["preview_id"])

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 2 and _state(world).previews == {}


def test_preview_sin_dibujar_no_escribe_y_confirmar_crea() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        antes = _snapshot(world)
        p = await _ok(c, "recinto", {**GENERADORAS["recinto"][0], "preview": True, "dibujar_preview": False})
        assert (p["handles"], p["entidades"], p["preview_dibujada"], p["capa"], p["capas"]) == ([], [], False, CAPA_PREVIEW, [])
        assert p["numeros"]["area"] == pytest.approx(12.35)
        despues = _snapshot(world)
        assert despues[0] == antes[0] and despues[3] == antes[3] == 0 and despues[2] == 1
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert r["n_preview_borradas"] == 0 and [e["tipo"] for e in r["entidades"]] == ["LWPOLYLINE", "MTEXT"]

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 1


def test_preview_guarda_parametros_sin_preview_ni_dibujar() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True, "dibujar_preview": True, "forzar": False})
        rec = _state(world).previews[p["preview_id"]]
        assert rec["params"] == {**MURO, "forzar": False} and rec["herramienta"] == "muro"
        assert rec["handles"] == p["handles"] and rec["espacio"] == "modelo"

    run(world.server(), body)


def test_descartar_preview_con_y_sin_entidades() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "ejes_rejilla", {**GENERADORAS["ejes_rejilla"][0], "preview": True})
        assert len(_en_capa(world, CAPA_PREVIEW)) == 12 and _state(world).pasos_deshacer == 1
        d = await _ok(c, "descartar_preview", {"preview_id": p["preview_id"]})
        assert d == {
            "pid": 18244,
            "preview_id": p["preview_id"],
            "descartada": True,
            "n_borradas": 12,
            "n_no_encontradas": 0,
            "herramienta": "ejes_rejilla",
            "insunits": 6,
            "unidades": "m",
            "advertencias": [],
        }
        assert _en_capa(world, CAPA_PREVIEW) == [] and _state(world).pasos_deshacer == 2
        # Sin entidades que borrar: no se abre paso de deshacer.
        q = await _ok(c, "muro", {**MURO, "preview": True, "dibujar_preview": False})
        d2 = await _ok(c, "descartar_preview", {"preview_id": q["preview_id"]})
        assert (d2["n_borradas"], d2["n_no_encontradas"]) == (0, 0) and _state(world).pasos_deshacer == 2
        nf = await _err(c, "confirmar", {"preview_id": q["preview_id"]})
        assert nf == "[NOT_FOUND] " + mensaje_preview_no_encontrada(q["preview_id"])

    run(world.server(), body)


def test_descartar_preview_inexistente_not_found_con_data() -> None:
    world = World(18244)
    err = _plugin_error(world, "descartar_preview", {"preview_id": "prev-000000000000"})
    assert (err.code, err.rpc_code, err.data["preview_id"]) == ("NOT_FOUND", -32003, "prev-000000000000")
    assert err.plugin_message == mensaje_preview_no_encontrada("prev-000000000000")


def test_preview_caduca_a_los_15_minutos_y_deja_huerfanas() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True})
        assert (await _ok(c, "estado"))["advertencias"] == []  # la preview viva no es huérfana
        _state(world).ahora += timedelta(minutes=14, seconds=59)
        assert _state(world).previews  # aún viva
        _state(world).ahora += timedelta(seconds=1)
        nf = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert nf == "[NOT_FOUND] " + mensaje_preview_no_encontrada(p["preview_id"])
        estado = await _ok(c, "estado")
        assert estado["advertencias"] == [advertencia_huerfanas(1)]
        assert estado["advertencias"][0].endswith('limpie con limpiar_observaciones {"confirmar": true, "capa": "MCP-PREVIEW"}.')
        gen = await _ok(c, "muro", {"eje": [[0, 1], [4, 1]], "espesor": 0.15})
        assert advertencia_huerfanas(1) in gen["advertencias"]
        limpio = await _ok(c, "limpiar_observaciones", {"confirmar": True, "capa": CAPA_PREVIEW})
        assert limpio["n_borradas"] == 1
        assert (await _ok(c, "estado"))["advertencias"] == []

    run(world.server(), body)


def test_caducar_no_escribe_nada() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        await _ok(c, "muro", {**MURO, "preview": True})
        antes = (len(_state(world).entities), _state(world).pasos_deshacer)
        _state(world).ahora += timedelta(hours=1)
        await _ok(c, "estado")
        assert (len(_state(world).entities), _state(world).pasos_deshacer) == antes
        assert len(_en_capa(world, CAPA_PREVIEW)) == 1

    run(world.server(), body)


def test_confirmar_tras_borrar_la_preview() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True})
        await _ok(c, "borrar", {"handles": p["handles"]})  # MCP-PREVIEW: sin confirmar (capa MCP-*)
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert r["n_preview_borradas"] == 0 and r["confirmado"] is True and len(r["handles"]) == 1

    run(world.server(), body)


def test_confirmar_conserva_la_preview_movida_de_capa() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True})
        h = p["handles"][0]
        _entity(world, h)["capa"] = "MUROS"  # type: ignore[index]  # el usuario la cambió de capa
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert r["n_preview_borradas"] == 0
        assert advertencia_preview_movida(h, "MUROS") in r["advertencias"]
        assert _entity(world, h) is not None

    run(world.server(), body)


def test_confirmar_tras_mover_el_ancla_no_escribe() -> None:
    world = World(18244)
    anclado = {"eje": [{"handle": "1A0", "punto": "inicio"}, {"handle": "1A0", "punto": "fin"}], "espesor": 0.15, "preview": True}

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", anclado)
        await _ok(c, "mover", {"handles": ["1A0"], "desplazamiento": [0, 0.5], "confirmar": True})
        antes = _snapshot(world)
        texto = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert texto == INVALIDO + detalle_ancla_cambiada("1A0")
        assert _snapshot(world) == antes and p["preview_id"] in _state(world).previews

    run(world.server(), body)
    rec_id = next(iter(_state(world).previews))
    err = _plugin_error(world, "confirmar", {"preview_id": rec_id})
    assert (err.data["preview_id"], err.data["handle"]) == (rec_id, "1A0")


def test_confirmar_tras_borrar_el_ancla_de_una_interseccion() -> None:
    world = World(18244)
    args = {**GENERADORAS["ejes_rejilla"][0], "origen": {"handle": "1A0", "interseccion_con": "1A1"}, "preview": True}

    async def body(c: Client) -> None:
        p = await _ok(c, "ejes_rejilla", args)
        assert p["puntos_resueltos"][0]["ancla"] == "1A0+1A1"
        await _ok(c, "borrar", {"handles": ["1A1"], "confirmar": True})
        texto = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert texto == INVALIDO + detalle_ancla_cambiada("1A1")

    run(world.server(), body)


def test_confirmar_con_anclas_intactas_recalcula() -> None:
    world = World(18244)
    anclado = {"eje": [{"handle": "1A0", "punto": "inicio"}, {"handle": "1A0", "punto": "fin"}], "espesor": 0.15, "preview": True}

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", anclado)
        await _ok(c, "mover", {"handles": ["1AA"], "desplazamiento": [0, 0.5], "confirmar": True})  # otra entidad
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert r["puntos_resueltos"] == p["puntos_resueltos"] and r["numeros"]["longitud_eje"] == pytest.approx(5.0)

    run(world.server(), body)


def test_confirmar_y_descartar_de_otro_documento() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True})
        _state(world).doc_key = "doc-OTRO.dwg"
        esperado = INVALIDO + detalle_preview_otro_documento(p["preview_id"])
        assert await _err(c, "confirmar", {"preview_id": p["preview_id"]}) == esperado
        assert await _err(c, "descartar_preview", {"preview_id": p["preview_id"]}) == esperado
        _state(world).doc_key = "doc-PLANO_Ñandú.dwg"
        await _ok(c, "confirmar", {"preview_id": p["preview_id"]})

    run(world.server(), body)


def test_confirmar_sin_unidades_recalcula_con_g2() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True, "dibujar_preview": False})
        _state(world).insunits = 0
        assert await _err(c, "confirmar", {"preview_id": p["preview_id"]}) == UNITS_UNKNOWN
        _state(world).insunits = 6
        assert (await _ok(c, "confirmar", {"preview_id": p["preview_id"]}))["confirmado"] is True

    run(world.server(), body)


def test_tope_de_20_previsualizaciones_desaloja_la_mas_antigua() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        ids = []
        for i in range(20):
            p = await _ok(c, "muro", {"eje": [[0, i], [4, i]], "espesor": 0.15, "preview": True, "dibujar_preview": False})
            assert advertencia_desalojo(ids[0]) not in p["advertencias"] if ids else True
            ids.append(p["preview_id"])
        assert len(set(ids)) == 20
        ultima = await _ok(c, "muro", {**MURO, "preview": True})
        assert ultima["advertencias"][-1] == advertencia_desalojo(ids[0])
        assert len(_state(world).previews) == 20
        nf = await _err(c, "confirmar", {"preview_id": ids[0]})
        assert nf == "[NOT_FOUND] " + mensaje_preview_no_encontrada(ids[0])
        await _ok(c, "confirmar", {"preview_id": ids[1]})

    run(world.server(), body)


def test_confirmar_schema_y_preview_id_invalido() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, "confirmar", {}) == INVALIDO + "preview_id: obligatorio"
        assert await _err(c, "confirmar", {"preview_id": ""}) == INVALIDO + "preview_id: debe tener al menos 1 carácter(es)"
        largo = await _err(c, "descartar_preview", {"preview_id": "p" * 65})
        assert largo == INVALIDO + "preview_id: debe tener como máximo 64 caracteres"
        assert await _err(c, "descartar_preview", {"preview_id": "x", "forzar": True}) == INVALIDO + "forzar: propiedad no permitida"

    run(world.server(), body)


def test_escrituras_de_fase4a_no_son_idempotentes_para_el_servidor() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {**MURO, "preview": True})
        await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        await _ok(c, "punto_de", {"ref": [0, 0]})

    run(world.server(), body)
    metodos = [r["method"] for r in _state(world).requests if r["method"] != "tools/manifest"]
    assert metodos == ["muro", "confirmar", "punto_de"]


# ---------------------------------------------------------------------------
# Correcciones de la auditoría de Fase 4A (2026-10-26)
# ---------------------------------------------------------------------------


def test_error_rpc_32005_se_traduce_a_invalid_geometry() -> None:
    err = PluginError.from_rpc_error({"code": -32005, "message": "Geometría no construible: x"})
    assert (err.code, err.rpc_code) == ("INVALID_GEOMETRY", -32005)
    assert RPC_CODE_TO_SYMBOLIC[-32005] == "INVALID_GEOMETRY"


@pytest.mark.parametrize("justificacion", ["centro", "izquierda", "derecha"])
def test_muro_45_grados_aceptado_con_cualquier_justificacion(justificacion: str) -> None:
    world = World(18244)
    a = math.radians(45)
    eje = [[0, 0], [4, 0], [4 - 4 * math.cos(a), 4 * math.sin(a)]]

    async def body(c: Client) -> None:
        await _ok(c, "muro", {"eje": eje, "espesor": 0.15, "justificacion": justificacion})

    run(world.server(), body)


@pytest.mark.parametrize("justificacion", ["centro", "izquierda", "derecha"])
def test_muro_angulo_agudo_mismo_mensaje_con_una_cifra_decimal(justificacion: str) -> None:
    world = World(18244)
    a = math.radians(20)
    eje = [[0, 0], [4, 0], [4 - 4 * math.cos(a), 4 * math.sin(a)]]

    async def body(c: Client) -> None:
        texto = await _err(c, "muro", {"eje": eje, "espesor": 0.15, "justificacion": justificacion})
        assert texto == GEOMETRIA + "eje: el ángulo en el vértice 1 (20.0°) es demasiado agudo para el inglete (límite 4× el espesor)"

    run(world.server(), body)


def test_confirmar_muro_por_handle_tras_editar_un_vertice_del_eje() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "muro", {"eje": "1A3", "espesor": 0.15, "preview": True})
        assert p["puntos_resueltos"] == []
        # Vértice interior movido sin cambiar los extremos (huella con vértices/longitud).
        _entity(world, "1A3")["vertices"][2] = [4.5, 3.0, 0.0]  # type: ignore[index]
        antes = _snapshot(world)
        texto = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert texto == INVALIDO + detalle_ancla_cambiada("1A3")
        assert _snapshot(world) == antes

    run(world.server(), body)


def test_confirmar_recinto_por_handle_tras_borrar_el_contorno() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "recinto", {"contorno": "1A3", "nombre": "Baño", "altura_texto": 0.15, "preview": True})
        await _ok(c, "borrar", {"handles": ["1A3"], "confirmar": True})
        texto = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert texto == INVALIDO + detalle_ancla_cambiada("1A3")

    run(world.server(), body)


def test_confirmar_offset_tras_mover_el_original() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "offset", {**GENERADORAS["offset"][0], "preview": True})
        await _ok(c, "mover", {"handles": ["1A0"], "desplazamiento": [0, 0.5], "confirmar": True})
        antes = _snapshot(world)
        texto = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert texto == INVALIDO + detalle_ancla_cambiada("1A0")
        assert _snapshot(world) == antes

    run(world.server(), body)


@pytest.mark.parametrize("nombre", ["array_rectangular", "array_polar"])
def test_confirmar_array_tras_borrar_un_original(nombre: str) -> None:
    world = World(18244)
    args, _ = GENERADORAS[nombre]
    h = args["handles"][0]

    async def body(c: Client) -> None:
        p = await _ok(c, nombre, {**args, "preview": True})
        await _ok(c, "borrar", {"handles": [h], "confirmar": True})
        texto = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert texto == INVALIDO + detalle_ancla_cambiada(h)

    run(world.server(), body)


def test_confirmar_por_handle_con_original_intacto_escribe() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, "offset", {**GENERADORAS["offset"][0], "preview": True})
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert r["confirmado"] is True and len(r["handles"]) == 1

    run(world.server(), body)


def test_recinto_por_handle_colapsa_vertices_duplicados_con_advertencia() -> None:
    world = World(18244)
    _entity(world, "1A3")["vertices"] = [  # type: ignore[index]
        [0.0, 0.0, 0.0], [5.0, 0.0, 0.0], [5.0, 0.0, 0.0], [5.0, 3.0, 0.0], [0.0, 3.0, 0.0], [0.0, 3.0, 0.0],
    ]

    async def body(c: Client) -> None:
        r = await _ok(c, "recinto", {"contorno": "1A3", "nombre": "Baño", "altura_texto": 0.15})
        assert r["numeros"]["n_vertices"] == 4 and r["numeros"]["area"] == pytest.approx(15.0)
        assert advertencia_duplicados_colapsados("1A3", 2) in r["advertencias"]

    run(world.server(), body)


def test_recinto_por_handle_con_menos_de_3_vertices_tras_colapsar_se_rechaza() -> None:
    world = World(18244)
    _entity(world, "1A3")["vertices"] = [  # type: ignore[index]
        [0.0, 0.0, 0.0], [0.0, 0.0, 0.0], [5.0, 0.0, 0.0], [5.0, 0.0, 0.0],
    ]

    async def body(c: Client) -> None:
        texto = await _err(c, "recinto", {"contorno": "1A3", "nombre": "X", "altura_texto": 0.15})
        assert texto == INVALIDO + "contorno: una polilínea cerrada necesita al menos 3 vértices"

    run(world.server(), body)


@pytest.mark.parametrize(
    ("conjunto", "estado"),
    [("capas_bloqueadas", "bloqueada"), ("capas_congeladas", "congelada")],
)
def test_offset_sin_capa_sobre_original_en_capa_no_escribible(conjunto: str, estado: str) -> None:
    world = World(18244)
    getattr(_state(world), conjunto).add("MUROS")

    async def body(c: Client) -> None:
        antes = _snapshot(world)
        texto = await _err(c, "offset", GENERADORAS["offset"][0])
        assert texto == INVALIDO + detalle_capa_heredada("1A0", estado, "MUROS")
        assert _snapshot(world) == antes
        # Con 'capa' explícita se escribe (el original no se modifica).
        r = await _ok(c, "offset", {**GENERADORAS["offset"][0], "capa": "MCP-OFFSET"})
        assert r["capa"] == "MCP-OFFSET"

    run(world.server(), body)
    err = _plugin_error(world, "offset", GENERADORAS["offset"][0])
    assert (err.data["capa"], err.data["handle"]) == ("MUROS", "1A0")


def test_offset_sin_capa_sobre_original_en_capa_apagada_avisa() -> None:
    world = World(18244)
    _state(world).capas_apagadas.add("MUROS")

    async def body(c: Client) -> None:
        r = await _ok(c, "offset", GENERADORAS["offset"][0])
        assert any(a.startswith("offset: la curva 1A0 está en la capa apagada 'MUROS'") for a in r["advertencias"])

    run(world.server(), body)


def test_muro_por_handle_colapsa_vertices_duplicados_con_advertencia() -> None:
    world = World(18244)
    _entity(world, "1A3")["vertices"] = [  # type: ignore[index]
        [0.0, 0.0, 0.0], [5.0, 0.0, 0.0], [5.0, 0.0, 0.0], [5.0, 3.0, 0.0], [0.0, 3.0, 0.0],
    ]

    async def body(c: Client) -> None:
        r = await _ok(c, "muro", {"eje": "1A3", "espesor": 0.15})
        assert advertencia_eje_duplicados_colapsados("1A3", 1) in r["advertencias"]
        assert r["numeros"]["n_tramos"] == 4 and r["numeros"]["longitud_eje"] == pytest.approx(16.0)
        # Con PuntoRef[] el duplicado sigue siendo error.
        texto = await _err(c, "muro", {"eje": [[0, 0], [5, 0], [5, 0], [5, 3]], "espesor": 0.15})
        assert texto == INVALIDO + "eje[2]: vértice duplicado con el anterior"

    run(world.server(), body)


@pytest.mark.parametrize(
    ("nombre", "args"),
    [
        ("muro", {"eje": "1A3", "espesor": 0.15}),
        ("recinto", {"contorno": "1A3", "nombre": "Baño", "altura_texto": 0.15}),
    ],
)
def test_confirmar_por_handle_tras_mover_la_polilinea_no_escribe(nombre: str, args: dict[str, Any]) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        p = await _ok(c, nombre, {**args, "preview": True})
        await _ok(c, "mover", {"handles": ["1A3"], "desplazamiento": [0, 0.5], "confirmar": True})
        antes = _snapshot(world)
        texto = await _err(c, "confirmar", {"preview_id": p["preview_id"]})
        assert texto == INVALIDO + detalle_ancla_cambiada("1A3")
        assert _snapshot(world) == antes and p["preview_id"] in _state(world).previews

    run(world.server(), body)


def test_huerfanas_sin_capa_mcp_preview_no_avisa() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert (await _ok(c, "estado"))["advertencias"] == []

    run(world.server(), body)
