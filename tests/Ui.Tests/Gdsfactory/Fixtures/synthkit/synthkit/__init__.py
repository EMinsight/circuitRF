"""A made-up gdsfactory kit for circuitRF's gated tests (brief-gdf-2 §6 Tier B).

Nothing here models a real process. Each cell exists to exercise one thing the adapter claims:
geometry with holes and transformed references, every port type including ``placement``, one
parameter of every default kind, a declared model, and the three ways a cell is not offered.
"""

from functools import partial

import gdsfactory as gf
from gdsfactory.pdk import Pdk
from gdsfactory.technology import LayerMap
from gdsfactory.typings import Layer


class SynthLayers(LayerMap):
    MET1: Layer = (10, 0)
    MET2: Layer = (20, 0)
    VIA1: Layer = (15, 0)
    MARK: Layer = (63, 0)   # the technology in the tests deliberately lacks this one


LAYER = SynthLayers

_STRIP = gf.cross_section.cross_section(width=1.0, layer=(10, 0))


def _helper(x: float) -> float:
    return x


@gf.cell
def rect_pad(width: float = 10.0, height: float = 6.0) -> gf.Component:
    """A rectangle."""
    c = gf.Component()
    c.add_polygon([(0, 0), (width, 0), (width, height), (0, height)], layer="MET1")
    c.add_port("e1", center=(0, height / 2), width=height, orientation=180, layer="MET1",
               port_type="electrical")
    return c


@gf.cell
def frame(outer: float = 20.0, inner: float = 8.0) -> gf.Component:
    """A polygon with a hole."""
    c = gf.Component()
    m = (outer - inner) / 2
    poly = gf.kdb.DPolygon(gf.kdb.DBox(0, 0, outer, outer))
    poly.insert_hole(gf.kdb.DBox(m, m, m + inner, m + inner))
    c.add_polygon(poly, layer="MET2")
    return c


@gf.cell
def turned(gap: float = 5.0) -> gf.Component:
    """References rotated 90 degrees and mirrored."""
    c = gf.Component()
    pad = rect_pad(width=4.0, height=2.0)
    a = c.add_ref(pad)
    a.rotate(90)
    b = c.add_ref(pad)
    b.mirror_x()
    b.move((gap + 4.0, 0))
    c.add_ports(a.ports, prefix="a_")
    c.add_ports(b.ports, prefix="b_")
    return c


@gf.cell
def ported(length: float = 12.0) -> gf.Component:
    """One port of each type, placement included."""
    c = gf.Component()
    c.add_polygon([(0, -1), (length, -1), (length, 1), (0, 1)], layer="MET1")
    c.add_port("e1", center=(0, 0), width=2.0, orientation=180, layer="MET1", port_type="electrical")
    c.add_port("o1", center=(length, 0), width=2.0, orientation=0, layer="MET1", port_type="optical")
    c.add_port("p1", center=(length / 2, 1), width=2.0, orientation=90, layer="MET1", port_type="placement")
    return c


@gf.cell
def kinds(
    flag: bool = True,
    count: int = 3,
    inductance: float = 3.3303e-11,
    size: float = 4.0,
    label: str = "pad",
    layer_name: str = "MET1",
    model: str = "synth_res",
    nothing: None = None,
    corners: tuple = (1.0, 2.0),
    helper=_helper,
    xs=_STRIP,
) -> gf.Component:
    """One parameter of every default kind, a layer-name text parameter, and a declared model."""
    c = gf.Component()
    w = size * count
    c.add_polygon([(0, 0), (w, 0), (w, size), (0, size)], layer=layer_name)
    c.add_port("e1", center=(0, size / 2), width=size, orientation=180, layer=layer_name,
               port_type="electrical")
    return c


@gf.cell
def info_model(length: float = 5.0) -> gf.Component:
    """A model declared only in info."""
    c = gf.Component()
    c.add_polygon([(0, 0), (length, 0), (length, 1), (0, 1)], layer="MET1")
    c.info["model"] = "synth_cap"
    return c


@gf.cell
def broken(size: float = 1.0) -> gf.Component:
    """Raises."""
    raise ValueError("synthkit broken cell: this cell always fails")


@gf.cell
def needs_arg(size: float) -> gf.Component:
    """Has a required argument."""
    c = gf.Component()
    c.add_polygon([(0, 0), (size, 0), (size, size), (0, size)], layer="MET1")
    return c


@gf.cell
def marked(size: float = 3.0) -> gf.Component:
    """Uses a layer the technology lacks."""
    c = gf.Component()
    c.add_polygon([(0, 0), (size, 0), (size, size), (0, size)], layer="MARK")
    c.add_polygon([(0, 0), (size, 0), (size, 1), (0, 1)], layer="MET1")
    return c


PDK = Pdk(
    name="synthkit",
    cells={
        "rect_pad": rect_pad,
        "frame": frame,
        "turned": turned,
        "ported": ported,
        "kinds": kinds,
        "info_model": info_model,
        "broken": broken,
        "needs_arg": needs_arg,
        "marked": marked,
        "wide_pad": partial(rect_pad, width=20.0),
    },
    layers=LAYER,
    cross_sections={"strip": partial(gf.cross_section.cross_section, width=1.0, layer=(10, 0))},
)
