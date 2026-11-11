"""Pruebas de la Fase 4B (protocolo 0.7, protocol.md §5G) a través del cliente MCP y del PipeClient.

El servidor es genérico: estas pruebas comprueban que las 4 herramientas nuevas se publican con
'escritura: true', sus descripciones literales y sus schemas, y que el plugin simulado (fake_plugin.py, sin
AutoCAD) reproduce el contrato: vano, union_muros y cotas_automaticas como generadoras (G2 sin escape, R4 salvo
forzar, preview_id, huella de anclas, BORRAR solo en definitivo y en confirmar, protección confirmar ya en la
previsualización), insertar_export como escritura sin previsualización, propiedades_dinamicas de insertar_bloque
y conservar_aspecto de normalizar_capas. Se simula la FORMA del contrato, no AutoCAD.
"""

from __future__ import annotations

import copy
import json
import math
from datetime import timedelta
from pathlib import Path
from typing import Any

import fake_plugin
import pytest
from fake_plugin import (
    ADVERTENCIA_ACEPTAR_SIN_UNIDADES,
    ADVERTENCIA_EXPORT_SIN_UNIDADES,
    ADVERTENCIA_SIN_CARPETAS,
    BIBLIOTECA_BLOQUES,
    CAPA_PREVIEW,
    CLAVES_G6,
    CONSERVAR_ASPECTO_DESCRIPCION,
    DESCRIPCIONES_FASE4B,
    HERRAMIENTAS_FASE4B,
    LPS,
    PREVIEW_TTL,
    SCHEMAS_FASE4B,
    FakePluginState,
    FakeTransport,
    _layer,
    _RpcError,
    advertencia_ancho_ambiguo,
    advertencia_columnas,
    advertencia_sombreados_en_hueco,
    advertencia_hatch_no_reconstruible,
    advertencia_ancho_escalado,
    advertencia_ancho_pendiente,
    advertencia_aspecto_fijado,
    advertencia_cambiarian_aspecto,
    advertencia_capas_ajenas,
    advertencia_estilo_cota,
    detalle_ancla_cambiada,
    detalle_ninguna_cara,
    detalle_varias_caras,
    manifest_tools,
    muro_rectangular,
)
from mcp import Client
from test_fase4a import GENERADORAS
from test_server import LOCAL_ORDER, MANIFEST_ORDER, World, _guardar_manifest, run, text_of

import mcp_autocad
from mcp_autocad.client import PipeClient
from mcp_autocad.discovery import Instance
from mcp_autocad.errors import PluginError

RAIZ = Path(__file__).resolve().parents[2]
PROTOCOL_MD = RAIZ / "protocol.md"
HANDLERS = RAIZ / "plugin" / "MCPAutoCAD.Plugin" / "Handlers"
HANDLER_CS = {
    "vano": HANDLERS / "Gen" / "VanoHandler.cs",
    "union_muros": HANDLERS / "Gen" / "UnionMurosHandler.cs",
    "cotas_automaticas": HANDLERS / "Gen" / "CotasAutomaticasHandler.cs",
    "insertar_export": HANDLERS / "Draw" / "InsertarExportHandler.cs",
}
INVALIDO = "[INVALID_PARAMS] Parámetros inválidos: "
UNITS_UNKNOWN = "[UNITS_UNKNOWN] El dibujo no declara unidades (INSUNITS=0)."
GENERADORAS_4B = ["vano", "union_muros", "cotas_automaticas"]
EXPORT = "C:\\copias\\FILE1.dwg"

# Argumentos mínimos válidos sobre el dibujo de la bandera fase4b.
VANO = {"muro": "2B1", "posicion": [2.5, 4.06], "ancho": 0.8, "tipo": "puerta"}
UNION_T = {"muro_a": "2B0", "muro_b": "2B2", "confirmar": True}
UNION_L = {"muro_a": "2B0", "muro_b": "2B3", "confirmar": True}
COTAS = {"handles": ["2B1"], "direccion": "x", "separacion": 0.15, "estilo": "COTA75"}
ARGS: dict[str, dict[str, Any]] = {
    "vano": VANO,
    "union_muros": UNION_T,
    "cotas_automaticas": COTAS,
    "insertar_export": {"ruta": EXPORT, "posicion": [1, 1]},
}


def _world(**kw: Any) -> World:
    return World(18244, **{"fase4b": True, **kw})


def _client(world: World) -> PipeClient:
    return world.client_factory(Instance(pid=18244, pipe="x", user="usuario"))


def _call(world: World, method: str, params: dict[str, Any]) -> dict[str, Any]:
    return _client(world).call(method, params)


def _error(world: World, method: str, params: dict[str, Any]) -> PluginError:
    with pytest.raises(PluginError) as exc:
        _client(world).call(method, params)
    return exc.value


def _state(world: World) -> FakePluginState:
    return world.states[18244]


def _handles(world: World) -> set[str]:
    return {e["handle"] for e in _state(world).entities}


def _entidad(world: World, handle: str) -> dict[str, Any]:
    return next(e for e in _state(world).entities if e["handle"] == handle)


def _agregar(world: World, entidad: dict[str, Any]) -> None:
    _state(world).entities.append(entidad)


def _poligono(handle: str, capa: str, vertices: list[list[float]]) -> dict[str, Any]:
    e = muro_rectangular(handle, capa, 0.0, 0.0, 1.0, 1.0)
    xs, ys = [v[0] for v in vertices], [v[1] for v in vertices]
    e["vertices"] = [[x, y, 0.0] for x, y in vertices]
    e["n_vertices"] = len(vertices)
    e["bbox"] = [[min(xs), min(ys), 0.0], [max(xs), max(ys), 0.0]]
    return e


def _detalle(world: World, method: str, params: dict[str, Any], code: str = "INVALID_PARAMS") -> str:
    err = _error(world, method, params)
    assert err.code == code, str(err)
    return str(err.data.get("detalle"))


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
# Publicación
# ---------------------------------------------------------------------------


def test_manifest_56_con_las_4_de_fase4b_antes_de_auditar_archivo() -> None:
    tools = manifest_tools()
    assert len(tools) == 56
    assert [t["name"] for t in tools] == MANIFEST_ORDER
    assert [t["name"] for t in tools][51:55] == HERRAMIENTAS_FASE4B == ["vano", "union_muros", "cotas_automaticas", "insertar_export"]


def test_33_herramientas_de_escritura() -> None:
    assert sum(1 for t in manifest_tools() if t["escritura"]) == 33


def test_versiones_0_8() -> None:
    assert (mcp_autocad.__version__, mcp_autocad.PROTOCOL_VERSION) == ("0.8.6", "0.8")
    assert (fake_plugin.FAKE_PLUGIN_VERSION, fake_plugin.FAKE_PROTOCOL_VERSION) == ("0.8.0", "0.8")


@pytest.mark.parametrize("name", HERRAMIENTAS_FASE4B)
def test_escritura_true_y_requiere_documento(name: str) -> None:
    t = next(x for x in manifest_tools() if x["name"] == name)
    assert t["escritura"] is True and t["requiere_documento"] is True


@pytest.mark.parametrize("name", HERRAMIENTAS_FASE4B)
def test_descripcion_literal_de_protocol_md(name: str) -> None:
    texto = PROTOCOL_MD.read_text(encoding="utf-8")
    assert f'Descripción del manifest (literal): `"{DESCRIPCIONES_FASE4B[name]}"`' in texto


@pytest.mark.parametrize("name", HERRAMIENTAS_FASE4B)
def test_descripcion_igual_a_la_del_handler(name: str) -> None:
    assert f'"{DESCRIPCIONES_FASE4B[name]}"' in HANDLER_CS[name].read_text(encoding="utf-8")


@pytest.mark.parametrize("name", HERRAMIENTAS_FASE4B)
def test_schema_cerrado_con_sus_obligatorios(name: str) -> None:
    schema = SCHEMAS_FASE4B[name]
    assert schema["type"] == "object" and schema["additionalProperties"] is False
    obligatorios = {
        "vano": ["muro", "posicion", "ancho", "tipo"],
        "union_muros": ["muro_a", "muro_b"],
        "cotas_automaticas": ["handles", "separacion"],
        "insertar_export": ["ruta", "posicion"],
    }
    assert schema["required"] == obligatorios[name]


@pytest.mark.parametrize("name", GENERADORAS_4B)
def test_generadoras_con_comunes_y_sin_aceptar_sin_unidades(name: str) -> None:
    props = SCHEMAS_FASE4B[name]["properties"]
    assert {"preview", "dibujar_preview", "espacio", "forzar"} <= set(props)
    assert "aceptar_sin_unidades" not in props


def test_insertar_export_sin_previsualizacion_y_con_aceptar_sin_unidades() -> None:
    props = SCHEMAS_FASE4B["insertar_export"]["properties"]
    assert "preview" not in props and "dibujar_preview" not in props
    assert "aceptar_sin_unidades" in props and "forzar" in props


def test_tools_list_publica_57_con_schema_del_manifest() -> None:
    world = _world()
    esperado = {t["name"]: t for t in manifest_tools()}

    async def body(c: Client) -> None:
        tools = (await c.list_tools()).tools
        assert [t.name for t in tools] == MANIFEST_ORDER + LOCAL_ORDER and len(tools) == 61
        for t in tools:
            if t.name in HERRAMIENTAS_FASE4B:
                assert t.description == esperado[t.name]["description"] == DESCRIPCIONES_FASE4B[t.name]
                assert t.input_schema == esperado[t.name]["inputSchema"]

    run(world.server(), body)


def test_manifest_en_disco_publica_55_sin_instancia(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.json"
    _guardar_manifest(_world(), manifest)
    guardado = json.loads(manifest.read_text(encoding="utf-8"))
    assert (guardado["protocol_version"], guardado["plugin_version"], len(guardado["tools"])) == ("0.8", "0.8.0", 56)

    async def body(c: Client) -> None:
        assert len((await c.list_tools()).tools) == 61

    world = World()
    run(world.server(manifest_path=manifest), body)


def test_schema_insertar_bloque_con_propiedades_dinamicas() -> None:
    schema = {t["name"]: t for t in manifest_tools()}["insertar_bloque"]
    p = schema["inputSchema"]["properties"]["propiedades_dinamicas"]
    assert (p["minProperties"], p["maxProperties"]) == (1, 20)
    assert "Puerta Dinamica LM, PuertaBaño, VENTANA01 y TANQUE 1000 LTS" in schema["description"]


def test_schema_normalizar_capas_con_conservar_aspecto_literal() -> None:
    schema = {t["name"]: t for t in manifest_tools()}["normalizar_capas"]["inputSchema"]
    p = schema["properties"]["conservar_aspecto"]
    assert p["default"] is False and p["description"] == CONSERVAR_ASPECTO_DESCRIPCION
    assert f'`"{CONSERVAR_ASPECTO_DESCRIPCION}"`' in PROTOCOL_MD.read_text(encoding="utf-8")


# ---------------------------------------------------------------------------
# G6 ampliada: handles_borrados
# ---------------------------------------------------------------------------


def test_claves_g6_con_handles_borrados_tras_handles() -> None:
    assert CLAVES_G6[CLAVES_G6.index("handles") + 1] == "handles_borrados"


@pytest.mark.parametrize("name", list(GENERADORAS))
@pytest.mark.parametrize("preview", [False, True])
def test_handles_borrados_vacio_en_las_generadoras_de_4a(name: str, preview: bool) -> None:
    world = World(18244)
    r = _call(world, name, {**GENERADORAS[name][0], "preview": preview})
    assert list(r) == CLAVES_G6 and r["handles_borrados"] == []


@pytest.mark.parametrize("name", GENERADORAS_4B)
def test_forma_g6_en_las_generadoras_de_4b(name: str) -> None:
    r = _call(_world(), name, ARGS[name])
    assert list(r)[: len(CLAVES_G6)] == CLAVES_G6
    assert r["preview"] is False and r["preview_id"] is None and r["numeros"]["n_entidades"] == len(r["entidades"])


# ---------------------------------------------------------------------------
# vano: respuesta
# ---------------------------------------------------------------------------


def test_vano_parte_el_rectangulo_en_dos_contornos_con_el_hueco() -> None:
    world = _world()

    async def body(c: Client) -> None:
        r = await _ok(c, "vano", VANO)
        h = r["numeros"]["hueco"]
        assert h["centro"] == pytest.approx([2.5, 4.06])
        assert h["a1"] == pytest.approx([2.1, 4.12]) and h["a2"] == pytest.approx([2.1, 4.0])
        assert h["b1"] == pytest.approx([2.9, 4.12]) and h["b2"] == pytest.approx([2.9, 4.0])
        assert (h["ancho"], h["espesor"], h["direccion"]) == pytest.approx((0.8, 0.12, 0.0))
        assert (h["distancia_desde_inicio"], h["longitud_tramo"]) == pytest.approx((2.5, 5.0))
        lados = r["numeros"]["contornos"]
        assert [x["lado"] for x in lados] == ["inicio", "fin"]
        assert all(x["n_vertices"] == 4 for x in lados)
        assert [x["area"] for x in lados] == pytest.approx([2.1 * 0.12, 2.1 * 0.12])
        assert [x["longitud"] for x in lados] == pytest.approx([4.44, 4.44])

    run(world.server(), body)


def test_vano_definitivo_borra_muro_y_hatch_y_crea_contornos_hatches_e_insert() -> None:
    world = _world()
    r = _call(world, "vano", VANO)
    assert r["handles_borrados"] == ["2B1", "2BA"]
    assert [e["tipo"] for e in r["entidades"]] == ["LWPOLYLINE", "LWPOLYLINE", "HATCH", "HATCH", "INSERT"]
    assert r["numeros"]["n_hatch_rehechos"] == 1 and r["numeros"]["n_entidades"] == 5
    assert "2B1" not in _handles(world) and "2BA" not in _handles(world)
    assert set(r["handles"]) <= _handles(world) and _state(world).pasos_deshacer == 1


def test_vano_contornos_conservan_la_capa_del_muro_e_insert_va_a_puertas() -> None:
    r = _call(_world(), "vano", {**VANO, "muro": "2B0", "posicion": [2.5, 0.07], "confirmar": True})
    capas = {e["tipo"]: e["capa"] for e in r["entidades"]}
    assert capas == {"LWPOLYLINE": "MUROS", "INSERT": "PUERTAS"}
    assert r["numeros"]["muro_original"] == {"handle": "2B0", "capa": "MUROS", "area": pytest.approx(0.75), "n_vertices": 4, "cara_opuesta": None}


def test_vano_hatches_rehechos_con_el_patron_sobre_cada_contorno() -> None:
    world = _world()
    r = _call(world, "vano", VANO)
    h1, h2 = (e for e in r["entidades"] if e["tipo"] == "HATCH")
    assert (h1["patron"], h2["patron"]) == ("ANSI31", "ANSI31")
    assert h1["contorno_handles"] == [r["entidades"][0]["handle"]] and h2["contorno_handles"] == [r["entidades"][1]["handle"]]
    e = _entidad(world, h1["handle"])
    assert (e["_escala"], e["_angulo"], e["capa"]) == (0.05, 45.0, "MCP-DIBUJO")


ASPECTO_HATCH = {"_color": 3, "_transparencia": 40.0, "_grosor": 25, "_tipo_linea": "HIDDEN"}


def test_vano_hatch_rehecho_conserva_el_aspecto_propio() -> None:
    world = _world()
    _entidad(world, "2BA").update(ASPECTO_HATCH)
    r = _call(world, "vano", VANO)
    nuevos = [e for e in r["entidades"] if e["tipo"] == "HATCH"]
    assert len(nuevos) == 2
    for n in nuevos:
        e = _entidad(world, n["handle"])
        assert {k: e[k] for k in ASPECTO_HATCH} == ASPECTO_HATCH


def test_union_hatch_rehecho_conserva_el_aspecto_propio() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 5.0, 4.0, 5.12, 7.0))
    _entidad(world, "2BA").update(ASPECTO_HATCH)
    r = _call(world, "union_muros", {"muro_a": "2B1", "muro_b": "2C1"})
    n = next(e for e in r["entidades"] if e["tipo"] == "HATCH")
    assert _entidad(world, n["handle"])["_color"] == 3 and _entidad(world, n["handle"])["_tipo_linea"] == "HIDDEN"


def test_vano_hatch_sin_aspecto_propio_sigue_bylayer() -> None:
    world = _world()
    r = _call(world, "vano", VANO)
    e = _entidad(world, next(n["handle"] for n in r["entidades"] if n["tipo"] == "HATCH"))
    assert not any(k in e for k in ASPECTO_HATCH)


def test_vano_preview_no_aplica_el_aspecto_y_confirmar_si() -> None:
    world = _world()
    _entidad(world, "2BA").update(ASPECTO_HATCH)
    p = _call(world, "vano", {**VANO, "preview": True})
    h = next(n["handle"] for n in p["entidades"] if n["tipo"] == "HATCH")
    assert not any(k in _entidad(world, h) for k in ASPECTO_HATCH)
    c = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    h = next(n["handle"] for n in c["entidades"] if n["tipo"] == "HATCH")
    assert _entidad(world, h)["_color"] == 3


