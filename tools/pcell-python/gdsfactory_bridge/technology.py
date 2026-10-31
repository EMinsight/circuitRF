"""``python -m gdsfactory_bridge.technology <module>`` — a kit's process, as one JSON object on stdout.

The source of the technology circuitRF builds from a gdsfactory kit (brief-gdf-3 R-gdf3-1). It READS and
nothing more: every judgement — which level is a conductor, what fills the gaps between them, what is
reported — is circuitRF's, made in ``GdsfactoryStackConverter`` and built by the one technology builder.
It imports the kit, so circuitRF runs it only after consent, exactly as it runs ``report``.

The document holds:

- ``layers``: the kit's layer name → ``[layer, datatype]``.
- ``layerViews``: the path of a layer-properties file the kit's own ``LayerViews.to_lyp`` wrote into the
  directory ``CRF_GDF_VIEWS_DIR`` names — one circuitRF made for this run, reads with the reader it
  already has, and removes itself (a fresh temporary directory when run by hand) — or null when the kit
  has no layer views.
- ``stack``: one entry per level, in the kit's order: ``name``, ``layer`` (``[layer, datatype]`` when the
  level names one layer, else the expression as text), ``zmin``, ``thickness``, ``material``,
  ``sidewall_angle``, ``info``.
- ``materials``: material name → ``{sigma, epsr, tand}``. Missing values are null, never 0.
- ``problems``: what could not be read.

**Material cards are found by TYPE, never by module name**: any object at module level in the kit's own
package (or one level inside a dict, list or tuple there) whose ``rf`` regime states a conductivity or a
permittivity, plus the ``Pdk``'s own material index when its entries do. A name is never what makes
something a card.
"""

from __future__ import annotations

import contextlib
import io
import json
import math
import os
import sys
import tempfile
from typing import Any

from .bridge import GDSFACTORY_MISSING, _find_pdk, _last_line


def _number(value: Any) -> float | None:
    """A finite float, from a number or from anything carrying one as ``.value``; else None."""
    if value is None or isinstance(value, bool):
        return None
    if not isinstance(value, (int, float)):
        value = getattr(value, "value", None)
        if value is None or isinstance(value, bool) or not isinstance(value, (int, float)):
            return None
    value = float(value)
    return value if math.isfinite(value) else None


def _card_values(card: Any) -> dict[str, float | None] | None:
    """``{sigma, epsr, tand}`` from an object whose ``rf`` regime states either; None otherwise."""
    rf = getattr(card, "rf", None)
    if rf is None:
        return None
    perm = getattr(rf, "permittivity", None)
    epsr = _number(getattr(perm, "eps_real", None)) if perm is not None else None
    eps_imag = _number(getattr(perm, "eps_imag", None)) if perm is not None else None
    sigma = _number(getattr(perm, "conductivity", None)) if perm is not None else None
    if sigma is None:
        sigma = _number(getattr(rf, "conductivity", None))
    if epsr is None and sigma is None:
        return None
    tand = abs(eps_imag) / epsr if eps_imag is not None and epsr else None
    return {"sigma": sigma, "epsr": epsr, "tand": tand}


def _cards_in(value: Any, key: Any = None):
    """(name, values) for ``value`` itself, or for each element when it is a dict, list or tuple."""
    found = _card_values(value)
    if found is not None:
        name = getattr(value, "name", None) or key
        if isinstance(name, str) and name:
            yield name, found
        return
    if isinstance(value, dict):
        for k, v in value.items():
            values = _card_values(v)
            name = getattr(v, "name", None) or k
            if values is not None and isinstance(name, str) and name:
                yield name, values
    elif isinstance(value, (list, tuple)):
        for v in value:
            values = _card_values(v)
            name = getattr(v, "name", None)
            if values is not None and isinstance(name, str) and name:
                yield name, values


def _materials(module_name: str, pdk: Any) -> dict[str, dict[str, float | None]]:
    """Every material card the kit's own package exposes, first statement of a name winning."""
    cards: dict[str, dict[str, float | None]] = {}
    prefix = module_name + "."
    for name in sorted(n for n in list(sys.modules) if n == module_name or n.startswith(prefix)):
        module = sys.modules.get(name)
        if module is None:
            continue
        for key, value in list(vars(module).items()):
            if key.startswith("__"):
                continue
            for card, values in _cards_in(value, key):
                cards.setdefault(card, values)
    for card, values in _cards_in(getattr(pdk, "materials_index", None) or {}):
        cards.setdefault(card, values)
    return cards


