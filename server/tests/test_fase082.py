"""Servidor 0.8.2 (protocol.md, cabecera "Cambios servidor 0.8.1 → 0.8.2", §5H.2, §5H.3, §5H.5).

1. Respaldo accoreconsole cuando ODA no genera el DXF, con un accoreconsole SIMULADO (nunca el real: conftest fija
   MCP_AUTOCAD_ACCORE en una ruta inexistente), y errores distinguibles: vacío, dañado, ODA sin respaldo.
2. Umbrales de unidad probable: piso de cota de mm en 0,02 m y extensión descartada en planos
   mezclados o con extensión absurda. Los vectores "medidos" se midieron el 2026-11-04 sobre copias reales.
3. Plazo por defecto 300 s, también para accoreconsole.
"""

from __future__ import annotations

import json
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any

import pytest

import conftest
from mcp_autocad import lote as lote_mod
from mcp_autocad import server as server_mod
from mcp_autocad.errors import PluginError
from mcp_autocad.oda import audit, convert
from mcp_autocad.oda.audit import auditar_archivo_oda
from mcp_autocad.oda.convert import OdaCanceladoError, OdaConversionError, OdaTimeoutError, dwg_a_dxf
from test_fase5 import Auditor, carpeta_obra, correr, fila, hacer_lote, respuesta, servidor, World
from test_oda_audit import FAKE_DXF, fake_oda, new_doc, oda_args

RAIZ = Path(__file__).resolve().parents[2]
PROTOCOL_MD = RAIZ / "protocol.md"

# ODA falso que, como el real con un DWG que no lee, sale con 0 y deja el motivo en <nombre>.dxf.err.
ODA_NO_LEE = "(salida / 'entrada.dxf.err').write_text('OdError thrown during readFile')\n"

# Salida de accoreconsole 2026 ante un DWG dañado (copiada de la prueba real con copias, 2026-11-04).
SALIDA_RECHAZO = (
    "AutoCAD Core Engine Console - Copyright 2025 Autodesk, Inc.  All rights reserved. (W.179.0.0)\r\n"
    "************************MessageBox****************************\r\n"
    "Mensaje de AutoCAD\r\nArchivo de dibujo no válido.\r\n>>>Responding: OK.\r\n"
    "ERROR: Something went wrong. ErrorStatus=434 (AcCoreConsole.cpp:1711).\r\n"
)


def fake_accore(tmp_path: Path, body: str) -> Path:
    """accoreconsole falso (.bat + script). Registra argv y el guion, y ejecuta ``body`` con ``salida_dxf`` (la ruta
    entre comillas de la 2.ª línea del guion) y ``escribir(texto)`` (stdout en UTF-16 LE, como el real)."""
    folder = tmp_path / "accore_falso"
    folder.mkdir()
    script = folder / "accore_falso.py"
    script.write_text(
        "import json, os, sys\n"
        "from pathlib import Path\n"
        "args = sys.argv[1:]\n"
        "guion = Path(args[args.index('/s') + 1])\n"
        # Como AutoCAD (confirmado por Fable con AutoCAD 2026): el guion se lee en la página de códigos ANSI, no en UTF-8.
        "texto = guion.read_bytes().decode('mbcs')\n"
        "salida_dxf = Path(texto.split('\\r\\n')[1].strip('\"'))\n"
        f"registro = Path({str(folder / 'registro.json')!r})\n"
        "registro.write_text(json.dumps({'argv': args, 'cwd': os.getcwd(), 'guion': texto,\n"
        "    'temp': os.environ.get('TEMP'), 'tmp': os.environ.get('TMP'),\n"
        "    'copia_existe': Path(args[args.index('/i') + 1]).is_file()}))\n"
        "def escribir(t):\n"
        "    sys.stdout.buffer.write(t.encode('utf-16-le')); sys.stdout.flush()\n" + body,
        encoding="utf-8",
    )
    bat = folder / "accoreconsole.bat"
    bat.write_text(f'@"{sys.executable}" -I "{script}" %*\n@exit /b %errorlevel%\n', encoding="ascii")
    return bat


ACCORE_ESCRIBE_DXF = f"escribir('Comando: _.DXFOUT\\r\\n'); salida_dxf.write_text({FAKE_DXF!r})\n"
ACCORE_RECHAZA = f"escribir({SALIDA_RECHAZO!r}); sys.exit(434)\n"


@pytest.fixture
def dwg(tmp_path: Path) -> Path:
    folder = tmp_path / "origen"
    folder.mkdir()
    path = folder / "PLANO año (1).dwg"
    path.write_bytes(b"AC1027" + b"\0" * 100)
    return path


def registro(exe: Path) -> dict[str, Any]:
    return json.loads((exe.parent / "registro.json").read_text())


# ---------------------------------------------------------------------------
# 1. Respaldo accoreconsole
# ---------------------------------------------------------------------------


def test_pytest_nunca_ve_el_accoreconsole_real() -> None:
    assert convert.accore_exe_por_defecto() == Path(conftest.ACCORE_INEXISTENTE)
    assert not Path(conftest.ACCORE_INEXISTENTE).exists()


