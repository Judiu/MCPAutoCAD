"""Pruebas de la Fase L (protocolo 0.6, protocol.md §5F) a través del cliente MCP y del PipeClient.

El servidor es genérico: estas pruebas comprueban que las 4 herramientas de limpieza se publican
con 'escritura: true', sus descripciones literales y los parámetros comunes confirmar/preview, y que
el plugin simulado (fake_plugin.py, sin AutoCAD) reproduce las reglas L1–L8 de §5F.0: sin
confirmar: true nunca se escribe (previsualización sin estado, el dibujo y los pasos de deshacer no
cambian), preview: false sin confirmar → INVALID_PARAMS exacto, confirmar + preview: true sigue
siendo previsualización, una escritura = un paso de deshacer con el mismo inventario que la
previsualización, e INSUNITS = 0 → UNITS_UNKNOWN solo al escribir. Además, los criterios propios de
borrar_fuera_de (§5F.1), purgar (§5F.2), normalizar_capas (§5F.3) y corregir_unidades_bloques (§5F.4)
sobre el dibujo ficticio de la bandera faseL. Se simula la FORMA del contrato, no AutoCAD.
"""

from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any

import pytest
from fake_plugin import (
    ADVERTENCIA_BLOQUES_SIN_UNIDADES,
    ADVERTENCIA_INSUNITS_0,
    CLAVES_L6,
    CONFIRMAR_LIMPIEZA_SCHEMA,
    DESCRIPCIONES_FASEL,
    DETALLE_PREVIEW_SIN_CONFIRMAR,
    HERRAMIENTAS_FASEL,
    PREVIEW_LIMPIEZA_SCHEMA,
    SCHEMAS_FASEL,
    FakePluginState,
    advertencia_anonimos_dinamicos,
    advertencia_excluir_inexistente,
    advertencia_origen_no_borrable,
    advertencia_propiedades_forzadas,
    advertencia_viewports_excluidos,
    advertencia_purga_difiere,
    advertencia_viewport,
    clasificar_caja,
    detalle_espacios_inexistentes,
    _layer,
    coord_l,
    manifest_tools,
)
from mcp import Client
from test_server import LOCAL_ORDER, MANIFEST_ORDER, World, _guardar_manifest, run, text_of

from mcp_autocad.client import PipeClient
from mcp_autocad.discovery import Instance
from mcp_autocad.errors import PluginError

NO_INSTANCE = "AutoCAD no está abierto o el plugin no cargó."
UNITS_UNKNOWN = "[UNITS_UNKNOWN] El dibujo no declara unidades (INSUNITS=0)."
INVALIDO = "[INVALID_PARAMS] Parámetros inválidos: "
CLEANWRITE_CS = Path(__file__).resolve().parents[2] / "plugin" / "MCPAutoCAD.Plugin" / "Handlers" / "Clean" / "CleanWrite.cs"
NORMALIZAR_CS = CLEANWRITE_CS.with_name("NormalizarCapasHandler.cs")

AVISO_VIEWPORT_1C8 = (
    "El viewport 1C8 del layout 'Layout1' mira la ventana de modelo [[0, 0], [30, 30]] que quedará parcialmente "
    "borrada: se borran 2 entidades que la intersecan y se conservan 3."
)

# Argumentos mínimos válidos de cada herramienta sobre el dibujo de Fase L (o con compensación de bloques).
ARGS: dict[str, dict[str, Any]] = {
    "borrar_fuera_de": {"contorno": "1C0"},
    "purgar": {},
    "normalizar_capas": {"mapa": {"MUEBLE": "mobiliario_fijo"}},
    "corregir_unidades_bloques": {},
}
# Estado de cada herramienta: corregir_unidades_bloques necesita bloques con metadato de unidades incorrecto.
ESTADO: dict[str, dict[str, Any]] = {
    "borrar_fuera_de": {"faseL": True},
    "purgar": {"faseL": True},
    "normalizar_capas": {"faseL": True},
    "corregir_unidades_bloques": {"bloques_compensacion": True},
}
# Campos propios de cada respuesta (§5F.1–§5F.4).
CAMPOS: dict[str, list[str]] = {
    "borrar_fuera_de": [
        "contorno", "espacio", "criterio", "n_total", "n_borrar", "n_conservar", "n_cruzan", "n_sin_caja",
        "n_excluidas", "n_borradas", "por_tipo", "por_capa", "excluidas_por_capa", "muestra", "viewports",
        "duracion_ms", "origen_texto",
    ],
    "purgar": ["categorias", "rondas", "purgables", "protegidos", "excluidos", "purgados", "duracion_ms", "origen_texto"],
    "normalizar_capas": [
        "mapa", "n_reasignar", "n_reasignadas", "por_origen", "n_excluidas", "espacios", "incluir_viewports",
        "n_propiedades_forzadas", "n_cambian_aspecto", "n_aspecto_fijado", "incluir_bloques",
        "borrar_capas_origen_vacias", "capas_borrar", "capas_borradas", "capas_creadas", "duracion_ms", "origen_texto",
    ],
    "corregir_unidades_bloques": ["origen_lista", "bloques", "n", "corregidos", "duracion_ms", "origen_texto"],
}
# Campo "lo hecho" de cada herramienta (null en previsualización).
HECHO = {
    "borrar_fuera_de": "n_borradas",
    "purgar": "purgados",
    "normalizar_capas": "n_reasignadas",
    "corregir_unidades_bloques": "corregidos",
}


def _world(**kw: Any) -> World:
    return World(18244, **kw)


def _client(world: World) -> PipeClient:
    return world.client_factory(Instance(pid=18244, pipe="x", user="usuario"))


def _call(world: World, method: str, params: dict[str, Any]) -> dict[str, Any]:
    return _client(world).call(method, params)


def _plugin_error(world: World, method: str, params: dict[str, Any]) -> PluginError:
    with pytest.raises(PluginError) as exc:
        _client(world).call(method, params)
    return exc.value


def _state(world: World) -> FakePluginState:
    return world.states[18244]


def _foto(world: World) -> dict[str, Any]:
    """Todo lo que una escritura de Fase L podría cambiar en el dibujo simulado."""
    st = _state(world)
    return copy.deepcopy(
        {
            "entities": st.entities,
            "layouts": st.layout_entities,
            "block_defs": st.block_defs,
            "capas_extra": st.capas_extra,
            "capas_creadas": st.capas_creadas,
            "capas_borradas": st.capas_borradas,
            "tipos_linea": st.tipos_linea,
            "estilos_texto": st.estilos_texto,
            "estilos_cota": st.estilos_cota,
            "regapps": st.regapps,
            "pasos": st.pasos_deshacer,
        }
    )


def _handles(world: World) -> list[str]:
    return [e["handle"] for e in _state(world).entities]


def _capas(world: World) -> set[str]:
    st = _state(world)
    from fake_plugin import FAKE_LAYERS

    todas = FAKE_LAYERS + st.capas_extra + st.capas_creadas
    return {c["nombre"] for c in todas if c["nombre"].casefold() not in st.capas_borradas}


async def _ok(c: Client, name: str, args: dict[str, Any]) -> dict[str, Any]:
    res = await c.call_tool(name, args)
    assert not res.is_error, text_of(res)
    data = json.loads(text_of(res))
    assert res.structured_content == data
    return data


async def _err(c: Client, name: str, args: dict[str, Any]) -> str:
    res = await c.call_tool(name, args)
    assert res.is_error is True, text_of(res)
    return text_of(res)


# ---------------------------------------------------------------------------
# Publicación (tools/list) y manifest
# ---------------------------------------------------------------------------


def test_manifest_55_en_el_orden_de_protocol_4() -> None:
    tools = manifest_tools()
    assert len(tools) == 56
    assert [t["name"] for t in tools] == MANIFEST_ORDER
    assert [t["name"] for t in tools][47:51] == HERRAMIENTAS_FASEL == MANIFEST_ORDER[47:51]
    assert HERRAMIENTAS_FASEL == ["borrar_fuera_de", "purgar", "normalizar_capas", "corregir_unidades_bloques"]
    # 33 de escritura en total (3 de Fases 0–2, 14 de Fase 3, 8 de Fase 4A, 4 de Fase L y 4 de Fase 4B).
    assert sum(1 for t in tools if t["escritura"]) == 33


def test_faseL_todas_de_escritura_y_requieren_documento() -> None:
    for t in manifest_tools()[47:51]:
        assert t["escritura"] is True, t["name"]
        assert t["requiere_documento"] is True, t["name"]


def test_descripciones_literales_de_5f() -> None:
    descripciones = {t["name"]: t["description"] for t in manifest_tools()}
    for name in HERRAMIENTAS_FASEL:
        assert descripciones[name] == DESCRIPCIONES_FASEL[name], name
    assert descripciones["borrar_fuera_de"].startswith("BORRA todas las entidades de un espacio que quedan FUERA")
    assert descripciones["purgar"].endswith("Sin confirmar: true solo PREVISUALIZA (nombres por categoría y entidades de cada bloque) y no modifica nada.")
    assert descripciones["normalizar_capas"].startswith("REASIGNA a otra capa")
    assert descripciones["corregir_unidades_bloques"].startswith("CORRIGE el metadato de unidades")


def test_descripciones_coinciden_con_protocol_md() -> None:
    texto = (Path(__file__).resolve().parents[2] / "protocol.md").read_text(encoding="utf-8")
    for name in HERRAMIENTAS_FASEL:
        assert f'Descripción del manifest (literal): `"{DESCRIPCIONES_FASEL[name]}"`' in texto, name


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_schema_con_confirmar_preview_y_cerrado(name: str) -> None:
    schema = {t["name"]: t["inputSchema"] for t in manifest_tools()}[name]
    assert schema == SCHEMAS_FASEL[name]
    assert schema["additionalProperties"] is False
    assert schema["properties"]["confirmar"] == CONFIRMAR_LIMPIEZA_SCHEMA
    assert schema["properties"]["preview"] == PREVIEW_LIMPIEZA_SCHEMA
    assert "aceptar_sin_unidades" not in schema["properties"]
    assert schema["properties"]["confirmar"]["default"] is False