def test_hatch_conserva_aspecto_en_el_plugin() -> None:
    # El simulador no ve el plugin: esta prueba mira la fuente de DrawHatch.
    fuente = (HANDLERS / "Gen" / "GeometryDrawer.cs").read_text(encoding="utf-8")
    for campo in ("e.Color = aspecto.Color", "e.Transparency = aspecto.Transparencia", "e.LineWeight = aspecto.Grosor", "e.Linetype = aspecto.TipoLinea"):
        assert campo in fuente


def test_vano_hatch_con_varios_bucles_sustituye_el_del_muro_y_conserva_los_demas() -> None:
    world = _world()
    _entidad(world, "2BB").update(ASPECTO_HATCH)
    r = _call(world, "vano", {**VANO, "muro": "2B4", "posicion": [42.5, 0.07]})
    assert not any("sombreado" in a for a in r["advertencias"])
    assert r["handles_borrados"] == ["2B4", "2BB"] and r["numeros"]["n_hatch_rehechos"] == 1
    nuevos = [e for e in r["entidades"] if e["tipo"] == "HATCH"]
    assert len(nuevos) == 1
    h = _entidad(world, nuevos[0]["handle"])
    assert h["_contorno"] == [e["handle"] for e in r["entidades"] if e["tipo"] == "LWPOLYLINE"] + ["2B5"]
    assert h["asociativo"] is True and h["_color"] == 3 and h["capa"] == "MCP-DIBUJO"


def test_vano_hatch_con_varios_bucles_en_preview_no_es_asociativo_ni_toca_el_original() -> None:
    world = _world()
    p = _call(world, "vano", {**VANO, "muro": "2B4", "posicion": [42.5, 0.07], "preview": True})
    h = _entidad(world, next(e["handle"] for e in p["entidades"] if e["tipo"] == "HATCH"))
    assert h["asociativo"] is False and "2BB" in _handles(world)
    c = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert "2BB" in c["handles_borrados"]


def test_vano_bucle_conservado_cambiado_no_confirma() -> None:
    world = _world()
    p = _call(world, "vano", {**VANO, "muro": "2B4", "posicion": [42.5, 0.07], "preview": True, "dibujar_preview": False})
    _call(world, "mover", {"handles": ["2B5"], "desplazamiento": [0, 0.5], "confirmar": True})
    assert _detalle(world, "confirmar", {"preview_id": p["preview_id"]}) == detalle_ancla_cambiada("2B5")


def test_union_hatch_con_varios_bucles_conserva_los_demas() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 45.0, 0.0, 47.0, 0.15))
    r = _call(world, "union_muros", {"muro_a": "2B4", "muro_b": "2C1"})
    assert r["handles_borrados"] == ["2B4", "2C1", "2BB"]
    h = _entidad(world, next(e["handle"] for e in r["entidades"] if e["tipo"] == "HATCH"))
    assert h["_contorno"][-1] == "2B5" and len(h["_contorno"]) == 2


def test_vano_hatch_con_bucle_mixto_no_se_rehace_y_avisa() -> None:
    world = _world()
    _entidad(world, "2BB")["_bucles"] = [["2B4", "2B5"]]
    r = _call(world, "vano", {**VANO, "muro": "2B4", "posicion": [42.5, 0.07]})
    assert advertencia_hatch_no_reconstruible("2BB") in r["advertencias"]
    assert r["handles_borrados"] == ["2B4"] and "2BB" in _handles(world) and r["numeros"]["n_hatch_rehechos"] == 0


def test_vano_inserta_puerta_dinamica_por_defecto_con_la_propiedad_de_ancho() -> None:
    r = _call(_world(), "vano", VANO)
    b = r["numeros"]["bloque"]
    assert b["nombre"] == "Puerta Dinamica LM" and b["catalogo"]["ancho_nominal"] == 0.9
    assert (b["ancho_por"], b["propiedad_ancho"], b["factor_ancho"]) == ("propiedad", "DistanceGeneralVano", None)
    assert b["escala"] == [1.0, 1.0, 1.0]
    distancia = next(p for p in r["propiedades_dinamicas"] if p["nombre"] == "DistanceGeneralVano")
    assert distancia["valor"] == 0.8 and distancia["unidades"] == "distancia" and distancia["solo_lectura"] is False


def test_vano_entidades_insert_describe_el_insert_sin_campos_de_la_raiz() -> None:
    r = _call(_world(), "vano", VANO)
    insert = r["entidades"][-1]
    assert insert["bloque"] == "Puerta Dinamica LM" and insert["dinamico"] is True
    assert insert["propiedades_aplicadas"] == ["DistanceGeneralVano"]
    assert "capas_importadas" not in insert and "propiedades_dinamicas" not in insert
    assert "propiedades_dinamicas" in r and "capas_importadas" in r


def test_handles_borrados_es_obligatorio_en_los_schemas_del_plugin() -> None:
    gen = (HANDLERS / "Gen" / "GenSchema.cs").read_text(encoding="utf-8")
    confirmar = (HANDLERS / "Gen" / "ConfirmarHandler.cs").read_text(encoding="utf-8")
    for fuente in (gen, confirmar):
        requeridos = fuente[fuente.index('""required"": [""pid""'):]
        assert '""handles_borrados""' in requeridos[: requeridos.index("]")]


def test_handles_borrados_sale_del_unico_camino_de_respuesta_del_plugin() -> None:
    fuente = (HANDLERS / "Gen" / "GeneratorBase.cs").read_text(encoding="utf-8")
    assert fuente.count('["handles_borrados"] = drawn.HandlesBorrados') == 1 and fuente.count("new Dictionary<string, object?>(StringComparer.Ordinal)\n            {\n                [\"pid\"]") == 1


@pytest.mark.parametrize("name", GENERADORAS_4B)
@pytest.mark.parametrize("dibujar", [True, False])
def test_handles_borrados_siempre_presente_en_4b_con_preview_y_confirmar(name: str, dibujar: bool) -> None:
    world = _world()
    p = _call(world, name, {**ARGS[name], "preview": True, "dibujar_preview": dibujar})
    assert p["handles_borrados"] == []
    c = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert isinstance(c["handles_borrados"], list)
    assert (c["handles_borrados"] == []) is (name == "cotas_automaticas")


def test_vano_plugin_no_pone_campos_de_la_raiz_en_la_entrada_insert() -> None:
    # El simulador no ve el plugin: esta prueba mira su fuente, para que los campos no vuelvan a la entrada INSERT.
    fuente = (HANDLERS / "Gen" / "GeometryDrawer.Blocks.cs").read_text(encoding="utf-8")
    registro = fuente[fuente.index('Register(br, "INSERT"'):]
    registro = registro[: registro.index("});")]
    assert '["bloque"]' in registro
    assert "capas_importadas" not in registro and "propiedades_dinamicas" not in registro


def test_vano_raiz_igual_en_la_respuesta_directa_y_en_confirmar() -> None:
    directa = _call(_world(), "vano", VANO)
    world = _world()
    p = _call(world, "vano", {**VANO, "dibujar_preview": False, "preview": True})
    c = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert set(directa) <= set(c)
    assert set(c) - set(directa) == {"confirmado", "n_preview_borradas", "herramienta"}
    assert c["propiedades_dinamicas"] == directa["propiedades_dinamicas"] != []
    assert c["capas_importadas"] == directa["capas_importadas"]
    insert = c["entidades"][-1]
    assert "capas_importadas" not in insert and "propiedades_dinamicas" not in insert


def test_vano_capas_importadas_solo_en_la_llamada_que_importa() -> None:
    world = _world()
    p = _call(world, "vano", {**VANO, "preview": True})
    assert p["capas_importadas"] == [] and p["propiedades_dinamicas"] != []
    assert "capas_importadas" not in p["entidades"][-1] and "propiedades_dinamicas" not in p["entidades"][-1]
    c = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert "capas_importadas" not in c and c["propiedades_dinamicas"] == p["propiedades_dinamicas"]


def test_vano_preview_sin_dibujar_no_tiene_insert_ni_propiedades() -> None:
    r = _call(_world(), "vano", {**VANO, "dibujar_preview": False, "preview": True})
    assert r["propiedades_dinamicas"] == [] and "capas_importadas" not in r and r["entidades"] == []


def test_vano_importa_la_definicion_una_sola_vez() -> None:
    world = _world()
    r1 = _call(world, "vano", VANO)
    assert r1["capas_importadas"] == []
    nombres = [d["nombre"] for d in _state(world).block_defs]
    assert nombres.count("Puerta Dinamica LM") == 1
    r2 = _call(world, "vano", {**VANO, "muro": "2B4", "posicion": [42.5, 0.07]})
    assert "capas_importadas" not in r2
    assert [d["nombre"] for d in _state(world).block_defs].count("Puerta Dinamica LM") == 1


def test_vano_ventana_por_defecto_en_capa_ventanas_sin_aviso_de_espesor() -> None:
    world = _world()
    r = _call(world, "vano", {**VANO, "tipo": "ventana"})
    assert r["numeros"]["bloque"]["nombre"] == "VENTANA01" and r["entidades"][-1]["capa"] == "ventanas"
    assert "ventanas" in r["capas_creadas"]
    assert not any("está dibujado para muros" in a for a in r["advertencias"])


def test_vano_ventana_rotacion_270_como_el_plano() -> None:
    b = _call(_world(), "vano", {**VANO, "tipo": "ventana"})["numeros"]["bloque"]
    assert b["rotacion"] == pytest.approx(270.0) and b["escala"] == [1.0, 1.0, 1.0] and b["espejo"] is False


def test_vano_capas_importadas_del_bloque() -> None:
    r = _call(_world(), "vano", {**VANO, "tipo": "ventana", "capa": "MCP-DIBUJO"})
    assert r["entidades"][-1]["capa"] == "MCP-DIBUJO" and r["capas_importadas"] == ["ventanas"]


def test_vano_hueco_sin_bloque() -> None:
    r = _call(_world(), "vano", {**VANO, "tipo": "hueco"})
    assert r["numeros"]["bloque"] is None and r["propiedades_dinamicas"] == []
    assert "INSERT" not in [e["tipo"] for e in r["entidades"]] and "capas_importadas" not in r


def test_vano_hueco_con_capa_avisa() -> None:
    r = _call(_world(), "vano", {**VANO, "tipo": "hueco", "capa": "MUROS"})
    assert "capa: no aplica con tipo hueco; los contornos conservan la capa del muro." in r["advertencias"]


def test_vano_no_dinamico_escala_con_advertencia() -> None:
    r = _call(_world(), "vano", {**VANO, "bloque": "PuertaBaño", "muro": "2B0", "posicion": [2.5, 0.07], "confirmar": True})
    b = r["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"]) == ("escala", None)
    assert b["factor_ancho"] == pytest.approx(0.8 / 0.65) and b["escala"][0] == pytest.approx(0.8 / 0.65)
    assert (
        "vano: el bloque 'PuertaBaño' no es dinámico (o no se encontró su propiedad de ancho); se escaló 1.231 a lo largo "
        "del vano: las jambas y la hoja quedan deformadas en proporción."
    ) in r["advertencias"]
    assert r["propiedades_dinamicas"] == []


def _biblioteca_con_propiedades(world: World, *extra: dict[str, Any]) -> None:
    """Sustituye la puerta de la biblioteca de este mundo por una copia con más propiedades."""
    biblioteca = copy.deepcopy(_state(world).biblioteca)
    biblioteca["Puerta Dinamica LM"]["propiedades"].extend(extra)
    _state(world).biblioteca = biblioteca


SEGUNDA_CANDIDATA = {
    "nombre": "Distancia2", "valor": 0.9, "solo_lectura": False, "unidades": "distancia", "valores_permitidos": None, "descripcion": "",
}


def _sin_nombre_configurado(monkeypatch: pytest.MonkeyPatch) -> None:
    """Catálogo como antes del gate: propiedad_ancho null en la puerta."""
    catalogo = copy.deepcopy(LPS["biblioteca_vanos"])
    catalogo["Puerta Dinamica LM"]["propiedad_ancho"] = None
    monkeypatch.setitem(LPS, "biblioteca_vanos", catalogo)


def _una_sola_candidata(world: World) -> None:
    biblioteca = copy.deepcopy(_state(world).biblioteca)
    props = biblioteca["Puerta Dinamica LM"]["propiedades"]
    props[:] = [p for p in props if p["nombre"] != "DistanceHojaDoble"]
    _state(world).biblioteca = biblioteca


def test_vano_biblioteca_sin_nombre_deduce_la_propiedad_en_la_referencia_creada(monkeypatch: pytest.MonkeyPatch) -> None:
    _sin_nombre_configurado(monkeypatch)
    world = _world()
    _una_sola_candidata(world)
    assert not any(d["nombre"] == "Puerta Dinamica LM" for d in _state(world).block_defs)
    r = _call(world, "vano", VANO)
    b = r["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"], b["factor_ancho"]) == ("propiedad", "DistanceGeneralVano", None)
    assert b["escala"] == [1.0, 1.0, 1.0] and not any("se escaló" in a for a in r["advertencias"])


def test_vano_biblioteca_preview_sin_dibujar_deja_el_ancho_pendiente_y_confirmar_lo_decide(monkeypatch: pytest.MonkeyPatch) -> None:
    _sin_nombre_configurado(monkeypatch)
    world = _world()
    _una_sola_candidata(world)
    p = _call(world, "vano", {**VANO, "preview": True, "dibujar_preview": False})
    b = p["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"], b["factor_ancho"]) == ("pendiente", None, None)
    assert advertencia_ancho_pendiente("Puerta Dinamica LM") in p["advertencias"]
    c = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert c["numeros"]["bloque"]["ancho_por"] == "propiedad" and c["numeros"]["bloque"]["propiedad_ancho"] == "DistanceGeneralVano"
    assert advertencia_ancho_pendiente("Puerta Dinamica LM") not in c["advertencias"]


def test_vano_biblioteca_sin_candidatas_cae_a_escala_al_dibujar(monkeypatch: pytest.MonkeyPatch) -> None:
    _sin_nombre_configurado(monkeypatch)
    world = _world()
    biblioteca = copy.deepcopy(_state(world).biblioteca)
    biblioteca["Puerta Dinamica LM"]["propiedades"][0]["valor"] = 0.5
    biblioteca["Puerta Dinamica LM"]["propiedades"][1]["valor"] = 0.5
    _state(world).biblioteca = biblioteca
    r = _call(world, "vano", VANO)
    b = r["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"]) == ("escala", None) and b["factor_ancho"] == pytest.approx(0.8 / 0.9)
    assert b["escala"][0] == pytest.approx(0.8 / 0.9)
    assert advertencia_ancho_escalado("Puerta Dinamica LM", 0.8 / 0.9) in r["advertencias"]


def test_vano_varias_candidatas_no_elige_y_lista_los_nombres(monkeypatch: pytest.MonkeyPatch) -> None:
    _sin_nombre_configurado(monkeypatch)
    r = _call(_world(), "vano", VANO)
    b = r["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"]) == ("escala", None) and b["factor_ancho"] == pytest.approx(0.8 / 0.9)
    aviso = advertencia_ancho_ambiguo("Puerta Dinamica LM", ["DistanceGeneralVano", "DistanceHojaDoble"], 0.8 / 0.9)
    assert aviso in r["advertencias"] and not any("no es dinámico" in a for a in r["advertencias"])
    assert "DistanceGeneralVano, DistanceHojaDoble" in aviso and "Fije propiedad_ancho en lps.json biblioteca_vanos." in aviso
    # §5G.1, gate 0.7: al caer a escala la entrada INSERT sigue diciendo que el bloque es dinámico.
    assert r["entidades"][-1]["dinamico"] is True


def test_vano_varias_candidatas_con_definicion_ya_en_el_dibujo_tampoco_elige(monkeypatch: pytest.MonkeyPatch) -> None:
    _sin_nombre_configurado(monkeypatch)
    world = _world()
    _call(world, "vano", VANO)
    r = _call(world, "vano", {**VANO, "muro": "2B4", "posicion": [42.5, 0.07]})
    assert "capas_importadas" not in r and r["numeros"]["bloque"]["ancho_por"] == "escala"
    assert advertencia_ancho_ambiguo("Puerta Dinamica LM", ["DistanceGeneralVano", "DistanceHojaDoble"], 0.8 / 0.9) in r["advertencias"]


def test_vano_varias_candidatas_con_nombre_configurado_usa_el_nombre(monkeypatch: pytest.MonkeyPatch) -> None:
    catalogo = copy.deepcopy(LPS["biblioteca_vanos"])
    catalogo["Puerta Dinamica LM"]["propiedad_ancho"] = "Distancia2"
    monkeypatch.setitem(LPS, "biblioteca_vanos", catalogo)
    world = _world()
    _biblioteca_con_propiedades(world, SEGUNDA_CANDIDATA)
    b = _call(world, "vano", VANO)["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"]) == ("propiedad", "Distancia2")


@pytest.mark.parametrize("valor", [5, {}, ["x"]])
def test_propiedades_dinamicas_mal_formadas_texto_del_plugin(valor: Any) -> None:
    args = {"nombre": "NoExiste", "posicion": [0, 5], "propiedades_dinamicas": valor}
    assert _detalle(_world(), "insertar_bloque", args) == "propiedades_dinamicas: debe ser un objeto con 1 a 20 propiedades"


