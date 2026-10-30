"""Turn a gdsfactory ``Pdk``'s cells into circuitRF generators.

This is to a gdsfactory kit what :mod:`cni.bridge` is to a ``cni`` cell library: the join, and
nothing more. A kit's entry script (written into the workspace by circuitRF, see
``docs/design/pcell-gdsfactory-bridge.md``) is::

    import circuitrf_pcell as crf
    from gdsfactory_bridge import register_pdk

    result = register_pdk("<module>")
    for problem in result.problems:
        print(problem, file=__import__("sys").stderr)
    crf.run()

**Importing this package never imports gdsfactory.** gdsfactory is imported by :func:`register_pdk`,
so an environment without it is answered with one sentence (:data:`GDSFACTORY_MISSING`) rather than a
traceback from the import line.
"""

from .bridge import GDSFACTORY_MISSING, PdkRegistration, register_pdk

__all__ = ["GDSFACTORY_MISSING", "PdkRegistration", "register_pdk"]