def test_ruta_de_accoreconsole_por_defecto_y_por_entorno(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("MCP_AUTOCAD_ACCORE", raising=False)
    assert convert.accore_exe_por_defecto() == Path(r"D:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe")
    monkeypatch.setenv("MCP_AUTOCAD_ACCORE", r"E:\acad\accoreconsole.exe")
    assert convert.accore_exe_por_defecto() == Path(r"E:\acad\accoreconsole.exe")


def test_guion_y_argumentos_de_accoreconsole(tmp_path: Path) -> None:
    salida = tmp_path / "con espacio" / "entrada.dxf"
    assert convert.guion_accore(salida) == f'_.DXFOUT\r\n"{salida}"\r\n_V\r\n2018\r\n16\r\n_.QUIT\r\n_Y\r\n'
    assert "\n\n" not in convert.guion_accore(salida).replace("\r\n", "\n")  # una línea vacía repetiría el comando
    args = convert.accore_args(Path("acc.exe"), Path("c.dwg"), Path("g.scr"), Path("perfil"))
    assert args == ["acc.exe", "/isolate", "mcp_autocad", "perfil", "/i", "c.dwg", "/s", "g.scr"]


def test_oda_no_lee_y_accoreconsole_convierte_la_misma_copia(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    work = tmp_path / "trabajo"
    work.mkdir()
    info: dict[str, Any] = {}
    antes = time.monotonic()
    dxf = dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=30, accore_exe=accore, info=info)
    assert dxf == work / "oda_out" / "entrada.dxf" and dxf.read_text() == FAKE_DXF
    assert info["conversor"] == "accoreconsole" and info["inicio"] >= antes
    r = registro(accore)
    copia = work / "oda_in" / "entrada.dwg"
    assert r["argv"] == ["/isolate", "mcp_autocad", str(work / "accore" / "perfil"), "/i", str(copia), "/s", str(work / "accore" / "dxfout.scr")]
    assert r["copia_existe"] and Path(r["cwd"]) == work / "accore"
    assert r["guion"] == convert.guion_accore(work / "oda_out" / "entrada.dxf")
    # Solo queda el DXF: ni la copia, ni el registro de ODA, ni su .err, ni la carpeta de accoreconsole.
    assert sorted(p.name for p in work.iterdir()) == ["oda_out"]
    assert sorted(p.name for p in (work / "oda_out").iterdir()) == ["entrada.dxf"]
    assert dwg.read_bytes().startswith(b"AC1027")


def test_oda_que_lee_no_lanza_accoreconsole(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, f"(salida / 'entrada.dxf').write_text({FAKE_DXF!r})\n")
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    info: dict[str, Any] = {}
    dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=oda, timeout_s=30, accore_exe=accore, info=info)
    assert info["conversor"] == "oda"
    assert not (accore.parent / "registro.json").exists()


def test_autocad_rechaza_el_dibujo_es_archivo_danado(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, ACCORE_RECHAZA)
    work = tmp_path / "trabajo"
    work.mkdir()
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=30, accore_exe=accore)
    e = info.value
    assert e.code == "INTERNAL" and str(e) == f"[INTERNAL] {convert.MENSAJE_DANADO}"
    assert e.data["motivo"] == "archivo_danado"
    assert "OdError thrown during readFile" in e.data["detalle"] and "ErrorStatus=434" in e.data["detalle"]
    assert "Archivo de dibujo no válido." in e.data["detalle"]  # UTF-16 decodificado
    assert str(work / "oda_in") not in e.data["detalle"]
    assert list(work.iterdir()) == []


def test_accoreconsole_sin_dxf_ni_rechazo_es_fallo_repetible(tmp_path: Path, dwg: Path) -> None:
    # Como la ruta mal codificada que encontró Fable: "Imposible abrir el archivo", código 0 y sin DXF.
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, "escribir('Imposible abrir el archivo.\\r\\nComando: _.QUIT\\r\\n')\n")
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=oda, timeout_s=30, accore_exe=accore)
    assert info.value.plugin_message == convert.MENSAJE_ACCORE_FALLO and info.value.data["motivo"] == "accoreconsole_fallo"
    assert "accoreconsole (código de salida 0)" in info.value.detalle and "Imposible abrir el archivo." in info.value.detalle


def test_codigo_distinto_de_cero_sin_evidencia_no_es_danado(tmp_path: Path, dwg: Path) -> None:
    # Licencia, arranque o cierre inesperado de AutoCAD: no se marca dañado un plano que puede ser válido.
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, "escribir('Licencia no disponible\\r\\n'); sys.exit(-1073741819)\n")
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=oda, timeout_s=30, accore_exe=accore)
    assert info.value.data["motivo"] == "accoreconsole_fallo" and info.value.plugin_message == convert.MENSAJE_ACCORE_FALLO


def test_dxf_con_codigo_distinto_de_cero_se_descarta(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, f"salida_dxf.write_text({FAKE_DXF[:20]!r}); sys.exit(5)\n")  # DXF truncado
    work = tmp_path / "trabajo"
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=30, accore_exe=accore)
    assert info.value.data["motivo"] == "accoreconsole_fallo" and "DXF descartado" in info.value.detalle
    assert list(work.iterdir()) == []