def test_vano_propiedad_ancho_de_lps_json_tiene_prioridad(monkeypatch: pytest.MonkeyPatch) -> None:
    catalogo = copy.deepcopy(LPS["biblioteca_vanos"])
    catalogo["Puerta Dinamica LM"]["propiedad_ancho"] = "distancegeneralvano"
    monkeypatch.setitem(LPS, "biblioteca_vanos", catalogo)
    b = _call(_world(), "vano", VANO)["numeros"]["bloque"]
    assert b["propiedad_ancho"] == "distancegeneralvano" and b["ancho_por"] == "propiedad"


def _catalogo_con_propiedad(monkeypatch: pytest.MonkeyPatch, nombre: str) -> None:
    catalogo = copy.deepcopy(LPS["biblioteca_vanos"])
    catalogo["Puerta Dinamica LM"]["propiedad_ancho"] = nombre
    monkeypatch.setitem(LPS, "biblioteca_vanos", catalogo)


DESCONOCIDA_NOMBRE = (
    "propiedades_dinamicas: propiedad desconocida 'NoExiste' (propiedades del bloque: DistanceGeneralVano, DistanceHojaDoble, Visibilidad1, Origin)"
)


def test_vano_propiedad_ancho_erronea_no_deduce_y_rechaza(monkeypatch: pytest.MonkeyPatch) -> None:
    _catalogo_con_propiedad(monkeypatch, "NoExiste")
    world = _world()
    antes = _handles(world)
    assert _detalle(world, "vano", VANO) == DESCONOCIDA_NOMBRE
    assert _handles(world) == antes and _state(world).pasos_deshacer == 0


def test_vano_propiedad_ancho_de_solo_lectura_rechaza(monkeypatch: pytest.MonkeyPatch) -> None:
    _catalogo_con_propiedad(monkeypatch, "Origin")
    assert _detalle(_world(), "vano", VANO) == "propiedades_dinamicas.Origin: es de solo lectura"


def test_vano_propiedad_ancho_erronea_sin_dibujar_pasa_y_la_rechaza_confirmar(monkeypatch: pytest.MonkeyPatch) -> None:
    _catalogo_con_propiedad(monkeypatch, "NoExiste")
    world = _world()
    p = _call(world, "vano", {**VANO, "preview": True, "dibujar_preview": False})
    b = p["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"]) == ("propiedad", "NoExiste")
    antes = _handles(world)
    assert _detalle(world, "confirmar", {"preview_id": p["preview_id"]}) == DESCONOCIDA_NOMBRE
    assert _handles(world) == antes


def test_vano_propiedades_del_cliente_sin_dibujar_no_se_validan_hasta_confirmar() -> None:
    world = _world()
    args = {**VANO, "propiedades_dinamicas": {"Nada": 1}}
    p = _call(world, "vano", {**args, "preview": True, "dibujar_preview": False})
    assert p["preview_id"] is not None and p["propiedades_dinamicas"] == []
    detalle = _detalle(world, "confirmar", {"preview_id": p["preview_id"]})
    assert detalle.startswith("propiedades_dinamicas: propiedad desconocida 'Nada'")


def test_vano_propiedades_del_cliente_con_preview_dibujada_se_validan_y_no_registran_nada() -> None:
    world = _world()
    antes = _handles(world)
    detalle = _detalle(world, "vano", {**VANO, "propiedades_dinamicas": {"Nada": 1}, "preview": True})
    assert detalle.startswith("propiedades_dinamicas: propiedad desconocida 'Nada'")
    assert _handles(world) == antes and _state(world).pasos_deshacer == 0


def test_vano_no_dinamico_con_propiedades_se_rechaza_tambien_sin_dibujar() -> None:
    args = {
        **VANO, "bloque": "PuertaBaño", "muro": "2B0", "posicion": [2.5, 0.07], "confirmar": True,
        "propiedades_dinamicas": {"A": 1}, "preview": True, "dibujar_preview": False,
    }
    assert _detalle(_world(), "vano", args) == "propiedades_dinamicas: el bloque 'PuertaBaño' no es dinámico"


@pytest.mark.parametrize(
    ("valor", "detalle"),
    [
        (5, "propiedades_dinamicas: debe ser un objeto con 1 a 20 propiedades"),
        ({}, "propiedades_dinamicas: debe ser un objeto con 1 a 20 propiedades"),
        ({"A": True}, "propiedades_dinamicas.A: debe ser un número o una cadena"),
        ({"A": 1, "a": 2}, "propiedades_dinamicas: propiedad repetida 'a'"),
    ],
)
def test_vano_forma_de_propiedades_al_leer_los_parametros(valor: Any, detalle: str) -> None:
    # Muro inexistente y sin dibujar: la forma se rechaza antes de mirar el dibujo.
    args = {**VANO, "muro": "ABCDE", "propiedades_dinamicas": valor, "preview": True, "dibujar_preview": False}
    assert _detalle(_world(), "vano", args) == detalle


def test_vano_nombre_en_blanco_se_rechaza_al_leer_los_parametros() -> None:
    args = {**VANO, "muro": "ABCDE", "propiedades_dinamicas": {"  ": 1}, "preview": True, "dibujar_preview": False}
    assert _detalle(_world(), "vano", args) == "propiedades_dinamicas: propiedad con nombre vacío"
    fuente = (HANDLERS / "Gen" / "VanoHandler.cs").read_text(encoding="utf-8")
    assert '"propiedades_dinamicas: propiedad con nombre vacío"' in fuente


def _puerta_con_propiedad_extra(**campos: Any) -> dict[str, Any]:
    return {"solo_lectura": False, "valores_permitidos": None, "descripcion": "", **campos}


@pytest.mark.parametrize(
    ("extra", "valor", "detalle"),
    [
        # Decide el tipo del valor actual, aunque las unidades sugieran lo contrario.
        ({"nombre": "Lista", "valor": 2, "unidades": "ninguna"}, "a", "propiedades_dinamicas.Lista: debe ser un número"),
        ({"nombre": "Rotulo", "valor": "x", "unidades": "distancia"}, 3, "propiedades_dinamicas.Rotulo: debe ser una cadena"),
    ],
)
def test_vano_tipo_del_valor_lo_decide_el_valor_actual(extra: dict[str, Any], valor: Any, detalle: str) -> None:
    world = _world()
    _biblioteca_con_propiedades(world, _puerta_con_propiedad_extra(**extra))
    assert _detalle(world, "vano", {**VANO, "propiedades_dinamicas": {extra["nombre"]: valor}}) == detalle


def test_vano_propiedad_numerica_sin_unidades_admite_un_numero() -> None:
    world = _world()
    _biblioteca_con_propiedades(world, _puerta_con_propiedad_extra(nombre="Lista", valor=2, unidades="ninguna"))
    r = _call(world, "vano", {**VANO, "propiedades_dinamicas": {"Lista": 3}})
    assert next(p for p in r["propiedades_dinamicas"] if p["nombre"] == "Lista")["valor"] == 3.0


def test_vano_rechazo_tras_importar_queda_en_el_log_del_plugin() -> None:
    fuente = (HANDLERS / "Gen" / "GeometryDrawer.Blocks.cs").read_text(encoding="utf-8")
    assert "catch (ToolException ex) when (p.DesdeBiblioteca)" in fuente
    assert "la definición podría quedar en el dibujo" in fuente


def test_vano_camino_pendiente_captura_cualquier_excepcion_del_plugin() -> None:
    fuente = (HANDLERS / "Gen" / "GeometryDrawer.Blocks.cs").read_text(encoding="utf-8")
    pendiente = fuente[fuente.index("ResolvePendingWidth(\n"):]
    pendiente = pendiente[: pendiente.index("if (encontrado?.Propiedad != null)")]
    assert "catch (Exception ex)" in pendiente and "PluginLog.Warn" in pendiente


def test_insertar_bloque_forma_de_propiedades_antes_de_mirar_el_dibujo() -> None:
    args = {"nombre": "NoExiste", "posicion": [0, 5], "propiedades_dinamicas": {" ": 1}}
    assert _detalle(_world(), "insertar_bloque", args) == "propiedades_dinamicas: propiedad con nombre vacío"


def test_vano_espesor_distinto_avisa() -> None:
    r = _call(_world(), "vano", VANO)
    assert (
        "vano: el bloque 'Puerta Dinamica LM' está dibujado para muros de 0.13; el muro mide 0.12; las jambas no cubren "
        "todo el espesor."
    ) in r["advertencias"]


@pytest.mark.parametrize(
    ("cara", "bisagra", "posicion", "rotacion", "espejo"),
    [
        ("izquierda", "inicio", [2.1, 4.0], 0.0, False),
        ("izquierda", "fin", [2.9, 4.0], 180.0, True),
        ("derecha", "inicio", [2.1, 4.12], 0.0, True),
        ("derecha", "fin", [2.9, 4.12], 180.0, False),
    ],
)
def test_vano_cara_y_bisagra(cara: str, bisagra: str, posicion: list[float], rotacion: float, espejo: bool) -> None:
    b = _call(_world(), "vano", {**VANO, "cara_apertura": cara, "bisagra": bisagra})["numeros"]["bloque"]
    assert b["posicion"][:2] == pytest.approx(posicion)
    assert b["rotacion"] == pytest.approx(rotacion) and b["espejo"] is espejo


def test_vano_caja_del_insert_incluye_la_hoja_hacia_la_cara_de_apertura() -> None:
    r = _call(_world(), "vano", {**VANO, "tipo": "hueco"})
    sin_hoja = r["numeros"]["bbox"]
    izquierda = _call(_world(), "vano", VANO)["entidades"][-1]["bbox"]
    derecha = _call(_world(), "vano", {**VANO, "cara_apertura": "derecha"})["entidades"][-1]["bbox"]
    assert sin_hoja[1][1] == pytest.approx(4.12)
    assert izquierda[1][1] == pytest.approx(4.92) and derecha[0][1] == pytest.approx(3.2)


@pytest.mark.parametrize(
    ("posicion", "centro_x", "distancia"),
    [
        ({"distancia": 1.5}, 1.5, 1.5),
        ({"distancia": 1.5, "desde": "inicio"}, 1.5, 1.5),
        ({"distancia": 1.5, "desde": "fin"}, 3.5, 3.5),
    ],
)
def test_vano_posicion_por_distancia(posicion: dict[str, Any], centro_x: float, distancia: float) -> None:
    h = _call(_world(), "vano", {**VANO, "posicion": posicion})["numeros"]["hueco"]
    assert h["centro"] == pytest.approx([centro_x, 4.06]) and h["distancia_desde_inicio"] == pytest.approx(distancia)


def test_vano_posicion_puntoref_por_handle_se_proyecta_y_se_resuelve() -> None:
    world = _world()
    r = _call(world, "vano", {**VANO, "posicion": {"handle": "2BD", "punto": "centro"}, "tipo": "hueco"})
    assert r["numeros"]["hueco"]["centro"] == pytest.approx([3.0, 4.06])
    assert r["puntos_resueltos"][0]["punto"][:2] == pytest.approx([3.0, 6.0])


def test_vano_propiedades_dinamicas_extra_se_aplican() -> None:
    r = _call(_world(), "vano", {**VANO, "propiedades_dinamicas": {"visibilidad1": "Cerrada"}})
    assert next(p for p in r["propiedades_dinamicas"] if p["nombre"] == "Visibilidad1")["valor"] == "Cerrada"


def test_vano_ancho_en_propiedades_dinamicas_se_ignora_con_aviso() -> None:
    r = _call(_world(), "vano", {**VANO, "propiedades_dinamicas": {"DistanceGeneralVano": 3}})
    assert next(p for p in r["propiedades_dinamicas"] if p["nombre"] == "DistanceGeneralVano")["valor"] == 0.8
    assert "propiedades_dinamicas: el ancho lo fija la herramienta; se ignoró el valor de 'DistanceGeneralVano'." in r["advertencias"]


def test_vano_en_contorno_en_l_mantiene_los_vertices_del_otro_lado() -> None:
    r = _call(_world(), "vano", {**VANO, "muro": "2B8", "posicion": [32.0, 0.07], "tipo": "hueco"})
    lados = {x["lado"]: x["n_vertices"] for x in r["numeros"]["contornos"]}
    assert lados == {"inicio": 4, "fin": 6}


def test_vano_primer_igual_ultimo_vertice_colapsa_y_avisa() -> None:
    world = _world()
    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [[0, 9], [5, 9], [5, 9.15], [0, 9.15], [0, 9]]))
    _entidad(world, "2C1")["cerrada"] = False
    r = _call(world, "vano", {**VANO, "muro": "2C1", "posicion": [2.5, 9.07], "tipo": "hueco"})
    assert any("2C1" in a and "duplicad" in a for a in r["advertencias"])


# ---------------------------------------------------------------------------
# vano: errores exactos de §6
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("cambio", "detalle"),
    [
        ({"muro": "2BD", "confirmar": True}, "muro: la entidad 2BD no es una polilínea cerrada (CIRCLE)"),
        ({"muro": "2B6", "confirmar": True}, "muro: la entidad 2B6 no es una polilínea cerrada (LWPOLYLINE)"),
        ({"muro": "2B7"}, "muro: la polilínea 2B7 tiene arcos; solo se admiten contornos rectos"),
        ({"posicion": {"distancia": 6}}, "posicion: la distancia 6 se sale del tramo (longitud 5)"),
        ({"ancho": 0}, "ancho: debe ser un número mayor que 0"),
        ({"ancho": -1}, "ancho: debe ser un número mayor que 0"),
        ({"bloque": "DP"}, "bloque: 'DP' no está en lps.json biblioteca_vanos; no se conoce su orientación ni su ancho nominal"),
        ({"tipo": "hueco", "bloque": "VENTANA01"}, "bloque: no aplica con tipo hueco"),
        ({"cara_apertura": "arriba"}, "cara_apertura: debe ser izquierda o derecha"),
        ({"bisagra": "centro"}, "bisagra: debe ser inicio o fin"),
        ({"espacio": "Layout1"}, "espacio: el muro 2B1 está en 'modelo'"),
        ({"muro": "2BF"}, "espacio: el muro 2BF está en 'Layout1'"),
        ({"propiedades_dinamicas": {"Nada": 1}}, "propiedades_dinamicas: propiedad desconocida 'Nada' (propiedades del bloque: DistanceGeneralVano, DistanceHojaDoble, Visibilidad1, Origin)"),
        ({"propiedades_dinamicas": {"Origin": "1"}}, "propiedades_dinamicas.Origin: es de solo lectura"),
        ({"bloque": "PuertaBaño", "propiedades_dinamicas": {"A": 1}}, "propiedades_dinamicas: el bloque 'PuertaBaño' no es dinámico"),
        ({"tipo": "hueco", "propiedades_dinamicas": {"A": 1}}, "propiedades_dinamicas: no aplica con tipo hueco"),
    ],
)
def test_vano_invalid_params_exacto(cambio: dict[str, Any], detalle: str) -> None:
    world = _world()
    antes = _handles(world)
    err = _error(world, "vano", {**VANO, **cambio})
    assert (err.code, err.data["detalle"]) == ("INVALID_PARAMS", detalle)
    assert _handles(world) == antes and _state(world).pasos_deshacer == 0


@pytest.mark.parametrize("handle", ["2BD", "2B6", "2B7"])
def test_vano_rechazo_del_muro_lleva_data_handle(handle: str) -> None:
    assert _error(_world(), "vano", {**VANO, "muro": handle, "confirmar": True}).data["handle"] == handle


def test_vano_bloque_no_catalogo_lleva_data_bloque() -> None:
    assert _error(_world(), "vano", {**VANO, "bloque": "DP"}).data["bloque"] == "DP"


@pytest.mark.parametrize(
    ("cambio", "detalle"),
    [
        ({"posicion": [0.2, 4.06]}, "ancho: el hueco (0.8) no cabe en el tramo (longitud 5): se sale del tramo o cruza una esquina"),
        ({"posicion": [4.8, 4.06]}, "ancho: el hueco (0.8) no cabe en el tramo (longitud 5): se sale del tramo o cruza una esquina"),
        ({"ancho": 5}, "ancho: debe ser menor que la longitud del tramo"),
        ({"ancho": 6}, "ancho: debe ser menor que la longitud del tramo"),
        ({"muro": "2B8", "posicion": {"distancia": 1}}, "muro: posicion por distancia solo se admite en contornos de 4 vértices"),
        (
            {"muro": "2B8", "posicion": [34.7, 0.07]},
            "ancho: el hueco (0.8) no cabe en el tramo (longitud 4.85): se sale del tramo o cruza una esquina",
        ),
        ({"muro": "2C0", "posicion": [52.9, 0.02], "ancho": 0.4}, "ancho: el hueco cruza un vértice del contorno (esquina u otro vano)"),
    ],
)
def test_vano_invalid_geometry_exacto(cambio: dict[str, Any], detalle: str) -> None:
    world = _world()
    err = _error(world, "vano", {**VANO, **cambio})
    assert (err.code, err.data["detalle"]) == ("INVALID_GEOMETRY", detalle)
    assert str(err) == f"[INVALID_GEOMETRY] Geometría no construible: {detalle}"


