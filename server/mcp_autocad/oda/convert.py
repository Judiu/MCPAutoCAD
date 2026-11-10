"""Conversión DWG → DXF con ODA File Converter y, de respaldo, accoreconsole de AutoCAD (protocol.md §5H.3).

ODA convierte por carpeta, así que cada trabajo le entrega una carpeta propia con
SOLO la copia del archivo: nunca se le pasa la carpeta de origen, de modo que no
lee ni ejecuta nada ajeno. El DWG original no se abre ni se toca más allá de
copiarlo. Aquí no se importa ezdxf: este módulo solo lanza el conversor.

Servidor 0.8.2: si ODA termina sin DXF (no lo lee) y existe accoreconsole (MCP_AUTOCAD_ACCORE), la MISMA copia
se convierte con accoreconsole y un guion fijo (DXFOUT a oda_out y QUIT sin guardar), con el mismo plazo, la misma
cancelación y la misma limpieza. Si AutoCAD también la rechaza, el archivo está dañado; un DWG de 0 bytes es vacío
y no se le pasa a ningún conversor. Medido el 2026-11-04 con AutoCAD 2026 sobre copias: 5 DWG que ODA no leía
salieron por accoreconsole (3–40 s); 5 dañados y uno de 0 bytes dieron "Archivo de dibujo no válido", ErrorStatus=434.
"""

from __future__ import annotations

import logging
import os
import shutil
import subprocess
import threading
import time
from pathlib import Path

from ..errors import McpAutocadError, PluginError, format_seconds

log = logging.getLogger(__name__)

ODA_EXE_ENV = "MCP_AUTOCAD_ODA"
ODA_TIMEOUT_ENV = "MCP_AUTOCAD_ODA_TIMEOUT"
ODA_EXE_DEFAULT = r"C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe"
# 0.8.2: 120 → 300 s. Con 4 lotes en paralelo, planos de 50–58 MB que tardan 80–116 s aislados pasaban de 120.
ODA_TIMEOUT_DEFAULT = 300.0
ACCORE_EXE_ENV = "MCP_AUTOCAD_ACCORE"
ACCORE_EXE_DEFAULT = r"D:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe"

CONVERSOR_ODA = "oda"
CONVERSOR_ACCORE = "accoreconsole"
# Carpeta propia de accoreconsole dentro de la de trabajo: cwd (escribe allí su registro), guion, salida y perfil aislado.
ACCORE_DIR = "accore"
ACCORE_SCRIPT = "dxfout.scr"
ACCORE_LOG = "accore_salida.log"
ACCORE_PERFIL = "perfil"
# /isolate: los cambios de variables de sistema y de registro de esta sesión van a un perfil propio y temporal, nunca al
# del usuario. Identificador fijo; la carpeta del perfil es del trabajo y se borra al acabar.
ACCORE_ISOLATE_ID = "mcp_autocad"
# Nombre del DXF que escribe accoreconsole en oda_out (ODA escribe entrada.dxf).
ACCORE_DXF = "entrada.dxf"
# TEMP/TMP de accoreconsole: una carpeta propia dentro de la suya. Sin esto deja en el %TEMP% del usuario un archivo de
# 0 bytes por ejecución (su salida lo dice: "Redirect stdout (file: ...\Temp\accc<n>)"); así no se borra nada ajeno.
ACCORE_TEMP = "temp"

# Argumentos fijos del contrato (§5H.3): versión, formato, sin recursión, con auditoría, filtro.
ODA_ARGS = ("ACAD2018", "DXF", "0", "1", "*.DWG")

INPUT_DIR = "oda_in"
OUTPUT_DIR = "oda_out"
LOG_FILE = "oda_salida.log"

# Nombre fijo de la copia: los nombres de los planos (tildes, paréntesis, longitud) no
# llegan al conversor y la extensión queda siempre en la forma que el filtro acepta.
INPUT_NAME = "entrada.dwg"

