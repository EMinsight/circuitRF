"""The gdsfactory adapter: describe a ``Pdk``'s cells, and generate them.

**Describe.** :func:`register_pdk` imports the kit's module, finds its ``Pdk`` and activates it.
Each ``pdk.cells`` entry becomes one generator, its parameters read from ``inspect.signature`` in
declaration order. Only scalar defaults are offered (bool, int, float, str); anything else keeps its
default and is counted, and a cell with a required argument is not offered at all. Every cell is
built once at its defaults while describing, because that is the only way to learn its declared
``info["model"]`` and whether it builds at all. A cell that raises is listed with its exception's
last line and offered no further.

**Floats pass through exactly, in the kit's own units.** Every float is declared ``real`` — never
``length`` — so circuitRF sends back precisely the number it was given. A kit's floats are not all
lengths: an inductance of ``3.33e-11`` treated as one would be rounded to zero on the DBU grid. The
geometry gdsfactory returns is what gets scaled, once, with :func:`circuitrf_pcell.dbu`.

**Every port is a pin, whatever its type, except ``placement``.** A port's type is gdsfactory's
routing vocabulary — a coplanar line's ports may be typed ``optical`` — not a statement about
connectivity. ``placement`` ports are alignment marks; they are counted, not drawn as pins.

**A kit's own drawing of a cell is found by name, never guessed.** A kit may ship a ``.csym`` (circuitRF's
own symbol) or an SVG glyph whose file stem is the cell's name, in a ``symbols/`` directory of the package
or beside the cell's module. The ``.csym`` wins over a glyph of the same stem. The report names the file;
circuitRF builds the symbol (brief-gdf-4). A glyph cell's ports go in the report too, because a glyph has
no pin markup and the pins are placed from the ports.

**Nothing is worked around.** A cell gdsfactory itself cannot build is reported with gdsfactory's own
message (a missing module is named as the error names it; a missing fixed-GDS file by its path), so
the reader can see whose install it is.
"""

from __future__ import annotations

import importlib
import inspect
import os
import sys
import traceback
from typing import Any, Callable

import circuitrf_pcell as crf

__all__ = ["GDSFACTORY_MISSING", "PdkRegistration", "register_pdk"]

#: The sentence for an environment with no gdsfactory in it. Shared in shape with circuitRF's own
#: discovery refusal (``GdsfactoryKitDiscovery``), so the reader meets one wording wherever it is
#: caught. ``{python}`` is the interpreter that looked.
GDSFACTORY_MISSING = (
    "gdsfactory is not installed in the Python environment at '{python}'. A kit installed with "
    "--no-deps leaves its dependencies behind: install gdsfactory into that environment. "
    "circuitRF installs nothing."
)

#: Reasons a cell is not offered, as the import report groups them.
REQUIRED_ARGUMENTS = "required arguments"
RAISED = "raised while being built at its defaults"
NO_SIGNATURE = "no readable signature"

#: How many names a "parameters not offered" problem lists before it says "…".
_LISTED = 6

#: The package directory a kit keeps its drawings in, when it does not keep them beside the cell's module.
SYMBOLS_DIR = "symbols"

#: A kit's own drawing, in the order a stem is looked for: circuitRF's own symbol wins over a glyph.
_DRAWINGS = ((".csym", "symbol"), (".svg", "glyph"))


class PdkRegistration:
    """What :func:`register_pdk` found, and what it could not offer.

    ``report`` is the structured form of the same facts, for the import report: cells offered, cells
    not offered grouped by reason, each cell's parameters not offered, its ``placement`` ports and its
    declared model.
    """

    def __init__(self, ids: list[str], problems: list[str], report: dict[str, Any] | None = None):
        self.ids = ids
        self.problems = problems
        self.report = report or {}

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"PdkRegistration({len(self.ids)} cells, {len(self.problems)} problems)"


def _last_line(exc: BaseException) -> str:
    return "".join(traceback.format_exception_only(type(exc), exc)).strip().splitlines()[-1]


def _find_pdk(module: Any, module_name: str, pdk_type: type) -> tuple[Any, str | None]:
    """``module.PDK``, else the only ``Pdk`` instance at module level, else a problem naming it."""
    named = getattr(module, "PDK", None)
    if isinstance(named, pdk_type):
        return named, None
    found = [(n, v) for n, v in vars(module).items() if isinstance(v, pdk_type)]
    if len(found) == 1:
        return found[0][1], None
    if not found:
        return None, (f"'{module_name}' defines no gdsfactory Pdk: there is no PDK attribute and no "
                      "Pdk instance at module level.")
    names = ", ".join(sorted(n for n, _ in found))
    return None, (f"'{module_name}' defines {len(found)} gdsfactory Pdk objects ({names}) and no PDK "
                  "attribute saying which one is the kit.")