def test_vano_contorno_de_tres_vertices() -> None:
    world = _world()
    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [[0, 9], [5, 9], [5, 10]]))
    detalle = _detalle(world, "vano", {**VANO, "muro": "2C1", "posicion": [2, 9.1]}, "INVALID_GEOMETRY")
    assert detalle == "muro: el contorno debe ser un polígono cerrado simple de al menos 4 vértices"


def test_vano_contorno_autointersecado() -> None:
    world = _world()
    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [[0, 9], [5, 10], [5, 9], [0, 10]]))
    detalle = _detalle(world, "vano", {**VANO, "muro": "2C1", "posicion": [2, 9.5]}, "INVALID_GEOMETRY")
    assert detalle == "muro: el contorno debe ser un polígono cerrado simple de al menos 4 vértices"


def test_vano_sin_caras_paralelas() -> None:
    world = _world()
    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [[0, 9], [5, 9], [6, 12], [1, 13]]))
    detalle = _detalle(world, "vano", {**VANO, "muro": "2C1", "posicion": [2, 9.1]}, "INVALID_GEOMETRY")
    assert detalle == "muro: no se encontró un tramo recto (dos caras paralelas) cerca del punto indicado"


def test_vano_contorno_resultante_autointersecado(monkeypatch: pytest.MonkeyPatch) -> None:
    llamadas = {"n": 0}

    def cruce(_xy: Any, _cerrada: bool, _tol: float = 1e-9) -> tuple[int, int] | None:
        llamadas["n"] += 1
        return None if llamadas["n"] == 1 else (0, 2)

    monkeypatch.setattr(fake_plugin, "_primer_cruce", cruce)
    detalle = _detalle(_world(), "vano", VANO, "INVALID_GEOMETRY")
    assert detalle == "muro: el contorno resultante se autointerseca (tramos 0 y 2)"


def test_vano_bloque_de_catalogo_ausente_not_found() -> None:
    world = _world()
    del _state(world).biblioteca["Puerta Dinamica LM"]
    err = _error(world, "vano", VANO)
    assert err.code == "NOT_FOUND" and err.data["bloque"] == "Puerta Dinamica LM"
    assert str(err).startswith("[NOT_FOUND] El bloque 'Puerta Dinamica LM' no está en el dibujo ni en la biblioteca (")


def test_vano_muro_inexistente_not_found() -> None:
    assert str(_error(_world(), "vano", {**VANO, "muro": "FFFF"})) == "[NOT_FOUND] No existe ninguna entidad con handle 'FFFF'."


def test_vano_capa_bloqueada_del_muro() -> None:
    world = _world()
    _state(world).capas_bloqueadas.add("MCP-DIBUJO")
    assert _detalle(world, "vano", VANO) == "handles[0]: la entidad está en una capa bloqueada"


@pytest.mark.parametrize(("name", "extra"), [("vano", "muro"), ("union_muros", "muro_a"), ("cotas_automaticas", "handles")])
def test_obligatorios_y_propiedad_no_permitida(name: str, extra: str) -> None:
    world = _world()
    args = {k: v for k, v in ARGS[name].items() if k != extra}
    assert _detalle(world, name, args) == f"{extra}: obligatorio"
    assert _detalle(world, name, {**ARGS[name], "otra": 1}) == "otra: propiedad no permitida"


# ---------------------------------------------------------------------------
# G2, R4 y protección confirmar
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("name", GENERADORAS_4B)
def test_g2_units_unknown_sin_escape(name: str) -> None:
    world = _world(insunits=0)

    async def body(c: Client) -> None:
        assert await _err(c, name, ARGS[name]) == UNITS_UNKNOWN
        assert await _err(c, name, {**ARGS[name], "preview": True}) == UNITS_UNKNOWN

    run(world.server(), body)
    assert _detalle(world, name, {**ARGS[name], "aceptar_sin_unidades": True}) == "aceptar_sin_unidades: propiedad no permitida"


def _muro_lejano(world: World) -> None:
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 1000.0, 1000.0, 1005.0, 1000.15))


def test_r4_vano_rechaza_fuera_de_la_extension_salvo_forzar() -> None:
    world = _world()
    _muro_lejano(world)
    args = {**VANO, "muro": "2C1", "posicion": [1002.5, 1000.07]}
    assert "fuera de la extensión del espacio destino 'modelo'" in _detalle(world, "vano", args)
    assert "2C1" in _handles(world)
    r = _call(world, "vano", {**args, "forzar": True})
    assert any(a.startswith("forzar: true: se escribió fuera de la extensión") for a in r["advertencias"])


def test_r4_union_rechaza_fuera_de_la_extension_salvo_forzar() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 1000.0, 1000.0, 1005.0, 1000.15))
    _agregar(world, muro_rectangular("2C2", "MCP-DIBUJO", 1005.0, 1000.0, 1005.15, 1003.0))
    args = {"muro_a": "2C1", "muro_b": "2C2"}
    assert "fuera de la extensión" in _detalle(world, "union_muros", args)
    assert _call(world, "union_muros", {**args, "forzar": True})["numeros"]["tipo_detectado"] == "L"


def test_r4_cotas_rechaza_fuera_de_la_extension_salvo_forzar() -> None:
    world = _world()
    _muro_lejano(world)
    args = {**COTAS, "handles": ["2C1"]}
    assert "fuera de la extensión" in _detalle(world, "cotas_automaticas", args)
    assert _call(world, "cotas_automaticas", {**args, "forzar": True})["numeros"]["n_cotas"] == 1


CONFIRMAR_2B0 = (
    "confirmar: 1 de las 1 entidades no están en capas MCP-* (p. ej. 2B0 en MUROS); pase confirmar: true para modificarlas"
)
CONFIRMAR_2B0_2B2 = (
    "confirmar: 2 de las 2 entidades no están en capas MCP-* (p. ej. 2B0 en MUROS, 2B2 en MUROS); pase confirmar: true "
    "para modificarlas"
)


def test_vano_exige_confirmar_en_muro_del_usuario() -> None:
    assert _detalle(_world(), "vano", {**VANO, "muro": "2B0", "posicion": [2.5, 0.07]}) == CONFIRMAR_2B0


def test_vano_exige_confirmar_ya_en_preview() -> None:
    world = _world()
    args = {**VANO, "muro": "2B0", "posicion": [2.5, 0.07], "preview": True}
    assert _detalle(world, "vano", args) == CONFIRMAR_2B0
    assert not _state(world).previews and _state(world).pasos_deshacer == 0


def test_vano_en_capa_mcp_no_exige_confirmar() -> None:
    assert _call(_world(), "vano", VANO)["handles_borrados"][0] == "2B1"


def test_union_exige_confirmar_de_los_dos_muros_ya_en_preview() -> None:
    world = _world()
    args = {"muro_a": "2B0", "muro_b": "2B2"}
    assert _detalle(world, "union_muros", args) == CONFIRMAR_2B0_2B2
    assert _detalle(world, "union_muros", {**args, "preview": True}) == CONFIRMAR_2B0_2B2
    assert not _state(world).previews


def test_confirmar_en_data_con_n_usuario_y_ejemplos() -> None:
    data = _error(_world(), "vano", {**VANO, "muro": "2B0", "posicion": [2.5, 0.07]}).data
    assert data["n_usuario"] == 1 and data["ejemplos"] == [{"handle": "2B0", "capa": "MUROS"}]


# ---------------------------------------------------------------------------
# Ciclo preview → confirmar / descartar_preview
# ---------------------------------------------------------------------------


def test_preview_no_borra_el_muro_y_dibuja_en_mcp_preview() -> None:
    world = _world()
    r = _call(world, "vano", {**VANO, "preview": True})
    assert r["preview"] is True and r["preview_dibujada"] is True and r["handles_borrados"] == []
    assert {"2B1", "2BA"} <= _handles(world)
    assert r["capa"] == CAPA_PREVIEW and {e["capa"] for e in r["entidades"]} == {CAPA_PREVIEW}
    assert _state(world).pasos_deshacer == 1


def test_preview_sin_dibujar_no_toca_nada_y_conserva_los_numeros() -> None:
    world = _world()
    antes = _handles(world)
    r = _call(world, "vano", {**VANO, "preview": True, "dibujar_preview": False})
    assert r["preview_dibujada"] is False and r["entidades"] == [] and r["numeros"]["n_entidades"] == 5
    assert _handles(world) == antes and _state(world).pasos_deshacer == 0
    assert r["propiedades_dinamicas"] == []


def test_confirmar_borra_el_muro_solo_entonces() -> None:
    world = _world()

    async def body(c: Client) -> None:
        p = await _ok(c, "vano", {**VANO, "preview": True})
        assert "2B1" in _handles(world)
        r = await _ok(c, "confirmar", {"preview_id": p["preview_id"]})
        assert r["confirmado"] is True and r["herramienta"] == "vano"
        assert r["handles_borrados"] == ["2B1", "2BA"] and r["n_preview_borradas"] == 5
        assert {e["capa"] for e in r["entidades"]} == {"MCP-DIBUJO", "PUERTAS"}
        assert "2B1" not in _handles(world) and not set(p["handles"]) & _handles(world)

    run(world.server(), body)
    assert _state(world).pasos_deshacer == 2


def test_descartar_deja_el_muro_intacto() -> None:
    world = _world()
    antes = copy.deepcopy(_entidad(world, "2B1"))

    async def body(c: Client) -> None:
        p = await _ok(c, "vano", {**VANO, "preview": True})
        d = await _ok(c, "descartar_preview", {"preview_id": p["preview_id"]})
        assert d["descartada"] is True and d["n_borradas"] == 5 and d["herramienta"] == "vano"

    run(world.server(), body)
    assert _entidad(world, "2B1") == antes and _state(world).pasos_deshacer == 2


def test_confirmar_con_el_muro_movido_no_escribe() -> None:
    world = _world()

    async def body(c: Client) -> None:
        p = await _ok(c, "vano", {**VANO, "preview": True})
        await _ok(c, "mover", {"handles": ["2B1"], "desplazamiento": [0, 0.01]})
        assert await _err(c, "confirmar", {"preview_id": p["preview_id"]}) == INVALIDO + detalle_ancla_cambiada("2B1")
        assert "2B1" in _handles(world)

    run(world.server(), body)


def test_confirmar_con_el_hatch_borrado_no_escribe() -> None:
    world = _world()
    p = _call(world, "vano", {**VANO, "preview": True})
    _call(world, "borrar", {"handles": ["2BA"]})
    assert _detalle(world, "confirmar", {"preview_id": p["preview_id"]}) == detalle_ancla_cambiada("2BA")


def test_preview_caduca_a_los_15_minutos() -> None:
    world = _world()
    p = _call(world, "vano", {**VANO, "preview": True, "dibujar_preview": False})
    _state(world).ahora += PREVIEW_TTL + timedelta(seconds=1)
    assert _error(world, "confirmar", {"preview_id": p["preview_id"]}).code == "NOT_FOUND"
    assert "2B1" in _handles(world)


def test_confirmar_vano_del_usuario_no_pide_confirmar_otra_vez() -> None:
    world = _world()
    p = _call(world, "vano", {**VANO, "muro": "2B0", "posicion": [2.5, 0.07], "confirmar": True, "preview": True})
    r = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert r["handles_borrados"] == ["2B0"] and "2B0" not in _handles(world)


def test_union_preview_y_confirmar() -> None:
    world = _world()
    p = _call(world, "union_muros", {**UNION_T, "preview": True})
    assert p["handles_borrados"] == [] and {"2B0", "2B2"} <= _handles(world)
    r = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert r["handles_borrados"] == ["2B0", "2B2"] and not {"2B0", "2B2"} & _handles(world)


def test_union_descartar_deja_los_dos_muros() -> None:
    world = _world()
    p = _call(world, "union_muros", {**UNION_T, "preview": True})
    _call(world, "descartar_preview", {"preview_id": p["preview_id"]})
    assert {"2B0", "2B2"} <= _handles(world)


def test_union_ancla_cambiada_si_se_mueve_un_muro() -> None:
    world = _world()
    p = _call(world, "union_muros", {**UNION_T, "preview": True})
    _call(world, "mover", {"handles": ["2B2"], "desplazamiento": [0, 0.5], "confirmar": True})
    assert _detalle(world, "confirmar", {"preview_id": p["preview_id"]}) == detalle_ancla_cambiada("2B2")


def test_cotas_preview_confirmar_y_ancla() -> None:
    world = _world()
    p = _call(world, "cotas_automaticas", {**COTAS, "preview": True})
    assert {e["capa"] for e in p["entidades"]} == {CAPA_PREVIEW}
    r = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert {e["capa"] for e in r["entidades"]} == {"Cotas"} and r["handles_borrados"] == []
    q = _call(world, "cotas_automaticas", {**COTAS, "preview": True})
    _call(world, "mover", {"handles": ["2B1"], "desplazamiento": [0.5, 0]})
    assert _detalle(world, "confirmar", {"preview_id": q["preview_id"]}) == detalle_ancla_cambiada("2B1")


def test_borrado_de_entidad_que_ya_no_es_del_tipo() -> None:
    transporte = FakeTransport(_state(_world()))
    plan = [{"tipo": "BORRAR", "handle": "1A0", "tipo_esperado": "LWPOLYLINE", "caja": None}]
    with pytest.raises(_RpcError) as exc:
        transporte._dibujar(plan, [], False, [])
    assert exc.value.data["detalle"] == "borrar: la entidad 1A0 ya no es LWPOLYLINE"


# ---------------------------------------------------------------------------
# union_muros
# ---------------------------------------------------------------------------


def test_union_t_de_8_vertices() -> None:
    world = _world()

    async def body(c: Client) -> None:
        r = await _ok(c, "union_muros", UNION_T)
        n = r["numeros"]
        assert (n["tipo_detectado"], n["n_vertices"]) == ("T", 8)
        assert n["area_a"] == pytest.approx(0.75) and n["area_b"] == pytest.approx(0.12 * 2.85)
        assert n["area_solape"] == pytest.approx(0.0) and n["area_union"] == pytest.approx(0.75 + 0.342)
        assert n["n_entidades"] == 1 and r["handles_borrados"] == ["2B0", "2B2"]

    run(world.server(), body)


def test_union_l_de_6_vertices_con_solape() -> None:
    n = _call(_world(), "union_muros", UNION_L)["numeros"]
    assert (n["tipo_detectado"], n["n_vertices"]) == ("L", 6)
    assert n["area_solape"] == pytest.approx(0.15 * 0.15)
    assert n["area_union"] == pytest.approx(n["area_a"] + n["area_b"] - n["area_solape"])


def test_union_contorno_cerrado_sin_colineales_en_la_capa_de_a() -> None:
    world = _world()
    r = _call(world, "union_muros", UNION_T)
    e = _entidad(world, r["handles"][0])
    assert e["cerrada"] is True and e["capa"] == "MUROS" and len(e["vertices"]) == 8
    assert e["longitud"] == pytest.approx(r["numeros"]["longitud_contorno"])


def test_union_un_paso_de_deshacer() -> None:
    world = _world()
    _call(world, "union_muros", UNION_T)
    assert _state(world).pasos_deshacer == 1


def test_union_cruce_de_12_vertices() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 60.0, 0.0, 65.0, 0.15))
    _agregar(world, muro_rectangular("2C2", "MCP-DIBUJO", 62.0, -2.0, 62.15, 2.0))
    n = _call(world, "union_muros", {"muro_a": "2C1", "muro_b": "2C2"})["numeros"]
    assert (n["tipo_detectado"], n["n_vertices"]) == ("cruce", 12)


def test_union_colineal_de_4_vertices() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 60.0, 0.0, 65.0, 0.15))
    _agregar(world, muro_rectangular("2C2", "MCP-DIBUJO", 65.0, 0.0, 68.0, 0.15))
    n = _call(world, "union_muros", {"muro_a": "2C1", "muro_b": "2C2"})["numeros"]
    assert (n["tipo_detectado"], n["n_vertices"]) == ("colineal", 4)
    assert n["area_union"] == pytest.approx(8 * 0.15)


def test_union_a_cualquier_angulo() -> None:
    world = _world()
    c, s = math.cos(math.radians(30)), math.sin(math.radians(30))

    def giro(x: float, y: float) -> list[float]:
        return [x * c - y * s, x * s + y * c]

    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [giro(*p) for p in [(0, 0), (5, 0), (5, 0.15), (0, 0.15)]]))
    _agregar(world, _poligono("2C2", "MCP-DIBUJO", [giro(*p) for p in [(2, 0.15), (2.12, 0.15), (2.12, 3), (2, 3)]]))
    assert _call(world, "union_muros", {"muro_a": "2C1", "muro_b": "2C2"})["numeros"]["tipo_detectado"] == "T"


@pytest.mark.parametrize(("tipo", "ok"), [("auto", True), ("T", True), ("L", False), ("cruce", False)])
def test_union_tipo_comprueba_el_encuentro(tipo: str, ok: bool) -> None:
    world = _world()
    if ok:
        assert _call(world, "union_muros", {**UNION_T, "tipo": tipo})["numeros"]["tipo_detectado"] == "T"
    else:
        detalle = _detalle(world, "union_muros", {**UNION_T, "tipo": tipo}, "INVALID_GEOMETRY")
        assert detalle == f"tipo: los muros forman un encuentro en T, no en {tipo}"


