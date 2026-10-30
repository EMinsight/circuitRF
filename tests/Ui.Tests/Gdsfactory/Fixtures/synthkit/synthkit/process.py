"""The synthetic kit's made-up process (brief-gdf-3 §4 Tier B).

A layer stack with a substrate, a well, an oxide, two metals and a via, and material cards in the open card
SHAPE some kits use: an object whose ``rf`` regime states a conductivity or a permittivity. The cards are
plain namespaces on purpose. circuitRF finds a card by what it exposes, never by the package that defined it,
and this module is not imported under any name circuitRF could look for.
"""

from types import SimpleNamespace as _Ns

from gdsfactory.technology import LayerLevel, LayerStack
from gdsfactory.technology.layer_stack import LogicalLayer


def _card(name: str, sigma: float, epsr: float | None = None) -> _Ns:
    """A material card: a conductor states its conductivity, a dielectric its permittivity."""
    if epsr is None:
        return _Ns(name=name, rf=_Ns(permittivity=None, conductivity=_Ns(value=sigma)))
    permittivity = _Ns(eps_real=_Ns(value=epsr), eps_imag=_Ns(value=epsr * 0.002), conductivity=_Ns(value=sigma))
    return _Ns(name=name, rf=_Ns(permittivity=permittivity, conductivity=None))


MATERIAL_CARDS = {
    card.name: card
    for card in (
        _card("synth_metal", 3.0e7),
        _card("synth_plug", 1.0e6),
        _card("synth_oxide", 0.0, 4.0),
        _card("synth_bulk", 2.0, 11.9),
    )
}


def _level(layer: tuple[int, int], zmin: float, thickness: float, material: str, **extra) -> LayerLevel:
    return LayerLevel(layer=LogicalLayer(layer=layer), zmin=zmin, thickness=thickness, material=material, **extra)


LAYER_STACK = LayerStack(
    layers={
        "substrate": _level((99, 0), -200.0, 200.0, "synth_bulk"),
        "well": _level((5, 0), -1.0, 1.2, "synth_bulk"),
        "ild": _level((98, 0), 0.0, 1.0, "synth_oxide"),
        "met1": _level((10, 0), 1.0, 0.5, "synth_metal"),
        "via1": _level((15, 0), 1.5, 0.7, "synth_plug", sidewall_angle=5.0),
        "met2": _level((20, 0), 2.2, 1.0, "synth_metal"),
    }
)