def test_dxf_de_oda_con_codigo_distinto_de_cero_pasa_a_accoreconsole(tmp_path: Path, dwg: Path) -> None:
    # ODA sale con 0 al convertir y al no leer (medido en 14 planos): un código ≠ 0 no es una conversión normal.
    oda = fake_oda(tmp_path, f"(salida / 'entrada.dxf').write_text({FAKE_DXF[:20]!r}); sys.exit(1)\n")
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    info: dict[str, Any] = {}
    dxf = dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=oda, timeout_s=30, accore_exe=accore, info=info)
    assert info["conversor"] == "accoreconsole" and dxf.read_text() == FAKE_DXF


def test_guion_en_ansi_con_carpeta_con_tildes_y_enie(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    work = tmp_path / "José Peña año" / "trabajo"
    work.mkdir(parents=True)
    info: dict[str, Any] = {}
    dxf = dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=30, accore_exe=accore, info=info)
    assert info["conversor"] == "accoreconsole" and dxf == work / "oda_out" / "entrada.dxf" and dxf.read_text() == FAKE_DXF
    assert registro(accore)["guion"] == convert.guion_accore(work / "oda_out" / "entrada.dxf")


def test_guion_se_codifica_en_ansi_y_no_en_utf8(tmp_path: Path) -> None:
    carpeta = tmp_path / "año"
    carpeta.mkdir()
    datos = convert.codificar_guion(carpeta)
    assert datos == convert.guion_accore(carpeta / "entrada.dxf").encode("mbcs")
    assert "año".encode("utf-8") not in datos  # "aÃ±o" era el fallo


def test_ruta_no_ansi_usa_el_nombre_corto(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    carpeta = tmp_path / "Жилой"
    monkeypatch.setattr(convert, "_ruta_corta", lambda _p: r"C:\TMP\ZILOJ~1")
    assert convert.codificar_guion(carpeta) == convert.guion_accore(Path(r"C:\TMP\ZILOJ~1") / "entrada.dxf").encode("mbcs")


def test_ruta_no_ansi_sin_nombre_corto_falla_claro(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, dwg: Path) -> None:
    monkeypatch.setattr(convert, "_ruta_corta", lambda _p: None)
    with pytest.raises(OdaConversionError) as info:
        convert.codificar_guion(tmp_path / "Жилой")
    assert info.value.plugin_message == convert.MENSAJE_RUTA_NO_ANSI and info.value.data["motivo"] == "accoreconsole_fallo"
    # Y por dwg_a_dxf: accoreconsole ni se lanza, y no queda nada.
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    work = tmp_path / "Жилой" / "trabajo"
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=30, accore_exe=accore)
    assert info.value.data["motivo"] == "accoreconsole_fallo" and not (accore.parent / "registro.json").exists()
    assert list(work.iterdir()) == []


def test_temp_de_accoreconsole_en_su_carpeta(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, "Path(os.environ['TEMP'], 'accc123').write_bytes(b''); " + ACCORE_ESCRIBE_DXF)
    work = tmp_path / "trabajo"
    dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=30, accore_exe=accore)
    r = registro(accore)
    assert r["temp"] == r["tmp"] == str(work / "accore" / "temp")
    assert sorted(p.name for p in work.iterdir()) == ["oda_out"]  # el accc<n> se fue con la carpeta de accoreconsole
    assert convert.accore_env(Path("x"))["TEMP"] == "x"


def test_sin_oda_usa_accoreconsole_directamente(tmp_path: Path, dwg: Path) -> None:
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    info: dict[str, Any] = {}
    dxf = dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=tmp_path / "no" / "oda.exe", timeout_s=30, accore_exe=accore, info=info)
    assert info["conversor"] == "accoreconsole" and dxf.read_text() == FAKE_DXF


def test_sin_oda_y_danado_lo_dice(tmp_path: Path, dwg: Path) -> None:
    accore = fake_accore(tmp_path, ACCORE_RECHAZA)
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=tmp_path / "no" / "oda.exe", timeout_s=30, accore_exe=accore)
    assert info.value.plugin_message == convert.MENSAJE_DANADO_SOLO_ACCORE and info.value.data["motivo"] == "archivo_danado"
    assert "no está instalado" in info.value.detalle


def test_sin_oda_ni_accoreconsole_oda_no_disponible(tmp_path: Path, dwg: Path) -> None:
    from mcp_autocad.oda.convert import OdaNoDisponibleError

    oda = tmp_path / "no" / "oda.exe"
    acc = tmp_path / "no" / "acc.exe"
    esperado = (
        f"[ODA_NO_DISPONIBLE] No se encontró ODA File Converter en '{oda}' ni accoreconsole en '{acc}'; "
        "instale uno de los dos o defina MCP_AUTOCAD_ODA o MCP_AUTOCAD_ACCORE."
    )
    with pytest.raises(OdaNoDisponibleError) as info:
        dwg_a_dxf(dwg, tmp_path / "t", oda_exe=oda, timeout_s=30, accore_exe=acc)
    assert str(info.value) == esperado and info.value.code == "ODA_NO_DISPONIBLE"
    with pytest.raises(OdaNoDisponibleError) as info:
        auditar_archivo_oda(dwg, **oda_args(tmp_path / "w", oda, accore_exe=acc))
    assert str(info.value) == esperado
    assert not (tmp_path / "t").exists() and not (tmp_path / "w").exists()
    # Sin respaldo pedido (accore_exe None), el literal de siempre.
    assert str(OdaNoDisponibleError(oda)) == (
        f"[ODA_NO_DISPONIBLE] No se encontró ODA File Converter en '{oda}'; instálelo o defina MCP_AUTOCAD_ODA."
    )