def test_descripcion_de_normalizar_capas_igual_al_handler() -> None:
    """La descripción del simulador es la del handler C# (concatenación de literales de Description)."""
    import re

    cs = NORMALIZAR_CS.read_text(encoding="utf-8")
    bloque = cs.split("public string Description =>", 1)[1].split('";', 1)[0] + '"'
    literal = "".join(m.replace('\\"', '"') for m in re.findall(r'"((?:[^"\\]|\\.)*)"', bloque))
    assert literal == DESCRIPCIONES_FASEL["normalizar_capas"]


def test_textos_de_schema_iguales_a_cleanwrite() -> None:
    cs = CLEANWRITE_CS.read_text(encoding="utf-8")
    assert json.dumps(CONFIRMAR_LIMPIEZA_SCHEMA["description"], ensure_ascii=False).replace('"', '""') in cs
    assert json.dumps(PREVIEW_LIMPIEZA_SCHEMA["description"], ensure_ascii=False).replace('"', '""') in cs
    assert f'"{DETALLE_PREVIEW_SIN_CONFIRMAR}"' in cs


def test_tools_list_publica_las_4_de_faseL() -> None:
    world = _world(faseL=True)
    esperado = {t["name"]: t for t in manifest_tools()}

    async def body(c: Client) -> None:
        tools = (await c.list_tools()).tools
        assert [t.name for t in tools] == MANIFEST_ORDER + LOCAL_ORDER
        assert len(tools) == 61
        publicadas = {t.name: t for t in tools}
        for name in HERRAMIENTAS_FASEL:
            assert publicadas[name].input_schema == esperado[name]["inputSchema"], name
            assert publicadas[name].description == esperado[name]["description"], name

    run(world.server(), body)


