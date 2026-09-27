// brief-em3d-61 Q4 -- spike material. Import an OCCT-written part, fragment it with an air box,
// recover two named faces by a tight bounding-box query, mesh second order. Units: micrometres.
SetFactory("OpenCASCADE");
Geometry.OCCBooleanPreserveNumbering = 1;
Geometry.OCCBoundsUseStl = 1;
e = 1e-3;
part[] = ShapeFromFile("part-v2.brep");
bb[] = BoundingBox Volume{part[0]};
Printf("Q4 part box %g %g %g .. %g %g %g", bb[0], bb[1], bb[2], bb[3], bb[4], bb[5]);
bx = newv; Box(bx) = {-500, -500, -500, 2000, 1800, 1500};
frag[] = BooleanFragments{ Volume{part[]}; Delete; }{ Volume{bx}; Delete; };
allV[] = Volume{:};
allS[] = Surface{:};
fil[] = Surface In BoundingBox{300 - e, 200 - e, 450 - e, 700 + e, 600 + e, 500 + e};
top[] = Surface In BoundingBox{0 - e, 0 - e, 500 - e, 1000 + e, 800 + e, 500 + e};
Printf("Q4 volumes %g surfaces %g fillet-hits %g top-hits %g", #allV[], #allS[], #fil[], #top[]);
Mesh.MeshSizeMax = 150;
Mesh.MeshSizeFromCurvature = 12;
Mesh.ElementOrder = 2;
Mesh.HighOrderOptimize = 2;