def test_lote_sin_oda_ni_accoreconsole_se_detiene_nombrando_los_dos(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MCP_AUTOCAD_ODA", str(tmp_path / "no_oda.exe"))
    monkeypatch.setenv("MCP_AUTOCAD_ACCORE", str(tmp_path / "no_acc.exe"))
    server = servidor(World(), tmp_path, server_mod._auditar_oda_real)
    lote = server.bridge.lote
    inicio = lote.iniciar({"ruta": str(carpeta_obra(tmp_path, "A.dwg")), "origen": "oda", "pausa_ms": 0})
    assert lote.esperar(inicio["id"], 20)
    e = lote.estado(inicio["id"])
    assert e["estado"] == "detenida"
    assert "no_oda.exe" in e["motivo_detencion"] and "no_acc.exe" in e["motivo_detencion"]
    assert "MCP_AUTOCAD_ACCORE" in e["motivo_detencion"]


def test_auditar_archivo_oda_sin_oda_con_accoreconsole(tmp_path: Path, dwg: Path) -> None:
    muestra = tmp_path / "muestra.dxf"
    new_doc().saveas(muestra)
    accore = fake_accore(tmp_path, f"import shutil\nshutil.copy({str(muestra)!r}, salida_dxf)\n")
    r = auditar_archivo_oda(dwg, **oda_args(tmp_path / "w", tmp_path / "no" / "oda.exe", accore_exe=accore))
    # ODA nunca vio el archivo: advertencia propia, no "ODA no pudo leer".
    assert r["conversor"] == "accoreconsole" and r["advertencias"][-1] == convert.ADVERTENCIA_SOLO_ACCORE
    assert convert.ADVERTENCIA_ACCORE not in r["advertencias"]


def test_info_solo_accore(tmp_path: Path, dwg: Path) -> None:
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    info: dict[str, Any] = {}
    dwg_a_dxf(dwg, tmp_path / "t1", oda_exe=tmp_path / "no" / "oda.exe", timeout_s=30, accore_exe=accore, info=info)
    assert info["solo_accore"] is True
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    info2: dict[str, Any] = {}
    dwg_a_dxf(dwg, tmp_path / "t2", oda_exe=oda, timeout_s=30, accore_exe=accore, info=info2)
    assert info2["conversor"] == "accoreconsole" and info2["solo_accore"] is False


def test_lote_deja_omitido_el_fallo_de_accoreconsole(tmp_path: Path) -> None:
    fallo = PluginError("INTERNAL", convert.MENSAJE_ACCORE_FALLO, {"detalle": "x", "motivo": "accoreconsole_fallo"})
    auditor = Auditor({"a.dwg": fallo})
    lote = hacer_lote(tmp_path, auditor)
    e = correr(lote, {"ruta": str(carpeta_obra(tmp_path, "A.dwg")), "origen": "oda"})
    f = fila(e, "A.dwg")
    assert f["estado"] == "omitido" and f["error"]["motivo"] == "accoreconsole_fallo"
    assert lote_mod.MOTIVOS_REPETIBLES == convert.MOTIVOS_REPETIBLES
    # reanudar la repite (las omitido no son definitivas) y ahora sale bien.
    auditor.por_nombre["a.dwg"] = respuesta("oda", conversor="accoreconsole")
    r = lote.iniciar({"reanudar": e["id"], "pausa_ms": 0})
    assert lote.esperar(r["id"], 20)
    f2 = fila(lote.estado(r["id"], 100), "A.dwg")
    assert f2["estado"] == "ok" and f2["conversor"] == "accoreconsole"


def test_oda_no_lee_y_no_hay_accoreconsole(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    falta = tmp_path / "no" / "accoreconsole.exe"
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=oda, timeout_s=30, accore_exe=falta)
    e = info.value
    assert str(e) == f"[INTERNAL] {convert.MENSAJE_SIN_RESPALDO}" and e.data["motivo"] == "oda_no_lee_sin_respaldo"
    assert str(falta) in e.data["detalle"] and "OdError" in e.data["detalle"]


def test_sin_respaldo_pedido_el_error_de_siempre(tmp_path: Path, dwg: Path) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=oda, timeout_s=30)
    assert str(info.value) == "[INTERNAL] No se pudo leer el archivo DWG." and info.value.data == {"detalle": "OdError thrown during readFile"}


def test_archivo_de_0_bytes_no_pasa_por_ningun_conversor(tmp_path: Path) -> None:
    vacio = tmp_path / "PLANOS CLIENTE_G.dwg"
    vacio.write_bytes(b"")
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    work = tmp_path / "trabajo"
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(vacio, work, oda_exe=oda, timeout_s=30, accore_exe=accore)
    assert str(info.value) == "[INTERNAL] El archivo DWG está vacío (0 bytes)." and info.value.data["motivo"] == "archivo_vacio"
    assert not (oda.parent / "registro.json").exists() and not (accore.parent / "registro.json").exists()
    assert list(work.iterdir()) == []