def _layer_names(pdk: Any) -> dict[tuple[int, int], str]:
    """``(layer, datatype)`` → the kit's own name for it."""
    names: dict[tuple[int, int], str] = {}
    try:
        for member in pdk.layers or []:
            names.setdefault((int(member.layer), int(member.datatype)), str(member.name))
    except Exception:  # noqa: BLE001 - a kit with no layer table still has cells
        pass
    return names


def _module_dir(fn: Any) -> str | None:
    """The directory of the module a cell function is defined in, or None when it cannot be told.

    A kit's cells are often ``functools.partial`` objects or decorated functions; both are unwrapped first.
    """
    target = fn
    for _ in range(8):
        inner = getattr(target, "func", None)
        if inner is None:
            break
        target = inner
    try:
        target = inspect.unwrap(target)
        path = inspect.getsourcefile(target) or inspect.getfile(target)
    except (TypeError, OSError):
        return None
    return os.path.dirname(os.path.abspath(path)) if path else None


def _drawing_dirs(package_dir: str | None, module_dir: str | None) -> list[str]:
    """Where a cell's drawing may be: the package's ``symbols/``, then the module's ``symbols/`` and the module's own
    directory. Nothing further — the further out it looks, the more likely a file matches by accident."""
    dirs: list[str] = []
    for d in (os.path.join(package_dir, SYMBOLS_DIR) if package_dir else None,
              os.path.join(module_dir, SYMBOLS_DIR) if module_dir else None,
              module_dir):
        if d and d not in dirs and os.path.isdir(d):
            dirs.append(d)
    return dirs


def _find_drawing(cell: str, dirs: list[str], listing: dict[str, set[str]]) -> tuple[str, str] | None:
    """``(kind, path)`` of the kit's own drawing of ``cell`` — kind ``symbol`` or ``glyph`` — or None.

    The file STEM is compared with the cell name exactly, against the directory's own listing: on a
    case-insensitive file system ``isfile`` would answer for ``Cell.svg`` when the cell is ``cell``.
    """
    for ext, kind in _DRAWINGS:
        for d in dirs:
            if d not in listing:
                try:
                    listing[d] = set(os.listdir(d))
                except OSError:
                    listing[d] = set()
            if cell + ext in listing[d]:
                return kind, os.path.join(d, cell + ext)
    return None


def _ports_of(component: Any) -> list[dict[str, Any]]:
    """Every port but ``placement`` ones, in µm and degrees, in the component's own order — what a glyph's pins
    are placed from."""
    ports = []
    for port in component.ports:
        if str(port.port_type) == "placement":
            continue
        x, y = port.center
        ports.append({"name": str(port.name), "x": float(x), "y": float(y),
                      "orientation": float(port.orientation if port.orientation is not None else 0.0)})
    return ports


def _kind_of(default: Any) -> str | None:
    """The circuitRF kind a default declares, or None when it is not offered.

    bool BEFORE int: a bool is an int in Python, and the other order sends every flag as 1.
    """
    if isinstance(default, bool):
        return "bool"
    if isinstance(default, int):
        return "int"
    if isinstance(default, float):
        return "real"
    if isinstance(default, str):
        return "string"
    return None


def _declare(name: str, kind: str, default: Any) -> "crf.Parameter":
    if kind == "bool":
        return crf.Parameter.flag(name, default)
    if kind == "int":
        return crf.Parameter.integer(name, default)
    if kind == "real":
        # NEVER Parameter.length: the number is the kit's, in the kit's units, and travels unconverted.
        return crf.Parameter.real(name, default)
    return crf.Parameter.text(name, default)


def _read(params: "crf.Parameters", name: str, kind: str, default: Any) -> Any:
    if kind == "bool":
        return params.flag(name, default)
    if kind == "int":
        return params.integer(name, default)
    if kind == "real":
        return params.real(name, default)
    return params.text(name, default)


def _listed(names: list[str]) -> str:
    shown = ", ".join(names[:_LISTED])
    return shown + (", …" if len(names) > _LISTED else "")