def test_union_capas_distintas_avisa_y_usa_la_de_a() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 2.0, -2.0, 2.12, 0.0))
    r = _call(world, "union_muros", {"muro_a": "2B0", "muro_b": "2C1", "confirmar": True})
    assert "union_muros: los muros están en capas distintas ('MUROS', 'MCP-DIBUJO'); el contorno unido va a 'MUROS'." in r["advertencias"]
    assert r["entidades"][0]["capa"] == "MUROS"


def test_union_capa_explicita() -> None:
    r = _call(_world(), "union_muros", {**UNION_T, "capa": "MCP-DIBUJO"})
    assert r["entidades"][0]["capa"] == "MCP-DIBUJO"


def test_union_rehace_el_sombreado_con_el_patron_de_a() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 5.0, 4.0, 5.12, 7.0))
    r = _call(world, "union_muros", {"muro_a": "2B1", "muro_b": "2C1"})
    assert r["handles_borrados"] == ["2B1", "2C1", "2BA"]
    assert [e["tipo"] for e in r["entidades"]] == ["LWPOLYLINE", "HATCH"] and r["entidades"][1]["patron"] == "ANSI31"


@pytest.mark.parametrize(
    ("args", "detalle"),
    [
        ({"muro_a": "2B1", "muro_b": "2B1"}, "muro_b: debe ser distinto de muro_a"),
        ({"muro_a": "2B1", "muro_b": "2B4", "tipo": "U"}, "tipo: debe ser L, T, cruce o auto"),
        ({"muro_a": "2B1", "muro_b": "2B7"}, "muro_b: la polilínea 2B7 tiene arcos; solo se admiten contornos rectos"),
        ({"muro_a": "2BC", "muro_b": "2B1"}, "muro_a: la entidad 2BC no es una polilínea cerrada (LINE)"),
        ({"muro_a": "2B1", "muro_b": "2BF"}, "handles: las entidades deben estar en el mismo espacio (2B1 en modelo, 2BF en Layout1)"),
        ({"muro_a": "2B1", "muro_b": "2B4", "espacio": "Layout1"}, "espacio: el muro 2B1 está en 'modelo'"),
    ],
)
def test_union_invalid_params_exacto(args: dict[str, Any], detalle: str) -> None:
    assert _detalle(_world(), "union_muros", args) == detalle


@pytest.mark.parametrize(
    ("args", "detalle"),
    [
        ({"muro_a": "2B8", "muro_b": "2B1"}, "muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)"),
        ({"muro_a": "2B1", "muro_b": "2B8"}, "muro_b: el contorno debe ser un cuadrilátero convexo (muro recto)"),
        ({"muro_a": "2B1", "muro_b": "2B4"}, "union: los muros no se tocan ni se solapan"),
        ({"muro_a": "2B0", "muro_b": "2B5", "confirmar": True}, "union: los muros solo se tocan en un punto; no hay encuentro que unir"),
        ({"muro_a": "2B0", "muro_b": "2B9", "confirmar": True}, "union: un muro está contenido en el otro"),
    ],
)
def test_union_invalid_geometry_exacto(args: dict[str, Any], detalle: str) -> None:
    world = _world()
    antes = _handles(world)
    assert _detalle(world, "union_muros", args, "INVALID_GEOMETRY") == detalle
    assert _handles(world) == antes


def test_union_cuadrilatero_concavo_rechazado() -> None:
    world = _world()
    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [[0, 9], [4, 9], [1, 10], [0, 12]]))
    detalle = _detalle(world, "union_muros", {"muro_a": "2C1", "muro_b": "2B1"}, "INVALID_GEOMETRY")
    assert detalle == "muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)"


def test_union_encuentro_no_reconocido() -> None:
    world = _world()
    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [[2.3, 1], [1, 2.3], [-0.3, 1], [1, -0.3]]))
    _agregar(world, muro_rectangular("2C2", "MCP-DIBUJO", 0.0, 0.0, 2.0, 2.0))
    detalle = _detalle(world, "union_muros", {"muro_a": "2C2", "muro_b": "2C1"}, "INVALID_GEOMETRY")
    assert detalle == "union: encuentro no reconocido (16 vértices)"


def test_union_rombo_en_la_esquina_de_7_vertices_es_l() -> None:
    world = _world()
    _agregar(world, _poligono("2C1", "MCP-DIBUJO", [[65, -0.925], [66, 0.075], [65, 1.075], [64, 0.075]]))
    _agregar(world, muro_rectangular("2C2", "MCP-DIBUJO", 60.0, 0.0, 65.0, 0.15))
    n = _call(world, "union_muros", {"muro_a": "2C2", "muro_b": "2C1"})["numeros"]
    assert (n["tipo_detectado"], n["n_vertices"]) == ("L", 7)


def test_union_l_con_saliente_de_8_vertices_no_es_t() -> None:
    world = _world()
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 60.0, 0.0, 63.0, 0.15))
    _agregar(world, muro_rectangular("2C2", "MCP-DIBUJO", 62.9, -0.05, 63.05, 2.0))
    args = {"muro_a": "2C1", "muro_b": "2C2", "tipo": "T"}
    assert _detalle(world, "union_muros", args, "INVALID_GEOMETRY") == "tipo: los muros forman un encuentro en L, no en T"
    n = _call(world, "union_muros", {"muro_a": "2C1", "muro_b": "2C2"})["numeros"]
    assert (n["tipo_detectado"], n["n_vertices"]) == ("L", 8)


def test_union_contorno_no_simple(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(fake_plugin, "_simplificar_lazo", lambda _v, _t: [(0.0, 0.0), (1.0, 1.0), (1.0, 0.0), (0.0, 1.0)])
    detalle = _detalle(_world(), "union_muros", UNION_T, "INVALID_GEOMETRY")
    assert detalle == "union: el contorno unido no es un polígono simple"


def test_union_resultado_no_convexo_no_admitido_como_entrada() -> None:
    world = _world()
    r = _call(world, "union_muros", UNION_L)
    _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 5.0, 0.0, 8.0, 0.15))
    detalle = _detalle(world, "union_muros", {"muro_a": r["handles"][0], "muro_b": "2C1", "confirmar": True}, "INVALID_GEOMETRY")
    assert detalle == "muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)"


# ---------------------------------------------------------------------------
# cotas_automaticas
# ---------------------------------------------------------------------------


def test_cotas_total_de_un_rectangulo_en_x() -> None:
    world = _world()

    async def body(c: Client) -> None:
        r = await _ok(c, "cotas_automaticas", COTAS)
        n = r["numeros"]
        assert n["medidas"] == pytest.approx([5.0]) and n["total"] == pytest.approx(5.0) and n["n_cotas"] == 1
        assert (n["direccion"], n["lado"], n["separacion"], n["estilo"]) == (0.0, "izquierda", 0.15, "COTA75")
        assert (n["dimscale"], n["cannoscale"]) == (1.0, "1:1")
        assert (n["n_puntos"], n["n_puntos_fundidos"]) == (4, 2)
        assert r["entidades"][0]["medida"] == pytest.approx(5.0) and r["entidades"][0]["estilo"] == "COTA75"

    run(world.server(), body)


def test_cotas_capa_cotas_por_concepto_y_se_crea() -> None:
    r = _call(_world(), "cotas_automaticas", COTAS)
    assert r["capa"] == "Cotas" and r["capas_creadas"] == ["Cotas"]


def test_cotas_capa_explicita() -> None:
    assert _call(_world(), "cotas_automaticas", {**COTAS, "capa": "MCP-DIBUJO"})["capa"] == "MCP-DIBUJO"


def test_cotas_cadena_una_por_tramo_y_total() -> None:
    args = {"handles": ["2BC", "2BD"], "direccion": "x", "separacion": 0.2, "estilo": "COTA75"}
    r = _call(_world(), "cotas_automaticas", args)
    assert r["numeros"]["medidas"] == pytest.approx([3.0, 7.0]) and r["numeros"]["total"] == pytest.approx(10.0)
    assert len(r["entidades"]) == 2


def test_cotas_cadena_false_una_sola() -> None:
    args = {"handles": ["2BC", "2BD"], "direccion": "x", "separacion": 0.2, "estilo": "COTA75", "cadena": False}
    r = _call(_world(), "cotas_automaticas", args)
    assert r["numeros"]["medidas"] == pytest.approx([10.0]) and r["numeros"]["n_cotas"] == 1


def test_cotas_direccion_y() -> None:
    n = _call(_world(), "cotas_automaticas", {**COTAS, "direccion": "y"})["numeros"]
    assert n["direccion"] == 90.0 and n["medidas"] == pytest.approx([0.12])


def test_cotas_direccion_en_grados() -> None:
    n = _call(_world(), "cotas_automaticas", {"handles": ["2BC"], "direccion": 45, "separacion": 0.2, "estilo": "COTA75"})["numeros"]
    assert n["direccion"] == 45 and n["medidas"] == pytest.approx([10 * math.cos(math.radians(45))])


@pytest.mark.parametrize(("handle", "grados"), [("2BC", 0.0), ("2B3", 90.0)])
def test_cotas_direccion_auto_dominante_de_la_primera(handle: str, grados: float) -> None:
    args = {"handles": [handle], "separacion": 0.2, "estilo": "COTA75"}
    assert _call(_world(), "cotas_automaticas", args)["numeros"]["direccion"] == pytest.approx(grados)


@pytest.mark.parametrize(("lado", "y"), [("izquierda", 4.27), ("derecha", 3.85)])
def test_cotas_lado_y_separacion_desde_el_punto_extremo(lado: str, y: float) -> None:
    world = _world()
    r = _call(world, "cotas_automaticas", {**COTAS, "lado": lado})
    assert r["numeros"]["lado"] == lado
    assert _entidad(world, r["handles"][0])["posicion_linea"][1] == pytest.approx(y)


def test_cotas_lado_puntoref() -> None:
    world = _world()
    r = _call(world, "cotas_automaticas", {**COTAS, "lado": [2, 1]})
    assert r["numeros"]["lado"] == "derecha" and r["puntos_resueltos"][0]["punto"][:2] == [2, 1]


def test_cotas_tolerancia_funde_puntos() -> None:
    world = _world()
    _agregar(world, fake_plugin._line("2C1", "MCP-DIBUJO", [0.0, 6.0, 0.0], [0.05, 7.0, 0.0]))
    args = {"handles": ["2C1"], "direccion": "x", "separacion": 0.1, "estilo": "COTA75"}
    assert _call(world, "cotas_automaticas", args)["numeros"]["n_cotas"] == 1
    assert _detalle(world, "cotas_automaticas", {**args, "tolerancia": 0.1}) == (
        "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota"
    )


def test_cotas_puntos_de_insert_y_texto() -> None:
    n = _call(_world(), "cotas_automaticas", {"handles": ["1A4", "1AA"], "direccion": "y", "separacion": 0.2, "estilo": "COTA75"})["numeros"]
    assert n["medidas"] == pytest.approx([1.5]) and n["n_puntos"] == 2


def test_cotas_sobre_entidades_del_usuario_no_exige_confirmar() -> None:
    world = _world()
    r = _call(world, "cotas_automaticas", {**COTAS, "handles": ["2B0"]})
    assert r["numeros"]["n_cotas"] == 1 and "2B0" in _handles(world)


def test_cotas_sin_estilo_usa_el_actual_con_advertencia() -> None:
    args = {k: v for k, v in COTAS.items() if k != "estilo"}
    r = _call(_world(), "cotas_automaticas", args)
    assert r["numeros"]["estilo"] == "COTA75" and advertencia_estilo_cota("COTA75") in r["advertencias"]
    assert advertencia_estilo_cota("COTA75") == (
        "cotas_automaticas: se usó el estilo de cota actual 'COTA75'; indique estilo para fijarlo (la oficina usa COTA75)."
    )


def test_cotas_con_estilo_sin_advertencia_y_sin_distinguir_mayusculas() -> None:
    r = _call(_world(), "cotas_automaticas", {**COTAS, "estilo": "cota75"})
    assert r["numeros"]["estilo"] == "COTA75" and not any("estilo de cota actual" in a for a in r["advertencias"])


def test_cotas_estilo_inexistente_not_found() -> None:
    err = _error(_world(), "cotas_automaticas", {**COTAS, "estilo": "NADA"})
    assert str(err) == "[NOT_FOUND] El estilo de cota 'NADA' no existe en el dibujo."


def test_cotas_espacio_destino_distinto_avisa() -> None:
    r = _call(_world(), "cotas_automaticas", {**COTAS, "espacio": "Layout1", "forzar": True})
    assert "cotas_automaticas: las entidades están en 'modelo' y las cotas se crean en 'Layout1' (parámetro espacio)." in r["advertencias"]


@pytest.mark.parametrize(
    ("cambio", "detalle"),
    [
        ({"handles": ["2BE"]}, "handles[0]: la entidad 2BE (SPLINE) no aporta puntos acotables"),
        ({"handles": ["2BC", "2BE"]}, "handles[1]: la entidad 2BE (SPLINE) no aporta puntos acotables"),
        ({"handles": ["2BD"]}, "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota"),
        ({"separacion": 0}, "separacion: debe ser un número mayor que 0"),
        ({"lado": "arriba"}, "lado: debe ser izquierda o derecha"),
        ({"direccion": "z"}, "direccion: debe ser x, y, auto o un número en grados"),
        ({"handles": ["2BC", "2BC"]}, "handles[1]: handle repetido"),
        ({"handles": ["2B1", "2BF"]}, "handles: las entidades deben estar en el mismo espacio (2B1 en modelo, 2BF en Layout1)"),
    ],
)
def test_cotas_invalid_params_exacto(cambio: dict[str, Any], detalle: str) -> None:
    assert _detalle(_world(), "cotas_automaticas", {**COTAS, **cambio}) == detalle


# ---------------------------------------------------------------------------
# insertar_export
# ---------------------------------------------------------------------------

CAMPOS_EXPORT = [
    "entidad", "archivo", "unidades_export", "factor_unidades", "escala_aplicada", "escala_unidades", "n_entidades_export",
    "n_entidades_excluidas", "capas_importadas", "capas_export", "bloques_importados", "puntos_resueltos", "origen_texto",
]


def test_export_forma_de_la_respuesta() -> None:
    world = _world()

    async def body(c: Client) -> None:
        r = await _ok(c, "insertar_export", ARGS["insertar_export"])
        assert set(CAMPOS_EXPORT) <= set(r)
        assert {"pid", "handles", "capa", "capa_creada", "espacio", "insunits", "unidades", "advertencias"} <= set(r)
        assert r["origen_texto"] == "contenido_del_plano" and r["archivo"] == EXPORT
        assert set(r["entidad"]) == {"handle", "bloque", "posicion", "escala", "rotacion", "bbox"}

    run(world.server(), body)


def test_export_factor_0_001_mm_a_m() -> None:
    r = _call(_world(), "insertar_export", ARGS["insertar_export"])
    assert (r["unidades_export"], r["factor_unidades"]) == (4, pytest.approx(0.001))
    assert r["escala_aplicada"] == pytest.approx([0.001] * 3) and r["escala_unidades"] == "auto"
    assert r["entidad"]["bbox"][0] == [1, 1] and r["entidad"]["bbox"][1] == pytest.approx([2.5, 2.5])


def test_export_escala_pedida_se_multiplica() -> None:
    r = _call(_world(), "insertar_export", {**ARGS["insertar_export"], "escala": 2})
    assert r["escala_aplicada"] == pytest.approx([0.002] * 3)


def test_export_escala_unidades_ninguna() -> None:
    r = _call(_world(), "insertar_export", {**ARGS["insertar_export"], "escala_unidades": "ninguna"})
    assert r["escala_aplicada"] == [1.0, 1.0, 1.0] and r["escala_unidades"] == "ninguna"


def test_export_sin_unidades_factor_1_con_aviso() -> None:
    r = _call(_world(), "insertar_export", {"ruta": "C:\\copias\\SIN_UNIDADES.dwg", "posicion": [1, 1]})
    assert r["factor_unidades"] == 1.0 and ADVERTENCIA_EXPORT_SIN_UNIDADES in r["advertencias"]
    assert ADVERTENCIA_EXPORT_SIN_UNIDADES == (
        "El export no declara unidades (INSUNITS=0): no se aplicó factor; pase escala explícita si el export está en mm."
    )


def test_export_nombre_derivado_y_correlativo() -> None:
    world = _world()
    assert _call(world, "insertar_export", ARGS["insertar_export"])["entidad"]["bloque"] == "CYPE_FILE1"
    assert _call(world, "insertar_export", ARGS["insertar_export"])["entidad"]["bloque"] == "CYPE_FILE1_2"
    assert _call(world, "insertar_export", ARGS["insertar_export"])["entidad"]["bloque"] == "CYPE_FILE1_3"


def test_export_nombre_dado() -> None:
    r = _call(_world(), "insertar_export", {**ARGS["insertar_export"], "nombre_bloque": "ESTRUCTURA"})
    assert r["entidad"]["bloque"] == "ESTRUCTURA"