def test_manifest_en_disco_publica_55_y_faseL_sin_instancia(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.json"
    _guardar_manifest(_world(), manifest)
    guardado = json.loads(manifest.read_text(encoding="utf-8"))
    assert guardado["protocol_version"] == "0.8" and guardado["plugin_version"] == "0.8.0"
    assert [t["name"] for t in guardado["tools"]] == MANIFEST_ORDER

    async def body(c: Client) -> None:
        assert len((await c.list_tools()).tools) == 61
        res = await c.call_tool("purgar", {})
        assert res.is_error and text_of(res) == NO_INSTANCE

    run(World().server(manifest_path=manifest), body)


# ---------------------------------------------------------------------------
# L1–L6 comunes a las cuatro herramientas
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_sin_confirmar_previsualiza_y_no_cambia_nada(name: str) -> None:
    world = _world(**ESTADO[name])
    antes = _foto(world)
    r = _call(world, name, ARGS[name])
    assert (r["preview"], r["escrito"]) == (True, False)
    assert r[HECHO[name]] is None
    assert r["duracion_ms"]["escritura"] is None
    assert _foto(world) == antes


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_respuesta_con_cabecera_l6_y_campos_propios(name: str) -> None:
    world = _world(**ESTADO[name])
    r = _call(world, name, ARGS[name])
    assert list(r)[: len(CLAVES_L6)] == CLAVES_L6
    for campo in CAMPOS[name]:
        assert campo in r, campo
    assert (r["pid"], r["insunits"], r["unidades"], r["advertencias"] is not None) == (18244, 6, "m", True)
    assert r["origen_texto"] == "contenido_del_plano"


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_preview_false_sin_confirmar_es_invalid_params_literal(name: str) -> None:
    world = _world(**ESTADO[name])
    antes = _foto(world)
    err = _plugin_error(world, name, {**ARGS[name], "preview": False})
    assert (err.code, err.rpc_code) == ("INVALID_PARAMS", -32602)
    assert err.data["detalle"] == DETALLE_PREVIEW_SIN_CONFIRMAR
    assert _foto(world) == antes


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_confirmar_con_preview_true_sigue_sin_escribir(name: str) -> None:
    world = _world(**ESTADO[name])
    antes = _foto(world)
    r = _call(world, name, {**ARGS[name], "confirmar": True, "preview": True})
    assert (r["preview"], r["escrito"], r[HECHO[name]]) == (True, False, None)
    assert _foto(world) == antes


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_confirmar_escribe_en_un_paso_con_el_inventario_de_la_preview(name: str) -> None:
    world = _world(**ESTADO[name])
    previa = _call(world, name, ARGS[name])
    r = _call(world, name, {**ARGS[name], "confirmar": True})
    assert (r["preview"], r["escrito"]) == (False, True)
    assert _state(world).pasos_deshacer == 1
    assert r["duracion_ms"]["escritura"] is not None
    # L5: el inventario es el mismo en ambos modos (salvo la cabecera y lo realmente hecho).
    variables = {"preview", "escrito", "duracion_ms", HECHO[name], "capas_borradas", "capas_creadas", "mapa", "n_aspecto_fijado"}
    assert {k: v for k, v in r.items() if k not in variables} == {k: v for k, v in previa.items() if k not in variables}


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_dos_escrituras_dos_pasos(name: str) -> None:
    world = _world(**ESTADO[name])
    _call(world, name, {**ARGS[name], "confirmar": True})
    if name == "normalizar_capas":
        _call(world, name, {"mapa": {"Layer7": "muros"}, "confirmar": True})
    else:
        _call(world, name, {**ARGS[name], "confirmar": True})
    assert _state(world).pasos_deshacer == 2


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_insunits_0_preview_y_escritura_segun_L3(name: str) -> None:
    """L3 desde 0.8.3: solo corregir_unidades_bloques rechaza la escritura; el resto escribe con la advertencia §2.1."""
    world = _world(insunits=0, **ESTADO[name])
    antes = _foto(world)
    r = _call(world, name, ARGS[name])
    assert r["preview"] is True and r["insunits"] == 0 and r["unidades"] == "desconocida"
    assert r["advertencias"][0] == ADVERTENCIA_INSUNITS_0
    assert _foto(world) == antes
    if name == "corregir_unidades_bloques":
        err = _plugin_error(world, name, {**ARGS[name], "confirmar": True})
        assert (err.code, err.rpc_code, str(err)) == ("UNITS_UNKNOWN", -32004, UNITS_UNKNOWN)
        assert _foto(world) == antes
    else:
        w = _call(world, name, {**ARGS[name], "confirmar": True})
        assert w["escrito"] is True and w["insunits"] == 0
        assert w["advertencias"][0] == ADVERTENCIA_INSUNITS_0


@pytest.mark.parametrize("name", HERRAMIENTAS_FASEL)
def test_propiedad_desconocida_rechazada(name: str) -> None:
    err = _plugin_error(_world(**ESTADO[name]), name, {**ARGS[name], "aceptar_sin_unidades": True})
    assert err.data["detalle"] == "aceptar_sin_unidades: propiedad no permitida"


def test_por_el_cliente_mcp_preview_y_escritura() -> None:
    world = _world(faseL=True)

    async def body(c: Client) -> None:
        p = await _ok(c, "borrar_fuera_de", {"contorno": "1C0"})
        assert p["preview"] is True and p["n_borrar"] == 3
        texto = await _err(c, "borrar_fuera_de", {"contorno": "1C0", "preview": False})
        assert texto == INVALIDO + DETALLE_PREVIEW_SIN_CONFIRMAR
        w = await _ok(c, "borrar_fuera_de", {"contorno": "1C0", "confirmar": True})
        assert w["escrito"] is True and w["n_borradas"] == 3

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 1


def test_por_el_cliente_mcp_units_unknown() -> None:
    world = _world(faseL=True, insunits=0)

    async def body(c: Client) -> None:
        # L3 desde 0.8.3: solo corregir_unidades_bloques rechaza; purgar/borrar_fuera_de/normalizar_capas escriben y advierten.
        assert await _err(c, "corregir_unidades_bloques", {"confirmar": True}) == UNITS_UNKNOWN
        assert (await _ok(c, "corregir_unidades_bloques", {}))["preview"] is True
        w = await _ok(c, "purgar", {"confirmar": True})
        assert w["escrito"] is True and w["insunits"] == 0 and w["advertencias"]

    run(world.server(), body)


# ---------------------------------------------------------------------------
# borrar_fuera_de (§5F.1)
# ---------------------------------------------------------------------------


def test_borrar_fuera_de_conteos_dentro_fuera_cruza_sin_caja() -> None:
    r = _call(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0"})
    assert r["contorno"] == {
        "handle": "1C0", "tipo": "LWPOLYLINE", "capa": "MCP-DIBUJO", "n_vertices": 4, "area": 100.0,
        "bbox": [[10.0, 10.0], [20.0, 20.0]],
    }
    assert (r["espacio"], r["criterio"]) == ("modelo", "caja_cruza")
    assert (r["n_total"], r["n_borrar"], r["n_conservar"], r["n_cruzan"], r["n_sin_caja"], r["n_excluidas"]) == (7, 3, 3, 1, 1, 0)
    assert r["n_total"] == r["n_borrar"] + r["n_conservar"] + r["n_sin_caja"]
    assert r["por_tipo"] == {"CIRCLE": 1, "INSERT": 1, "LINE": 1}
    assert r["por_capa"] == {"MUEBLE": 2, "Layer7": 1}
    assert r["muestra"] == ["1C3", "1C4", "1C5"]
    assert r["excluidas_por_capa"] == {}


def test_borrar_fuera_de_escribe_y_conserva_contorno_dentro_cruza_y_sin_caja() -> None:
    world = _world(faseL=True)
    r = _call(world, "borrar_fuera_de", {"contorno": "1C0", "confirmar": True})
    assert r["n_borradas"] == r["n_borrar"] == 3
    assert _handles(world) == ["1C0", "1C1", "1C2", "1C6", "1C7"]
    # El VIEWPORT de Layout1 sigue (el contorno vive en el modelo).
    assert [e["handle"] for e in _state(world).layout_entities["Layout1"]] == ["1C8"]


def test_borrar_fuera_de_criterio_caja_dentro_borra_la_que_cruza() -> None:
    world = _world(faseL=True)
    r = _call(world, "borrar_fuera_de", {"contorno": "1C0", "criterio": "caja_dentro", "confirmar": True})
    assert (r["criterio"], r["n_borrar"], r["n_conservar"], r["n_cruzan"]) == ("caja_dentro", 4, 2, 1)
    assert "1C6" not in _handles(world) and "1C0" in _handles(world)


def test_borrar_fuera_de_criterio_invalido() -> None:
    err = _plugin_error(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0", "criterio": "caja"})
    assert err.data["detalle"] == "criterio: debe ser caja_cruza o caja_dentro"


def test_borrar_fuera_de_excluir_capas_sin_distinguir_mayusculas() -> None:
    world = _world(faseL=True)
    r = _call(world, "borrar_fuera_de", {"contorno": "1C0", "excluir_capas": ["mueble"], "confirmar": True})
    assert (r["n_borrar"], r["n_conservar"], r["n_excluidas"]) == (1, 5, 2)
    assert r["excluidas_por_capa"] == {"MUEBLE": 2}
    assert r["n_total"] == r["n_borrar"] + r["n_conservar"] + r["n_sin_caja"]
    assert _handles(world) == ["1C0", "1C1", "1C2", "1C3", "1C4", "1C6", "1C7"]


def test_borrar_fuera_de_excluir_capas_inexistentes() -> None:
    err = _plugin_error(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0", "excluir_capas": ["NOPE", "MUEBLE", "X2"]})
    assert err.code == "NOT_FOUND"
    assert err.plugin_message == "excluir_capas: capas inexistentes: NOPE, X2"
    assert err.data["capas"] == ["NOPE", "X2"]


def test_borrar_fuera_de_viewport_parcial_con_advertencia_literal() -> None:
    r = _call(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0"})
    assert r["viewports"] == [
        {"layout": "Layout1", "handle": "1C8", "ventana": [[0.0, 0.0], [30.0, 30.0]], "estado": "parcial", "n_borradas": 2, "n_conservadas": 3}
    ]
    assert r["advertencias"] == [AVISO_VIEWPORT_1C8, "1 entidad(es) sin caja calculable se conservaron (REGION 1)."]


def test_borrar_fuera_de_viewport_vacia_y_sin_cambio() -> None:
    world = _world(faseL=True)
    _state(world).viewports_layout = [
        {"layout": "Layout1", "handle": "1C8", "ventana": [[24.0, 24.0], [29.0, 26.0]]},
        {"layout": "Layout2", "handle": "2D0", "ventana": [[100.0, 100.0], [110.0, 110.0]]},
    ]
    r = _call(world, "borrar_fuera_de", {"contorno": "1C0"})
    assert [(v["estado"], v["n_borradas"], v["n_conservadas"]) for v in r["viewports"]] == [("vacia", 1, 0), ("sin_cambio", 0, 0)]
    assert (
        "El viewport 1C8 del layout 'Layout1' mira la ventana de modelo [[24, 24], [29, 26]] que quedará vacía: se "
        "borran 1 entidades que la intersecan."
    ) in r["advertencias"]
    assert not any("2D0" in a for a in r["advertencias"])


def test_advertencia_viewport_coordenadas_dos_decimales() -> None:
    aviso = advertencia_viewport("Layout2", "1789D", "parcial", [[2934.33, 3436.5], [3784.36, 4081.46]], 4044, 4701)
    assert aviso == (
        "El viewport 1789D del layout 'Layout2' mira la ventana de modelo [[2934.33, 3436.5], [3784.36, 4081.46]] que "
        "quedará parcialmente borrada: se borran 4044 entidades que la intersecan y se conservan 4701."
    )
    assert (coord_l(3180.30), coord_l(3180.304), coord_l(-0.001), coord_l(12.0)) == ("3180.3", "3180.3", "0", "12")
    assert advertencia_viewport("Layout1", "12D44", "sin_cambio", [[0, 0], [1, 1]], 0, 0) is None


def test_borrar_fuera_de_contorno_inexistente() -> None:
    err = _plugin_error(_world(faseL=True), "borrar_fuera_de", {"contorno": "FFFF"})
    assert (err.code, err.data["handle"], err.plugin_message) == ("NOT_FOUND", "FFFF", "No existe ninguna entidad con handle 'FFFF'.")


@pytest.mark.parametrize(
    ("handle", "detalle"),
    [
        ("1C1", "contorno: la entidad 1C1 no es una polilínea cerrada ni un círculo (LINE)"),
        ("1C9", "contorno: la entidad 1C9 está dentro de una definición de bloque"),
    ],
)
def test_borrar_fuera_de_contorno_de_otro_tipo_o_en_bloque(handle: str, detalle: str) -> None:
    err = _plugin_error(_world(faseL=True), "borrar_fuera_de", {"contorno": handle})
    assert (err.code, err.data["detalle"]) == ("INVALID_PARAMS", detalle)


def test_borrar_fuera_de_polilinea_abierta() -> None:
    world = _world(faseL=True)
    _state(world).entities[0]["cerrada"] = False
    err = _plugin_error(world, "borrar_fuera_de", {"contorno": "1C0"})
    assert err.data["detalle"] == "contorno: la polilínea 1C0 no está cerrada"


def test_borrar_fuera_de_contorno_autointersecado_y_area_0() -> None:
    world = _world(faseL=True)
    contorno = _state(world).entities[0]
    contorno["vertices"] = [[10, 10, 0], [20, 20, 0], [20, 10, 0], [10, 20, 0]]
    err = _plugin_error(world, "borrar_fuera_de", {"contorno": "1C0"})
    assert (err.code, err.rpc_code, err.data["detalle"]) == (
        "INVALID_GEOMETRY", -32005, "contorno: la polilínea se autointerseca (tramos 0 y 2)",
    )
    contorno["vertices"] = [[10, 10, 0], [20, 10, 0], [10, 10, 0]]
    err = _plugin_error(world, "borrar_fuera_de", {"contorno": "1C0"})
    assert err.data["detalle"] == "contorno: se necesitan al menos 3 vértices"


def test_borrar_fuera_de_contorno_circulo() -> None:
    world = _world(faseL=True)
    r = _call(world, "borrar_fuera_de", {"contorno": "1C5"})
    assert (r["contorno"]["tipo"], r["contorno"]["n_vertices"]) == ("CIRCLE", 64)
    # Dentro del círculo r=1 en (40, 40) no hay nada: todo lo que tiene caja se borra salvo el propio círculo.
    assert (r["n_borrar"], r["n_conservar"], r["n_sin_caja"]) == (6, 0, 1)
    assert "1C5" not in r["muestra"] and "1C0" in r["muestra"]


def test_borrar_fuera_de_espacio_distinto_del_contorno() -> None:
    err = _plugin_error(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0", "espacio": "Layout1"})
    assert err.data["detalle"] == "contorno: la entidad 1C0 no está en el espacio 'Layout1'"
    err = _plugin_error(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0", "espacio": "Layout9"})
    assert (err.code, err.data["espacio"]) == ("NOT_FOUND", "Layout9")
    assert _call(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0", "espacio": "modelo"})["espacio"] == "modelo"


def test_borrar_fuera_de_capa_bloqueada_bloquea_toda_la_llamada() -> None:
    world = _world(faseL=True, capas_bloqueadas={"mueble"})
    antes = _foto(world)
    err = _plugin_error(world, "borrar_fuera_de", {"contorno": "1C0", "confirmar": True})
    assert err.data["detalle"] == (
        "capas bloqueadas o de xref con entidades a borrar: MUEBLE (2); desbloquéelas o páselas en excluir_capas"
    )
    assert err.data["capas"] == ["MUEBLE"]
    assert _foto(world) == antes
    # Pasándola en excluir_capas, la llamada sí se puede hacer.
    assert _call(world, "borrar_fuera_de", {"contorno": "1C0", "excluir_capas": ["MUEBLE"], "confirmar": True})["n_borradas"] == 1


def test_borrar_fuera_de_capa_congelada_se_borra_con_advertencia() -> None:
    world = _world(faseL=True, capas_congeladas={"Layer7"})
    r = _call(world, "borrar_fuera_de", {"contorno": "1C0", "confirmar": True})
    assert r["n_borradas"] == 3
    assert "1 entidad(es) en capas congeladas o apagadas se borraron." in r["advertencias"]


def test_borrar_fuera_de_muestra_acotada() -> None:
    r = _call(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0", "muestra": 2})
    assert r["muestra"] == ["1C3", "1C4"]
    err = _plugin_error(_world(faseL=True), "borrar_fuera_de", {"contorno": "1C0", "muestra": 51})
    assert err.data["detalle"] == "muestra: debe ser un entero entre 1 y 50"


@pytest.mark.parametrize(
    ("bbox", "esperado"),
    [
        ([[12, 12], [18, 18]], "dentro"),
        ([[10, 10], [20, 20]], "dentro"),  # el borde cuenta como dentro
        ([[15, 15], [15, 15]], "dentro"),  # caja de tamaño 0
        ([[25, 25], [28, 26]], "fuera"),
        ([[20.5, 0], [30, 30]], "fuera"),
        ([[15, 15], [25, 16]], "cruza"),
        ([[20, 12], [25, 13]], "cruza"),  # toca el borde desde fuera
        ([[0, 0], [30, 30]], "cruza"),  # contiene al contorno
    ],
)
def test_clasificar_caja_criterio_de_5f1(bbox: list[list[float]], esperado: str) -> None:
    poligono = [(10.0, 10.0), (20.0, 10.0), (20.0, 20.0), (10.0, 20.0)]
    assert clasificar_caja(poligono, bbox) == esperado


def test_clasificar_caja_contorno_no_convexo() -> None:
    # "U": la caja en el hueco de la U tiene las 4 esquinas fuera pero no es "dentro"; la que cubre el hueco cruza.
    u = [(0.0, 0.0), (30.0, 0.0), (30.0, 30.0), (20.0, 30.0), (20.0, 10.0), (10.0, 10.0), (10.0, 30.0), (0.0, 30.0)]
    assert clasificar_caja(u, [[12, 15], [18, 25]]) == "fuera"
    assert clasificar_caja(u, [[5, 15], [25, 20]]) == "cruza"
    assert clasificar_caja(u, [[1, 1], [29, 9]]) == "dentro"


# ---------------------------------------------------------------------------
# purgar (§5F.2)
# ---------------------------------------------------------------------------


def _nombres(bloque: dict[str, Any]) -> list[tuple[str, int]]:
    return [(f["nombre"], f["ronda"]) for f in bloque["nombres"]]


def test_purgar_punto_fijo_por_categoria_y_ronda() -> None:
    r = _call(_world(faseL=True), "purgar", {})
    assert r["categorias"] == ["bloques", "capas", "tipos_linea", "estilos_texto", "estilos_cota", "regapps"]
    assert r["rondas"] == 3
    p = r["purgables"]
    assert _nombres(p["bloques"]) == [("DP", 1), ("BASURA_jgytfyuh", 1), ("*U1", 1), ("BASURA_ANIDADO", 2)]
    assert (p["bloques"]["n"], p["bloques"]["n_con_nombre"], p["bloques"]["n_anonimos"], p["bloques"]["n_entidades"]) == (4, 3, 1, 12)
    assert p["bloques"]["nombres"][0] == {"nombre": "DP", "n_entidades": 6, "ronda": 1, "unidades": 4}
    # CAPABLOQUE solo la usan BASURA_jgytfyuh (ronda 1) y BASURA_ANIDADO (ronda 2) → ronda 3.
    assert _nombres(p["capas"]) == [
        ("A-WALL-FULL", 1), ("CAPAVACIA", 1), ("Layer9", 1), ("PUERTAS", 1), ("Rotulo", 1), ("CAPABLOQUE", 3),
    ]
    # DASHED solo la usa CAPAVACIA → ronda 2.
    assert _nombres(p["tipos_linea"]) == [("CENTER", 1), ("DASHED", 2)]
    assert _nombres(p["estilos_texto"]) == [("ROMANS", 1)]
    assert _nombres(p["estilos_cota"]) == [("COTA_VIEJA", 1)]
    assert _nombres(p["regapps"]) == [("ACAD_PSEXT", 1)]
    assert all(v["nombres_truncados"] is False for v in p.values())


def test_purgar_protegidos_por_las_exclusiones_fijas() -> None:
    r = _call(_world(faseL=True, capa_actual="Rotulo"), "purgar", {})
    assert r["protegidos"] == {
        "bloques": ["TOPO"],
        "capas": ["DEFPOINTS", "Rotulo (actual)"],
        "tipos_linea": ["ByBlock", "ByLayer (actual)"],
        "estilos_texto": ["Standard (actual)"],
        "estilos_cota": ["ISO-25 (actual)", "Standard"],
        "regapps": ["ACAD"],
    }
    capas = [n for n, _r in _nombres(r["purgables"]["capas"])]
    assert "Rotulo" not in capas and "MCP-DIBUJO" not in capas and "0" not in capas and "DEFPOINTS" not in capas


def test_purgar_capa_mcp_sin_uso_nunca_se_purga() -> None:
    world = _world(faseL=True)
    _state(world).entities = [e for e in _state(world).entities if e["capa"] != "MCP-DIBUJO"]
    r = _call(world, "purgar", {"categorias": ["capas"]})
    assert "MCP-DIBUJO" in r["protegidos"]["capas"]
    assert "MCP-DIBUJO" not in [n for n, _r in _nombres(r["purgables"]["capas"])]


def test_auditar_capas_del_dibujo_faseL_con_el_estandar_v2() -> None:
    # Mismas clasificaciones y sugerencias que LayerClassifier con standards/lps.json v2 (2026-10-28): MUEBLE ya no es
    # estándar (lo era en la v1) y sugiere "muebles fijos" por la palabra clave MUEBLE; A-WALL-FULL no tiene palabra
    # clave en español → sugerencia null.
    r = _call(_world(faseL=True), "auditar_capas", {"limit": 1000})
    filas = {c["nombre"]: (c["clasificacion"], c["sugerencia"]) for c in r["capas"]}
    assert filas == {
        "0": ("estandar", None),
        "A-WALL-FULL": ("ajena_revit_aia", None),
        "CAPABLOQUE": ("fuera_de_estandar", None),
        "CAPAVACIA": ("fuera_de_estandar", None),
        "DEFPOINTS": ("estandar", None),
        "Layer7": ("generica", None),
        "Layer9": ("generica", None),
        "MCP-DIBUJO": ("mcp", None),
        "MUEBLE": ("fuera_de_estandar", "muebles fijos"),
        "MUROS": ("estandar", None),
        "PUERTAS": ("estandar", None),
        "Rotulo": ("estandar", None),
        "TEXTOS": ("estandar", None),
    }
    assert r["resumen"]["por_clasificacion"]["fuera_de_estandar"] == 3
    # Las capas estándar del simulador están en capas_permitidas v2 con sus mismas propiedades.
    from fake_plugin import LPS

    permitidas = {k.casefold(): v for k, v in LPS["capas_permitidas"].items()}
    for c in r["capas"]:
        if c["clasificacion"] == "estandar":
            spec = permitidas[c["nombre"].casefold()]
            assert (c["color"], c["grosor"]) == (spec["color"], spec["grosor"]), c["nombre"]
        elif c["clasificacion"] != "mcp":
            assert c["nombre"].casefold() not in permitidas, c["nombre"]


def test_purgar_escritura_borra_los_simbolos_en_un_paso() -> None:
    world = _world(faseL=True)
    r = _call(world, "purgar", {"confirmar": True})
    assert r["purgados"] == r["purgables"]
    st = _state(world)
    assert sorted(d["nombre"] for d in st.block_defs) == ["TOPO", "erere"]
    assert {"CAPAVACIA", "CAPABLOQUE", "Layer9", "PUERTAS"}.isdisjoint(_capas(world))
    assert {"MUROS", "MUEBLE", "DEFPOINTS", "MCP-DIBUJO", "0"} <= _capas(world)
    assert st.tipos_linea == ["ByBlock", "ByLayer", "Continuous"]
    assert (st.estilos_texto, st.estilos_cota, st.regapps) == (["Standard", "Arial"], ["ISO-25", "Standard"], ["ACAD", "AcadAnnotative"])
    assert st.pasos_deshacer == 1
    # Punto fijo: una segunda purga no encuentra nada.
    r2 = _call(world, "purgar", {})
    assert all(v["n"] == 0 for v in r2["purgables"].values()) and r2["rondas"] == 0


def test_purgar_categorias_parciales() -> None:
    world = _world(faseL=True)
    r = _call(world, "purgar", {"categorias": ["capas", "bloques"]})
    assert r["categorias"] == ["bloques", "capas"]
    assert set(r["purgables"]) == {"bloques", "capas"} == set(r["protegidos"])
    # Sin purgar tipos de línea, DASHED no aparece; sin bloques, CAPABLOQUE sigue usada por BASURA_jgytfyuh.
    r = _call(world, "purgar", {"categorias": ["capas"]})
    assert ("CAPABLOQUE", 3) not in _nombres(r["purgables"]["capas"])
    assert _nombres(r["purgables"]["capas"])[-1] == ("Rotulo", 1)
    r = _call(world, "purgar", {"categorias": ["tipos_linea"]})
    assert _nombres(r["purgables"]["tipos_linea"]) == [("CENTER", 1)]


def test_purgar_todo_equivale_a_las_seis() -> None:
    world = _world(faseL=True)
    assert _call(world, "purgar", {"categorias": ["todo"]}) == _call(world, "purgar", {}) == _call(
        world, "purgar", {"categorias": ["regapps", "bloques", "capas", "tipos_linea", "estilos_texto", "estilos_cota"]}
    )


def test_purgar_categoria_invalida_literal() -> None:
    err = _plugin_error(_world(faseL=True), "purgar", {"categorias": ["capas", "grupos"]})
    assert err.data["detalle"] == (
        "categorias[1]: debe ser uno de bloques, capas, tipos_linea, estilos_texto, estilos_cota, regapps, todo"
    )


def test_purgar_excluir_y_advertencia_de_inexistente() -> None:
    world = _world(faseL=True)
    r = _call(world, "purgar", {"excluir": ["basura_JGYTFYUH", "NoExiste"], "confirmar": True})
    assert r["excluidos"] == ["BASURA_jgytfyuh"]
    assert advertencia_excluir_inexistente("NoExiste") in r["advertencias"]
    assert advertencia_excluir_inexistente("NoExiste") == "excluir: no existe ningún símbolo llamado 'NoExiste'."
    # BASURA_jgytfyuh queda y por tanto BASURA_ANIDADO y CAPABLOQUE siguen referenciados.
    nombres = [n for n, _r in _nombres(r["purgados"]["bloques"])]
    assert "BASURA_jgytfyuh" not in nombres and "BASURA_ANIDADO" not in nombres
    assert {"BASURA_jgytfyuh", "BASURA_ANIDADO"} <= {d["nombre"] for d in _state(world).block_defs}


def test_purgar_muestra_trunca_nombres_pero_no_n() -> None:
    r = _call(_world(faseL=True), "purgar", {"muestra": 2})
    assert r["purgables"]["capas"]["n"] == 6 and len(r["purgables"]["capas"]["nombres"]) == 2
    assert r["purgables"]["capas"]["nombres_truncados"] is True


def test_purgar_bloque_dinamico_la_escritura_difiere_y_avisa() -> None:
    world = _world(faseL=True, purga_dinamica=True)
    previa = _call(world, "purgar", {"categorias": ["bloques"]})
    assert ("PUERTA_DIN", 1) in _nombres(previa["purgables"]["bloques"])
    r = _call(world, "purgar", {"categorias": ["bloques"], "confirmar": True})
    assert ("PUERTA_DIN", 1) not in _nombres(r["purgados"]["bloques"])
    assert r["purgados"]["bloques"]["n"] == previa["purgables"]["bloques"]["n"] - 1
    assert advertencia_purga_difiere(1, 0) in r["advertencias"]
    assert advertencia_purga_difiere(1, 0) == (
        "La purga real difiere de la previsualización: 1 símbolo(s) previstos no se purgaron (referenciados por "
        "xdata, diccionarios o bloques dinámicos) y 0 no previstos sí."
    )
    assert "PUERTA_DIN" in {d["nombre"] for d in _state(world).block_defs}


def test_purgar_y_bloques_despues() -> None:
    world = _world(faseL=True)
    _call(world, "purgar", {"confirmar": True})
    r = _call(world, "bloques", {"incluir_anonimos": True})
    assert [b["nombre"] for b in r["bloques"]] == ["erere", "TOPO"]


# ---------------------------------------------------------------------------
# normalizar_capas (§5F.3)
# ---------------------------------------------------------------------------


def test_normalizar_concepto_capa_creada_y_por_origen() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"MUEBLE": "mobiliario_fijo", "Layer7": "muros"}})
    assert r["mapa"] == [
        {"origen": "MUEBLE", "destino": "muebles fijos", "destino_concepto": "mobiliario_fijo", "destino_existe": False, "destino_creada": False, "n": 3},
        {"origen": "Layer7", "destino": "MUROS", "destino_concepto": "muros", "destino_existe": True, "destino_creada": False, "n": 1},
    ]
    assert r["n_reasignar"] == 4
    sin_excluidas = {"n": 0, "por_espacio": {}, "viewports": {}, "viewports_papel": {}}
    # 0.6.1: el VIEWPORT 1C8 de Layout1 (capa Layer7) ya no se reasigna por defecto: va en excluidas.viewports.
    assert r["por_origen"] == {
        "MUEBLE": {"por_tipo": {"ATTRIB": 1, "INSERT": 1, "LINE": 1}, "por_espacio": {"modelo": 3}, "excluidas": sin_excluidas},
        "Layer7": {
            "por_tipo": {"CIRCLE": 1},
            "por_espacio": {"modelo": 1},
            "excluidas": {"n": 1, "por_espacio": {}, "viewports": {"Layout1": 1}, "viewports_papel": {}},
        },
    }
    assert (r["n_excluidas"], r["espacios"], r["incluir_viewports"]) == (1, None, False)
    assert (r["capas_creadas"], r["capas_borrar"], r["capas_borradas"]) == ([], [], None)


def test_normalizar_escritura_reasigna_crea_destino_y_conserva_atributo_en_0() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"MUEBLE": "mobiliario_fijo"}, "confirmar": True})
    assert (r["n_reasignadas"], r["capas_creadas"]) == (3, ["muebles fijos"])
    assert r["mapa"][0]["destino_creada"] is True
    st = _state(world)
    por_handle = {e["handle"]: e for e in st.entities}
    assert por_handle["1C3"]["capa"] == por_handle["1C4"]["capa"] == "muebles fijos"
    atributos = {a["handle"]: a["capa"] for a in por_handle["1C4"]["_atributos"]}
    assert atributos == {"1CA": "muebles fijos", "1CB": "0"}
    assert "muebles fijos" in _capas(world)
    assert st.pasos_deshacer == 1


def test_normalizar_propiedades_forzadas_no_se_cambian() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"MUEBLE": "MUROS"}, "confirmar": True})
    assert r["n_propiedades_forzadas"] == 1
    assert advertencia_propiedades_forzadas(1) in r["advertencias"]
    assert advertencia_propiedades_forzadas(639) == (
        "639 entidad(es) reasignada(s) conservan color, tipo de línea o grosor propios (no ByLayer); no se cambiaron."
    )
    assert next(e for e in _state(world).entities if e["handle"] == "1C3")["_color"] == 1