MAX_OUTPUT_CHARS = 2000
KILL_WAIT_S = 5.0
# Cada cuánto se mira la cancelación mientras corre el conversor o se lee el DXF.
POLL_S = 0.1

READ_ERROR_MESSAGE = "No se pudo leer el archivo DWG."
# Servidor 0.8.2 (§5H.3, §5H.5): mismo código INTERNAL, mensaje distinto y data.motivo para separar los casos.
MENSAJE_VACIO = "El archivo DWG está vacío (0 bytes)."
MENSAJE_DANADO = "El archivo DWG está dañado o no es un dibujo válido: ni ODA File Converter ni AutoCAD (accoreconsole) pudieron leerlo."
MENSAJE_SIN_RESPALDO = (
    "ODA File Converter no pudo leer el archivo DWG y no hay accoreconsole para reintentarlo: puede no estar dañado; "
    "ábralo en AutoCAD o defina MCP_AUTOCAD_ACCORE."
)
MENSAJE_DANADO_SOLO_ACCORE = (
    "El archivo DWG está dañado o no es un dibujo válido: AutoCAD (accoreconsole) no pudo leerlo "
    "(ODA File Converter no está instalado)."
)
MENSAJE_ACCORE_FALLO = (
    "AutoCAD (accoreconsole) no generó el DXF sin indicar que el dibujo no sea válido: puede ser un fallo de AutoCAD "
    "(arranque, licencia, cierre inesperado), no del archivo; en el lote queda omitido y se repite al reanudar."
)
MENSAJE_RUTA_NO_ANSI = (
    "La carpeta de trabajo tiene caracteres que accoreconsole no puede leer en su guion (página de códigos ANSI del "
    "sistema) y no tiene nombre corto 8.3: use una carpeta de usuario o LOCALAPPDATA con nombre representable."
)
MOTIVO_VACIO = "archivo_vacio"
MOTIVO_DANADO = "archivo_danado"
MOTIVO_SIN_RESPALDO = "oda_no_lee_sin_respaldo"
# Servidor 0.8.2 (auditoría de Fable): fallo de accoreconsole sin evidencia de dibujo no válido. NO es definitivo: el lote
# deja la fila "omitido" para que reanudar la repita (un plano válido no queda marcado como dañado por un fallo de AutoCAD).
MOTIVO_ACCORE_FALLO = "accoreconsole_fallo"
MOTIVOS_REPETIBLES = (MOTIVO_ACCORE_FALLO,)
# Evidencia, en la salida de accoreconsole, de que AutoCAD rechazó el dibujo (medido con AutoCAD 2026 en español:
# "Archivo de dibujo no válido." y "ErrorStatus=434"). "ErrorStatus=" no depende del idioma; las otras dos son el
# mensaje en español y en inglés. Un código de salida ≠ 0 sin esta evidencia NO es "dañado".
ACCORE_EVIDENCIA_RECHAZO = ("errorstatus=", "dibujo no válido", "invalid drawing")
ETAPA_ACCORE = "accoreconsole de AutoCAD"
ADVERTENCIA_ACCORE = (
    "ODA File Converter no pudo leer el archivo; se convirtió a DXF con accoreconsole de AutoCAD (sin guardar el DWG)."
)
# Servidor 0.8.2: sin ODA instalado, ODA nunca vio el archivo; la advertencia no dice que "no pudo leerlo".
ADVERTENCIA_SOLO_ACCORE = (
    "ODA File Converter no está instalado; se convirtió a DXF con accoreconsole de AutoCAD (sin guardar el DWG)."
)