def test_export_capa_por_defecto_mcp_dibujo_y_capa_destino() -> None:
    assert _call(_world(), "insertar_export", ARGS["insertar_export"])["capa"] == "MCP-DIBUJO"
    r = _call(_world(), "insertar_export", {**ARGS["insertar_export"], "capa_destino": "estructura"})
    assert (r["capa"], r["capa_creada"]) == ("6. ESTRUCTURA", True)


def test_export_clasifica_capas_cype_y_ajenas_con_aviso() -> None:
    r = _call(_world(), "insertar_export", ARGS["insertar_export"])
    assert r["capas_export"] == {"cype": ["ARM_PILARES", "CIM_ZAPATAS"], "otras": ["TEXTO_CYPE"]}
    assert r["capas_importadas"] == {"cype": ["ARM_PILARES", "CIM_ZAPATAS"], "otras": ["TEXTO_CYPE"]}
    assert advertencia_capas_ajenas(["TEXTO_CYPE"]) in r["advertencias"]
    assert advertencia_capas_ajenas(["TEXTO_CYPE"]) == (
        'El export trae 1 capa(s) fuera del grupo CYPE: TEXTO_CYPE; pase filtrar_capas: "cype" para dejarlas fuera.'
    )


def test_export_aviso_de_capas_ajenas_trunca_a_cinco() -> None:
    r = _call(_world(), "insertar_export", {"ruta": "C:\\copias\\AJENAS.dwg", "posicion": [1, 1]})
    assert 'El export trae 7 capa(s) fuera del grupo CYPE: A1, A2, A3, A4, A5 (y 2 más); pase filtrar_capas: "cype" para dejarlas fuera.' in r["advertencias"]


def test_export_filtrar_capas_cype() -> None:
    world = _world()
    r = _call(world, "insertar_export", {**ARGS["insertar_export"], "filtrar_capas": "cype"})
    assert (r["n_entidades_export"], r["n_entidades_excluidas"]) == (7, 1)
    assert r["capas_importadas"] == {"cype": ["ARM_PILARES", "CIM_ZAPATAS"], "otras": []}
    assert r["capas_export"]["otras"] == ["TEXTO_CYPE"]
    assert not any("fuera del grupo CYPE" in a for a in r["advertencias"])
    assert not any(c["nombre"] == "TEXTO_CYPE" for c in _state(world).capas_creadas)


def test_export_capas_que_ya_existen_no_se_informan_como_importadas() -> None:
    world = _world()
    _call(world, "insertar_export", ARGS["insertar_export"])
    r = _call(world, "insertar_export", ARGS["insertar_export"])
    assert r["capas_importadas"] == {"cype": [], "otras": []}


def test_export_bloques_importados_y_definicion_con_unidades() -> None:
    world = _world()
    r = _call(world, "insertar_export", ARGS["insertar_export"])
    assert r["bloques_importados"] == ["PILAR_C1"]
    d = next(x for x in _state(world).block_defs if x["nombre"] == "CYPE_FILE1")
    assert d["unidades_bloque"] == 4 and d["n_inserciones"] == 1


def test_export_un_paso_de_deshacer_y_un_insert() -> None:
    world = _world()
    r = _call(world, "insertar_export", ARGS["insertar_export"])
    assert _state(world).pasos_deshacer == 1 and _entidad(world, r["handles"][0])["tipo"] == "INSERT"


def test_export_posicion_puntoref() -> None:
    r = _call(_world(), "insertar_export", {"ruta": EXPORT, "posicion": {"handle": "2BC", "punto": "fin"}})
    assert r["entidad"]["posicion"][:2] == [10.0, 6.0] and r["puntos_resueltos"][0]["punto"][:2] == [10.0, 6.0]


def test_export_rotacion() -> None:
    assert _call(_world(), "insertar_export", {**ARGS["insertar_export"], "rotacion": 90})["entidad"]["rotacion"] == 90


def test_export_sin_carpetas_permitidas_avisa() -> None:
    r = _call(_world(), "insertar_export", ARGS["insertar_export"])
    assert ADVERTENCIA_SIN_CARPETAS in r["advertencias"]
    assert ADVERTENCIA_SIN_CARPETAS == (
        "lps.json no define carpetas_export_permitidas: se admite cualquier ruta absoluta .dwg legible; añada la clave "
        "para limitar las carpetas."
    )


def test_export_dentro_de_las_carpetas_permitidas_no_avisa() -> None:
    world = _world(carpetas_export=["C:\\copias\\"])
    r = _call(world, "insertar_export", {**ARGS["insertar_export"], "ruta": "c:\\copias\\file1.dwg"})
    assert ADVERTENCIA_SIN_CARPETAS not in r["advertencias"]


def test_export_fuera_de_las_carpetas_permitidas() -> None:
    world = _world(carpetas_export=["C:\\otra"])
    detalle = _detalle(world, "insertar_export", ARGS["insertar_export"])
    assert detalle == "ruta: fuera de las carpetas permitidas de lps.json (carpetas_export_permitidas)"


@pytest.mark.parametrize(
    ("ruta", "detalle"),
    [
        ("planos\\FILE1.dwg", "ruta: debe ser una ruta absoluta"),
        ("FILE1.dwg", "ruta: debe ser una ruta absoluta"),
        ("C:\\copias\\FILE1.dxf", "ruta: solo se admiten archivos .dwg"),
        ("C:\\copias\\FILE1", "ruta: solo se admiten archivos .dwg"),
        ("C:\\copias\\..\\FILE1.dwg", "ruta: no se admiten '..' ni comodines"),
        ("C:\\copias\\*.dwg", "ruta: no se admiten '..' ni comodines"),
        ("C:\\copias\\FILE?.dwg", "ruta: no se admiten '..' ni comodines"),
    ],
)
def test_export_ruta_invalida(ruta: str, detalle: str) -> None:
    world = _world()
    assert _detalle(world, "insertar_export", {"ruta": ruta, "posicion": [1, 1]}) == detalle
    assert _state(world).pasos_deshacer == 0


def test_export_ruta_unc_admitida_en_la_validacion() -> None:
    err = _error(_world(), "insertar_export", {"ruta": "\\\\srv\\obra\\X.dwg", "posicion": [1, 1]})
    assert err.code == "NOT_FOUND"


def test_export_inexistente_not_found() -> None:
    ruta = "C:\\copias\\NADA.dwg"
    err = _error(_world(), "insertar_export", {"ruta": ruta, "posicion": [1, 1]})
    assert str(err) == f"[NOT_FOUND] El archivo '{ruta}' no existe." and err.data["archivo"] == ruta


def test_export_ilegible_internal() -> None:
    assert _error(_world(), "insertar_export", {"ruta": "C:\\copias\\ILEGIBLE.dwg", "posicion": [1, 1]}).code == "INTERNAL"


def test_export_nombre_existente() -> None:
    world = _world()
    assert _detalle(world, "insertar_export", {**ARGS["insertar_export"], "nombre_bloque": "dp"}) == "nombre_bloque: el bloque 'dp' ya existe en el dibujo"


def test_export_como_xref() -> None:
    assert _detalle(_world(), "insertar_export", {**ARGS["insertar_export"], "como": "xref"}) == "como: xref no se admite en insertar_export; use adjuntar_xref para adjuntar una referencia externa"


def test_export_escala_cero() -> None:
    assert _detalle(_world(), "insertar_export", {**ARGS["insertar_export"], "escala": 0}) == "escala: debe ser un número distinto de 0"


def test_export_preview_no_admitido() -> None:
    assert _detalle(_world(), "insertar_export", {**ARGS["insertar_export"], "preview": True}) == "preview: propiedad no permitida"


def test_export_g2_con_escape_aceptar_sin_unidades() -> None:
    world = _world(insunits=0)

    async def body(c: Client) -> None:
        assert await _err(c, "insertar_export", ARGS["insertar_export"]) == UNITS_UNKNOWN
        r = await _ok(c, "insertar_export", {**ARGS["insertar_export"], "aceptar_sin_unidades": True})
        assert ADVERTENCIA_ACEPTAR_SIN_UNIDADES in r["advertencias"] and r["factor_unidades"] == 1.0
        assert any("No se aplicó factor de unidades" in a for a in r["advertencias"])

    run(world.server(), body)


def test_export_r4_salvo_forzar() -> None:
    world = _world()
    args = {"ruta": EXPORT, "posicion": [5000, 5000]}
    assert "fuera de la extensión" in _detalle(world, "insertar_export", args)
    assert _state(world).pasos_deshacer == 0
    assert _call(world, "insertar_export", {**args, "forzar": True})["entidad"]["bloque"] == "CYPE_FILE1"


def test_export_no_escribe_si_la_capa_destino_esta_bloqueada() -> None:
    world = _world()
    _state(world).capas_bloqueadas.add("MCP-DIBUJO")
    assert _detalle(world, "insertar_export", ARGS["insertar_export"]) == "capa bloqueada"
    assert not any(d["nombre"].startswith("CYPE_") for d in _state(world).block_defs)


# ---------------------------------------------------------------------------
# insertar_bloque: biblioteca ampliada y propiedades dinámicas (§5G.6)
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("nombre", ["LPS_ROTULO", "Puerta Dinamica LM", "PuertaBaño", "VENTANA01", "TANQUE 1000 LTS"])
def test_biblioteca_con_los_cinco_bloques_en_metros(nombre: str) -> None:
    r = _call(_world(), "insertar_bloque", {"nombre": nombre, "posicion": [1, 1], "desde_biblioteca": True})
    assert r["importado_de_biblioteca"] is True and r["unidades_bloque"] == 6 and r["factor_unidades"] == 1.0
    assert r["archivo"].endswith(f"\\biblioteca\\{nombre}.dwg")


@pytest.mark.parametrize(
    ("nombre", "dinamico"), [("Puerta Dinamica LM", True), ("VENTANA01", True), ("PuertaBaño", False), ("TANQUE 1000 LTS", False), ("LPS_ROTULO", False)]
)
def test_insertar_bloque_campos_dinamico_y_propiedades_siempre_presentes(nombre: str, dinamico: bool) -> None:
    r = _call(_world(), "insertar_bloque", {"nombre": nombre, "posicion": [1, 1], "desde_biblioteca": True})
    assert r["dinamico"] is dinamico and isinstance(r["propiedades_dinamicas"], list)
    assert bool(r["propiedades_dinamicas"]) is dinamico


def test_insertar_bloque_forma_de_las_propiedades() -> None:
    r = _call(_world(), "insertar_bloque", {"nombre": "Puerta Dinamica LM", "posicion": [1, 1], "desde_biblioteca": True})
    assert all(set(p) == {"nombre", "valor", "solo_lectura", "unidades", "valores_permitidos", "descripcion"} for p in r["propiedades_dinamicas"])
    assert {"distancia", "ninguna"} >= {p["unidades"] for p in r["propiedades_dinamicas"]}


def test_insertar_bloque_aplica_propiedades_sin_distinguir_mayusculas() -> None:
    r = _call(
        _world(),
        "insertar_bloque",
        {"nombre": "Puerta Dinamica LM", "posicion": [1, 1], "desde_biblioteca": True, "propiedades_dinamicas": {"distancegeneralvano": 0.8, "VISIBILIDAD1": "Cerrada"}},
    )
    valores = {p["nombre"]: p["valor"] for p in r["propiedades_dinamicas"]}
    assert valores["DistanceGeneralVano"] == 0.8 and valores["Visibilidad1"] == "Cerrada"


def test_insertar_bloque_propiedades_en_definicion_ya_importada() -> None:
    world = _world()
    args = {"nombre": "VENTANA01", "posicion": [1, 1], "desde_biblioteca": True}
    _call(world, "insertar_bloque", args)
    r = _call(world, "insertar_bloque", {"nombre": "VENTANA01", "posicion": [2, 1], "propiedades_dinamicas": {"Distance1": 1.5}})
    assert r["importado_de_biblioteca"] is False and r["dinamico"] is True
    assert r["propiedades_dinamicas"][0]["valor"] == 1.5


def test_insertar_bloque_definicion_dinamica_en_block_defs() -> None:
    world = _world()
    _call(world, "insertar_bloque", {"nombre": "VENTANA01", "posicion": [1, 1], "desde_biblioteca": True})
    d = next(x for x in _state(world).block_defs if x["nombre"] == "VENTANA01")
    assert d["dinamico"] is True and d["unidades_bloque"] == 6


@pytest.mark.parametrize(
    ("nombre", "props", "detalle"),
    [
        ("PuertaBaño", {"DistanceGeneralVano": 1}, "propiedades_dinamicas: el bloque 'PuertaBaño' no es dinámico"),
        ("LPS_ROTULO", {"X": 1}, "propiedades_dinamicas: el bloque 'LPS_ROTULO' no es dinámico"),
        ("VENTANA01", {"Nada": 1}, "propiedades_dinamicas: propiedad desconocida 'Nada' (propiedades del bloque: Distance1)"),
        ("Puerta Dinamica LM", {"Origin": "x"}, "propiedades_dinamicas.Origin: es de solo lectura"),
        ("Puerta Dinamica LM", {"Visibilidad1": "Otra"}, "propiedades_dinamicas.Visibilidad1: el valor Otra no está entre los admitidos (Abierta, Cerrada)"),
        ("Puerta Dinamica LM", {"DistanceGeneralVano": "a"}, "propiedades_dinamicas.DistanceGeneralVano: debe ser un número"),
        ("Puerta Dinamica LM", {"Visibilidad1": 3}, "propiedades_dinamicas.Visibilidad1: debe ser una cadena"),
        ("Puerta Dinamica LM", {"DistanceGeneralVano": -1}, "propiedades_dinamicas.DistanceGeneralVano: AutoCAD rechazó el valor (eInvalidInput)"),
    ],
)
def test_insertar_bloque_propiedades_errores_exactos(nombre: str, props: dict[str, Any], detalle: str) -> None:
    world = _world()
    antes = _handles(world)
    args = {"nombre": nombre, "posicion": [1, 1], "desde_biblioteca": True, "propiedades_dinamicas": props}
    assert _detalle(world, "insertar_bloque", args) == detalle
    assert _handles(world) == antes


def test_insertar_bloque_propiedad_desconocida_con_data_propiedades() -> None:
    args = {"nombre": "VENTANA01", "posicion": [1, 1], "desde_biblioteca": True, "propiedades_dinamicas": {"Nada": 1}}
    assert _error(_world(), "insertar_bloque", args).data["propiedades"] == ["Distance1"]


def test_insertar_bloque_en_el_dibujo_sin_propiedades_responde_igual_que_0_6_1_mas_los_campos() -> None:
    r = _call(World(18244), "insertar_bloque", {"nombre": "DP", "posicion": [0, 0], "escala_unidades": "ninguna"})
    assert r["dinamico"] is False and r["propiedades_dinamicas"] == []


def test_biblioteca_simulada_sin_capas_sobrantes() -> None:
    assert BIBLIOTECA_BLOQUES["Puerta Dinamica LM"]["capas"] == ["PUERTAS"]
    assert all("MUROS" not in b["capas"] and "Defpoints" not in b["capas"] for b in BIBLIOTECA_BLOQUES.values())


def test_biblioteca_vanos_del_lps_real_con_los_tres_bloques() -> None:
    assert list(LPS["biblioteca_vanos"]) == ["Puerta Dinamica LM", "PuertaBaño", "VENTANA01"]


# ---------------------------------------------------------------------------
# normalizar_capas: conservar_aspecto (§5G.5)
# ---------------------------------------------------------------------------

MAPA = {"mapa": {"Layer7": "MUROS"}}


def test_conservar_preview_cuenta_y_avisa_sin_el_parametro() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", MAPA)
    assert r["n_cambian_aspecto"] == 1 and r["n_aspecto_fijado"] is None
    assert advertencia_cambiarian_aspecto(1) in r["advertencias"]
    assert advertencia_cambiarian_aspecto(1) == (
        "1 entidad(es) cambiarían de aspecto al pasar a su capa destino (color, tipo de línea, grosor o transparencia "
        "heredados de la capa origen); pase conservar_aspecto: true para fijarlos en cada entidad antes de moverla."
    )


def test_conservar_preview_con_el_parametro_cuenta_y_no_avisa() -> None:
    r = _call(_world(faseL=True), "normalizar_capas", {**MAPA, "conservar_aspecto": True})
    assert r["n_cambian_aspecto"] == 1 and r["n_aspecto_fijado"] is None
    assert advertencia_cambiarian_aspecto(1) not in r["advertencias"]


def test_conservar_preview_no_cambia_el_dibujo() -> None:
    world = _world(faseL=True)
    antes = copy.deepcopy(_state(world).entities)
    _call(world, "normalizar_capas", {**MAPA, "conservar_aspecto": True})
    assert _state(world).entities == antes and _state(world).pasos_deshacer == 0