def test_normalizar_incluir_bloques_y_anonimos_dinamicos() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"MUEBLE": "MUROS"}, "incluir_bloques": True, "confirmar": True})
    assert r["mapa"][0]["n"] == 4
    assert r["por_origen"]["MUEBLE"]["por_espacio_bloques"] == {"bloque:erere": 1}
    assert r["por_origen"]["MUEBLE"]["por_tipo"] == {"ATTRIB": 1, "INSERT": 1, "LINE": 1, "LWPOLYLINE": 1}
    assert r["n_propiedades_forzadas"] == 2
    assert advertencia_anonimos_dinamicos(1) in r["advertencias"]
    defs = {d["nombre"]: d for d in _state(world).block_defs}
    assert defs["erere"]["_contenido"][0]["capa"] == "MUROS"
    assert defs["*U1"]["_contenido"][0]["capa"] == "MUEBLE"  # los *U… no se tocan


def test_normalizar_borrar_capas_origen_vacias() -> None:
    world = _world(faseL=True)
    args = {
        "mapa": {"MUEBLE": "MUROS", "Layer7": "TEXTOS"},
        "incluir_bloques": True,
        "incluir_viewports": True,
        "borrar_capas_origen_vacias": True,
    }
    r = _call(world, "normalizar_capas", args)
    # MUEBLE sigue usada dentro del anónimo *U1: no queda vacía.
    assert (r["capas_borrar"], r["capas_borradas"]) == (["Layer7"], None)
    r = _call(world, "normalizar_capas", {**args, "confirmar": True})
    assert r["capas_borradas"] == ["Layer7"]
    assert "Layer7" not in _capas(world) and "MUEBLE" in _capas(world)