class OdaNoDisponibleError(McpAutocadError):
    """ODA_NO_DISPONIBLE (§6.1): el ejecutable no existe en la ruta configurada.

    Servidor 0.8.2: con ``accore_exe`` (el respaldo que tampoco existe) el mensaje nombra los dos y las dos variables;
    sin él (llamada sin respaldo), el literal de siempre."""

    code = "ODA_NO_DISPONIBLE"

    def __init__(self, oda_exe: Path | str, accore_exe: Path | str | None = None) -> None:
        self.oda_exe = str(oda_exe)
        self.accore_exe = None if accore_exe is None else str(accore_exe)
        if self.accore_exe is None:
            texto = f"No se encontró ODA File Converter en '{self.oda_exe}'; instálelo o defina MCP_AUTOCAD_ODA."
        else:
            texto = (
                f"No se encontró ODA File Converter en '{self.oda_exe}' ni accoreconsole en '{self.accore_exe}'; "
                "instale uno de los dos o defina MCP_AUTOCAD_ODA o MCP_AUTOCAD_ACCORE."
            )
        super().__init__(f"[ODA_NO_DISPONIBLE] {texto}")


class OdaTimeoutError(McpAutocadError):
    """ODA_TIMEOUT (§6.1): el conversor (se mató) o la lectura del DXF (``etapa``) superó el plazo."""

    code = "ODA_TIMEOUT"

    def __init__(self, seconds: float, archivo: str, etapa: str = "ODA File Converter") -> None:
        self.seconds = float(seconds)
        self.archivo = archivo
        super().__init__(f"[ODA_TIMEOUT] {etapa} superó {format_seconds(seconds)} s con '{archivo}'.")


class OdaCanceladoError(McpAutocadError):
    """La auditoría se canceló a mitad del archivo: el conversor se mató y no hay resultado (§5H.2)."""

    code = "CANCELADO"

    def __init__(self, archivo: str) -> None:
        self.archivo = archivo
        super().__init__(f"[CANCELADO] Auditoría cancelada con '{archivo}'.")


class OdaConversionError(PluginError):
    """INTERNAL "No se pudo leer el archivo DWG." con data.detalle (§5H.3).

    Es un PluginError para que el servidor y el lote traten igual este fallo y el
    del plugin: mismo código, mismo mensaje, detalle en ``data``.

    Servidor 0.8.2: ``mensaje`` y ``motivo`` distinguen archivo vacío, dañado y "ODA no lo lee y no hay respaldo";
    sin ellos, el mensaje y los datos de siempre.
    """

    def __init__(self, detalle: str, mensaje: str = READ_ERROR_MESSAGE, motivo: str | None = None) -> None:
        self.detalle = detalle
        self.motivo = motivo
        data: dict[str, str] = {"detalle": detalle}
        if motivo is not None:
            data["motivo"] = motivo
        super().__init__("INTERNAL", mensaje, data)


def oda_exe_por_defecto() -> Path:
    """Ruta del conversor: MCP_AUTOCAD_ODA o la instalación conocida."""
    env = os.environ.get(ODA_EXE_ENV, "").strip()
    return Path(env) if env else Path(ODA_EXE_DEFAULT)


def accore_exe_por_defecto() -> Path:
    """Ruta del conversor de respaldo: MCP_AUTOCAD_ACCORE o la instalación conocida de AutoCAD 2026."""
    env = os.environ.get(ACCORE_EXE_ENV, "").strip()
    return Path(env) if env else Path(ACCORE_EXE_DEFAULT)


def timeout_por_defecto() -> float:
    """Segundos por archivo y por conversor: MCP_AUTOCAD_ODA_TIMEOUT (número > 0) o 300."""
    env = os.environ.get(ODA_TIMEOUT_ENV, "").strip()
    if not env:
        return ODA_TIMEOUT_DEFAULT
    try:
        value = float(env)
    except ValueError:
        log.warning("%s no es un número (%r): se usa %g s", ODA_TIMEOUT_ENV, env, ODA_TIMEOUT_DEFAULT)
        return ODA_TIMEOUT_DEFAULT
    if not 0 < value < float("inf"):
        log.warning("%s debe ser mayor que 0 (%r): se usa %g s", ODA_TIMEOUT_ENV, env, ODA_TIMEOUT_DEFAULT)
        return ODA_TIMEOUT_DEFAULT
    return value