@pytest.fixture
def accore_lento(monkeypatch: pytest.MonkeyPatch) -> list[subprocess.Popen]:
    """La llamada a accoreconsole (lleva /isolate) lanza un proceso real que no termina; ODA va por el Popen real."""
    lanzados: list[subprocess.Popen] = []
    real = subprocess.Popen

    def popen(args: list[str], **kwargs: Any) -> subprocess.Popen:
        if "/isolate" in args:
            lanzados.append(real([sys.executable, "-I", "-c", "import time; time.sleep(60)"], **kwargs))
            return lanzados[-1]
        return real(args, **kwargs)

    monkeypatch.setattr(convert.subprocess, "Popen", popen)
    return lanzados


def test_accoreconsole_con_el_mismo_plazo_se_mata(tmp_path: Path, dwg: Path, accore_lento: list[subprocess.Popen]) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    work = tmp_path / "trabajo"
    work.mkdir()
    inicio = time.perf_counter()
    with pytest.raises(OdaTimeoutError) as info:
        dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=2.5, accore_exe=accore)
    assert time.perf_counter() - inicio < 15
    assert str(info.value) == f"[ODA_TIMEOUT] accoreconsole de AutoCAD superó 2.5 s con '{dwg.name}'."
    assert info.value.code == "ODA_TIMEOUT"
    assert accore_lento[0].poll() is not None  # ningún proceso colgado
    assert list(work.iterdir()) == []


def test_cancelar_mata_accoreconsole(tmp_path: Path, dwg: Path, accore_lento: list[subprocess.Popen]) -> None:
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    cancelar = threading.Event()
    work = tmp_path / "trabajo"
    work.mkdir()

    def cancelar_cuando_arranque() -> None:
        while not accore_lento:
            time.sleep(0.05)
        cancelar.set()

    threading.Thread(target=cancelar_cuando_arranque, daemon=True).start()
    with pytest.raises(OdaCanceladoError):
        dwg_a_dxf(dwg, work, oda_exe=oda, timeout_s=60, cancelar=cancelar, accore_exe=accore)
    assert accore_lento[0].poll() is not None
    assert list(work.iterdir()) == []


def test_timeout_de_oda_no_pasa_a_accoreconsole(tmp_path: Path, dwg: Path) -> None:
    folder = tmp_path / "falso"
    folder.mkdir()
    lento = folder / "lento.bat"
    lento.write_text("@ping -n 4 127.0.0.1 >nul 2>&1\n", encoding="ascii")
    accore = fake_accore(tmp_path, ACCORE_ESCRIBE_DXF)
    with pytest.raises(OdaTimeoutError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=lento, timeout_s=0.3, accore_exe=accore)
    assert "ODA File Converter superó" in str(info.value)
    assert not (accore.parent / "registro.json").exists()


def test_salida_utf16_se_decodifica() -> None:
    assert convert._decode_accore("ErrorStatus=434 ñ".encode("utf-16-le")) == "ErrorStatus=434 ñ"
    assert convert._decode_accore(b"\xff\xfe" + "válido".encode("utf-16-le")) == "válido"
    assert convert._decode_accore("sin nulos".encode("utf-8")) == "sin nulos"


def test_auditar_archivo_oda_informa_el_conversor(tmp_path: Path, dwg: Path) -> None:
    muestra = tmp_path / "muestra.dxf"
    new_doc().saveas(muestra)
    oda = fake_oda(tmp_path, ODA_NO_LEE)
    accore = fake_accore(tmp_path, f"import shutil\nshutil.copy({str(muestra)!r}, salida_dxf)\n")
    work = tmp_path / "tmp" / "id"
    r = auditar_archivo_oda(dwg, **oda_args(work, oda, accore_exe=accore))
    assert (r["origen"], r["conversor"]) == ("oda", "accoreconsole")
    assert r["advertencias"][-1] == convert.ADVERTENCIA_ACCORE
    assert not work.exists()
    json.dumps(r)


def test_auditar_archivo_oda_con_oda_dice_oda(tmp_path: Path, dwg: Path) -> None:
    muestra = tmp_path / "muestra.dxf"
    new_doc().saveas(muestra)
    oda = fake_oda(tmp_path, f"import shutil\nshutil.copy({str(muestra)!r}, salida / 'entrada.dxf')\n")
    r = auditar_archivo_oda(dwg, **oda_args(tmp_path / "w", oda))
    assert r["conversor"] == "oda" and convert.ADVERTENCIA_ACCORE not in r["advertencias"]