def _layer_of(level: Any) -> list[int] | str | None:
    """``[layer, datatype]`` when the level names one layer; the expression as text otherwise."""
    logical = getattr(level, "layer", None)
    if logical is None:
        return None
    inner = getattr(logical, "layer", logical)
    if isinstance(inner, tuple) and len(inner) == 2:
        try:
            return [int(inner[0]), int(inner[1])]
        except (TypeError, ValueError):
            pass
    layer, datatype = getattr(inner, "layer", None), getattr(inner, "datatype", None)
    if isinstance(layer, int) and isinstance(datatype, int):
        return [layer, datatype]
    return str(logical)


def _info(level: Any) -> dict[str, Any]:
    raw = getattr(level, "info", None) or {}
    try:
        return json.loads(json.dumps(dict(raw), default=str))
    except (TypeError, ValueError):
        return {}


def read(module_name: str) -> dict[str, Any]:
    """The kit's process as a JSON-ready dict. Never raises for a kit's own failure: it is a problem."""
    problems: list[str] = []
    body: dict[str, Any] = {"module": module_name, "layers": {}, "layerViews": None, "stack": [],
                            "materials": {}, "problems": problems}
    try:
        import importlib

        from gdsfactory.pdk import Pdk
    except ImportError:
        problems.append(GDSFACTORY_MISSING.format(python=sys.executable))
        return body

    try:
        module = importlib.import_module(module_name)
    except Exception as exc:  # noqa: BLE001 - a kit that will not import is reported, not raised
        problems.append(f"Could not import '{module_name}': {_last_line(exc)}")
        return body

    pdk, problem = _find_pdk(module, module_name, Pdk)
    if pdk is None:
        problems.append(problem)
        return body
    try:
        pdk.activate()
    except Exception as exc:  # noqa: BLE001
        problems.append(f"'{module_name}': its Pdk could not be activated: {_last_line(exc)}")
        return body

    try:
        for member in pdk.layers or []:
            body["layers"].setdefault(str(member.name), [int(member.layer), int(member.datatype)])
    except Exception as exc:  # noqa: BLE001 - a kit with no layer map still has a stack
        problems.append(f"The kit's layer map could not be read: {_last_line(exc)}")

    try:
        # None is a kit with no layer views, not a failure: the layer map stands in for them.
        views = getattr(pdk, "layer_views", None)
        if views is not None and not hasattr(views, "to_lyp") and hasattr(pdk, "get_layer_views"):
            views = pdk.get_layer_views()
        if views is not None and hasattr(views, "to_lyp"):
            # circuitRF hands over a directory it made and removes itself; run by hand, a fresh one.
            out_dir = os.environ.get("CRF_GDF_VIEWS_DIR") or tempfile.mkdtemp(prefix="crf-gdf-views-")
            path = os.path.join(out_dir, "layer_views.lyp")
            views.to_lyp(path)
            body["layerViews"] = path
    except Exception as exc:  # noqa: BLE001
        problems.append(f"The kit's layer views could not be written: {_last_line(exc)}")

    stack = getattr(pdk, "layer_stack", None)
    if stack is None:
        problems.append(f"'{module_name}' states no layer stack.")
    else:
        try:
            for name, level in stack.layers.items():
                body["stack"].append({
                    "name": str(name),
                    "layer": _layer_of(level),
                    "zmin": _number(getattr(level, "zmin", None)),
                    "thickness": _number(getattr(level, "thickness", None)),
                    "material": getattr(level, "material", None),
                    "sidewall_angle": _number(getattr(level, "sidewall_angle", None)),
                    "info": _info(level),
                })
        except Exception as exc:  # noqa: BLE001
            problems.append(f"The kit's layer stack could not be read: {_last_line(exc)}")

    try:
        body["materials"] = _materials(module_name, pdk)
    except Exception as exc:  # noqa: BLE001
        problems.append(f"The kit's material cards could not be read: {_last_line(exc)}")
    return body


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print("usage: python -m gdsfactory_bridge.technology <module>", file=sys.stderr)
        return 2

    # A kit that prints while it imports must not corrupt the one line this writes.
    noise = io.StringIO()
    with contextlib.redirect_stdout(noise):
        body = read(argv[0])
    if noise.getvalue():
        sys.stderr.write(noise.getvalue())
    print(json.dumps(body))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