def test_conservar_escritura_fija_y_avisa() -> None:
    world = _world(faseL=True)

    async def body(c: Client) -> None:
        r = await _ok(c, "normalizar_capas", {**MAPA, "conservar_aspecto": True, "confirmar": True})
        assert r["n_aspecto_fijado"] == 1 and r["n_reasignadas"] == 1 and r["n_propiedades_forzadas"] == 0
        assert advertencia_aspecto_fijado(1, 0) in r["advertencias"]
        assert advertencia_aspecto_fijado(1, 0) == (
            "conservar_aspecto: se fijaron color, tipo de línea, grosor o transparencia en 1 entidad(es) para conservar su aspecto."
        )

    run(world.server(), body)
    e = _entidad(world, "1C5")
    assert e["capa"] == "MUROS" and (e["_color"], e["_grosor"]) == (7, -3)
    assert _state(world).pasos_deshacer == 1


def test_sin_conservar_la_escritura_no_fija_nada() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {**MAPA, "confirmar": True})
    e = _entidad(world, "1C5")
    assert r["n_aspecto_fijado"] == 0 and r["n_propiedades_forzadas"] == 0
    assert "_color" not in e and e["capa"] == "MUROS"


def test_mismo_aspecto_sigue_bylayer() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"MUEBLE": "mobiliario_fijo"}, "conservar_aspecto": True, "confirmar": True})
    assert r["n_cambian_aspecto"] == 0 and r["n_aspecto_fijado"] == 0
    assert not any("conservar_aspecto" in a for a in r["advertencias"])


def test_solo_se_fija_lo_que_difiere() -> None:
    world = _world(faseL=True)
    _state(world).capas_extra.append(_layer("MISMO", 7, 50, "fuera_de_estandar"))
    _state(world).entities.append(fake_plugin._line("2D0", "MISMO", [1.0, 1.0, 0.0], [2.0, 1.0, 0.0]))
    _call(world, "normalizar_capas", {"mapa": {"MISMO": "MUROS"}, "conservar_aspecto": True, "confirmar": True})
    e = _entidad(world, "2D0")
    assert e["_color"] == 7 and "_grosor" not in e and "_tipo_linea" not in e


def test_color_propio_no_se_toca_pero_cuenta_como_forzada() -> None:
    world = _world(faseL=True)
    args = {"mapa": {"MUEBLE": "MUROS"}, "conservar_aspecto": True}
    forzadas_preview = _call(world, "normalizar_capas", args)["n_propiedades_forzadas"]
    r = _call(world, "normalizar_capas", {**args, "confirmar": True})
    e = _entidad(world, "1C3")
    assert e["_color"] == 1 and "_grosor" in e
    assert r["n_aspecto_fijado"] == r["n_cambian_aspecto"]
    # Significado previo a 0.7: lo fijado por conservar_aspecto no suma y el conteo no supera lo reasignado.
    assert r["n_propiedades_forzadas"] == forzadas_preview == 1
    assert r["n_propiedades_forzadas"] <= r["n_reasignadas"]


def test_destino_que_se_crea_usa_las_propiedades_de_lps_json() -> None:
    world = _world(faseL=True)
    r = _call(world, "normalizar_capas", {"mapa": {"Layer7": "estructura"}, "conservar_aspecto": True})
    assert r["mapa"][0]["destino_existe"] is False and r["n_cambian_aspecto"] == 1


def test_no_imprimir_solo_se_avisa() -> None:
    world = _world(faseL=True)
    _state(world).capas_extra.append({**_layer("NOPLOT", 7, -3, "fuera_de_estandar"), "plot": False})
    _state(world).entities.append(fake_plugin._line("2D0", "NOPLOT", [1.0, 1.0, 0.0], [2.0, 1.0, 0.0]))
    r = _call(world, "normalizar_capas", {"mapa": {"NOPLOT": "MUROS"}, "conservar_aspecto": True, "confirmar": True})
    assert advertencia_aspecto_fijado(1, 1) in r["advertencias"]
    assert advertencia_aspecto_fijado(1, 1).endswith("; 1 pierden el 'no imprimir' de su capa origen (no se fija por entidad).")


def test_conservar_dentro_de_bloques_con_incluir_bloques() -> None:
    world = _world(faseL=True)
    definicion = next(d for d in _state(world).block_defs if d["nombre"] == "erere")
    definicion["_contenido"] = [fake_plugin._line("2D1", "Layer7", [0.0, 0.0, 0.0], [1.0, 0.0, 0.0])]
    con = _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "incluir_bloques": True})
    sin = _call(world, "normalizar_capas", MAPA)
    assert (con["n_cambian_aspecto"], sin["n_cambian_aspecto"]) == (2, 1)
    _call(world, "normalizar_capas", {"mapa": {"Layer7": "MUROS"}, "incluir_bloques": True, "conservar_aspecto": True, "confirmar": True})
    assert definicion["_contenido"][0]["_color"] == 7


def test_conservar_debe_ser_booleano() -> None:
    assert _detalle(_world(faseL=True), "normalizar_capas", {**MAPA, "conservar_aspecto": 1}) == "conservar_aspecto: debe ser booleano"


def test_campos_nuevos_tambien_sin_el_parametro_en_el_dibujo_faseL() -> None:
    r = _call(_world(faseL=True), "normalizar_capas", {"mapa": {"MUEBLE": "mobiliario_fijo"}})
    assert r["n_cambian_aspecto"] == 0 and r["n_aspecto_fijado"] is None
    assert not any("cambiarían de aspecto" in a for a in r["advertencias"])


# ---------------------------------------------------------------------------
# Correcciones del gate en vivo (0.7.1)
CARAS = {"muro": "2D1", "posicion": [63.0, 10.07], "ancho": 1.2, "tipo": "puerta", "forzar": True}
LAMINA = "C:\\copias\\lamina.dwg"
CAPAS_CYPE_REALES = [
    "RECUAD_LIMITES", "RECUAD_MARCO", "COLUMNAS_EJES", "COLUMNA_DE_CONCRETO", "COLUMNA_RAYA", "COLUMNA_REF", "COLUMNA_DIM",
    "PA_NUM_PERALTE", "PA_ARM_PUN", "ARR_LONG_INF", "ARR_LONG_INF_ROT", "ARR_L_I_IG_LINEA", "ZAP_CONTORNO", "ZAP_ESQUEMA_ARM",
    "ZAP_DIMENSION_Y_ARM", "PUNTO_FIJO", "TABLAS", "TEXTO_TABLAS", "LEY_TXT_ARRANQUES", "VGC_CONTORNO", "VGC_COTAS", "VGC_VARILLAS",
    "VGC_ESTRIBOS", "VGC_TEXTOS", "ALZADO_TITULO_DE_LA_TABLA_DE_DESPIECE", "ALZADO_LINEAS_DE_LA_TABLA_DE_DESPIECE",
    "ALZADO_LINEAS_DE_LA_TABLA_DE_DATOS_DE_LAS_COLUMNAS", "ALZADO_TEXTOS_DE_LA_TABLA_DE_DATOS_DE_LAS_COLUMNAS",
    "ALZADO_NOMBRES_DE_LAS_PLANTAS", "ALZADO_LOSAS", "ALZADO_PILAR_DE_HORMIGON", "ALZADO_SEGMENTOS_DE_CORTE_DE_SECCIONES",
    "PILCP_ARM_LONG_LINEAS_DE_LAS_ETIQUETAS_DE_LAS_POSICIONES_DE_ARMADO_LONGITUDINAL", "PILCP_ARM_LONG_REFERENCIA",
    "ALZADO_TITULOS_DE_LOS_ALZADOS_DE_LAS_COLUMNAS", "ALZADO_COTAS_DE_LAS_PLANTAS", "ALZADO_COTAS_DE_LAS_LOSAS", "ALZADO_COTAS",
    "ALZADO_TEXTO_DE_COTAS", "SECC_CONCRETO", "PILCPSECC_ARM_LONG_REFERENCIA",
    "PILCPSECC_ARM_LONG_LINEAS_DE_LAS_ETIQUETAS_DE_LAS_POSICIONES_DE_ARMADO_LONGITUDINAL", "SECC_COTAS",
    "SECC_LINEAS_DE_PROLONGACION_DE_LAS_COTAS", "ALZADO_TEXTOS_DE_LA_TABLA_DE_CUANTIFICACION",
    "ALZADO_LINEAS_DE_LA_TABLA_DE_CUANTIFICACION", "ALZADO_LINEAS_DE_LA_TABLA_DE_CUANTIFICACION_DEL_CUADRO_DE_COLUMNAS",
    "ALZADO_TEXTOS_DE_LA_TABLA_DE_CUANTIFICACION_DEL_CUADRO_DE_COLUMNAS",
]


def _muro_de_caras(world: World, capa: str = "MCP-DIBUJO") -> None:
    """Medidas de 946C5 / 946C6: caras de 6 y 5,7 m a 0,15, la corta metida 0,15 en cada extremo."""
    _agregar(world, fake_plugin._line("2D1", capa, [60.0, 10.15, 0.0], [66.0, 10.15, 0.0]))
    _agregar(world, fake_plugin._line("2D2", capa, [65.85, 10.0, 0.0], [60.15, 10.0, 0.0]))


def _longitudes(r: dict[str, Any]) -> list[float]:
    return [round(x["longitud"], 6) for x in r["numeros"]["lineas"]]


def test_schema_vano_admite_cara_opuesta_opcional() -> None:
    schema = next(t for t in manifest_tools() if t["name"] == "vano")
    assert "cara_opuesta" in schema["inputSchema"]["properties"] and "cara_opuesta" not in schema["inputSchema"]["required"]
    assert "cara_opuesta" in (HANDLERS / "Gen" / "VanoHandler.cs").read_text(encoding="utf-8")


def test_vano_en_caras_line_parte_en_cuatro_con_dos_tapas() -> None:
    world = _world()
    _muro_de_caras(world)
    r = _call(world, "vano", {**CARAS, "cara_opuesta": "2D2"})
    assert r["handles_borrados"] == ["2D1", "2D2"] and not {"2D1", "2D2"} & _handles(world)
    assert [e["tipo"] for e in r["entidades"]] == ["LINE"] * 6 + ["INSERT"]
    n = r["numeros"]
    assert n["hueco"]["espesor"] == pytest.approx(0.15) and n["hueco"]["longitud_tramo"] == pytest.approx(5.7)
    assert n["hueco"]["direccion"] == pytest.approx(0.0) and n["contornos"] == []
    assert _longitudes(r) == [2.4, 2.4, 2.25, 2.25]
    assert [x["origen"] for x in n["lineas"]] == ["2D1", "2D1", "2D2", "2D2"]
    assert n["muro_original"] == {"handle": "2D1", "capa": "MCP-DIBUJO", "area": None, "n_vertices": 2, "cara_opuesta": "2D2"}
    assert n["n_entidades"] == 7 and n["bloque"]["ancho_por"] == "propiedad"
    tapas = [_entidad(world, e["handle"]) for e in r["entidades"][4:6]]
    assert [t["longitud"] if "longitud" in t else None for t in tapas] == [pytest.approx(0.15)] * 2


def test_vano_en_caras_line_busca_la_paralela_mas_cercana() -> None:
    world = _world()
    _muro_de_caras(world)
    _agregar(world, fake_plugin._line("2D3", "MCP-DIBUJO", [60.0, 10.4, 0.0], [66.0, 10.4, 0.0]))
    _agregar(world, fake_plugin._line("2D4", "MUROS", [60.0, 10.1, 0.0], [66.0, 10.1, 0.0]))
    r = _call(world, "vano", CARAS)
    assert r["numeros"]["muro_original"]["cara_opuesta"] == "2D2"


def test_vano_sin_cara_candidata_rechaza() -> None:
    world = _world()
    _agregar(world, fake_plugin._line("2D1", "MCP-DIBUJO", [60.0, 10.15, 0.0], [66.0, 10.15, 0.0]))
    _agregar(world, fake_plugin._line("2D2", "MCP-DIBUJO", [60.0, 9.7, 0.0], [66.0, 9.7, 0.0]))
    assert _detalle(world, "vano", CARAS) == detalle_ninguna_cara("2D1", "MCP-DIBUJO")


def test_vano_con_varias_caras_candidatas_las_lista() -> None:
    world = _world()
    _muro_de_caras(world)
    _agregar(world, fake_plugin._line("2D3", "MCP-DIBUJO", [60.0, 10.3, 0.0], [66.0, 10.3, 0.0]))
    err = _error(world, "vano", CARAS)
    assert err.data["detalle"] == detalle_varias_caras("MCP-DIBUJO", [("2D2", 0.15), ("2D3", 0.15)])
    assert err.data["candidatas"] == ["2D2", "2D3"]


def test_vano_cara_partida_por_otro_vano_elige_el_trozo_que_abarca_el_hueco() -> None:
    world = _world()
    _agregar(world, fake_plugin._line("2D1", "MCP-DIBUJO", [60.0, 10.15, 0.0], [66.0, 10.15, 0.0]))
    _agregar(world, fake_plugin._line("2D2", "MCP-DIBUJO", [60.0, 10.0, 0.0], [62.0, 10.0, 0.0]))
    _agregar(world, fake_plugin._line("2D3", "MCP-DIBUJO", [62.9, 10.0, 0.0], [66.0, 10.0, 0.0]))
    r = _call(world, "vano", {**CARAS, "posicion": [64.5, 10.07], "ancho": 0.8})
    assert r["numeros"]["muro_original"]["cara_opuesta"] == "2D3"
    assert _longitudes(r)[2:] == [1.2, 1.1]


@pytest.mark.parametrize(
    ("cambio", "detalle"),
    [
        ({"cara_opuesta": "2D1"}, "cara_opuesta: debe ser distinta de muro"),
        ({"cara_opuesta": "2BD"}, "cara_opuesta: la entidad 2BD no es una LINE (CIRCLE)"),
        ({"muro": "2B1", "cara_opuesta": "2D2"}, "cara_opuesta: solo aplica si muro es una LINE"),
        ({"cara_opuesta": "2D5"}, "cara_opuesta: la línea 2D5 no es paralela a la cara del muro"),
        ({"cara_opuesta": "2D6"}, "cara_opuesta: las líneas 2D1 y 2D6 no se solapan a lo largo del tramo"),
        ({"cara_opuesta": "2D7"}, "cara_opuesta: las líneas 2D1 y 2D7 coinciden"),
        ({"cara_opuesta": "2D2", "posicion": {"distancia": 7}}, "posicion: la distancia 7 se sale del tramo (longitud 5.7)"),
    ],
)
def test_vano_caras_line_invalid_params(cambio: dict[str, Any], detalle: str) -> None:
    world = _world()
    _muro_de_caras(world)
    _agregar(world, fake_plugin._line("2D5", "MCP-DIBUJO", [60.0, 10.0, 0.0], [66.0, 11.0, 0.0]))
    _agregar(world, fake_plugin._line("2D6", "MCP-DIBUJO", [70.0, 10.0, 0.0], [71.0, 10.0, 0.0]))
    _agregar(world, fake_plugin._line("2D7", "MCP-DIBUJO", [61.0, 10.15, 0.0], [65.0, 10.15, 0.0]))
    assert _detalle(world, "vano", {**CARAS, **cambio}) == detalle


@pytest.mark.parametrize(
    ("cambio", "detalle"),
    [
        ({"posicion": [60.3, 10.07]}, "ancho: el hueco (1.2) no cabe en el tramo (longitud 5.7): se sale del tramo o cruza una esquina"),
        ({"ancho": 5.7}, "ancho: debe ser menor que la longitud del tramo"),
    ],
)
def test_vano_caras_line_hueco_fuera_del_solape(cambio: dict[str, Any], detalle: str) -> None:
    world = _world()
    _muro_de_caras(world)
    assert _detalle(world, "vano", {**CARAS, "cara_opuesta": "2D2", **cambio}, "INVALID_GEOMETRY") == detalle


def test_vano_caras_line_posicion_por_distancia_desde_el_solape() -> None:
    world = _world()
    _muro_de_caras(world)
    r = _call(world, "vano", {**CARAS, "cara_opuesta": "2D2", "posicion": {"distancia": 2.85}, "ancho": 0.8})
    assert r["numeros"]["hueco"]["distancia_desde_inicio"] == pytest.approx(2.85)
    assert r["numeros"]["hueco"]["centro"] == pytest.approx([63.0, 10.075])


def test_vano_caras_line_exige_confirmar_ya_en_la_preview() -> None:
    world = _world()
    _muro_de_caras(world, "MUROS")
    detalle = "confirmar: 2 de las 2 entidades no están en capas MCP-* (p. ej. 2D1 en MUROS, 2D2 en MUROS); pase confirmar: true para modificarlas"
    assert _detalle(world, "vano", {**CARAS, "preview": True}) == detalle


def test_vano_caras_line_preview_no_toca_las_lineas_y_confirmar_las_parte() -> None:
    world = _world()
    _muro_de_caras(world)
    pasos = _state(world).pasos_deshacer
    p = _call(world, "vano", {**CARAS, "preview": True})
    assert p["handles_borrados"] == [] and {"2D1", "2D2"} <= _handles(world)
    assert {e["capa"] for e in p["entidades"]} == {CAPA_PREVIEW}
    c = _call(world, "confirmar", {"preview_id": p["preview_id"]})
    assert c["handles_borrados"] == ["2D1", "2D2"] and not {"2D1", "2D2"} & _handles(world)
    assert _state(world).pasos_deshacer == pasos + 2