def test_normalizar_origen_0_sin_incluir_bloques_y_no_se_borra() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"0": "TEXTOS"}, "borrar_capas_origen_vacias": True})
    assert r["mapa"][0]["n"] == 1  # el ATTRIB 1CB
    assert r["capas_borrar"] == []
    assert advertencia_origen_no_borrable("0") in r["advertencias"]
    assert advertencia_origen_no_borrable("0") == (
        "borrar_capas_origen_vacias: la capa '0' se conserva aunque quede vacía (capa 0, DEFPOINTS o capa actual)."
    )


def test_normalizar_origen_con_0_entidades_no_es_error() -> None:
    r = _call(_world(faseL=True), "normalizar_capas", {"mapa": {"CAPAVACIA": "MUROS"}})
    assert r["mapa"][0]["n"] == 0 and r["n_reasignar"] == 0


def test_normalizar_error_1_origenes_inexistentes_todos() -> None:
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"NOPE": "MUROS", "MUEBLE": "MUROS", "X2": "MUROS"}})
    assert (err.code, err.plugin_message, err.data["capas"]) == (
        "NOT_FOUND", "mapa: capas de origen inexistentes: NOPE, X2", ["NOPE", "X2"],
    )


@pytest.mark.parametrize(
    ("mapa", "incluir", "detalle"),
    [
        ({"DEFPOINTS": "MUROS"}, False, "mapa: la capa DEFPOINTS no se normaliza"),
        (
            {"0": "MUROS"},
            True,
            "mapa: la capa 0 no puede ser origen con incluir_bloques: true (la capa 0 dentro de los bloques es correcta)",
        ),
        ({"MUEBLE": "MUROS", "mueble": "TEXTOS"}, False, "mapa: origen repetido 'mueble'"),
        ({"MUROS": "muros"}, False, "mapa['MUROS']: origen y destino son la misma capa"),
        (
            {"MUEBLE": "MUROS", "MUROS": "TEXTOS"},
            False,
            "mapa: el destino 'MUROS' es también origen (cadena o ciclo); aplane el mapa para que cada origen vaya "
            "directamente a su capa final",
        ),
        (
            {"MUEBLE": "Layer7", "Layer7": "MUEBLE"},
            False,
            "mapa: el destino 'Layer7' es también origen (cadena o ciclo); aplane el mapa para que cada origen vaya "
            "directamente a su capa final",
        ),
        ({"MUEBLE": "MAL*NOMBRE"}, False, "capa: nombre de capa no válido"),
    ],
)
def test_normalizar_errores_invalid_params_literales(mapa: dict[str, str], incluir: bool, detalle: str) -> None:
    world = _world(faseL=True)
    antes = _foto(world)
    err = _plugin_error(world, "normalizar_capas", {"mapa": mapa, "incluir_bloques": incluir, "confirmar": True})
    assert (err.code, err.data["detalle"]) == ("INVALID_PARAMS", detalle)
    assert _foto(world) == antes


def test_normalizar_error_3_destino_no_creable() -> None:
    # Como el plugin: mensaje de capa inexistente y el texto de LayerMapValidator.MissingDestination en data.detalle
    # (propio de normalizar_capas: aquí MCP-* no es destino; el texto de la Fase 3 no cambia).
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"MUEBLE": "INVENTADA"}})
    assert err.code == "NOT_FOUND"
    assert err.plugin_message == "La capa 'INVENTADA' no existe en el dibujo."
    assert err.data["detalle"] == (
        "mapa['MUEBLE']: la capa destino 'INVENTADA' no existe y no está en lps.json (capas_permitidas)"
    )
    assert err.data["capa"] == "INVENTADA"


