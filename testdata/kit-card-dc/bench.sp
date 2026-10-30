* DC operating point of every synthetic card (brief-gdf-1b R-gdf1b-5).
* Each zero-volt source is an ammeter in series with one device terminal.
.include cards.lib
.temp 26.85
.options tnom=26.85

VCC vcc 0 3.3
VIN in  0 0.9
VB2 b2x 0 2.0

* the area + perimeter junction, forward biased through 10k
R1   vcc da   10k
VAD1 da  da1  0
D1   da1 0    dsyn_area  area=4
VAD2 da  da2  0
D2   da2 0    dsyn_perim area=8

* NPN, common emitter
RB   in   b1x  47k
VAB1 b1x  b1   0
RC   vcc  c1x  4.7k
VAC1 c1x  c1   0
Q1   c1   b1   0  qsyn_n area=2

* PNP, emitter fed from the rail, base held
RE2  vcc  e2x  2.2k
VAE2 e2x  e2   0
VAB2 b2x  b2   0
Q2   0    b2   e2 qsyn_p

* sheet resistor in a divider
VAR3 vcc  r3x  0
R3   r3x  r3   rsyn w=2u l=10u
R4   r3   0    1k

.op
.end
