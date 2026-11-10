"""Pruebas de la Fase 3 (protocolo 0.4, protocol.md §5D) a través del cliente MCP.

El servidor es genérico: estas pruebas comprueban que las 14 herramientas de
dibujo primitivo se publican con escritura=True y se llaman sin código propio en
server.py, que cada respuesta tiene la forma del contrato (R6 + campos propios)
y que los errores del plugin (UNITS_UNKNOWN, NOT_FOUND, INVALID_PARAMS de capa,
de confirmar y de ubicación) llegan al cliente MCP como "[<código>] <mensaje>".
Se usa el plugin simulado de fake_plugin.py (sin pipes reales ni AutoCAD).
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

import pytest
from fake_plugin import (
    ADVERTENCIA_ACEPTAR_SIN_UNIDADES,
    DETALLE_CAPA_NO_CREABLE,
    HERRAMIENTAS_FASE3,
    HERRAMIENTAS_MODIFICACION,
    LPS_ROTULO_TAGS,
    PLANO_N_CONTENIDO,
    FakePluginState,
    manifest_tools,
)
from mcp import Client
from test_server import LOCAL_ORDER, MANIFEST_ORDER, World, _guardar_manifest, run, text_of

import mcp_autocad.server as server_module
from mcp_autocad.client import PipeClient
from mcp_autocad.discovery import Instance
from mcp_autocad.errors import PluginError

NO_INSTANCE = "AutoCAD no está abierto o el plugin no cargó."
R6 = ["pid", "handles", "capa", "capa_creada", "espacio", "insunits", "unidades", "advertencias"]
R6_MODIFICACION = ["pid", "handles", "n", "n_usuario", "espacio", "capas", "insunits", "unidades", "advertencias"]
COMUNES_DIBUJO = {"capa", "espacio", "forzar", "aceptar_sin_unidades"}
COMUNES_MODIFICACION = {"handles", "confirmar", "forzar", "aceptar_sin_unidades"}
CUADRADO = [[0, 0], [4, 0], [4, 3], [0, 3]]

# Argumentos mínimos válidos y campos propios de 'entidad' (§5D.2–§5D.7) de las herramientas que crean entidades.
CREACION: dict[str, tuple[dict[str, Any], list[str]]] = {
    "polilinea": (
        {"vertices": CUADRADO, "cerrada": True},
        ["handle", "n_vertices", "cerrada", "longitud", "area", "bbox"],
    ),
    "circulo": ({"centro": [2, 2], "radio": 1}, ["handle", "centro", "radio", "longitud", "area"]),
    "arco": (
        {"centro": [2, 2], "radio": 1, "angulo_inicio": 0, "angulo_fin": 90},
        ["handle", "centro", "radio", "angulo_inicio", "angulo_fin", "longitud"],
    ),
    "texto": (
        {"posicion": [1, 1], "texto": "SALA", "altura": 0.2},
        ["handle", "posicion", "altura", "rotacion", "justificacion", "estilo", "bbox"],
    ),
    "mtext": (
        {"posicion": [1, 1], "texto": "NOTA", "altura": 0.2},
        ["handle", "posicion", "altura", "ancho", "rotacion", "adjunto", "estilo", "bbox"],
    ),
    "insertar_bloque": (
        {"nombre": "DP", "posicion": [2, 1]},
        ["handle", "bloque", "posicion", "escala", "rotacion", "n_atributos", "bbox"],
    ),
    "cota": (
        {"tipo": "lineal", "p1": [0, 0], "p2": [5, 0], "posicion_linea": [2.5, -1]},
        ["handle", "tipo", "medida", "estilo", "texto", "p1", "p2", "posicion_linea", "bbox"],
    ),
    "hatch": (
        {"contorno": {"handles": ["1A3"]}, "patron": "ANSI31"},
        ["handle", "patron", "escala", "angulo", "area", "n_bucles", "asociativo", "contorno_handles", "bbox"],
    ),
}
# Argumentos válidos (con confirmar) de las cinco herramientas de modificación sobre 1A0 (MUROS).
MODIFICACION: dict[str, tuple[dict[str, Any], list[str]]] = {
    "mover": ({"desplazamiento": [1, 0]}, ["desplazamiento"]),
    "copiar": ({"desplazamiento": [0, 1]}, ["desplazamiento", "handles_origen"]),
    "rotar": ({"base": [0, 0], "angulo": 10}, ["base", "angulo"]),
    "escalar": ({"base": [0, 0], "factor": 1.1}, ["base", "factor"]),
    "borrar": ({}, []),
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


def _entity(world: World, handle: str) -> dict[str, Any] | None:
    st = world.states[18244]
    for entities in [st.entities, *st.layout_entities.values()]:
        for e in entities:
            if e["handle"] == handle:
                return e
    return None


def _snapshot(state: FakePluginState) -> tuple[int, int, int]:
    return (len(state.entities), sum(len(v) for v in state.layout_entities.values()), len(state.capas_creadas))


# ---------------------------------------------------------------------------
# Publicación (tools/list) y manifest
# ---------------------------------------------------------------------------


def test_tools_list_publica_las_14_de_fase3_con_schema_del_manifest() -> None:
    world = World(18244)
    esperado = {t["name"]: t for t in manifest_tools()}

    async def body(c: Client) -> None:
        tools = (await c.list_tools()).tools
        assert [t.name for t in tools] == MANIFEST_ORDER + LOCAL_ORDER
        assert len(tools) == 61
        publicadas = {t.name: t for t in tools}
        for name in HERRAMIENTAS_FASE3:
            tool = publicadas[name]
            assert tool.input_schema == esperado[name]["inputSchema"], name
            assert tool.input_schema["additionalProperties"] is False, name
            assert tool.description == esperado[name]["description"], name

    run(world.server(), body)


def test_manifest_fase3_en_orden_y_de_escritura() -> None:
    tools = manifest_tools()
    assert [t["name"] for t in tools][22:36] == HERRAMIENTAS_FASE3 == MANIFEST_ORDER[22:36]
    for t in tools[22:36]:
        assert t["escritura"] is True and t["requiere_documento"] is True, t["name"]


def test_schemas_con_parametros_comunes() -> None:
    schemas = {t["name"]: t["inputSchema"] for t in manifest_tools()}
    for name in HERRAMIENTAS_FASE3:
        props = set(schemas[name]["properties"])
        if name == "crear_capa":
            assert props == {"nombre"} and schemas[name]["required"] == ["nombre"]
        elif name in HERRAMIENTAS_MODIFICACION:
            assert COMUNES_MODIFICACION <= props, name
            assert not {"capa", "espacio"} & props, name
            assert "handles" in schemas[name]["required"], name
        else:
            assert COMUNES_DIBUJO <= props, name
        for flag in ("forzar", "aceptar_sin_unidades"):
            if flag in props:
                assert schemas[name]["properties"][flag] == {**schemas[name]["properties"][flag], "type": "boolean", "default": False}
    assert schemas["insertar_bloque"]["properties"]["escala_unidades"]["enum"] == ["auto", "ninguna"]
    assert schemas["cota"]["properties"]["tipo"]["enum"] == ["lineal", "alineada"]
    # linea (§5.4) admite desde 0.4 los parámetros de R2/R4/R5 y sigue admitiendo exigir_unidades.
    linea = schemas["linea"]["properties"]
    assert {"aceptar_sin_unidades", "espacio", "forzar", "exigir_unidades"} <= set(linea)


def test_servidor_sin_ramas_por_herramienta_de_fase3() -> None:
    fuente = Path(server_module.__file__).read_text(encoding="utf-8")
    for name in HERRAMIENTAS_FASE3:
        assert f'"{name}"' not in fuente, name


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


def test_escrituras_de_fase3_no_idempotentes() -> None:
    world = _SpyWorld(18244)

    async def body(c: Client) -> None:
        await _ok(c, "crear_capa", {"nombre": "cotas"})
        await _ok(c, "circulo", {"centro": [1, 1], "radio": 0.5})
        await _ok(c, "borrar", {"handles": ["1B0"], "confirmar": True})

    run(world.server(), body)
    por_metodo = {m: i for m, i in world.calls if m != "tools/manifest"}
    assert por_metodo == {"crear_capa": False, "circulo": False, "borrar": False}


# ---------------------------------------------------------------------------
# Forma de la respuesta (R6 + campos propios)
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", list(CREACION))
def test_creacion_devuelve_r6_y_entidad(name: str) -> None:
    world = World(18244)
    args, campos = CREACION[name]

    async def body(c: Client) -> None:
        data = await _ok(c, name, args)
        for campo in R6:
            assert campo in data, campo
        assert data["pid"] == 18244 and data["espacio"] == "modelo"
        assert (data["insunits"], data["unidades"]) == (6, "m")
        assert len(data["handles"]) == 1 and data["handles"][0] == data["entidad"]["handle"]
        assert list(data["entidad"]) == campos
        # La entidad existe en el dibujo, en la capa informada.
        assert _entity(world, data["handles"][0])["capa"] == data["capa"]
        if name == "insertar_bloque":
            assert data["origen_texto"] == "contenido_del_plano"
        else:
            assert "origen_texto" not in data

    run(world.server(), body)
    assert [r["params"] for r in world.states[18244].requests if r["method"] == name] == [args]


@pytest.mark.parametrize("name", HERRAMIENTAS_MODIFICACION)
def test_modificacion_devuelve_campos_de_5d8(name: str) -> None:
    world = World(18244)
    args, propios = MODIFICACION[name]

    async def body(c: Client) -> None:
        data = await _ok(c, name, {"handles": ["1A0"], "confirmar": True, **args})
        assert list(data) == R6_MODIFICACION + propios
        assert (data["n"], data["n_usuario"], data["espacio"], data["capas"]) == (1, 1, "modelo", ["MUROS"])
        assert "capa" not in data and "capa_creada" not in data

    run(world.server(), body)


def test_capas_por_defecto_cota_y_hatch() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        cota = await _ok(c, "cota", CREACION["cota"][0])
        assert (cota["capa"], cota["capa_creada"]) == ("Cotas", True)
        assert cota["entidad"]["medida"] == pytest.approx(5.0)
        assert cota["entidad"]["estilo"] == "ISO-25"
        hatch = await _ok(c, "hatch", CREACION["hatch"][0])
        assert (hatch["capa"], hatch["capa_creada"]) == ("Achurados", True)
        resto = await _ok(c, "circulo", {"centro": [1, 1], "radio": 0.5})
        assert (resto["capa"], resto["capa_creada"]) == ("MCP-DIBUJO", False)

    run(world.server(), body)


# ---------------------------------------------------------------------------
# crear_capa (§5D.1)
# ---------------------------------------------------------------------------


def test_crear_capa_idempotente() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        primera = await _ok(c, "crear_capa", {"nombre": "cotas"})
        assert (primera["capa"], primera["creada"], primera["origen"]) == ("Cotas", True, "concepto")
        assert (primera["color"], primera["tipo_linea"], primera["grosor"], primera["plot"]) == (8, "Continuous", 13, True)
        assert (primera["bloqueada"], primera["congelada"], primera["apagada"]) == (False, False, False)
        antes = _snapshot(world.states[18244])
        segunda = await _ok(c, "crear_capa", {"nombre": "COTAS"})
        assert (segunda["capa"], segunda["creada"]) == ("Cotas", False)
        assert _snapshot(world.states[18244]) == antes
        mcp = await _ok(c, "crear_capa", {"nombre": "MCP-EJES"})
        assert (mcp["creada"], mcp["color"], mcp["origen"]) == (True, 3, "nombre")

    run(world.server(), body)


def test_crear_capa_existente_no_estandar_se_devuelve() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "crear_capa", {"nombre": "Layer9"})
        assert (data["capa"], data["creada"]) == ("Layer9", False)

    run(world.server(), body)


@pytest.mark.parametrize(
    ("nombre", "texto"),
    [
        (
            "CAPA_INVENTADA",
            "[INVALID_PARAMS] Parámetros inválidos: nombre: solo se crean capas de lps.json "
            "(capas_permitidas/capas_mcp) o MCP-*",
        ),
        ("MAL*NOMBRE", "[INVALID_PARAMS] Parámetros inválidos: capa: nombre de capa no válido"),
    ],
)
def test_crear_capa_errores(nombre: str, texto: str) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, "crear_capa", {"nombre": nombre}) == texto

    run(world.server(), body)
    assert world.states[18244].capas_creadas == []


# ---------------------------------------------------------------------------
# R2 unidades, R3 capa, R4 ubicación, R5 espacio
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", [n for n in HERRAMIENTAS_FASE3 if n != "crear_capa"] + ["linea"])
def test_units_unknown_sin_aceptar_sin_unidades(name: str) -> None:
    world = World(18244, insunits=0)
    if name in HERRAMIENTAS_MODIFICACION:
        args = {"handles": ["1A0"], "confirmar": True, **MODIFICACION[name][0]}
    elif name == "linea":
        args = {"inicio": [0, 0], "fin": [1, 0]}
    else:
        args = CREACION[name][0]
    antes = _snapshot(world.states[18244])

    async def body(c: Client) -> None:
        assert await _err(c, name, args) == "[UNITS_UNKNOWN] El dibujo no declara unidades (INSUNITS=0)."
        data = await _ok(c, name, {**args, "aceptar_sin_unidades": True})
        assert (data["insunits"], data["unidades"]) == (0, "desconocida")
        assert ADVERTENCIA_ACEPTAR_SIN_UNIDADES in data["advertencias"]

    with pytest.raises(PluginError) as exc:
        _client(world).call(name, args)
    assert (exc.value.code, exc.value.rpc_code) == ("UNITS_UNKNOWN", -32004)
    assert _snapshot(world.states[18244]) == antes
    run(world.server(), body)


def test_crear_capa_no_aplica_r2() -> None:
    world = World(18244, insunits=0)

    async def body(c: Client) -> None:
        assert (await _ok(c, "crear_capa", {"nombre": "textos"}))["capa"] == "TEXTOS"

    run(world.server(), body)


def test_capa_inventada_not_found_sin_escribir() -> None:
    world = World(18244)
    antes = _snapshot(world.states[18244])

    async def body(c: Client) -> None:
        texto = await _err(c, "polilinea", {"vertices": CUADRADO, "capa": "CAPA_INVENTADA"})
        assert texto == "[NOT_FOUND] La capa 'CAPA_INVENTADA' no existe en el dibujo."

    run(world.server(), body)
    err = _plugin_error(world, "circulo", {"centro": [1, 1], "radio": 1, "capa": "CAPA_INVENTADA"})
    assert (err.code, err.data["capa"], err.data["detalle"]) == ("NOT_FOUND", "CAPA_INVENTADA", DETALLE_CAPA_NO_CREABLE)
    assert _snapshot(world.states[18244]) == antes


def test_capa_por_concepto_nombre_y_estandar() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        muros = await _ok(c, "polilinea", {"vertices": CUADRADO, "capa": "muros"})
        assert (muros["capa"], muros["capa_creada"]) == ("MUROS", False)
        ejes = await _ok(c, "polilinea", {"vertices": CUADRADO, "capa": "ejes"})
        assert (ejes["capa"], ejes["capa_creada"]) == ("EJES", True)
        # Proyeccion usa el tipo de línea CONTINUA, no cargado en el dibujo ficticio.
        proy = await _ok(c, "circulo", {"centro": [1, 1], "radio": 1, "capa": "proyeccion"})
        assert proy["capa"] == "Proyeccion"
        assert (
            "La capa Proyeccion se creó con tipo de línea Continuous: 'CONTINUA' no está cargado en el dibujo."
            in proy["advertencias"]
        )

    run(world.server(), body)


@pytest.mark.parametrize(
    ("campo", "detalle"),
    [("capas_bloqueadas", "capa bloqueada"), ("capas_congeladas", "capa congelada")],
)
def test_capa_bloqueada_o_congelada_invalid_params(campo: str, detalle: str) -> None:
    world = World(18244, **{campo: {"muros"}})

    async def body(c: Client) -> None:
        texto = await _err(c, "polilinea", {"vertices": CUADRADO, "capa": "MUROS"})
        assert texto == f"[INVALID_PARAMS] Parámetros inválidos: {detalle}"

    run(world.server(), body)
    err = _plugin_error(world, "linea", {"inicio": [0, 0], "fin": [1, 0], "capa": "muros"})
    assert (err.data["detalle"], err.data["capa"]) == (detalle, "MUROS")


def test_capa_apagada_escribe_con_advertencia() -> None:
    world = World(18244, capas_apagadas={"TEXTOS"})

    async def body(c: Client) -> None:
        data = await _ok(c, "texto", {"posicion": [1, 1], "texto": "X", "altura": 0.2, "capa": "textos"})
        assert data["advertencias"] == ["La capa TEXTOS está apagada: las entidades no serán visibles."]

    run(world.server(), body)


def test_ubicacion_fuera_de_extension_y_forzar() -> None:
    world = World(18244)
    antes = _snapshot(world.states[18244])
    lejos = {"centro": [5000, 5000], "radio": 1}

    async def body(c: Client) -> None:
        texto = await _err(c, "circulo", lejos)
        assert texto.startswith(
            "[INVALID_PARAMS] Parámetros inválidos: entidades[0] [[4999, 4999], [5001, 5001]] fuera de la extensión "
            "del espacio destino 'modelo' [["
        )
        assert "indique en 'espacio' el layout correcto" in texto
        assert texto.endswith("No se escribió nada; pase forzar: true para escribir igualmente.")
        assert _snapshot(world.states[18244]) == antes
        forzado = await _ok(c, "circulo", {**lejos, "forzar": True})
        assert forzado["advertencias"][-1].startswith("forzar: true: se escribió fuera de la extensión del espacio destino 'modelo'")
        assert forzado["advertencias"][-1].endswith("(entidades 0).")

    run(world.server(), body)
    err = _plugin_error(world, "polilinea", {"vertices": [[5000, 5000], [5001, 5000]]})
    assert (err.data["espacio"], err.data["vertices_fuera"]) == ("modelo", [0])
    assert err.data["detalle"].startswith("vertices[0] ")
    assert err.data["extension"] == [[-50.0, -50.0], [50.0, 50.0]]


def test_ubicacion_coordenadas_de_modelo_con_layout1_activo() -> None:
    world = World(18244, espacio_activo="Layout1", limites_layout={"Layout1": [[0.0, 0.0], [420.0, 297.0]]})

    async def body(c: Client) -> None:
        texto = await _err(c, "polilinea", {"vertices": [[3273, 3989], [3278, 3994]]})
        assert "fuera de la extensión del espacio destino 'Layout1' [[0, 0], [420, 297]]" in texto
        assert "use espacio:\"modelo\" (o el layout correcto)" in texto
        en_papel = await _ok(c, "polilinea", {"vertices": [[10, 10], [20, 10]]})
        assert en_papel["espacio"] == "Layout1"
        assert _entity(world, en_papel["handles"][0]) in world.states[18244].layout_entities["Layout1"]

    run(world.server(), body)


def test_espacio_explicito_y_espacio_inexistente() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "texto", {"posicion": [1, 1], "texto": "PAPEL", "altura": 2.5, "espacio": "layout1"})
        assert data["espacio"] == "Layout1"
        texto = await _err(c, "texto", {"posicion": [1, 1], "texto": "X", "altura": 1, "espacio": "Layout9"})
        assert texto == (
            "[NOT_FOUND] El layout 'Layout9' no existe en el dibujo. Disponibles: modelo, Layout1"
            " (nombre de la pestaña, no el título del plano; listar_layouts da sus viewports)."
        )

    run(world.server(), body)


def test_linea_adaptada_a_0_4() -> None:
    world = World(18244, espacio_activo="Layout1")

    async def body(c: Client) -> None:
        data = await _ok(c, "linea", {"inicio": [0, 0], "fin": [5, 0], "exigir_unidades": False})
        # Desde 0.4 'espacio' es el nombre del layout (antes "papel").
        assert (data["espacio"], data["capa"], data["capa_creada"]) == ("Layout1", "MCP-DIBUJO", False)
        modelo = await _ok(c, "linea", {"lineas": [{"inicio": [0, 0], "fin": [1, 1]}], "espacio": "modelo", "capa": "ejes"})
        assert (modelo["espacio"], modelo["capa"], modelo["capa_creada"]) == ("modelo", "EJES", True)
        lejos = await _err(c, "linea", {"inicio": [5000, 5000], "fin": [5001, 5000], "espacio": "modelo"})
        assert lejos.startswith("[INVALID_PARAMS] Parámetros inválidos: lineas[0] ")

    run(world.server(), body)


# ---------------------------------------------------------------------------
# Geometría y textos (§5D.2–§5D.4, R7)
# ---------------------------------------------------------------------------


def test_polilinea_cerrada_y_validaciones() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "polilinea", {"vertices": [[0, 0, 1], [4, 0], [4, 3], [0, 3]], "cerrada": True})
        assert data["entidad"]["area"] == pytest.approx(12.0)
        assert data["entidad"]["longitud"] == pytest.approx(14.0)
        assert data["advertencias"] == ["vertices: la componente z se ignoró (LWPOLYLINE en el plano XY)."]
        abierta = await _ok(c, "polilinea", {"vertices": [[0, 0], [1, 1], [1, 0], [0, 1]]})
        assert abierta["entidad"]["area"] is None and abierta["entidad"]["cerrada"] is False
        pajarita = await _err(c, "polilinea", {"vertices": [[0, 0], [1, 1], [1, 0], [0, 1]], "cerrada": True})
        assert pajarita == (
            "[INVALID_PARAMS] Parámetros inválidos: vertices: la polilínea cerrada se autointerseca (tramos 0 y 2)"
        )
        dup = await _err(c, "polilinea", {"vertices": [[0, 0], [0, 0], [1, 0]]})
        assert dup == "[INVALID_PARAMS] Parámetros inválidos: vertices[1]: vértice duplicado con el anterior"
        bul = await _err(c, "polilinea", {"vertices": CUADRADO, "bulges": [0, 0]})
        assert bul == "[INVALID_PARAMS] Parámetros inválidos: bulges: debe tener tantos elementos como vertices"

    run(world.server(), body)


def test_circulo_y_arco() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        arco = await _ok(c, "arco", {"centro": [2, 2], "radio": 2, "angulo_inicio": -90, "angulo_fin": 450})
        assert (arco["entidad"]["angulo_inicio"], arco["entidad"]["angulo_fin"]) == (270.0, 90.0)
        assert arco["entidad"]["longitud"] == pytest.approx(2 * 3.141592653589793)
        igual = await _err(c, "arco", {"centro": [2, 2], "radio": 1, "angulo_inicio": 0, "angulo_fin": 360})
        assert igual == "[INVALID_PARAMS] Parámetros inválidos: angulo_fin: debe ser distinto de angulo_inicio"
        radio = await _err(c, "circulo", {"centro": [2, 2], "radio": 0})
        assert radio == "[INVALID_PARAMS] Parámetros inválidos: radio: debe ser un número mayor que 0"

    run(world.server(), body)


def test_texto_literal_con_porcentajes_y_saltos() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "texto", {"posicion": [1, 1], "texto": "Ángulo 45%%d al 50%\nfin", "altura": 0.2})
        assert data["advertencias"] == [
            "texto: los saltos de línea se sustituyeron por espacios (TEXT es de una sola línea); use mtext."
        ]
        entidad = _entity(world, data["handles"][0])
        assert entidad["texto"] == "Ángulo 45%%d al 50% fin"
        assert entidad["_contenido"] == "Ángulo 45%%%%%%d al 50% fin"
        estilo = await _err(c, "texto", {"posicion": [1, 1], "texto": "X", "altura": 0.2, "estilo": "NOEXISTE"})
        assert estilo == "[NOT_FOUND] El estilo de texto 'NOEXISTE' no existe en el dibujo."

    run(world.server(), body)


def test_mtext_literal_con_llaves_y_barras() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "mtext", {"posicion": [1, 1], "texto": "a\\b{c}\nlinea 2", "altura": 0.2, "adjunto": "medio_centro"})
        assert data["entidad"]["adjunto"] == "medio_centro"
        assert _entity(world, data["handles"][0])["_contenido"] == "a\\\\b\\{c\\}\\Plinea 2"

    run(world.server(), body)


def test_cota_lineal_alineada_y_errores() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        lineal = await _ok(c, "cota", {"tipo": "lineal", "p1": [0, 0], "p2": [3, 4], "posicion_linea": [0, -1]})
        assert lineal["entidad"]["medida"] == pytest.approx(3.0)
        alineada = await _ok(
            c, "cota", {"tipo": "alineada", "p1": [0, 0], "p2": [3, 4], "posicion_linea": [0, 1], "texto_sustituto": "<> m"}
        )
        assert alineada["entidad"]["medida"] == pytest.approx(5.0) and alineada["entidad"]["texto"] == "<> m"
        rot = await _err(c, "cota", {"tipo": "alineada", "p1": [0, 0], "p2": [3, 4], "posicion_linea": [0, 1], "rotacion": 0})
        assert rot == "[INVALID_PARAMS] Parámetros inválidos: rotacion: solo aplica a tipo lineal"
        p2 = await _err(c, "cota", {"tipo": "lineal", "p1": [1, 1], "p2": [1, 1], "posicion_linea": [0, 1]})
        assert p2 == "[INVALID_PARAMS] Parámetros inválidos: p2: coincide con p1"

    run(world.server(), body)


def test_hatch_por_handles_asociativo_y_por_vertices() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        asociativo = await _ok(c, "hatch", {"contorno": {"handles": ["1a3"]}, "patron": "solid"})
        e = asociativo["entidad"]
        assert (e["asociativo"], e["contorno_handles"], e["n_bucles"], e["area"]) == (True, ["1A3"], 1, 15.0)
        libre = await _ok(c, "hatch", {"contorno": {"vertices": CUADRADO}, "patron": "ANSI31", "escala": 2})
        e = libre["entidad"]
        assert (e["asociativo"], e["contorno_handles"], e["n_bucles"], e["escala"]) == (False, None, 1, 2.0)
        assert e["area"] == pytest.approx(12.0)
        ambos = await _err(c, "hatch", {"contorno": {"handles": ["1A3"], "vertices": CUADRADO}, "patron": "SOLID"})
        assert ambos == "[INVALID_PARAMS] Parámetros inválidos: contorno: indique handles o vertices (solo uno)"
        abierta = await _err(c, "hatch", {"contorno": {"handles": ["1A0"]}, "patron": "SOLID"})
        assert abierta == (
            "[INVALID_PARAMS] Parámetros inválidos: contorno.handles[0]: la entidad no es un contorno cerrado (LINE)"
        )
        patron = await _err(c, "hatch", {"contorno": {"handles": ["1A3"]}, "patron": "NOEXISTE"})
        assert patron == "[NOT_FOUND] El patrón de sombreado 'NOEXISTE' no existe."

    run(world.server(), body)


# ---------------------------------------------------------------------------
# insertar_bloque (§5D.5)
# ---------------------------------------------------------------------------


def test_insertar_bloque_dp_existente_con_atributos_y_unidades() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        data = await _ok(c, "insertar_bloque", {"nombre": "dp", "posicion": [2, 1], "atributos": {"ancho": "1.00"}})
        assert data["entidad"]["bloque"] == "DP" and data["entidad"]["n_atributos"] == 2
        assert data["atributos"] == {"ANCHO": "1.00", "TIPO": "P1"}
        assert len(data["handles_atributos"]) == 2 and data["handles"] == [data["entidad"]["handle"]]
        assert data["importado_de_biblioteca"] is False
        assert "archivo" not in data and "capas_importadas" not in data
        # DP está en mm y el dibujo en m: "auto" aplica 0,001 con advertencia (§5C.3: posible metadato incorrecto).
        assert (data["unidades_bloque"], data["factor_unidades"]) == (4, 0.001)
        assert data["escala_aplicada"] == pytest.approx([0.001, 0.001, 0.001])
        assert data["advertencias"][-1].startswith("Se aplicó el factor de unidades 0.001 (bloque en mm, dibujo en m)")
        ninguna = await _ok(c, "insertar_bloque", {"nombre": "DP", "posicion": [3, 1], "escala_unidades": "ninguna", "escala": [-1, 1]})
        assert ninguna["escala_aplicada"] == [-1.0, 1.0, -1.0] and ninguna["advertencias"] == []

    run(world.server(), body)


def test_insertar_lps_rotulo_desde_biblioteca_con_25_atributos_y_plano_n() -> None:
    world = World(18244, espacio_activo="Layout1")
    valores = {tag: f"VALOR {i}" for i, tag in enumerate(LPS_ROTULO_TAGS)}
    valores["PLANO_N"] = "3"

    async def body(c: Client) -> None:
        sin_biblioteca = await _err(c, "insertar_bloque", {"nombre": "LPS_ROTULO", "posicion": [300, 50]})
        assert sin_biblioteca == "[NOT_FOUND] El bloque 'LPS_ROTULO' no existe en el dibujo."
        data = await _ok(
            c,
            "insertar_bloque",
            {"nombre": "LPS_ROTULO", "posicion": [300, 50], "desde_biblioteca": True, "capa": "rotulo", "atributos": valores},
        )
        assert (data["capa"], data["capa_creada"], data["espacio"]) == ("Rotulo", False, "Layout1")
        assert data["importado_de_biblioteca"] is True
        assert data["archivo"].endswith("\\Contents\\biblioteca\\LPS_ROTULO.dwg")
        # Rótulo v2 (2026-10-31): el archivo solo trae 0 y Rotulo, que ya están en el dibujo → no importa ninguna.
        assert data["capas_importadas"] == []
        assert (data["unidades_bloque"], data["factor_unidades"], data["escala_aplicada"]) == (6, 1.0, [1.0, 1.0, 1.0])
        assert data["entidad"]["n_atributos"] == 25 and len(data["handles_atributos"]) == 25
        assert sorted(data["atributos"]) == sorted(LPS_ROTULO_TAGS)
        assert data["atributos"]["PLANO_N"] == "3 de"
        assert data["atributos"]["PROYECTO"] == valores["PROYECTO"]
        assert data["origen_texto"] == "contenido_del_plano"
        # PLANO_N conserva el formato en línea: solo cambia el número.
        insert = _entity(world, data["handles"][0])
        plano_n = next(a for a in insert["_atributos"] if a["tag"] == "PLANO_N")
        assert plano_n["_contenido"] == PLANO_N_CONTENIDO.replace("p34;1\\f", "p34;3\\f")
        # Segunda inserción: la definición ya está en el dibujo (no se importa otra vez).
        otra = await _ok(c, "insertar_bloque", {"nombre": "LPS_ROTULO", "posicion": [300.05, 50.3], "atributos": {"plano_n": "12 de"}})
        assert otra["importado_de_biblioteca"] is False and otra["atributos"]["PLANO_N"] == "12 de"

    run(world.server(), body)


def test_insertar_lps_rotulo_sin_capa_rotulo_solo_importa_rotulo() -> None:
    # Rótulo v2 (2026-10-31): en un dibujo sin la capa Rotulo, la única capa que trae el archivo es Rotulo
    # (con las propiedades del estándar); ninguna de exportación (…_Pen_No__…) ni Cotas.
    world = World(18244, capas_borradas={"rotulo"})

    async def body(c: Client) -> None:
        data = await _ok(c, "insertar_bloque", {"nombre": "LPS_ROTULO", "posicion": [0, 0], "desde_biblioteca": True})
        assert data["importado_de_biblioteca"] is True and data["capa"] == "MCP-DIBUJO"
        assert data["capas_importadas"] == ["Rotulo"]
        # (listar_capas del simulador sigue ocultando 'rotulo' por capas_borradas: se mira la capa creada.)
        creadas = {capa["nombre"]: capa for capa in world.states[18244].capas_creadas}
        assert list(creadas) == ["Rotulo"]
        assert (creadas["Rotulo"]["color"], creadas["Rotulo"]["grosor"]) == (150, 80)

    run(world.server(), body)


@pytest.mark.parametrize(
    ("args", "texto"),
    [
        (
            {"nombre": "C:\\bloques\\LPS_ROTULO.dwg", "posicion": [0, 0]},
            "[INVALID_PARAMS] Parámetros inválidos: nombre: solo se admite el nombre del bloque, sin rutas",
        ),
        (
            {"nombre": "..\\LPS_ROTULO", "posicion": [0, 0], "desde_biblioteca": True},
            "[INVALID_PARAMS] Parámetros inválidos: nombre: solo se admite el nombre del bloque, sin rutas",
        ),
        (
            {"nombre": "*U1", "posicion": [0, 0]},
            "[INVALID_PARAMS] Parámetros inválidos: nombre: no se insertan bloques anónimos",
        ),
        (
            {"nombre": "DP", "posicion": [0, 0], "escala": 0},
            "[INVALID_PARAMS] Parámetros inválidos: escala: debe ser un número distinto de 0 o un array [sx, sy] / "
            "[sx, sy, sz]",
        ),
        (
            {"nombre": "DP", "posicion": [0, 0], "atributos": {"COLOR": "rojo"}},
            "[INVALID_PARAMS] Parámetros inválidos: atributos: etiqueta desconocida 'COLOR' (etiquetas del bloque: "
            "ANCHO, TIPO)",
        ),
        (
            {"nombre": "DP", "posicion": [0, 0], "atributos": {"ancho": "1", "ANCHO": "2"}},
            "[INVALID_PARAMS] Parámetros inválidos: atributos: etiqueta repetida 'ANCHO'",
        ),
        (
            {"nombre": "PUERTA_X", "posicion": [0, 0], "desde_biblioteca": True},
            "[NOT_FOUND] El bloque 'PUERTA_X' no está en el dibujo ni en la biblioteca (",
        ),
    ],
)
def test_insertar_bloque_errores(args: dict[str, Any], texto: str) -> None:
    world = World(18244)
    antes = _snapshot(world.states[18244])

    async def body(c: Client) -> None:
        assert (await _err(c, "insertar_bloque", args)).startswith(texto)

    run(world.server(), body)
    assert _snapshot(world.states[18244]) == antes


def test_insertar_bloque_errores_llevan_data() -> None:
    world = World(18244)
    err = _plugin_error(world, "insertar_bloque", {"nombre": "DP", "posicion": [0, 0], "atributos": {"X": "1"}})
    assert (err.code, err.data["etiquetas"]) == ("INVALID_PARAMS", ["ANCHO", "TIPO"])
    err = _plugin_error(world, "insertar_bloque", {"nombre": "LPS_ROTULO", "posicion": [0, 0]})
    assert (err.code, err.data["bloque"]) == ("NOT_FOUND", "LPS_ROTULO")
    assert err.data["detalle"] == "pase desde_biblioteca: true si el bloque está en la biblioteca"
    err = _plugin_error(world, "insertar_bloque", {"nombre": "PUERTA_X", "posicion": [0, 0], "desde_biblioteca": True})
    assert err.data["archivo"].endswith("\\biblioteca\\PUERTA_X.dwg")


# ---------------------------------------------------------------------------
# mover / copiar / rotar / escalar / borrar (§5D.8)
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", HERRAMIENTAS_MODIFICACION)
def test_modificar_entidades_del_usuario_exige_confirmar(name: str) -> None:
    world = World(18244)
    args = {"handles": ["1A0", "1AA"], **MODIFICACION[name][0]}
    antes = copy_entities(world)

    async def body(c: Client) -> None:
        texto = await _err(c, name, args)
        assert texto == (
            "[INVALID_PARAMS] Parámetros inválidos: confirmar: 2 de las 2 entidades no están en capas MCP-* "
            "(p. ej. 1A0 en MUROS, 1AA en TEXTOS); pase confirmar: true para modificarlas"
        )

    run(world.server(), body)
    assert copy_entities(world) == antes
    err = _plugin_error(world, name, args)
    assert (err.data["n_usuario"], err.data["ejemplos"]) == (2, [{"handle": "1A0", "capa": "MUROS"}, {"handle": "1AA", "capa": "TEXTOS"}])


def copy_entities(world: World) -> str:
    return json.dumps(world.states[18244].entities, sort_keys=True)


def test_mover_y_borrar_con_confirmar() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        movido = await _ok(c, "mover", {"handles": ["1A0"], "desplazamiento": [1, 2], "confirmar": True})
        assert movido["handles"] == ["1A0"] and movido["desplazamiento"] == [1.0, 2.0, 0.0]
        linea = _entity(world, "1A0")
        assert (linea["inicio"], linea["fin"]) == ([1.0, 2.0, 0.0], [6.0, 2.0, 0.0])
        borrado = await _ok(c, "borrar", {"handles": ["1A0", "1B0"], "confirmar": True})
        assert (borrado["n"], borrado["n_usuario"], borrado["capas"]) == (2, 2, ["MUROS"])
        assert _entity(world, "1A0") is None and _entity(world, "1B0") is None
        otra = await _err(c, "borrar", {"handles": ["1A0"], "confirmar": True})
        assert otra == "[NOT_FOUND] No existe ninguna entidad con handle '1A0'."

    run(world.server(), body)


def test_entidades_mcp_no_necesitan_confirmar() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        h = (await _ok(c, "circulo", {"centro": [1, 1], "radio": 0.5}))["handles"][0]
        rotado = await _ok(c, "rotar", {"handles": [h], "base": [0, 0], "angulo": 90})
        assert rotado["n_usuario"] == 0 and rotado["angulo"] == 90.0
        assert _entity(world, h)["centro"] == pytest.approx([-1.0, 1.0, 0.0])
        escalado = await _ok(c, "escalar", {"handles": [h], "base": [0, 0], "factor": 2})
        assert escalado["factor"] == 2.0 and _entity(world, h)["radio"] == pytest.approx(1.0)
        borrado = await _ok(c, "borrar", {"handles": [h]})
        assert (borrado["n_usuario"], borrado["capas"]) == (0, ["MCP-DIBUJO"])

    run(world.server(), body)


def test_copiar_devuelve_handles_nuevos() -> None:
    world = World(18244)
    n_antes = len(world.states[18244].entities)

    async def body(c: Client) -> None:
        data = await _ok(c, "copiar", {"handles": ["1A0", "1A4"], "desplazamiento": [0, 10], "confirmar": True})
        assert data["handles_origen"] == ["1A0", "1A4"]
        assert len(data["handles"]) == 2 and not set(data["handles"]) & set(data["handles_origen"])
        copia_insert = _entity(world, data["handles"][1])
        assert copia_insert["posicion"] == [1.0, 10.0, 0.0]
        # Los atributos del INSERT se clonan con handles propios.
        originales = {a["handle"] for a in _entity(world, "1A4")["_atributos"]}
        assert not originales & {a["handle"] for a in copia_insert["_atributos"]}
        cero = await _ok(c, "copiar", {"handles": ["1A1"], "desplazamiento": [0, 0], "confirmar": True})
        assert cero["desplazamiento"] == [0.0, 0.0, 0.0]

    run(world.server(), body)
    assert len(world.states[18244].entities) == n_antes + 3


@pytest.mark.parametrize(
    ("name", "args", "texto"),
    [
        ("mover", {"handles": ["1A0"], "desplazamiento": [0, 0]}, "desplazamiento: no puede ser cero"),
        ("rotar", {"handles": ["1A0"], "base": [0, 0], "angulo": 360}, "angulo: no puede ser 0"),
        ("escalar", {"handles": ["1A0"], "base": [0, 0], "factor": 1}, "factor: no puede ser 1"),
        ("borrar", {"handles": ["1A0", "1a0"]}, "handles[1]: handle repetido"),
        ("borrar", {"handles": ["1A5"], "confirmar": True}, "handles[0]: es un atributo; use el handle del INSERT"),
    ],
)
def test_modificacion_errores_de_parametros(name: str, args: dict[str, Any], texto: str) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        assert await _err(c, name, args) == f"[INVALID_PARAMS] Parámetros inválidos: {texto}"

    run(world.server(), body)


def test_modificacion_capa_bloqueada_y_espacios_distintos() -> None:
    world = World(18244, capas_bloqueadas={"MUROS"})
    world.states[18244].layout_entities["Layout1"].append(
        {"handle": "1FB", "tipo": "LINE", "capa": "MCP-DIBUJO", "bbox": [[1.0, 1.0, 0.0], [2.0, 1.0, 0.0]]}
    )

    async def body(c: Client) -> None:
        bloqueada = await _err(c, "borrar", {"handles": ["1A0"], "confirmar": True})
        assert bloqueada == "[INVALID_PARAMS] Parámetros inválidos: handles[0]: la entidad está en una capa bloqueada"
        espacios = await _err(c, "borrar", {"handles": ["1AA", "1FB"], "confirmar": True})
        assert espacios == (
            "[INVALID_PARAMS] Parámetros inválidos: handles: las entidades deben estar en el mismo espacio "
            "(1AA en modelo, 1FB en Layout1)"
        )

    run(world.server(), body)
    err = _plugin_error(world, "mover", {"handles": ["1A0"], "desplazamiento": [1, 0], "confirmar": True})
    assert (err.data["handle"], err.data["capa"]) == ("1A0", "MUROS")


def test_mover_fuera_de_extension_rechazado_salvo_forzar() -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        args = {"handles": ["1AA"], "desplazamiento": [10000, 0], "confirmar": True}
        texto = await _err(c, "mover", args)
        assert texto.startswith("[INVALID_PARAMS] Parámetros inválidos: entidades[0] [[10001, 1.5], [10002.1, 1.7]]")
        assert _entity(world, "1AA")["posicion"] == [1.0, 1.5, 0.0]
        forzado = await _ok(c, "mover", {**args, "forzar": True})
        assert forzado["advertencias"][-1].endswith("(entidades 0).")

    run(world.server(), body)


# ---------------------------------------------------------------------------
# Manifest en disco y versión del protocolo
# ---------------------------------------------------------------------------


def test_manifest_en_disco_publica_55_y_fase3_sin_instancia(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.json"
    _guardar_manifest(World(18244), manifest)
    guardado = json.loads(manifest.read_text(encoding="utf-8"))
    assert guardado["protocol_version"] == "0.8" and guardado["plugin_version"] == "0.8.0"
    assert len(guardado["tools"]) == 56
    assert [t["name"] for t in guardado["tools"]] == MANIFEST_ORDER

    async def body(c: Client) -> None:
        tools = [t.name for t in (await c.list_tools()).tools]
        assert tools == MANIFEST_ORDER + LOCAL_ORDER and len(tools) == 61
        res = await c.call_tool("insertar_bloque", {"nombre": "LPS_ROTULO", "posicion": [0, 0], "desde_biblioteca": True})
        assert res.is_error and text_of(res) == NO_INSTANCE

    run(World().server(manifest_path=manifest), body)


def test_manifest_0_3_avisa_y_publica(caplog: pytest.LogCaptureFixture) -> None:
    world = World(18244, protocol_version="0.3")

    async def body(c: Client) -> None:
        assert len((await c.list_tools()).tools) == 61

    with caplog.at_level(logging.WARNING, logger="mcp_autocad.server"):
        run(world.server(), body)
    mensajes = [r.getMessage() for r in caplog.records]
    assert any("'0.3'" in m and "el servidor implementa 0.8" in m for m in mensajes), mensajes


def test_error_programado_units_unknown_se_reenvia() -> None:
    world = World(18244)
    world.states[18244].program_error("hatch", "UNITS_UNKNOWN")

    async def body(c: Client) -> None:
        texto = await _err(c, "hatch", CREACION["hatch"][0])
        assert texto == "[UNITS_UNKNOWN] El dibujo no declara unidades (INSUNITS=0)."

    run(world.server(), body)