def remove_tree(path: Path) -> bool:
    """Borra un archivo o carpeta temporal. Reintenta: en Windows el antivirus o un
    proceso recién muerto pueden retener el archivo un instante. True si ya no existe."""
    for attempt in range(5):
        try:
            if path.is_dir():
                shutil.rmtree(path)
            else:
                path.unlink(missing_ok=True)
        except OSError:
            log.debug("No se pudo borrar %s (intento %d)", path, attempt + 1)
        if not path.exists():
            return True
        time.sleep(0.2)
    log.warning("No se pudo borrar la carpeta temporal %s", path)
    return False


def remove_work_files(carpeta_trabajo: Path) -> bool:
    """Borra lo que crea la conversión; nunca el resto de la carpeta (el lote guarda allí su copia)."""
    results = [remove_tree(carpeta_trabajo / name) for name in (INPUT_DIR, OUTPUT_DIR, LOG_FILE, ACCORE_DIR)]
    return all(results)


def _hidden_startupinfo() -> subprocess.STARTUPINFO | None:
    """ODA es una aplicación Qt: CREATE_NO_WINDOW no basta para ocultar una ventana gráfica."""
    if os.name != "nt":
        return None
    info = subprocess.STARTUPINFO()
    info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    info.wShowWindow = subprocess.SW_HIDE
    return info


def _read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace").strip()
    except OSError:
        return ""


def _oda_output(carpeta_trabajo: Path, output_dir: Path, nombre: str) -> str:
    """Salida del conversor + sus informes .err: ODA sale con código 0 aunque no pueda leer el
    archivo y deja allí el motivo. La ruta temporal de la copia se sustituye por el nombre real."""
    parts = [_read_text(carpeta_trabajo / LOG_FILE)]
    if output_dir.is_dir():
        parts.extend(_read_text(item) for item in sorted(output_dir.iterdir()) if item.suffix.lower() == ".err")
    text = " | ".join(part for part in parts if part)
    copy_path = str(carpeta_trabajo / INPUT_DIR / INPUT_NAME)
    text = text.replace(copy_path, nombre).replace(copy_path.replace("\\", "/"), nombre)
    return text[:MAX_OUTPUT_CHARS]


def _find_dxf(output_dir: Path) -> Path | None:
    if not output_dir.is_dir():
        return None
    return next((item for item in sorted(output_dir.iterdir()) if item.suffix.lower() == ".dxf"), None)


def _kill(process: subprocess.Popen, etapa: str = "ODA File Converter") -> None:
    process.kill()
    try:
        process.wait(timeout=KILL_WAIT_S)
    except subprocess.TimeoutExpired:
        log.warning("%s no terminó tras matarlo (pid %s)", etapa, process.pid)


def _wait_converter(
    process: subprocess.Popen,
    timeout_s: float,
    nombre: str,
    cancelar: threading.Event | None,
    etapa: str = "ODA File Converter",
) -> None:
    """Espera por tramos cortos: el lote puede pedir cancelar en cualquier momento (§5H.2)."""
    deadline = time.monotonic() + timeout_s
    while True:
        remaining = deadline - time.monotonic()
        try:
            process.communicate(timeout=max(min(remaining, POLL_S), 0))
            return
        except subprocess.TimeoutExpired as exc:
            if cancelar is not None and cancelar.is_set():
                _kill(process, etapa)
                raise OdaCanceladoError(nombre) from exc
            if remaining <= POLL_S:
                _kill(process, etapa)
                raise OdaTimeoutError(timeout_s, nombre, etapa) from exc


