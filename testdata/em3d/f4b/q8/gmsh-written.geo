// brief-em3d-61 Q8 -- spike material. A STEP file written by another open-source tool (Gmsh 4.15.2):
// a block with a bore and a filleted pin standing on it. Run: gmsh gmsh-written.geo -0
SetFactory("OpenCASCADE");
Box(1) = {0, 0, 0, 1000, 800, 500};
Cylinder(2) = {300, 400, -100, 0, 0, 700, 120};
BooleanDifference(3) = { Volume{1}; Delete; }{ Volume{2}; Delete; };
Cylinder(4) = {700, 400, 500, 0, 0, 400, 80};
c[] = Curve In BoundingBox{600, 300, 899, 800, 500, 901};
f[] = Fillet{4}{c[]}{20};
Save "gmsh-written.step";