@pytest.mark.parametrize("destino", ["A<B", "A|B", " textos", "textos ", "MCP-A<B", "A`B"])
def test_normalizar_error_3_destino_no_valido_antes_que_mcp_y_not_found(destino: str) -> None:
    """Corrección de auditoría (ronda 3): paridad con el plugin (antes daba NOT_FOUND con 'A<B')."""
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"MUEBLE": destino}})
    assert (err.code, err.data["detalle"], err.data["capa"]) == ("INVALID_PARAMS", "capa: nombre de capa no válido", destino)


def test_normalizar_error_3_destino_no_valido_orden_en_el_mapa() -> None:
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"MUEBLE": "A<B", "Layer7": "MCP-X"}})
    assert (err.code, err.data["capa"]) == ("INVALID_PARAMS", "A<B")
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"MUEBLE": "MCP-X", "Layer7": "A<B"}})
    assert (err.code, err.data["capa"]) == ("INVALID_PARAMS", "MCP-X")


def test_normalizar_error_1_origen_vacio_invalid_params() -> None:
    world = _world(faseL=True)
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"NOPE": "MUROS", "   ": "MUROS"}})
    assert (err.code, err.data["detalle"]) == ("INVALID_PARAMS", "mapa: la capa origen no puede estar vacía")
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"": "MUROS"}})
    assert (err.code, err.data["detalle"]) == (
        "INVALID_PARAMS", "mapa: cada capa origen debe tener entre 1 y 255 caracteres",
    )


def _world_con_mcp_preview(**kw: Any) -> World:
    world = _world(faseL=True, **kw)
    _state(world).capas_extra.append(_layer("MCP-PREVIEW", 4, -3, "mcp"))
    return world


@pytest.mark.parametrize(
    ("destino", "capa"),
    [
        ("MCP-LIMPIEZA", "MCP-LIMPIEZA"),  # inexistente: no se crea
        ("mcp-preview", "MCP-PREVIEW"),  # existente: nombre real del dibujo
        ("dibujo_mcp", "MCP-DIBUJO"),  # por concepto de capas_preferidas
    ],
)
def test_normalizar_error_3_destino_mcp_invalid_params(destino: str, capa: str) -> None:
    """Corrección de auditoría: mover entidades del usuario a MCP-* les quitaría la protección de §5D.8."""
    world = _world_con_mcp_preview()
    antes = _foto(world)
    for confirmar in (False, True):
        err = _plugin_error(world, "normalizar_capas", {"mapa": {"MUEBLE": destino}, "confirmar": confirmar})
        assert (err.code, err.data["detalle"], err.data["capa"]) == (
            "INVALID_PARAMS",
            f"mapa['MUEBLE']: la capa destino '{capa}' es una capa del MCP (MCP-*); las entidades del usuario no se "
            "mueven a capas del MCP",
            capa,
        )
    assert _foto(world) == antes and _state(world).pasos_deshacer == antes["pasos"]


def test_normalizar_error_destino_mcp_antes_que_no_creable() -> None:
    # En la comprobación 3 se recorre el mapa en orden: la primera entrada con destino del MCP responde aunque la
    # siguiente tenga un destino no creable; y el destino del MCP gana a origen = destino (comprobación 4).
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"MUEBLE": "MCP-X", "Layer7": "INVENTADA"}})
    assert (err.code, err.data["capa"]) == ("INVALID_PARAMS", "MCP-X")
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"Layer7": "INVENTADA", "MUEBLE": "MCP-X"}})
    assert (err.code, err.data["capa"]) == ("NOT_FOUND", "INVENTADA")


@pytest.mark.parametrize(
    ("origen", "destino", "real"),
    [
        ("MCP-PREVIEW", "TEXTOS", "MCP-PREVIEW"),
        ("mcp-preview", "textos", "MCP-PREVIEW"),
        ("MCP-DIBUJO", "MUROS", "MCP-DIBUJO"),
    ],
)
def test_normalizar_error_2_origen_mcp_invalid_params(origen: str, destino: str, real: str) -> None:
    """Corrección de auditoría: un origen MCP-* convertiría previsualizaciones u observaciones en entidades del usuario."""
    world = _world_con_mcp_preview()
    antes = _foto(world)
    err = _plugin_error(world, "normalizar_capas", {"mapa": {origen: destino}, "confirmar": True})
    assert (err.code, err.data["detalle"], err.data["capa"]) == (
        "INVALID_PARAMS",
        f"mapa['{origen}']: la capa origen '{real}' es una capa del MCP (MCP-*); sus entidades no se mueven a capas "
        "del usuario",
        real,
    )
    assert _foto(world) == antes


def test_normalizar_orden_comprobacion_2_defpoints_origen_mcp_cero_repetidos() -> None:
    world = _world_con_mcp_preview()
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"MCP-PREVIEW": "MUROS", "DEFPOINTS": "MUROS"}})
    assert err.data["detalle"] == "mapa: la capa DEFPOINTS no se normaliza"
    err = _plugin_error(
        world, "normalizar_capas", {"mapa": {"0": "MUROS", "MCP-PREVIEW": "MUROS"}, "incluir_bloques": True}
    )
    assert err.data["capa"] == "MCP-PREVIEW"
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"MUEBLE": "MUROS", "mueble": "MUROS", "MCP-PREVIEW": "MUROS"}})
    assert err.data["capa"] == "MCP-PREVIEW"


@pytest.mark.parametrize(
    ("estado", "capa", "detalle"),
    [
        ({"capas_bloqueadas": {"MUEBLE"}}, "MUEBLE", "capa bloqueada"),
        ({"capas_congeladas": {"muros"}}, "MUROS", "capa congelada"),
    ],
)
def test_normalizar_error_6_bloqueada_o_congelada(estado: dict[str, Any], capa: str, detalle: str) -> None:
    err = _plugin_error(_world(faseL=True, **estado), "normalizar_capas", {"mapa": {"MUEBLE": "MUROS"}})
    assert (err.data["detalle"], err.data["capa"]) == (detalle, capa)


def test_normalizar_orden_de_validacion_inexistente_antes_que_cadena() -> None:
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"MUEBLE": "MUROS", "MUROS": "TEXTOS", "NOPE": "X"}})
    assert err.code == "NOT_FOUND" and err.data["capas"] == ["NOPE"]


def test_normalizar_destino_de_capas_permitidas_se_crea_y_capa_apagada_avisa() -> None:
    world = _world(faseL=True, capas_apagadas={"MUEBLE"})
    r = _call(world, "normalizar_capas", {"mapa": {"MUEBLE": "ESCALERA"}, "confirmar": True})
    assert r["capas_creadas"] == ["ESCALERA"] and r["mapa"][0]["destino_existe"] is False
    assert r["mapa"][0]["destino_concepto"] is None
    assert "La capa MUEBLE está apagada: las entidades no serán visibles." in r["advertencias"]


def test_normalizar_orden_comprobacion_6_bloqueada_congelada_xref() -> None:
    # Mismo orden que LayerMapValidator.StateError: bloqueada → congelada → xref (por capa).
    world = _world(faseL=True, capas_bloqueadas={"XR|MUROS"}, capas_congeladas={"XR|MUROS"})
    _state(world).capas_extra.append(_layer("XR|MUROS", 7, -3, "xref"))
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"XR|MUROS": "MUROS"}})
    assert (err.data["detalle"], err.data["capa"]) == ("capa bloqueada", "XR|MUROS")
    _state(world).capas_bloqueadas.clear()
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"XR|MUROS": "MUROS"}})
    assert (err.data["detalle"], err.data["capa"]) == ("capa congelada", "XR|MUROS")
    _state(world).capas_congeladas.clear()
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"XR|MUROS": "MUROS"}})
    assert (err.data["detalle"], err.data["capa"]) == ("capa dependiente de xref", "XR|MUROS")


# ---------------------------------------------------------------------------
# corregir_unidades_bloques (§5F.4)
# ---------------------------------------------------------------------------


def test_corregir_lista_por_defecto_la_de_auditar_bloques() -> None:
    world = _world(bloques_compensacion=True)
    r = _call(world, "corregir_unidades_bloques", {})
    assert r["origen_lista"] == "auditar_bloques"
    assert [b["nombre"] for b in r["bloques"]] == ["C_MM", "DP", "LAV_MM"]
    assert r["n"] == 3 and r["corregidos"] is None
    lav = next(b for b in r["bloques"] if b["nombre"] == "LAV_MM")
    assert (lav["unidades_actuales"], lav["unidades_actuales_nombre"], lav["unidades_propuestas"], lav["n_inserciones"]) == (4, "mm", 6, 2)
    assert lav["tamano_real_m"] == pytest.approx([0.45, 0.51]) and lav["tamano_por_anidada"] is False
    assert next(b for b in r["bloques"] if b["nombre"] == "C_MM")["tamano_por_anidada"] is True
    # Los ids son los mismos que da auditar_bloques.
    audit = _call(world, "auditar_bloques", {"seccion": ["unidades_distintas"]})
    ids = {h["datos"]["bloque"]: h["id"] for h in audit["hallazgos"]}
    assert all(b["hallazgo_id"] == ids[b["nombre"]] for b in r["bloques"])
    # MESA_MM (alta) y VENTANA_MM (compensado) no son candidatos.
    assert {"MESA_MM", "VENTANA_MM", "FLECHA_MM"}.isdisjoint(b["nombre"] for b in r["bloques"])


