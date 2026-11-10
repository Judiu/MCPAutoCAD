"""Descubrimiento y selección de instancias de AutoCAD (protocol.md §1.3 y §7)."""

from __future__ import annotations

import getpass
import logging
import os
import re
import threading
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Any, Literal

from .errors import NoInstanceError

log = logging.getLogger(__name__)

PIPE_DIR = r"\\.\pipe\\"
PIPE_PREFIX = "\\\\.\\pipe\\"  # \\.\pipe\
PIPE_NAME_RE = re.compile(r"^mcp-autocad-(?P<usuario>.+)-(?P<pid>\d+)$")
ENV_PID = "MCP_AUTOCAD_PID"

SelectionMode = Literal["auto", "env", "manual"]


@dataclass(frozen=True)
class Instance:
    """Instancia de AutoCAD con el plugin cargado."""

    pid: int
    pipe: str  # ruta completa: \\.\pipe\mcp-autocad-<usuario>-<pid>
    user: str


def current_user() -> str:
    user = os.environ.get("USERNAME")
    if user:
        return user
    try:
        return getpass.getuser()
    except Exception:  # noqa: BLE001
        return ""


def parse_pipe_name(name: str) -> tuple[str, int] | None:
    """Devuelve (usuario, pid) si el nombre cumple el patrón del contrato."""
    match = PIPE_NAME_RE.match(name)
    if match is None:
        return None
    return match.group("usuario"), int(match.group("pid"))


def list_instances(user: str | None = None) -> list[Instance]:
    """Lista los pipes del plugin del usuario actual, ordenados por PID.

    Nunca lanza: si el listado de \\\\.\\pipe\\ falla, devuelve [].
    """
    wanted = (user if user is not None else current_user()).casefold()
    try:
        names = os.listdir(PIPE_DIR)
    except Exception as exc:  # noqa: BLE001 - el listado puede fallar de muchas formas
        log.warning("No se pudo listar los named pipes: %s", exc)
        return []
    found: dict[int, Instance] = {}
    for name in names:
        parsed = parse_pipe_name(name)
        if parsed is None:
            continue
        pipe_user, pid = parsed
        if pipe_user.casefold() != wanted:
            continue
        found[pid] = Instance(pid=pid, pipe=PIPE_PREFIX + name, user=pipe_user)
    return [found[pid] for pid in sorted(found)]


class InstanceSelector:
    """Política de selección de protocol.md §1.3.

    Prioridad: MCP_AUTOCAD_PID > selección manual (seleccionar_instancia) >
    única instancia > error NO_INSTANCE (con la lista de PIDs si hay varias).
    """

    def __init__(
        self,
        lister: Callable[[], list[Instance]] = list_instances,
        environ: Mapping[str, str] | None = None,
    ) -> None:
        self._lister = lister
        self._environ = environ if environ is not None else os.environ
        self._manual_pid: int | None = None
        self._lock = threading.Lock()

    # -- estado -------------------------------------------------------------
    def env_pid(self) -> int | None:
        raw = self._environ.get(ENV_PID)
        if raw is None or raw.strip() == "":
            return None
        try:
            return int(raw.strip())
        except ValueError:
            log.warning("%s=%r no es un entero; se ignora.", ENV_PID, raw)
            return None

    @property
    def manual_pid(self) -> int | None:
        return self._manual_pid

    @property
    def selection_mode(self) -> SelectionMode:
        if self.env_pid() is not None:
            return "env"
        if self._manual_pid is not None:
            return "manual"
        return "auto"

    def instances(self) -> list[Instance]:
        try:
            return list(self._lister())
        except Exception as exc:  # noqa: BLE001
            log.warning("Fallo al listar instancias: %s", exc)
            return []

    # -- política ------------------------------------------------------------
    def resolve(self, instances: list[Instance] | None = None) -> Instance:
        """Devuelve la instancia a usar o lanza NoInstanceError."""
        found = self.instances() if instances is None else instances
        by_pid = {inst.pid: inst for inst in found}
        env_pid = self.env_pid()
        if env_pid is not None:
            if env_pid in by_pid:
                return by_pid[env_pid]
            raise NoInstanceError(by_pid.keys(), requested_pid=env_pid)
        with self._lock:
            manual = self._manual_pid
        if manual is not None:
            if manual in by_pid:
                return by_pid[manual]
            raise NoInstanceError(by_pid.keys(), requested_pid=manual)
        if len(found) == 1:
            return found[0]
        if len(found) > 1:
            raise NoInstanceError(by_pid.keys())
        raise NoInstanceError()

    def select(self, pid: int) -> Instance:
        """Fija la instancia para el resto de la sesión (seleccionar_instancia)."""
        found = self.instances()
        by_pid = {inst.pid: inst for inst in found}
        if pid not in by_pid:
            raise NoInstanceError(by_pid.keys(), requested_pid=pid)
        with self._lock:
            self._manual_pid = pid
        return by_pid[pid]

    def payload(self) -> dict[str, Any]:
        """Forma de la herramienta `instancias` (§7)."""
        found = self.instances()
        try:
            selected: int | None = self.resolve(found).pid
        except NoInstanceError:
            selected = None
        return {
            "instancias": [
                {"pid": inst.pid, "pipe": inst.pipe, "seleccionada": inst.pid == selected} for inst in found
            ],
            "seleccion": self.selection_mode,
        }