def _run(
    args: list[str],
    cwd: Path,
    log_path: Path,
    timeout_s: float,
    nombre: str,
    cancelar: threading.Event | None,
    etapa: str,
    env: dict[str, str] | None = None,
) -> int:
    """Proceso sin ventana, con la entrada cerrada (EOF) y la salida en un archivo y no en un pipe: así esperar
    al conversor nunca depende de que un proceso hijo suyo cierre el pipe tras matarlo. ``env`` None → el del servidor."""
    with open(log_path, "wb") as output:
        try:
            process = subprocess.Popen(  # noqa: S603 - ejecutable configurado, argumentos fijos, sin shell
                args,
                cwd=cwd,
                env=env,
                stdin=subprocess.DEVNULL,
                stdout=output,
                stderr=subprocess.STDOUT,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                startupinfo=_hidden_startupinfo(),
            )
        except OSError as exc:
            raise OdaConversionError(f"No se pudo ejecutar {etapa}: {type(exc).__name__}: {exc}") from exc
        _wait_converter(process, timeout_s, nombre, cancelar, etapa)
    return process.returncode


def _run_converter(
    oda_exe: Path,
    input_dir: Path,
    output_dir: Path,
    carpeta_trabajo: Path,
    timeout_s: float,
    nombre: str,
    cancelar: threading.Event | None,
) -> int:
    args = [str(oda_exe), str(input_dir), str(output_dir), *ODA_ARGS]
    return _run(args, carpeta_trabajo, carpeta_trabajo / LOG_FILE, timeout_s, nombre, cancelar, "ODA File Converter")


def guion_accore(salida_dxf: Path | str) -> str:
    """Guion de accoreconsole (§5H.3), probado con AutoCAD 2026 en español sobre copias el 2026-11-04.

    Comandos y opciones con "_" (independientes del idioma): DXFOUT a ``salida_dxf`` (entre comillas: la ruta puede
    llevar espacios), opción Versión 2018, 16 decimales (formato ASCII), y QUIT respondiendo Sí a "¿Desea realmente
    desechar todos los cambios realizados al dibujo?": el DWG nunca se guarda. Sin QUIT, accoreconsole espera más
    órdenes al acabar el guion y no termina nunca (medido: hasta matarlo al vencer el plazo). Si DXFOUT fallara, las
    líneas siguientes son comandos desconocidos y QUIT sigue cerrando la sesión. Fin de línea CRLF exacto: una línea
    vacía repite el comando anterior (medido: "FILEDIA" repetido dejó la sesión atascada). No se cambia FILEDIA:
    accoreconsole pide el nombre en la línea de comandos. El texto se guarda con ``codificar_guion`` (ANSI), nunca UTF-8.
    """
    lineas = ["_.DXFOUT", f'"{salida_dxf}"', "_V", "2018", "16", "_.QUIT", "_Y"]
    return "\r\n".join(lineas) + "\r\n"


def _ansi(texto: str) -> bytes | None:
    """``texto`` en la página de códigos ANSI del sistema (CP_ACP, codec "mbcs" en Windows), o None si algún carácter
    no es representable. Estricto: "mbcs" con errors="strict" no sustituye por el carácter "más parecido"."""
    try:
        return texto.encode("mbcs" if os.name == "nt" else "latin-1", errors="strict")
    except UnicodeEncodeError:
        return None


def _ruta_corta(path: Path) -> str | None:
    """Nombre corto 8.3 de una carpeta existente (GetShortPathNameW), o None si el volumen no los genera."""
    if os.name != "nt":
        return None
    import ctypes  # noqa: PLC0415 - solo en Windows y solo en el caso raro de una ruta no ANSI

    buffer = ctypes.create_unicode_buffer(32768)
    n = ctypes.windll.kernel32.GetShortPathNameW(str(path), buffer, len(buffer))
    return buffer.value if 0 < n < len(buffer) else None