def test_corregir_escritura_y_auditar_bloques_deja_de_marcarlos() -> None:
    world = _world(bloques_compensacion=True)
    r = _call(world, "corregir_unidades_bloques", {"confirmar": True})
    assert r["corregidos"] == ["C_MM", "DP", "LAV_MM"] and r["n"] == 3
    defs = {d["nombre"]: d for d in _state(world).block_defs}
    assert defs["LAV_MM"]["unidades_bloque"] == defs["DP"]["unidades_bloque"] == defs["C_MM"]["unidades_bloque"] == 6
    assert defs["MESA_MM"]["unidades_bloque"] == 4
    audit = _call(world, "auditar_bloques", {"seccion": ["unidades_distintas"]})
    marcados = {h["datos"]["bloque"] for h in audit["hallazgos"]}
    assert {"LAV_MM", "DP", "C_MM"}.isdisjoint(marcados) and "MESA_MM" in marcados
    assert _call(world, "corregir_unidades_bloques", {})["bloques"] == []


def test_corregir_y_insertar_bloque_ya_sin_factor() -> None:
    world = _world()
    antes = _call(world, "insertar_bloque", {"nombre": "DP", "posicion": [1, 1]})
    assert any("factor de unidades" in a for a in antes["advertencias"])
    _call(world, "corregir_unidades_bloques", {"bloques": ["dp"], "confirmar": True})
    despues = _call(world, "insertar_bloque", {"nombre": "DP", "posicion": [2, 1]})
    assert not any("factor de unidades" in a for a in despues["advertencias"])


def test_corregir_lista_explicita_valida() -> None:
    r = _call(_world(bloques_compensacion=True), "corregir_unidades_bloques", {"bloques": ["lav_mm"]})
    assert (r["origen_lista"], [b["nombre"] for b in r["bloques"]], r["n"]) == ("parametro", ["LAV_MM"], 1)


@pytest.mark.parametrize(
    ("bloque", "motivo"),
    [
        ("BASURA_jgytfyuh", "unidades iguales a las del dibujo"),
        ("SILLA_MM", "sin inserciones"),
        ("VENTANA_MM", "inserciones compensadas"),
        ("MESA_MM", "tamaño real no plausible"),
        ("FLECHA_MM", "tamaño no medible"),
    ],
)
def test_corregir_cada_motivo_de_no_marcado(bloque: str, motivo: str) -> None:
    world = _world(bloques_compensacion=True)
    silla = copy.deepcopy(next(d for d in _state(world).block_defs if d["nombre"] == "MESA_MM"))
    silla.update({"nombre": "SILLA_MM", "n_inserciones": 0})
    _state(world).block_defs.append(silla)
    err = _plugin_error(world, "corregir_unidades_bloques", {"bloques": ["LAV_MM", bloque]})
    assert err.data["detalle"] == (
        f"bloques[1]: '{bloque}' no está marcado por auditar_bloques como metadato de unidades incorrecto ({motivo})"
    )


@pytest.mark.parametrize("bloque", ["TOPO", "*U1"])
def test_corregir_layout_xref_o_anonimo(bloque: str) -> None:
    err = _plugin_error(_world(), "corregir_unidades_bloques", {"bloques": [bloque]})
    assert err.data["detalle"] == f"bloques[0]: '{bloque}' es un layout, una xref o un bloque anónimo"


def test_corregir_definiciones_inexistentes() -> None:
    err = _plugin_error(_world(), "corregir_unidades_bloques", {"bloques": ["NOPE", "DP", "X2"]})
    assert (err.code, err.plugin_message, err.data["bloques"]) == (
        "NOT_FOUND", "bloques: definiciones inexistentes: NOPE, X2", ["NOPE", "X2"],
    )


def test_corregir_insunits_0_preview_vacia_con_advertencia() -> None:
    r = _call(_world(insunits=0, bloques_compensacion=True), "corregir_unidades_bloques", {})
    assert r["bloques"] == [] and r["n"] == 0
    assert r["advertencias"][-1] == ADVERTENCIA_BLOQUES_SIN_UNIDADES == "INSUNITS=0: no se comparan las unidades de los bloques."


# -- Correcciones del gate en vivo (0.6.1): VIEWPORT de layout y parámetro espacios -----------------------------------

VIEWPORT_PAPEL = {"handle": "1D0", "tipo": "VIEWPORT", "capa": "Layer7", "_numero": 1, "bbox": [[0.0, 0.0, 0.0], [420.0, 297.0, 0.0]]}


def _world_con_papel() -> World:
    """Dibujo de Fase L con el viewport n.º 1 de Layout1 (1D0, espacio papel) en la capa Layer7, antes de 1C8."""
    world = _world(faseL=True)
    _state(world).layout_entities["Layout1"].insert(0, copy.deepcopy(VIEWPORT_PAPEL))
    return world


def _capa_en_layout(world: World, handle: str) -> str:
    return next(e["capa"] for e in _state(world).layout_entities["Layout1"] if e["handle"] == handle)


def test_normalizar_viewports_excluidos_por_defecto_con_aviso_literal() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}})
    assert r["mapa"][0]["n"] == 1 and r["n_reasignar"] == 1 and r["n_excluidas"] == 1
    assert r["por_origen"]["Layer7"]["excluidas"]["viewports"] == {"Layout1": 1}
    aviso = advertencia_viewports_excluidos([("Layout1", 1)])
    assert aviso == (
        "1 viewport(s) de layout en capas origen no se reasignaron (Layout1 1); pase incluir_viewports: true para "
        "reasignarlos."
    )
    assert aviso in r["advertencias"]
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "confirmar": True})
    assert r["n_reasignadas"] == 1
    assert _capa_en_layout(world, "1C8") == "Layer7"


def test_normalizar_incluir_viewports_reasigna_los_viewports_sin_aviso() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "incluir_viewports": True, "confirmar": True})
    assert r["n_reasignadas"] == 2 and r["n_excluidas"] == 0 and r["incluir_viewports"] is True
    assert r["por_origen"]["Layer7"]["por_tipo"] == {"CIRCLE": 1, "VIEWPORT": 1}
    assert not any("viewport(s) de layout" in a for a in r["advertencias"])
    assert _capa_en_layout(world, "1C8") == "MUROS"


def test_normalizar_viewport_1_nunca_se_reasigna() -> None:
    world = _world_con_papel()
    for incluir in (False, True):
        r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "incluir_viewports": incluir})
        excl = r["por_origen"]["Layer7"]["excluidas"]
        assert excl["viewports_papel"] == {"Layout1": 1}
        assert excl["viewports"] == ({} if incluir else {"Layout1": 1})
        # El n.º 1 no entra en el aviso (pasar incluir_viewports no lo movería).
        aviso = advertencia_viewports_excluidos([("Layout1", 0 if incluir else 1)])
        assert (aviso in r["advertencias"]) if aviso else not any("viewport(s) de layout" in a for a in r["advertencias"])
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "incluir_viewports": True, "confirmar": True})
    assert (_capa_en_layout(world, "1D0"), _capa_en_layout(world, "1C8")) == ("Layer7", "MUROS")


def test_normalizar_viewport_sin_numero_el_primero_es_el_papel() -> None:
    world = _world(faseL=True)
    papel = {**copy.deepcopy(VIEWPORT_PAPEL), "_numero": -1}
    _state(world).layout_entities["Layout1"].insert(0, papel)
    _state(world).layout_entities["Layout1"][-1]["_numero"] = -1  # 1C8 sin número: no es el primero
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "incluir_viewports": True})
    assert r["por_origen"]["Layer7"]["excluidas"]["viewports_papel"] == {"Layout1": 1}
    assert r["por_origen"]["Layer7"]["por_tipo"] == {"CIRCLE": 1, "VIEWPORT": 1}


def test_normalizar_espacios_solo_modelo() -> None:
    """Gate prueba 6: {"0": "Cotas"} con espacios ["modelo"] → solo el modelo, 0 viewports, sin aviso."""
    world = _world_con_papel()
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "espacios": ["MODELO"]})
    assert r["espacios"] == ["modelo"]
    assert r["por_origen"]["Layer7"]["por_espacio"] == {"modelo": 1}
    assert r["por_origen"]["Layer7"]["por_tipo"] == {"CIRCLE": 1}
    assert r["por_origen"]["Layer7"]["excluidas"] == {
        "n": 2, "por_espacio": {"Layout1": 2}, "viewports": {}, "viewports_papel": {},
    }
    assert not any("viewport(s) de layout" in a for a in r["advertencias"])
    _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "espacios": ["modelo"], "confirmar": True})
    assert (_capa_en_layout(world, "1D0"), _capa_en_layout(world, "1C8")) == ("Layer7", "Layer7")
    assert next(e for e in _state(world).entities if e["handle"] == "1C5")["capa"] == "MUROS"


def test_normalizar_espacios_solo_un_layout_sin_distinguir_mayusculas() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS", "MUEBLE": "MUROS"}, "espacios": ["layout1"], "incluir_viewports": True})
    assert r["espacios"] == ["Layout1"]
    assert r["por_origen"]["Layer7"]["por_espacio"] == {"Layout1": 1}
    assert r["por_origen"]["Layer7"]["excluidas"]["por_espacio"] == {"modelo": 1}
    # El INSERT 1C4 y su ATTRIB 1CA (capa MUEBLE) están en el modelo: excluidos, contados por espacio.
    assert r["por_origen"]["MUEBLE"]["excluidas"] == {"n": 3, "por_espacio": {"modelo": 3}, "viewports": {}, "viewports_papel": {}}
    assert r["n_reasignar"] == 1 and r["n_excluidas"] == 4


def test_normalizar_espacios_y_incluir_bloques_independientes() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"MUEBLE": "MUROS"}, "espacios": ["Layout1"], "incluir_bloques": True})
    # Las definiciones de bloque dependen solo de incluir_bloques: la LWPOLYLINE de 'erere' se reasigna.
    assert r["por_origen"]["MUEBLE"]["por_espacio_bloques"] == {"bloque:erere": 1}
    assert r["por_origen"]["MUEBLE"]["por_espacio"] == {}
    assert r["mapa"][0]["n"] == 1