@pytest.mark.parametrize("cambiada", ["2D1", "2D2"])
def test_vano_caras_line_ancla_de_cada_cara(cambiada: str) -> None:
    world = _world()
    _muro_de_caras(world)
    p = _call(world, "vano", {**CARAS, "preview": True, "dibujar_preview": False})
    _call(world, "mover", {"handles": [cambiada], "desplazamiento": [0.01, 0], "confirmar": True, "forzar": True})
    assert _detalle(world, "confirmar", {"preview_id": p["preview_id"]}) == detalle_ancla_cambiada(cambiada)


def test_vano_caras_line_conservan_aspecto_y_las_tapas_van_bylayer() -> None:
    world = _world()
    _muro_de_caras(world)
    aspecto = {"_color": 3, "_grosor": 25, "_tipo_linea": "HIDDEN"}
    _entidad(world, "2D1").update(aspecto)
    r = _call(world, "vano", {**CARAS, "cara_opuesta": "2D2"})
    piezas = [_entidad(world, e["handle"]) for e in r["entidades"][:6]]
    assert all(piezas[i]["_color"] == 3 and piezas[i]["_tipo_linea"] == "HIDDEN" for i in (0, 1))
    assert not any("_color" in piezas[i] for i in (2, 3, 4, 5))


def test_vano_caras_line_tipo_hueco_sin_bloque() -> None:
    world = _world()
    _muro_de_caras(world)
    r = _call(world, "vano", {**CARAS, "tipo": "hueco"})
    assert [e["tipo"] for e in r["entidades"]] == ["LINE"] * 6 and r["numeros"]["bloque"] is None


def test_vano_caras_line_hatch_de_las_dos_caras_se_reparte_en_dos_bucles() -> None:
    world = _world()
    _agregar(world, fake_plugin._line("2D1", "MCP-DIBUJO", [60.0, 10.15, 0.0], [66.0, 10.15, 0.0]))
    _agregar(world, fake_plugin._line("2D2", "MCP-DIBUJO", [60.0, 10.0, 0.0], [66.0, 10.0, 0.0]))
    _agregar(world, fake_plugin._line("2D3", "MCP-DIBUJO", [60.0, 10.0, 0.0], [60.0, 10.15, 0.0]))
    _agregar(world, fake_plugin._line("2D4", "MCP-DIBUJO", [66.0, 10.0, 0.0], [66.0, 10.15, 0.0]))
    hatch = copy.deepcopy(_entidad(world, "2BA"))
    hatch.update({"handle": "2D5", "_bucles": [["2D1", "2D2", "2D3", "2D4"]], "_contorno": ["2D1", "2D2", "2D3", "2D4"]})
    _agregar(world, hatch)
    r = _call(world, "vano", {**CARAS, "cara_opuesta": "2D2", "ancho": 0.8})
    assert r["handles_borrados"] == ["2D1", "2D2", "2D5"] and r["numeros"]["n_hatch_rehechos"] == 1
    h = _entidad(world, next(e["handle"] for e in r["entidades"] if e["tipo"] == "HATCH"))
    assert {"2D3", "2D4"} <= set(h["_contorno"])


def test_vano_caras_line_hatch_de_una_sola_cara_avisa() -> None:
    world = _world()
    _muro_de_caras(world)
    _agregar(world, fake_plugin._line("2D3", "MCP-DIBUJO", [60.0, 12.0, 0.0], [66.0, 12.0, 0.0]))
    hatch = copy.deepcopy(_entidad(world, "2BA"))
    hatch.update({"handle": "2D5", "_bucles": [["2D1", "2D3"]], "_contorno": ["2D1", "2D3"]})
    _agregar(world, hatch)
    r = _call(world, "vano", {**CARAS, "cara_opuesta": "2D2"})
    assert advertencia_hatch_no_reconstruible("2D5") in r["advertencias"] and "2D5" in _handles(world)


@pytest.mark.parametrize("con_caras", [True, False])
def test_vano_sobre_columna_avisa(con_caras: bool) -> None:
    world = _world()
    columna = fake_plugin._line("2D9", "columnas", [62.9, 10.0, 0.0], [63.1, 10.15, 0.0])
    _agregar(world, columna)
    if con_caras:
        _muro_de_caras(world)
        args = {**CARAS, "cara_opuesta": "2D2"}
    else:
        _agregar(world, muro_rectangular("2C1", "MCP-DIBUJO", 60.0, 10.0, 66.0, 10.15))
        args = {**CARAS, "muro": "2C1"}
    assert advertencia_columnas("columnas", ["2D9"]) in _call(world, "vano", args)["advertencias"]


def test_vano_lejos_de_la_columna_no_avisa() -> None:
    world = _world()
    _muro_de_caras(world)
    _agregar(world, fake_plugin._line("2D9", "columnas", [65.0, 10.0, 0.0], [65.2, 10.15, 0.0]))
    assert not any("columnas" in a for a in _call(world, "vano", {**CARAS, "cara_opuesta": "2D2"})["advertencias"])


def test_advertencia_columnas_cita_cinco_y_cuenta_el_resto() -> None:
    assert advertencia_columnas("columnas", list("ABCDEFG")) == (
        "vano: el hueco cae sobre 7 entidad(es) de la capa 'columnas' (A, B, C, D, E y 2 más): revise la estructura antes de abrirlo."
    )


def test_vano_ventana_la_propiedad_recibe_ancho_mas_ajuste() -> None:
    r = _call(_world(), "vano", {**VANO, "tipo": "ventana", "ancho": 1.2})
    assert r["numeros"]["bloque"]["catalogo"]["ajuste_ancho"] == 0.0045
    assert next(p for p in r["propiedades_dinamicas"] if p["nombre"] == "Distance1")["valor"] == pytest.approx(1.2045)


def test_vano_puerta_sin_ajuste() -> None:
    r = _call(_world(), "vano", VANO)
    assert r["numeros"]["bloque"]["catalogo"]["ajuste_ancho"] == 0
    assert next(p for p in r["propiedades_dinamicas"] if p["nombre"] == "DistanceGeneralVano")["valor"] == pytest.approx(0.8)


def test_lps_json_tiene_nombres_reales_y_ajuste() -> None:
    c = LPS["biblioteca_vanos"]
    assert (c["Puerta Dinamica LM"]["propiedad_ancho"], c["VENTANA01"]["propiedad_ancho"]) == ("DistanceGeneralVano", "Distance1")
    assert (c["Puerta Dinamica LM"]["ajuste_ancho"], c["PuertaBaño"]["ajuste_ancho"], c["VENTANA01"]["ajuste_ancho"]) == (0, 0, 0.0045)


def test_vano_ventana_deduce_con_el_nominal_mas_ajuste(monkeypatch: pytest.MonkeyPatch) -> None:
    catalogo = copy.deepcopy(LPS["biblioteca_vanos"])
    catalogo["VENTANA01"]["propiedad_ancho"] = None
    monkeypatch.setitem(LPS, "biblioteca_vanos", catalogo)
    b = _call(_world(), "vano", {**VANO, "tipo": "ventana"})["numeros"]["bloque"]
    assert (b["ancho_por"], b["propiedad_ancho"]) == ("propiedad", "Distance1")


def test_insert_dinamico_lee_la_definicion_en_el_plugin() -> None:
    fuente = (HANDLERS / "Draw" / "DynamicBlockSupport.cs").read_text(encoding="utf-8")
    assert "br.DynamicBlockTableRecord" in fuente


def test_export_lamina_con_auto_se_rechaza_sin_escribir() -> None:
    world = _world()
    antes = _handles(world)
    err = _error(world, "insertar_export", {"ruta": LAMINA, "posicion": [1, 1]})
    assert err.data["detalle"] == (
        "escala_unidades: el export es una lámina de papel en centímetros (A1, A3) a escala 1:50, no un modelo a escala real: su "
        "INSUNITS no sirve y el factor automático la dejaría a una escala equivocada. Pase escala: 0.5 y escala_unidades: 'ninguna'"
    )
    assert err.data["escalas"] == [50] and err.data["escalas_sugeridas"] == pytest.approx([0.5])
    assert err.data["formatos"] == ["A1", "A3"] and _handles(world) == antes and _state(world).pasos_deshacer == 0


def test_export_lamina_con_varias_escalas_las_lista() -> None:
    detalle = _detalle(_world(), "insertar_export", {"ruta": "C:\\copias\\lamina_escalas.dwg", "posicion": [1, 1]})
    assert "con varias escalas (1:20 → 0.2, 1:50 → 0.5)" in detalle and "Elija la del plano" in detalle


def test_export_lamina_sin_escala_lo_dice() -> None:
    detalle = _detalle(_world(), "insertar_export", {"ruta": "C:\\copias\\lamina_sin_escala.dwg", "posicion": [1, 1]})
    assert "sin ningún texto 'Escala 1:N' legible" in detalle and "Pase escala: N × 0.01" in detalle


def test_export_lamina_con_ninguna_entra_a_la_escala_pedida() -> None:
    r = _call(_world(), "insertar_export", {"ruta": LAMINA, "posicion": [1, 1], "escala": 0.5, "escala_unidades": "ninguna", "forzar": True})
    assert r["factor_unidades"] == 1.0 and r["escala_aplicada"] == [0.5, 0.5, 0.5]
    assert r["entidad"]["bbox"] == [[1.0, 1.0], [43.0, pytest.approx(30.7)]]


def test_export_factor_es_uno_con_ninguna() -> None:
    r = _call(_world(), "insertar_export", {"ruta": EXPORT, "posicion": [1, 1], "escala_unidades": "ninguna", "forzar": True})
    assert r["factor_unidades"] == 1.0 and r["unidades_export"] == 4


def test_export_r4_mira_la_caja_real_y_no_solo_el_punto() -> None:
    world = _world()
    # El punto queda muy lejos, pero la caja de la lámina llega al dibujo: R4 pregunta si la caja lo toca.
    cerca = {"ruta": LAMINA, "posicion": [-20000, -20000], "escala": 500.0, "escala_unidades": "ninguna"}
    assert _call(world, "insertar_export", cerca)["entidad"]["bbox"] is not None
    lejos = {"ruta": LAMINA, "posicion": [90000, 90000], "escala": 0.5, "escala_unidades": "ninguna", "nombre_bloque": "OTRO"}
    assert "fuera de la extensión" in _detalle(_world(), "insertar_export", lejos)


def test_export_bbox_aplica_la_rotacion() -> None:
    args = {"ruta": LAMINA, "posicion": [0, 0], "escala": 0.5, "escala_unidades": "ninguna", "rotacion": 90, "forzar": True}
    caja = _call(_world(), "insertar_export", args)["entidad"]["bbox"]
    assert caja == [[pytest.approx(-29.7), pytest.approx(0.0, abs=1e-9)], [pytest.approx(0.0, abs=1e-9), pytest.approx(42.0)]]


def test_export_plugin_usa_la_caja_real_para_r4_y_bbox() -> None:
    fuente = (HANDLERS / "Draw" / "InsertarExportHandler.cs").read_text(encoding="utf-8")
    assert "v.Caja ?? PlacementCheck.PointBox(posicion)" in fuente and "AcadUtil.TryBBox2(br) ?? v.Caja" in fuente
    assert 'auto ? factor ?? 1.0 : 1.0' in fuente


def test_lps_json_cubre_todas_las_capas_del_export_real() -> None:
    patrones = LPS["grupos_externos_permitidos"]["cype"]

    def es_cype(c: str) -> bool:
        return any(c.casefold().startswith(p[:-1].casefold()) if p.endswith("*") else c.casefold() == p.casefold() for p in patrones)

    assert len(CAPAS_CYPE_REALES) == 48
    assert [c for c in CAPAS_CYPE_REALES if not es_cype(c)] == []
    assert not es_cype("TEXTO_CYPE") and not es_cype("MUROS")


def test_timeout_de_vista_png_en_el_plugin_y_en_el_servidor() -> None:
    fuente = (HANDLERS / "VistaPngHandler.cs").read_text(encoding="utf-8")
    assert "public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs" in fuente
    cliente = PipeClient(None, timeout=15.0)  # type: ignore[arg-type]
    assert cliente.timeout_for("vista_png") == 75.0 and cliente.timeout_for("vano") == 15.0
    largo = PipeClient(None, timeout=120.0)  # type: ignore[arg-type]
    assert largo.timeout_for("vista_png") == 120.0


def test_timeout_de_insertar_export_en_el_plugin_y_en_el_servidor() -> None:
    fuente = (HANDLERS / "Draw" / "InsertarExportHandler.cs").read_text(encoding="utf-8")
    assert "public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs" in fuente
    assert '" ya se insertó antes de repetir; un CTRL+Z lo retira."' in fuente
    assert PipeClient(None, timeout=15.0).timeout_for("insertar_export") == 75.0  # type: ignore[arg-type]


def test_insertar_export_lee_el_archivo_una_sola_vez() -> None:
    fuente = (HANDLERS / "Draw" / "InsertarExportHandler.cs").read_text(encoding="utf-8")
    assert fuente.count("ReadExport(p.Ruta)") == 1 and fuente.count("ScanSide(side)") == 1
    # UndoGroup.Run puede devolver una tarea pendiente: el finally de Execute no debe descartar la base en ese caso.
    assert "resultado is Task { IsCompleted: false }" in fuente and "DisposeSide(v.Side)" in fuente


def test_vano_avisa_del_sombreado_que_cruza_el_hueco_aunque_no_sea_asociativo() -> None:
    world = _world()
    _muro_de_caras(world)
    hatch = copy.deepcopy(_entidad(world, "2BA"))
    hatch.update({"handle": "2D8", "asociativo": False, "_contorno": [], "bbox": [[60.0, 9.0, 0.0], [66.0, 11.0, 0.0]]})
    _agregar(world, hatch)
    r = _call(world, "vano", {**CARAS, "cara_opuesta": "2D2"})
    assert advertencia_sombreados_en_hueco(["2D8"]) in r["advertencias"]
    assert advertencia_sombreados_en_hueco(["2D8"]) == "vano: 1 sombreado(s) cruzan el hueco y no se rehacen (2D8): revíselos."
    assert "2D8" in _handles(world) and "2D8" not in r["handles_borrados"]


def test_vano_no_avisa_del_sombreado_lejano_ni_del_que_se_rehace() -> None:
    world = _world()
    _muro_de_caras(world)
    hatch = copy.deepcopy(_entidad(world, "2BA"))
    hatch.update({"handle": "2D8", "asociativo": False, "_contorno": [], "bbox": [[64.5, 9.0, 0.0], [66.0, 11.0, 0.0]]})
    _agregar(world, hatch)
    assert not any("cruzan el hueco" in a for a in _call(world, "vano", {**CARAS, "cara_opuesta": "2D2"})["advertencias"])
    assert not any("cruzan el hueco" in a for a in _call(_world(), "vano", VANO)["advertencias"])


def test_vano_rechazo_con_cara_automatica_dice_cual_se_eligio() -> None:
    world = _world()
    _muro_de_caras(world)
    err = _error(world, "vano", {**CARAS, "posicion": [60.3, 10.07]})
    assert err.code == "INVALID_GEOMETRY" and err.data["cara_opuesta"] == "2D2"


def test_vano_rechazo_con_cara_indicada_no_la_repite() -> None:
    world = _world()
    _muro_de_caras(world)
    assert "cara_opuesta" not in _error(world, "vano", {**CARAS, "cara_opuesta": "2D2", "posicion": [60.3, 10.07]}).data


def test_vano_busqueda_prefiere_la_que_cubre_el_hueco_a_la_mas_cercana() -> None:
    world = _world()
    _agregar(world, fake_plugin._line("2D1", "MCP-DIBUJO", [60.0, 10.15, 0.0], [66.0, 10.15, 0.0]))
    _agregar(world, fake_plugin._line("2D2", "MCP-DIBUJO", [60.0, 10.05, 0.0], [62.0, 10.05, 0.0]))
    _agregar(world, fake_plugin._line("2D3", "MCP-DIBUJO", [60.0, 10.0, 0.0], [66.0, 10.0, 0.0]))
    assert _call(world, "vano", CARAS)["numeros"]["muro_original"]["cara_opuesta"] == "2D3"


def test_vano_margen_minimo_de_las_piezas() -> None:
    world = _world()
    _muro_de_caras(world)
    # 0,005 de pieza con respecto a la cara corta: no cabe; con 0,02, sí.
    detalle = _detalle(world, "vano", {**CARAS, "cara_opuesta": "2D2", "posicion": [60.755, 10.07]}, "INVALID_GEOMETRY")
    assert detalle == "ancho: el hueco (1.2) no cabe en el tramo (longitud 5.7): se sale del tramo o cruza una esquina"
    assert _call(world, "vano", {**CARAS, "cara_opuesta": "2D2", "posicion": [60.77, 10.07]})["numeros"]["hueco"]["ancho"] == 1.2


def test_is_dynamic_usa_la_transaccion_del_llamador() -> None:
    fuente = (HANDLERS / "Draw" / "DynamicBlockSupport.cs").read_text(encoding="utf-8")
    assert "IsDynamic(BlockReference br, Transaction tr)" in fuente and "TopTransaction" not in fuente.split("public static bool IsDynamic")[1]