def codificar_guion(output_dir: Path) -> bytes:
    """Bytes del guion para DXFOUT a ``output_dir/entrada.dxf``.

    accoreconsole lee el .scr como ANSI (CP_ACP), no como UTF-8: con UTF-8, una carpeta con "ñ" llega como "Ã±",
    DXFOUT responde "Imposible abrir el archivo" y no hay DXF (confirmado por Fable con AutoCAD 2026; con "mbcs"
    funciona). Por eso: (1) la ruta en ANSI, que cubre cualquier usuario de Windows cuyo nombre esté en la página de
    códigos del sistema (José, Peña con cp1252); (2) si no es representable (p. ej. cirílico en un sistema cp1252), el
    nombre corto 8.3 de oda_out, que es ASCII, si el volumen lo genera; (3) si tampoco, OdaConversionError con
    MENSAJE_RUTA_NO_ANSI, motivo accoreconsole_fallo (fila no definitiva). Los argumentos /i, /s y /isolate no pasan por
    aquí: van por la línea de órdenes en UTF-16 (CreateProcessW).
    """
    datos = _ansi(guion_accore(output_dir / ACCORE_DXF))
    if datos is not None:
        return datos
    corta = _ruta_corta(output_dir)
    if corta is not None:
        datos = _ansi(guion_accore(Path(corta) / ACCORE_DXF))
        if datos is not None and corta.isascii():
            return datos
    raise OdaConversionError(f"Ruta del DXF no representable en ANSI: '{output_dir}'.", MENSAJE_RUTA_NO_ANSI, MOTIVO_ACCORE_FALLO)


def accore_args(accore_exe: Path, copia: Path, guion: Path, perfil: Path) -> list[str]:
    """/isolate: variables de sistema y registro de la sesión van a un perfil temporal, no al del usuario."""
    return [str(accore_exe), "/isolate", ACCORE_ISOLATE_ID, str(perfil), "/i", str(copia), "/s", str(guion)]


def accore_env(temp: Path) -> dict[str, str]:
    """Entorno del servidor con TEMP y TMP en la carpeta propia de accoreconsole (se borra con ella)."""
    return {**os.environ, "TEMP": str(temp), "TMP": str(temp)}


def _decode_accore(raw: bytes) -> str:
    """accoreconsole escribe su salida en UTF-16 LE (con o sin BOM); si no lo parece, UTF-8."""
    if raw.startswith(b"\xff\xfe"):
        return raw[2:].decode("utf-16-le", errors="replace")
    if len(raw) >= 2 and raw.count(b"\x00") * 3 >= len(raw):
        return raw.decode("utf-16-le", errors="replace")
    return raw.decode("utf-8", errors="replace")


def _accore_output(log_path: Path, copia: Path, nombre: str) -> tuple[str, bool]:
    """(líneas útiles de la salida de accoreconsole sin la ruta temporal de la copia, hay evidencia de rechazo)."""
    try:
        text = _decode_accore(log_path.read_bytes())
    except OSError:
        return "", False
    lineas = [linea.strip() for linea in text.replace("\r", "").split("\n") if linea.strip()]
    rechazo = [linea for linea in lineas if any(c in linea.lower() for c in ACCORE_EVIDENCIA_RECHAZO)]
    otras = ("imposible", "unable", "error", "no se puede", "cannot")
    utiles = rechazo or [linea for linea in lineas if any(c in linea.lower() for c in otras)] or lineas[-5:]
    texto = " | ".join(utiles)
    for ruta in (str(copia), copia.as_posix()):
        texto = texto.replace(ruta, nombre)
    return texto[:MAX_OUTPUT_CHARS], bool(rechazo)