def _make_generator(gf: Any, cell: str, offered: list[tuple[str, str, Any]],
                    kit_layers: dict[tuple[int, int], str]) -> Callable[..., "crf.Result"]:
    def generate(params: "crf.Parameters", tech: "crf.Technology") -> "crf.Result":
        # Only what differs from the default is passed, so gdsfactory's own cell cache keys on exactly
        # what the cell function would have seen had it been called by hand.
        kwargs = {}
        for name, kind, default in offered:
            value = _read(params, name, kind, default)
            if value != default:
                kwargs[name] = value

        component = gf.get_component(cell, **kwargs)
        per_micron = getattr(tech, "dbu_per_micron", None) or 1000
        kit_dbu = float(component.kcl.dbu)  # micrometres per kit database unit

        def scale_um(value_um: float) -> int:
            return crf.dbu(value_um * per_micron)

        by_key = {(e.layer, e.datatype): crf.Layer(e.layer, e.datatype) for e in tech.layers}
        missing: dict[tuple[int, int], str] = {}

        def layer_for(key: tuple[int, int]) -> "crf.Layer":
            if key in by_key:
                return by_key[key]
            kit_name = kit_layers.get(key)
            if kit_name and tech.layer_named(kit_name) is not None:
                return tech.layer_named(kit_name)
            # NOT dropped: kept on the kit's own number, and said. A layer silently lost is a
            # cell that draws incompletely and looks fine.
            missing[key] = kit_name or ""
            return crf.Layer(*key)

        shapes: list[crf.Shape] = []
        polygons = component.get_polygons(by="tuple")
        for key in sorted(polygons):
            layer = layer_for((int(key[0]), int(key[1])))
            for poly in polygons[key]:
                hull = [scale_um(c * kit_dbu) for p in poly.each_point_hull() for c in (p.x, p.y)]
                holes = [[scale_um(c * kit_dbu) for p in poly.each_point_hole(h) for c in (p.x, p.y)]
                         for h in range(poly.holes())]
                shapes.append(crf.Polygon(layer, hull, holes or None))

        pins: list[crf.Pin] = []
        placement = 0
        for port in component.ports:
            if str(port.port_type) == "placement":
                placement += 1
                continue
            info = port.layer_info
            x, y = port.center
            pins.append(crf.Pin(
                str(port.name), scale_um(x), scale_um(y),
                layer_for((int(info.layer), int(info.datatype))),
                scale_um(port.width),
                float(port.orientation if port.orientation is not None else 0.0)))

        diagnostics: list[str] = []
        if missing and tech.layers:
            listed = ", ".join(f"{l}/{d}" + (f" ({n})" if n else "") for (l, d), n in sorted(missing.items()))
            diagnostics.append(f"Layers the technology lacks, kept on the kit's own numbers: {listed}.")
        elif missing:
            diagnostics.append("No technology was resolved, so every layer keeps the kit's own number.")
        if placement:
            diagnostics.append(f"{placement} placement port(s) are alignment marks and were not made pins.")
        return crf.Result(shapes=shapes, pins=pins, diagnostics=diagnostics)

    return generate


