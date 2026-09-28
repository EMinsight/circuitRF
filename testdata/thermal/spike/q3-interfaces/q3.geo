// brief-em3d-72 Q3 — two touching solids after the lowering's fragment share ONE surface: can a reader
// find it, and which volume lies on each side, from what Gmsh writes? SPIKE MATERIAL. Micrometres.
// Run: gmsh q3.geo -3 -format msh22 -o q3.msh   (writes q3-entities.txt)
// A die (1 mm x 1 mm x 100 um) on a flange (3 x 3 x 1 mm) that is larger than it, plus a lid touching
// the die's top: the die/flange contact is PART of the flange's top face, so the fragment must split it.
SetFactory("OpenCASCADE");
Box(1) = {1000, 1000, 1000, 1000, 1000, 100};   // die
Box(2) = {0, 0, 0, 3000, 3000, 1000};           // flange
Box(3) = {1200, 1200, 1100, 600, 600, 50};      // lid on the die
BooleanFragments{ Volume{1, 2, 3}; Delete; }{ }
Physical Volume("die", 1) = {1};
Physical Volume("flange", 2) = {2};
Physical Volume("lid", 3) = {3};
// The entity table: for every surface, the volumes it bounds. Boundary{} of each volume, inverted here.
allV[] = Volume{:};
allS[] = Surface{:};
Printf("# q3 entity table: surface <tag> bounds <n> volumes: <v...>") > "q3-entities.txt";
For i In {0:#allS[]-1}
  n = 0; a = -1; b = -1;
  For j In {0:#allV[]-1}
    bnd[] = Abs(Boundary{ Volume{allV[j]}; });
    For k In {0:#bnd[]-1}
      If (bnd[k] == allS[i])
        If (n == 0) a = allV[j]; Else b = allV[j]; EndIf
        n += 1;
      EndIf
    EndFor
  EndFor
  If (n == 2)
    Printf("surface %g bounds 2 volumes: %g %g", allS[i], a, b) >> "q3-entities.txt";
  EndIf
EndFor
// the contact surfaces as physical groups, so the mesh carries them too
e = 1e-3;
dieFlange[] = Surface In BoundingBox{1000 - e, 1000 - e, 1000 - e, 2000 + e, 2000 + e, 1000 + e};
dieLid[] = Surface In BoundingBox{1200 - e, 1200 - e, 1100 - e, 1800 + e, 1800 + e, 1100 + e};
Physical Surface("die_flange", 12) = {dieFlange[]};
Physical Surface("die_lid", 13) = {dieLid[]};
Printf("die_flange query: %g surface(s), tag %g; die_lid query: %g surface(s), tag %g", #dieFlange[], dieFlange[0], #dieLid[], dieLid[0]) >> "q3-entities.txt";
Mesh.MeshSizeMax = 150;
Mesh.ElementOrder = 2;