def _convert_accore(
    accore_exe: Path,
    copia: Path,
    output_dir: Path,
    carpeta_trabajo: Path,
    timeout_s: float,
    nombre: str,
    cancelar: threading.Event | None,
    detalle_oda: str,
    solo_accore: bool = False,
) -> Path:
    """Conversión de la MISMA copia con accoreconsole; devuelve el DXF o lanza el error de los dos fallos.

    Errores: evidencia de rechazo en la salida → MENSAJE_DANADO (definitivo); sin DXF por cualquier otra causa (código
    ≠ 0 sin evidencia, o 0 sin DXF) → MENSAJE_ACCORE_FALLO, motivo accoreconsole_fallo (no definitivo). Con código ≠ 0
    el DXF que hubiera se descarta: podría estar truncado."""
    work = carpeta_trabajo / ACCORE_DIR
    remove_tree(work)
    # oda_out vacío: los informes .err de ODA no se confunden con la salida de AutoCAD.
    remove_tree(output_dir)
    try:
        (work / ACCORE_PERFIL).mkdir(parents=True)
        (work / ACCORE_TEMP).mkdir()
        output_dir.mkdir(exist_ok=True)
        guion = work / ACCORE_SCRIPT
        datos = codificar_guion(output_dir)
        guion.write_bytes(datos)
    except OSError as exc:
        raise OdaConversionError(f"No se pudo preparar accoreconsole: {type(exc).__name__}: {exc}") from exc
    if cancelar is not None and cancelar.is_set():
        raise OdaCanceladoError(nombre)
    log_path = work / ACCORE_LOG
    args = accore_args(accore_exe, copia, guion, work / ACCORE_PERFIL)
    return_code = _run(args, work, log_path, timeout_s, nombre, cancelar, ETAPA_ACCORE, accore_env(work / ACCORE_TEMP))
    dxf = _find_dxf(output_dir)
    if dxf is not None and return_code == 0:
        return dxf
    salida, rechazo = _accore_output(log_path, copia, nombre)
    descartado = " (DXF descartado: código de salida ≠ 0)" if dxf is not None else ""
    detalle = (
        f"ODA File Converter: {detalle_oda} · accoreconsole (código de salida {return_code}){descartado}: "
        f"{salida or 'sin salida'}"
    )[:MAX_OUTPUT_CHARS]
    if rechazo:
        raise OdaConversionError(detalle, MENSAJE_DANADO_SOLO_ACCORE if solo_accore else MENSAJE_DANADO, MOTIVO_DANADO)
    raise OdaConversionError(detalle, MENSAJE_ACCORE_FALLO, MOTIVO_ACCORE_FALLO)


def _disponible(exe: Path | None) -> bool:
    return exe is not None and Path(exe).is_file()


