// Q1 variant: a CURVED wire (a spline through a loop's points), feet on the bottom face, OCC fragment.
SetFactory("OpenCASCADE");
Box(1) = {0, 0, 0, 2000, 1000, 800};
Point(1101) = {200, 500, 0}; Point(1102) = {300, 500, 450}; Point(1103) = {700, 500, 600};
Point(1104) = {1300, 500, 550}; Point(1105) = {1800, 500, 0};
Spline(1001) = {1101, 1102, 1103, 1104, 1105};
f[] = BooleanFragments{ Volume{1}; Delete; }{ Curve{1001}; Delete; };
// a spline THROUGH points overshoots them: this one reaches x = 159.5, 40 um outside its own foot
c[] = Curve In BoundingBox{100, 499, -1, 1801, 501, 700};
Printf("wire curves after fragment: %g", #c[]);
Physical Volume("mould") = {Volume{:}};
Physical Curve("wire") = {c[]};
Mesh.MeshSizeMax = 120;
Field[1] = Distance; Field[1].CurvesList = {c[]}; Field[1].Sampling = 200;
Field[2] = Threshold; Field[2].InField = 1; Field[2].SizeMin = 20; Field[2].SizeMax = 120; Field[2].DistMin = 20; Field[2].DistMax = 400;
Background Field = 2;
Mesh.MeshSizeFromPoints = 0; Mesh.MeshSizeExtendFromBoundary = 0;
Mesh.ElementOrder = 2; Mesh.HighOrderOptimize = 2;
