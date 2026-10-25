# Microstrip junction arms

Regression data for brief DFR26-5 (R-dfr26-5-3): `MTEE` and `MCROSS` simulate the arm lengths they draw
(`L1`-`L3`, `L1`-`L4`) as MLIN sections from each pin to the junction's reference plane, with the junction
between them.

`junction-arms.cnl` holds four circuits, gated by `tests/Core.Tests/Devices/Microstrip/JunctionArmTests.cs`:

1. A tee with no branch past its through line, pin 3 open, against one MLIN of `L1 + L2`.
2. A tee with a real branch against the same junction built by hand from an `MTEE` with no lengths and three MLINs.
3. A cross whose arms 2 and 4 stop at arms 1/3's edge, pins 2 and 4 open, against one MLIN of `L1 + L3`.
4. A cross with four real arms against its junction built by hand with four MLINs.

The reference is exact, not fitted: two uniform lines of the same width cascaded ARE one line of their summed
length, and a junction behind lines is the hand-built circuit by definition. Agreement is to 1e-9 in S
(50 Ω) from 0.1 to 10 GHz.