def dwg_a_dxf(
    ruta_dwg: Path,
    carpeta_trabajo: Path,
    *,
    oda_exe: Path,
    timeout_s: float,
    cancelar: threading.Event | None = None,
    accore_exe: Path | None = None,
    info: dict | None = None,
) -> Path:
    """Convierte una COPIA de ``ruta_dwg`` y devuelve el DXF en ``<carpeta_trabajo>/oda_out/``.

    El DXF es responsabilidad de quien llama (``remove_work_files``); la copia, el
    registro y la carpeta de accoreconsole se borran siempre aquí, y también oda_out si la conversión falla.
    ``cancelar`` (opcional, §5H.2): al activarse se mata el conversor que esté corriendo.

    Servidor 0.8.2: un DWG de 0 bytes no se pasa a ningún conversor (MENSAJE_VACIO). Si ODA termina sin DXF (o con
    código ≠ 0: su DXF se descarta) y ``accore_exe`` existe, la misma copia se convierte con accoreconsole, con su
    propio ``timeout_s`` (el mismo plazo, no lo que le sobró a ODA). Sin ODA instalado pero con accoreconsole, se usa
    accoreconsole directamente; OdaNoDisponibleError solo si faltan los dos. ``accore_exe`` dado pero inexistente →
    MENSAJE_SIN_RESPALDO; ``None`` (llamada sin respaldo) → el mensaje de siempre. Un ODA_TIMEOUT de ODA no pasa a
    accoreconsole: el archivo no es ilegible sino lento, y en el lote queda omitido para reanudar.
    ``info`` (opcional): se rellena con ``conversor`` ("oda" | "accoreconsole"), ``inicio`` (time.monotonic() al
    lanzar el conversor que generó el DXF), para el plazo de la lectura, y ``solo_accore`` (True si ODA no estaba
    instalado y convirtió accoreconsole).
    Lanza OdaNoDisponibleError, OdaTimeoutError, OdaCanceladoError u OdaConversionError.
    """
    ruta_dwg = Path(ruta_dwg)
    carpeta_trabajo = Path(carpeta_trabajo)
    oda_exe = Path(oda_exe)
    hay_oda = oda_exe.is_file()
    if not hay_oda and not _disponible(accore_exe):
        raise OdaNoDisponibleError(oda_exe, accore_exe)

    input_dir = carpeta_trabajo / INPUT_DIR
    output_dir = carpeta_trabajo / OUTPUT_DIR
    copia = input_dir / INPUT_NAME
    converted = False
    try:
        # Restos de un trabajo interrumpido: oda_in debe contener solo esta copia.
        remove_work_files(carpeta_trabajo)
        try:
            input_dir.mkdir(parents=True)
            output_dir.mkdir()
            shutil.copyfile(ruta_dwg, copia)
            vacio = copia.stat().st_size == 0
        except OSError as exc:
            raise OdaConversionError(f"No se pudo copiar el archivo: {type(exc).__name__}: {exc}") from exc
        if vacio:
            raise OdaConversionError("El archivo mide 0 bytes: no se pasó a ningún conversor.", MENSAJE_VACIO, MOTIVO_VACIO)

        if cancelar is not None and cancelar.is_set():
            raise OdaCanceladoError(ruta_dwg.name)
        dxf: Path | None = None
        conversor = CONVERSOR_ODA
        inicio = time.monotonic()
        if hay_oda:
            return_code = _run_converter(oda_exe, input_dir, output_dir, carpeta_trabajo, timeout_s, ruta_dwg.name, cancelar)
            dxf = _find_dxf(output_dir)
            # Medido el 2026-11-04: ODA sale con 0 tanto si convierte (11 planos) como si no lee el archivo (3). Un
            # código ≠ 0 no es una conversión normal: su DXF podría estar truncado y no se usa.
            if dxf is not None and return_code != 0:
                log.warning("ODA terminó con código %s y dejó un DXF de %s: se descarta", return_code, ruta_dwg.name)
                dxf = None
            if dxf is None:
                output = _oda_output(carpeta_trabajo, output_dir, ruta_dwg.name)
                detalle_oda = output or f"ODA File Converter no generó el DXF (código de salida {return_code})."
        else:
            detalle_oda = f"no está instalado en '{oda_exe}'; se usó accoreconsole directamente."
        if dxf is None:
            if accore_exe is None:
                raise OdaConversionError(detalle_oda)
            accore_exe = Path(accore_exe)
            if not accore_exe.is_file():
                detalle = f"{detalle_oda} · accoreconsole no encontrado en '{accore_exe}'."
                raise OdaConversionError(detalle[:MAX_OUTPUT_CHARS], MENSAJE_SIN_RESPALDO, MOTIVO_SIN_RESPALDO)
            log.info("Sin DXF de ODA para %s; se convierte con accoreconsole", ruta_dwg.name)
            inicio = time.monotonic()
            dxf = _convert_accore(
                accore_exe, copia, output_dir, carpeta_trabajo, timeout_s, ruta_dwg.name, cancelar, detalle_oda, not hay_oda
            )
            conversor = CONVERSOR_ACCORE
        if info is not None:
            info["conversor"] = conversor
            info["inicio"] = inicio
            # Sin ODA instalado: la advertencia de la respuesta no debe decir que ODA "no pudo leer" el archivo.
            info["solo_accore"] = conversor == CONVERSOR_ACCORE and not hay_oda
        converted = True
        return dxf
    finally:
        remove_tree(input_dir)
        remove_tree(carpeta_trabajo / LOG_FILE)
        remove_tree(carpeta_trabajo / ACCORE_DIR)
        if not converted:
            remove_tree(output_dir)