def register_pdk(module_name: str, registry: Any = None) -> PdkRegistration:
    """Register every cell of ``module_name``'s gdsfactory ``Pdk`` as a circuitRF generator.

    **A cell that cannot be offered is skipped and reported, never fatal** — one broken cell must not
    cost a designer every other cell in the kit.
    """
    try:
        import gdsfactory as gf
        from gdsfactory.pdk import Pdk
    except ImportError:
        sentence = GDSFACTORY_MISSING.format(python=sys.executable)
        return PdkRegistration([], [sentence], {"problem": sentence})

    try:
        module = importlib.import_module(module_name)
    except Exception as exc:  # noqa: BLE001 - a kit that will not import is reported, not raised
        sentence = f"Could not import '{module_name}': {_last_line(exc)}"
        return PdkRegistration([], [sentence], {"problem": sentence})

    pdk, problem = _find_pdk(module, module_name, Pdk)
    if pdk is None:
        return PdkRegistration([], [problem], {"problem": problem})

    try:
        pdk.activate()
    except Exception as exc:  # noqa: BLE001
        sentence = f"'{module_name}': its Pdk could not be activated: {_last_line(exc)}"
        return PdkRegistration([], [sentence], {"problem": sentence})

    kit_layers = _layer_names(pdk)
    layer_name_set = set(kit_layers.values())
    module_file = getattr(module, "__file__", None)
    package_dir = os.path.dirname(os.path.abspath(module_file)) if module_file else None
    listing: dict[str, set[str]] = {}

    ids: list[str] = []
    problems: list[str] = []
    not_offered: dict[str, list[list[str]]] = {}
    params_not_offered: dict[str, list[str]] = {}
    placement_ports: dict[str, int] = {}
    models: dict[str, str] = {}
    declared_names: dict[str, list[str]] = {}
    symbols: dict[str, str] = {}
    glyphs: dict[str, str] = {}
    glyph_ports: dict[str, list[dict[str, Any]]] = {}

    def refuse(cell: str, reason: str, detail: str) -> None:
        not_offered.setdefault(reason, []).append([cell, detail])
        problems.append(f"{cell}: not offered ({reason}): {detail}")

    for cell in sorted(pdk.cells):
        fn = pdk.cells[cell]
        try:
            signature = inspect.signature(fn)
        except (TypeError, ValueError) as exc:
            refuse(cell, NO_SIGNATURE, str(exc))
            continue

        ordinary: list[tuple[str, str, Any]] = []
        layer_params: list[tuple[str, str, Any]] = []
        hidden: list[str] = []
        required: list[str] = []
        for p in signature.parameters.values():
            if p.kind in (p.VAR_POSITIONAL, p.VAR_KEYWORD):
                continue
            if p.default is p.empty:
                required.append(p.name)
                continue
            kind = _kind_of(p.default)
            if kind is None:
                hidden.append(p.name)
            elif kind == "string" and p.default in layer_name_set:
                # A layer name: a real parameter, but not the one a designer reaches for first.
                layer_params.append((p.name, kind, p.default))
            else:
                ordinary.append((p.name, kind, p.default))

        if required:
            refuse(cell, REQUIRED_ARGUMENTS, ", ".join(required))
            continue

        try:
            built = gf.get_component(cell)
            # Flattened too, exactly as generate flattens: a cell gdsfactory can build but not flatten
            # (its all-angle components, an API mismatch inside gdsfactory) is refused HERE, with
            # gdsfactory's own message, rather than offered and failing on placement. Not worked around.
            built.get_polygons(by="tuple")
        except Exception as exc:  # noqa: BLE001 - gdsfactory's own message is the report
            refuse(cell, RAISED, _last_line(exc))
            continue

        offered = ordinary + layer_params
        model = next((d for n, k, d in offered if n == "model" and isinstance(d, str) and d), None)
        if model is None:
            declared = dict(getattr(built, "info", {}) or {}).get("model")
            model = str(declared) if declared else None

        declarations = [_declare(n, k, d) for n, k, d in offered]
        # A model the cell declares only in its info is still reported as the generator's model, so
        # the palette's model step can pair it. Declared as an OUTPUT: there is no such argument to
        # pass back, so it is shown and never sent to the cell.
        if model is not None and not any(n == "model" for n, _, _ in offered):
            declarations.append(crf.Parameter("model", "string", crf.NONE, model, computed=True))

        try:
            (registry or crf.default_registry).add(
                cell, declarations, _make_generator(gf, cell, offered, kit_layers))
        except ValueError as exc:
            problems.append(str(exc))
            continue

        ids.append(cell)
        declared_names[cell] = [d.name for d in declarations]
        if hidden:
            params_not_offered[cell] = hidden
            problems.append(f"{cell}: {len(hidden)} parameter(s) not offered: {_listed(hidden)}")
        placements = sum(1 for port in built.ports if str(port.port_type) == "placement")
        if placements:
            placement_ports[cell] = placements
        if model:
            models[cell] = model

        drawing = _find_drawing(cell, _drawing_dirs(package_dir, _module_dir(fn)), listing)
        if drawing is not None and drawing[0] == "symbol":
            symbols[cell] = drawing[1]
        elif drawing is not None:
            glyphs[cell] = drawing[1]
            glyph_ports[cell] = _ports_of(built)

    report = {
        "module": module_name,
        "gdsfactoryVersion": getattr(gf, "__version__", ""),
        "offered": ids,
        "notOffered": not_offered,
        "parametersNotOffered": params_not_offered,
        "placementPorts": placement_ports,
        "models": models,
        "parameters": declared_names,
        "symbols": symbols,
        "glyphs": glyphs,
        "ports": glyph_ports,
    }
    return PdkRegistration(ids, problems, report)