def test_plazo_de_lectura_cuenta_desde_accoreconsole(tmp_path: Path, dwg: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    recibido: list[float] = []

    def convertir(*_a: Any, info: dict[str, Any] | None = None, **_k: Any) -> Path:
        assert info is not None
        info.update(conversor="accoreconsole", inicio=time.monotonic() + 5.0)  # como si ODA hubiera tardado 5 s en fallar
        return tmp_path / "x.dxf"

    def leer(_dxf: Path, _nombre: str, deadline: float, timeout_s: float, _cancelar: Any) -> Any:
        recibido.append(deadline - time.monotonic())
        raise OdaTimeoutError(timeout_s, "x", audit.ETAPA_LECTURA)

    monkeypatch.setattr(audit, "dwg_a_dxf", convertir)
    monkeypatch.setattr(audit, "_read_dxf", leer)
    with pytest.raises(OdaTimeoutError):
        auditar_archivo_oda(dwg, **{**oda_args(tmp_path / "w", Path(sys.executable)), "timeout_s": 10.0})
    assert recibido[0] > 14.0  # 5 + 10, no los 10 desde antes de ODA


def test_servidor_pasa_accoreconsole_de_entorno(tmp_path: Path, monkeypatch: pytest.MonkeyPatch, dwg: Path) -> None:
    llamadas: list[dict[str, Any]] = []
    monkeypatch.setenv("MCP_AUTOCAD_ACCORE", str(tmp_path / "acc.exe"))
    monkeypatch.setattr(audit, "auditar_archivo_oda", lambda ruta, **kw: llamadas.append(kw) or respuesta("oda", conversor="accoreconsole"))
    server = servidor(World(), tmp_path, server_mod._auditar_oda_real)
    resultado = server.bridge.call_tool("auditar_archivo", {"ruta": str(dwg), "origen": "oda"})
    assert resultado.is_error is False
    assert llamadas[0]["accore_exe"] == tmp_path / "acc.exe" and llamadas[0]["timeout_s"] == 300.0
    assert json.loads(resultado.content[0].text)["conversor"] == "accoreconsole"


def test_fila_del_lote_con_conversor_y_motivo_de_error(tmp_path: Path) -> None:
    danado = PluginError("INTERNAL", convert.MENSAJE_DANADO, {"detalle": "x", "motivo": "archivo_danado"})
    auditor = Auditor(
        {
            "por_oda.dwg": respuesta("oda", conversor="oda"),
            "por_accore.dwg": respuesta("oda", conversor="accoreconsole"),
            "raro.dwg": respuesta("oda", conversor="texto del plano"),
            "plugin.dwg": respuesta("plugin"),
            "danado.dwg": danado,
            "viejo.dwg": PluginError("INTERNAL", "No se pudo leer el archivo DWG.", {"detalle": "x"}),
        }
    )
    carpeta = carpeta_obra(tmp_path, "por_oda.dwg", "por_accore.dwg", "raro.dwg", "plugin.dwg", "danado.dwg", "viejo.dwg")
    e = correr(hacer_lote(tmp_path, auditor), {"ruta": str(carpeta), "origen": "oda"})
    assert fila(e, "por_oda.dwg")["conversor"] == "oda" and fila(e, "por_accore.dwg")["conversor"] == "accoreconsole"
    assert fila(e, "raro.dwg")["conversor"] is None and fila(e, "plugin.dwg")["conversor"] is None
    d = fila(e, "danado.dwg")
    assert d["estado"] == "error" and d["error"] == {"code": "INTERNAL", "message": convert.MENSAJE_DANADO, "motivo": "archivo_danado"}
    assert fila(e, "viejo.dwg")["error"] == {"code": "INTERNAL", "message": "No se pudo leer el archivo DWG."}


# ---------------------------------------------------------------------------
# 2. Umbrales de unidad probable
# ---------------------------------------------------------------------------


def ev(cota: Any, n_cotas: Any, extension: Any, n_puntos: Any, largo: Any, coord: Any = None) -> dict[str, Any]:
    datos = {"cota_mediana": cota, "n_cotas": n_cotas, "extension": extension, "n_puntos": n_puntos, "largo_mediano": largo}
    if coord is not None:
        datos["coord_mediana"] = coord
    return datos


def test_constantes_082() -> None:
    assert lote_mod.RANGO_COTA_M == (0.1, 100.0) and lote_mod.RANGO_COTA_MM_M == (0.02, 100.0)
    assert lote_mod.UMBRAL_EXTENSION_MEZCLADA == 1e5 and lote_mod.FACTOR_EXTENSION_MEZCLADA == 20.0
    # Sin regla de georreferenciado (decisión del organizador): la extensión p5–p95 es invariante a traslación.
    for nombre in ("UMBRAL_COORD_GEORREFERENCIA", "FACTOR_EXTENSION_GEORREFERENCIA", "SUFIJO_EXTENSION_GEORREFERENCIA"):
        assert not hasattr(lote_mod, nombre), nombre
    assert convert.ODA_TIMEOUT_DEFAULT == 300.0


# Medidos el 2026-11-04 con ODA + evidencia_unidad 0.8.2 sobre copias (INSUNITS, evidencia, unidad 0.8.1 → 0.8.2).
# Sin regla de georreferenciado: un plano agrupado lejos del origen conserva su extensión (TOPO y GENENERAL LOTE 7
# dan lo mismo que en 0.8.1); solo "mezclado" (ROLDANILLO) y "absurda" la descartan.
MEDIDOS_082 = {
    "TOPO ARQUITECTONICO DEMOLICION PARCIAL": (4, ev(55.333334, 6, 113.204225, 2773, 0.1524, 4867025.790331), "m", "m",
                                               "única unidad compatible (cotas, extensión)"),
    "GENENERAL LOTE 7 URBANIZACION X": (4, ev(80.995499, 4, 607.973858, 106, 70.086368, 4867754.244723), "cm",
                                                  "cm", "única unidad compatible (extensión, largo de segmentos)"),
    "200-P10_00-T-17-E3": (4, ev(50.0, 132, 49678.638067, 1073, 16.454483, 18890.941223), "cm", "indeterminada",
                           "varias unidades compatibles (cotas, extensión): cm, mm"),
    "200-P02_00-T-17-E7": (4, ev(75.0, 363, 33545.99054, 2664, 32.0, 67417.942592), "cm", "indeterminada",
                           "varias unidades compatibles (cotas, extensión): cm, mm"),
    "ROLDANILLO-NIVELES": (6, ev(None, 0, 585182.641247, 26635, 4.600004, 4669.234864), "cm", "indeterminada",
                           "varias unidades compatibles (largo de segmentos): m, cm" + lote_mod.SUFIJO_EXTENSION_MEZCLADA),
    "Calle 17 no.3-43": (4, ev(0.61, 735, 304059.934007, 6340, 0.429784, 303763.277625), "indeterminada", "indeterminada",
                         "ninguna unidad compatible (cotas, extensión)"),
    "E-0X PAVIMENTO RIGIDO OK": (6, ev(0.2, 63, 9149218.340184, 128764, 0.402802, 8995677.530079), "indeterminada",
                                 "indeterminada", "ninguna unidad compatible (cotas, extensión)"),
    "DIVISION PREDIO LA GRANJA 21062024": (4, ev(None, 0, 3874194.737476, 509, 1.8, 4863666.599753), "indeterminada",
                                           "indeterminada", "ninguna unidad compatible (extensión, largo de segmentos)"),
    "Planta PIR": (4, ev(None, 0, 196121.577519, 1748, 125.476, 416221.059146), "indeterminada", "indeterminada",
                   "varias unidades compatibles (extensión, largo de segmentos): cm, mm"),
    "Vivienda Chia-Plano - A2": (4, ev(1.714329, 1, 106145.364251, 488, 2125.0, 39408.076163), "mm", "mm",
                                 "única unidad compatible (extensión, largo de segmentos)"),
    "Columnas": (4, ev(None, 0, 129757.601, 5502, 200.0, 70767.25), "indeterminada", "indeterminada",
                 "varias unidades compatibles (extensión, largo de segmentos): cm, mm"),
}


@pytest.mark.parametrize("nombre", list(MEDIDOS_082))
def test_planos_medidos_082(nombre: str) -> None:
    insunits, evidencia, antes, despues, motivo = MEDIDOS_082[nombre]
    assert lote_mod.unidad_probable(evidencia, insunits) == {"unidad": despues, "motivo": motivo}
    # Sin coord_mediana (fila de un lote anterior) el veredicto de 0.8.1 se conserva, salvo el piso de mm.
    sin_coord = {k: v for k, v in evidencia.items() if k != "coord_mediana"}
    if nombre not in ("200-P10_00-T-17-E3", "200-P02_00-T-17-E7"):
        assert lote_mod.unidad_probable(sin_coord, insunits)["unidad"] == antes


def test_piso_de_cota_de_mm_y_no_de_cm() -> None:
    # 0,02 m incluido en mm; por debajo, no.
    assert lote_mod.unidad_probable(ev(20.0, 5, None, 0, None), 4)["motivo"] == "varias unidades compatibles (cotas): m, cm, mm"
    assert lote_mod.unidad_probable(ev(19.99, 5, None, 0, None), 4)["motivo"] == "varias unidades compatibles (cotas): m, cm"
    # cm conserva 0,1 m: los vectores 4 y 7 de los 16 medidos (cotas de 5,39 y 5,63) siguen en m.
    assert lote_mod.unidad_probable(ev(5.3931, 11, 281.307, 188, 12.6846), 6)["unidad"] == "m"
    assert lote_mod.unidad_probable(ev(5.633, 36, 235.277, 408, 13.1406), 6)["unidad"] == "m"


def test_extension_absurda_se_descarta() -> None:
    # 3e7: 30 000 km en m, 300 km en cm, 30 km en mm: ninguna unidad la admite; deciden las cotas.
    r = lote_mod.unidad_probable(ev(0.8, 50, 3e7, 500, None), 6)
    assert r == {"unidad": "m", "motivo": "única unidad compatible (cotas)" + lote_mod.SUFIJO_EXTENSION_ABSURDA}
    # Solo la extensión, y absurda: sin evidencia, y lo dice.
    assert lote_mod.unidad_probable(ev(None, 0, 3e7, 500, None))["motivo"] == "sin evidencia suficiente" + lote_mod.SUFIJO_EXTENSION_ABSURDA
    # Grande pero admisible en mm (1e7 = 10 km): sigue siendo prueba, como en 0.8.1.
    assert lote_mod.unidad_probable(ev(0.8, 50, 1e7, 500, None))["motivo"] == "ninguna unidad compatible (cotas, extensión)"


def test_georreferenciado_agrupado_conserva_la_extension_y_mezclado_limites() -> None:
    base = ev(0.8, 50, 25.0, 500, None)
    # Coordenadas enormes y plano agrupado: la extensión (invariante a traslación) sigue siendo prueba.
    for coord in (99999.0, 1e5, 4.86e6, 1e9):
        assert lote_mod.unidad_probable({**base, "coord_mediana": coord})["motivo"] == "única unidad compatible (cotas, extensión)"
    # Un plano en mm de 300 m junto al origen (coord 1,5e5, extensión 3e5): conserva su extensión.
    assert lote_mod.unidad_probable(ev(None, 0, 3e5, 500, 3000.0, 1.5e5), 4) == {
        "unidad": "mm", "motivo": "única unidad compatible (extensión, largo de segmentos)"}
    mezcla = ev(None, 0, 1e5, 500, 4.6)
    assert lote_mod.unidad_probable({**mezcla, "coord_mediana": 5000.0})["motivo"].endswith(lote_mod.SUFIJO_EXTENSION_MEZCLADA)
    assert not lote_mod.unidad_probable({**mezcla, "coord_mediana": 5000.1})["motivo"].endswith(lote_mod.SUFIJO_EXTENSION_MEZCLADA)
    # Extensión menor que 100 000: nunca "mezclado", aunque sea 20 veces la mediana.
    assert "descartada" not in lote_mod.unidad_probable(ev(None, 0, 99999.0, 500, 4.6, 10.0))["motivo"]


@pytest.mark.parametrize("raro", [True, "1e6", float("nan"), float("inf"), -5.0])
def test_coord_mediana_rara_no_descarta(raro: Any) -> None:
    assert lote_mod.unidad_probable({**ev(0.8, 50, 25.0, 500, None), "coord_mediana": raro})["unidad"] == "m"


def test_sufijos_en_orden_con_muestra_truncada() -> None:
    r = lote_mod.unidad_probable({**ev(None, 0, 585182.6, 26635, 4.6, 4669.2), "truncada": True}, 6)
    assert r["motivo"] == "varias unidades compatibles (largo de segmentos): m, cm" + lote_mod.SUFIJO_EXTENSION_MEZCLADA + "; muestra truncada"


def test_evidencia_mide_coord_mediana() -> None:
    doc = new_doc()
    modelo = doc.modelspace()
    for i in range(30):
        modelo.add_line((586000 + i, 521000), (586000 + i, 521010))  # georreferenciado
    e = audit.evidencia_unidad(doc)
    assert e["coord_mediana"] == pytest.approx(586014.5)
    doc2 = new_doc()
    doc2.modelspace().add_line((-3, 4), (5, -10))
    assert audit.evidencia_unidad(doc2)["coord_mediana"] == pytest.approx(7.0)  # max(mediana |x| 4, mediana |y| 7)


def test_fila_guarda_coord_mediana() -> None:
    f = lote_mod.fila_vacia("C:\\a.dwg")
    lote_mod.rellenar_fila(f, respuesta("oda", insunits=6, _unidad=ev(None, 0, 585182.6, 26635, 4.6, 4669.2)), [])
    assert f["evidencia_unidad"]["coord_mediana"] == 4669.2
    assert f["unidad_probable"] == "indeterminada" and f["motivo_unidad"].endswith(lote_mod.SUFIJO_EXTENSION_MEZCLADA)


# ---------------------------------------------------------------------------
# Documentación
# ---------------------------------------------------------------------------


def test_protocolo_documenta_082() -> None:
    texto = PROTOCOL_MD.read_text(encoding="utf-8")
    cabecera = texto[: texto.index("Este documento es la ÚNICA fuente")]
    assert "Cambios servidor 0.8.1 → 0.8.2" in cabecera
    respaldo = texto[texto.index("### 5H.3 ") : texto.index("### 5H.4 ")]
    literales = texto[texto.index("### 5H.5 ") : texto.index("### 5H.6 ")]
    lote_sec = texto[texto.index("### 5H.2 ") : texto.index("### 5H.3 ")]
    for literal in (
        convert.MENSAJE_VACIO,
        convert.MENSAJE_DANADO,
        convert.MENSAJE_SIN_RESPALDO,
        convert.ADVERTENCIA_ACCORE,
        convert.MENSAJE_DANADO_SOLO_ACCORE,
        convert.MENSAJE_ACCORE_FALLO,
        convert.MENSAJE_RUTA_NO_ANSI,
        convert.ADVERTENCIA_SOLO_ACCORE,
    ):
        assert literal in literales, literal
    no_disponible = str(convert.OdaNoDisponibleError("<ruta>", "<ruta accoreconsole>")).removeprefix("[ODA_NO_DISPONIBLE] ")
    assert no_disponible in texto[texto.index("### 6.1 ") :] and no_disponible in respaldo
    for motivo in ("archivo_vacio", "archivo_danado", "oda_no_lee_sin_respaldo", "accoreconsole_fallo"):
        assert f"`{motivo}`" in literales, motivo
    assert "MCP_AUTOCAD_ACCORE" in respaldo and "/isolate" in respaldo and "defecto 300" in respaldo
    assert "página de códigos ANSI" in respaldo and "accore\\temp\\" in respaldo and "Sin ODA instalado" in respaldo
    assert "invariante a traslación" in lote_sec and '"accoreconsole_fallo"' in lote_sec
    assert "georreferenciadas (mediana" not in texto  # la regla (a) se retiró
    for sufijo in (lote_mod.SUFIJO_EXTENSION_MEZCLADA, lote_mod.SUFIJO_EXTENSION_ABSURDA):
        assert sufijo.removeprefix("; ") in literales, sufijo
    assert "`conversor`" in lote_sec and "`coord_mediana`" in texto and "[0,02; 100]" in lote_sec
