// brief-em3d-72 Q2 — two embedded heat-source surfaces through the Palace lowering's ONE fragment.
// SPIKE MATERIAL. Micrometres. Run: gmsh q2.geo -3 -format msh22 -o q2.msh  (writes q2-entities.txt)
//   (a) a strip INSIDE a solid: a 2 um x 60 um source rectangle at z = 95 in a 100 um die, 5 um below its
//       top face (a channel under a field plate), touching nothing;
//   (b) a foot contact patch ON the shared face of a pad and the mould above it: 50 um x 25 um on a
//       100 um x 100 um pad.
SetFactory("OpenCASCADE");
Box(1) = {0, 0, 0, 100, 100, 100};            // die
Box(2) = {200, 0, 0, 100, 100, 20};           // pad
Box(3) = {150, -50, 20, 200, 200, 80};        // mould block sitting on the pad (overlaps nothing)
Rectangle(1010) = {49, 20, 95, 2, 60};          // (a) strip inside the die
Rectangle(1011) = {225, 37.5, 20, 50, 25};      // (b) foot patch on the pad's top face
frag[] = BooleanFragments{ Volume{1, 2, 3}; Delete; }{ Surface{1010, 1011}; Delete; };
e = 1e-3;
strip[] = Surface In BoundingBox{49 - e, 20 - e, 95 - e, 51 + e, 80 + e, 95 + e};
foot[] = Surface In BoundingBox{225 - e, 37.5 - e, 20 - e, 275 + e, 62.5 + e, 20 + e};
die[] = Volume In BoundingBox{-e, -e, -e, 100 + e, 100 + e, 100 + e};
pad[] = Volume In BoundingBox{200 - e, -e, -e, 300 + e, 100 + e, 20 + e};
mould[] = Volume In BoundingBox{150 - e, -50 - e, 20 - e, 350 + e, 150 + e, 100 + e};
Physical Surface("strip", 101) = {strip[]};
Physical Surface("foot", 102) = {foot[]};
Physical Volume("die", 1) = {die[]};
Physical Volume("pad", 2) = {pad[]};
Physical Volume("mould", 3) = {mould[]};
allV[] = Volume{:};
Printf("strip %g surfaces (tag %g), foot %g surfaces (tag %g), volumes die %g pad %g mould %g, all volumes %g",
       #strip[], strip[0], #foot[], foot[0], #die[], #pad[], #mould[], #allV[]) > "q2-entities.txt";
Mesh.MeshSizeMax = 20;
Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0;
Field[1] = Distance; Field[1].SurfacesList = {strip[], foot[]}; Field[1].Sampling = 60;
Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = 1; Field[2].SizeMax = 20; Field[2].DistMin = 1; Field[2].DistMax = 60;
Background Field = 2;
Mesh.ElementOrder = 2; Mesh.HighOrderOptimize = 2;