def test_normalizar_espacios_inexistentes_not_found_con_la_lista() -> None:
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "espacios": ["modelo", "Lamina9", "X"]})
    detalle = detalle_espacios_inexistentes(["Lamina9", "X"], ["Layout1"])
    assert detalle == "espacios: layouts inexistentes: Lamina9, X (espacios del dibujo: modelo, Layout1)"
    assert (err.code, err.plugin_message) == ("NOT_FOUND", detalle)
    assert (err.data["espacios"], err.data["layouts"], err.data["detalle"]) == (["Lamina9", "X"], ["Layout1"], detalle)


def test_normalizar_espacios_despues_del_mapa() -> None:
    err = _plugin_error(_world(faseL=True), "normalizar_capas", {"mapa": {"NOPE": "MUROS"}, "espacios": ["Lamina9"]})
    assert err.plugin_message == "mapa: capas de origen inexistentes: NOPE"


@pytest.mark.parametrize(
    ("espacios", "detalle"),
    [
        ([], 'espacios: debe ser un array de 1 a 100 nombres ("modelo" o nombres de layout)'),
        ("modelo", 'espacios: debe ser un array de 1 a 100 nombres ("modelo" o nombres de layout)'),
        (["modelo", ""], "espacios[1]: debe ser una cadena de 1 a 255 caracteres"),
        (["modelo", 3], "espacios[1]: debe ser una cadena de 1 a 255 caracteres"),
        (["Layout1", "modelo", "LAYOUT1"], "espacios: espacio repetido 'LAYOUT1'"),
    ],
)
def test_normalizar_espacios_forma_invalid_params(espacios: Any, detalle: str) -> None:
    world = _world(faseL=True)
    antes = _foto(world)
    err = _plugin_error(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "espacios": espacios, "confirmar": True})
    assert (err.code, err.data["detalle"]) == ("INVALID_PARAMS", detalle)
    assert _foto(world) == antes


def test_normalizar_borrar_vacias_no_borra_capa_con_entidades_excluidas() -> None:
    """La capa origen con entidades en espacios o viewports excluidos no se borra (Database.Purge decide)."""
    for extra in ({"espacios": ["modelo"]}, {}):
        world = _world(faseL=True)
        args = {"mapa": {"Layer7": "TEXTOS"}, "borrar_capas_origen_vacias": True, **extra}
        r = _call(world, "normalizar_capas", args)
        assert r["capas_borrar"] == []
        r = _call(world, "normalizar_capas", {**args, "confirmar": True})
        assert r["capas_borradas"] == [] and "Layer7" in _capas(world)
        assert _capa_en_layout(world, "1C8") == "Layer7"


def test_normalizar_layout_no_actual_papel_por_getviewports_aunque_no_sea_el_primero() -> None:
    """O1: en un layout no actual Number = −1 en todos; el de papel (primero de Layout.GetViewports()) no es el primero
    del registro. Con incluir_viewports: true sigue en viewports_papel y no se reasigna; el primero sí."""
    world = _world(faseL=True)
    layout = _state(world).layout_entities["Layout1"]
    layout[-1]["_numero"] = -1  # 1C8, ventana de modelo, primero del registro entre los viewports
    layout.append({**copy.deepcopy(VIEWPORT_PAPEL), "_numero": -1, "_papel_getviewports": True})
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "incluir_viewports": True, "confirmar": True})
    excl = r["por_origen"]["Layer7"]["excluidas"]
    assert excl["viewports_papel"] == {"Layout1": 1} and excl["viewports"] == {}
    assert r["por_origen"]["Layer7"]["por_tipo"] == {"CIRCLE": 1, "VIEWPORT": 1}
    assert (_capa_en_layout(world, "1D0"), _capa_en_layout(world, "1C8")) == ("Layer7", "MUROS")


# ---- patron_ajeno / sugerencia_patron del simulador frente a los casos reales de LayerClassifier (.NET) ----
# Los casos se leen de las [InlineData] de los tests de GeometryCore (fuente única, datos reales de copia_real.dwg y
# del lps.json v2): si el clasificador .NET o sus casos cambian, este test obliga a mantener el simulador igual.

_TESTS_AUDIT = Path(__file__).resolve().parents[2] / "plugin" / "MCPAutoCAD.GeometryCore.Tests" / "Audit"


def _inline_data(archivo: str, metodo: str) -> list[tuple[Any, ...]]:
    """Argumentos de las [InlineData(...)] que preceden a 'public void <metodo>(' en el archivo C#."""
    import ast
    import re

    lineas = (_TESTS_AUDIT / archivo).read_text(encoding="utf-8").splitlines()
    fin = next(i for i, l in enumerate(lineas) if re.search(rf"public void {metodo}\(", l))
    casos: list[tuple[Any, ...]] = []
    i = fin - 1
    while i >= 0 and (lineas[i].strip().startswith("[") or lineas[i].strip().startswith("//")):
        m = re.match(r"\s*\[InlineData\((.*)\)\]", lineas[i])
        if m:
            args = re.sub(r"\bnull\b", "None", m.group(1))
            casos.append(tuple(ast.literal_eval("(" + args + ",)")))
        i -= 1
    return list(reversed(casos))


def _sugerencia_simulada(nombre: str, clasificacion: str) -> str | None:
    """sugerencia de LayerClassifier.Classify: solo clases 5–8; mapa_normalizacion gana a la palabra clave."""
    from fake_plugin import LPS, _mapa_normalizacion, sugerencia_por_palabra

    if clasificacion not in ("ajena_revit_aia", "ajena_exportacion", "generica", "fuera_de_estandar"):
        return None
    return _mapa_normalizacion(LPS).get(nombre.casefold()) or sugerencia_por_palabra(nombre)


CASOS_COPIA_REAL = _inline_data("LayerClassifierRealDataTests.cs", "CopiaReal_CapasConcretas")
CASOS_TABLA_AIA = _inline_data("LayerClassifierTests.cs", "SuggestForPattern_TablaAia")
CASOS_V2 = _inline_data("LayerClassifierTests.cs", "PatronAjeno_EnLaV2_SonAjenasConDestinoDecidido")


def test_casos_reales_leidos_de_los_tests_net() -> None:
    assert len(CASOS_COPIA_REAL) == 22 and all(len(c) == 5 for c in CASOS_COPIA_REAL)
    assert len(CASOS_TABLA_AIA) == 14 and all(len(c) == 2 for c in CASOS_TABLA_AIA)
    assert len(CASOS_V2) == 6 and all(len(c) == 6 for c in CASOS_V2)


@pytest.mark.parametrize(("nombre", "clasif", "sugerencia", "patron", "sugerencia_patron"), CASOS_COPIA_REAL)
def test_patron_del_simulador_igual_que_el_clasificador_real_copia_real(
    nombre: str, clasif: str, sugerencia: str | None, patron: str | None, sugerencia_patron: str | None
) -> None:
    from fake_plugin import campos_patron

    assert campos_patron(nombre) == (patron, sugerencia_patron)
    assert _sugerencia_simulada(nombre, clasif) == sugerencia


@pytest.mark.parametrize(("nombre", "esperado"), CASOS_TABLA_AIA)
def test_sugerencia_por_patron_del_simulador_tabla_aia(nombre: str, esperado: str | None) -> None:
    from fake_plugin import sugerencia_por_patron

    assert sugerencia_por_patron(nombre) == esperado


@pytest.mark.parametrize(("nombre", "_color", "_grosor", "clasif", "sugerencia", "sugerencia_patron"), CASOS_V2)
def test_patron_del_simulador_v2_mapa_gana(
    nombre: str, _color: int, _grosor: int, clasif: str, sugerencia: str | None, sugerencia_patron: str | None
) -> None:
    from fake_plugin import campos_patron

    assert campos_patron(nombre)[1] == sugerencia_patron
    assert _sugerencia_simulada(nombre, clasif) == sugerencia


def test_mapa_normalizacion_del_simulador_tolerante_como_lps_layer_rules() -> None:
    # Mismo JSON que MapaNormalizacion_Lectura_ToleranteYConConceptos (LayerClassifierTests).
    from fake_plugin import _mapa_normalizacion

    lps = {
        "capas_preferidas": {"muros": "MUROS"},
        "capas_permitidas": {"MUROS": {}, "AC": {}},
        "capas_mcp": {"MCP-DIBUJO": {}},
        "mapa_normalizacion": {"MURO VIEJO": "Muros", "X": 5, "Y": "", "ac": "MUROS", "MCP-DIBUJO": "MUROS", "Z": "OTRA"},
    }
    assert _mapa_normalizacion(lps) == {"muro viejo": "MUROS", "z": "OTRA"}
    assert _mapa_normalizacion({}) == {}


def test_auditar_capas_y_auditar_dibujo_emiten_patron_ajeno() -> None:
    world = _world(faseL=True)
    r = _call(world, "auditar_capas", {"limit": 1000})
    filas = {c["nombre"]: (c["patron_ajeno"], c["sugerencia_patron"]) for c in r["capas"]}
    assert filas["A-WALL-FULL"] == ("revit_aia", "MUROS")
    assert filas["Layer7"] == ("generica", None)
    assert filas["Layer9"] == ("generica", None)
    for nombre in ("0", "MUROS", "MUEBLE", "CAPAVACIA", "MCP-DIBUJO", "DEFPOINTS"):
        assert filas[nombre] == (None, None), nombre
    d = _call(world, "auditar_dibujo", {"reglas": ["auditar_capas"], "limit": 1000})
    datos = {h["mensaje"].split("'")[1]: h["datos"] for h in d["hallazgos"] if h["regla"] == "auditar_capas"}
    assert datos["A-WALL-FULL"]["patron_ajeno"] == "revit_aia"
    assert datos["A-WALL-FULL"]["sugerencia_patron"] == "MUROS"
    assert datos["MUEBLE"]["patron_ajeno"] is None and datos["MUEBLE"]["sugerencia"] == "muebles fijos"
