// brief-em3d-28 R-em3d28-1c — THE one shader source for every 3D-view backend. tools/ShaderGen
// cross-compiles it offline to scene.metal (Metal), scene.hlsl (D3D11) and scene.spv (Vulkan); each
// generated file records this file's SHA-256, and Viewer3DFrameGateTests.Gate1b fails when they disagree.
// Edit this file, then regenerate:  tools/ShaderGen  ->  shadergen src/Ui/Viewer3D/Shaders/scene.wgsl src/Ui/Viewer3D/Shaders
//
// The uniform block is Scene3DFramePlan's: vp, eye, clip, the hovered (object, face), flags, the selection
// mode, the selection (brief-em3d-43 R-em3d43-5: up to 64 (object, face) pairs), then brief 29's field blocks
// (FieldUniforms: phase, range, mode, dB, the colour map's stops — brief-em3d-96: four, one per drawn plot), then
// brief 45's drawing grid (PlaneGrid.Fill), then brief 106's look block (Scene3DFramePlan.FillLook, with brief 107's lighting
// after it: Scene3DFramePlan.PlanLighting) — 2,560 bytes.
// A vertex is Scene3DVertex: position, object id, RGBA8 colour, face (24 bytes); a FIELD vertex is
// FieldVertex: position, the value's real part, its imaginary part (36 bytes).

// brief-em3d-96 — one drawn plot's colour block (FieldUniforms, 288 bytes).
struct FB {
    // x cos φ, y sin φ, z range lo, w range hi
    fphase: vec4f,
    // x mode (0 |v| of a vector, 1 |Re{v e^jφ}|, 2 a real scalar, 3 Re{v e^jφ} of a scalar, 4 |v| of a
    // scalar), y dB, z the colour map's stop count, w 1 when the field is a ClipPlane plot's slice: it lies on the
    // plot's own plane, so the view's section plane does not cut it
    fmode: vec4f,
    // (t, r, g, b) per stop
    stops: array<vec4f, 16>,
};

struct U {
    vp: mat4x4f,
    eye: vec4f,
    clip: vec4f,
    hover: u32,
    hover_face: u32,
    flags: u32,
    // 0 Object, 1 Face, 2 Vertex (brief-em3d-43 R-em3d43-2)
    mode: u32,
    nsel: u32,
    // 3D editor bugs round 3 — clip units per pixel, x and y: a thickened edge's pixel offset (vs)
    ppx: f32,
    ppy: f32,
    pad2: u32,
    // (object, face) pairs, two per vec4u: entry k is sel[k / 2].xy or .zw
    sel: array<vec4u, 32>,
    // brief-em3d-96 — one block per drawn plot; a field draw's mx.id.y names its own
    f: array<FB, 4>,
    // brief-em3d-45 — the drawing grid (PlaneGrid.Fill): the plane's u and v axes with each one's fine phase, its
    // normal and offset, the ray's origin (w: 1 perspective, 0 orthographic), (minor, major every, -, on), the line
    // colour, where the world origin's lines are (local u, v) and the coarse phases, the colours of the lines along u
    // and v; then (3D editor bugs round 1) the ray per clip position: origin = gq + x gox + y goy, direction =
    // gd + x gdx + y gdy.
    gu: vec4f,
    gv: vec4f,
    gn: vec4f,
    gq: vec4f,
    gs: vec4f,
    gcol: vec4f,
    gax: vec4f,
    gcu: vec4f,
    gcv: vec4f,
    gox: vec4f,
    goy: vec4f,
    gd: vec4f,
    gdx: vec4f,
    gdy: vec4f,
    // brief-em3d-106 — the realistic view's look (zero while the view is not realistic): x 2^EV, y the environment's intensity,
    // z cos and w sin of its rotation about +z
    lk: vec4f,
    // x the backdrop (0 none: the clear colour, 1 a vertical gradient, 2 the environment), y the environment's last level
    lk1: vec4f,
    // the key light's world direction, and its radiance
    key: vec4f,
    keyc: vec4f,
    // the gradient's top and bottom colours (display values)
    bg0: vec4f,
    bg1: vec4f,
    // the backdrop's ray per clip position: direction = bd + x bdx + y bdy
    bd: vec4f,
    bdx: vec4f,
    bdy: vec4f,
    // the nine irradiance coefficients (Pbr.Irradiance), in the environment's frame
    sh: array<vec4f, 9>,
    // brief-em3d-107 — the key light's shadow map: world to its clip space (x, y across the map, z its depth in [0, 1])
    lvp: mat4x4f,
    // x 1 with shadows, y the filter's radius (world), z the map's uv per world unit, w its depth per world unit
    ls: vec4f,
    // the light's frame: right (w the receiver's normal offset, world), up (w the key light's share of a level floor's light),
    // forward — from the light into the scene
    lr: vec4f,
    lu: vec4f,
    lf: vec4f,
    // x 1 with occlusion, y its radius (world), z the blur's reach (world), w the picture's pixels per window pixel (1 live: a supersampled
    // export's cap on the radius in pixels grows with it, so the darkening has the same world reach as the view's)
    ao: vec4f,
    // the view's ray per clip position: origin = aro + x arox + y aroy, direction = ard + x ardx + y ardy (ard the view's forward, so the
    // parameter along a ray is its depth): what the occlusion pass rebuilds a pixel's point from, and the ground's ray
    aro: vec4f,
    arox: vec4f,
    aroy: vec4f,
    ard: vec4f,
    ardx: vec4f,
    ardy: vec4f,
    // the ground: its centre (x, y), its z, its radius
    gnd: vec4f,
};
@group(0) @binding(0) var<uniform> u: U;

// brief-em3d-46 R-em3d46-1a — the per-draw transform (Scene3DFramePlan.Transforms): the identity, or a drag's
// preview moving what is already drawn, so a drag uploads no geometry. A second uniform binding, set per draw:
// Metal setVertexBytes at buffer 2, a D3D11 cbuffer at b1, a Vulkan dynamic offset into binding 1. Not an
// immediate (push constant): naga writes those to HLSL as ConstantBuffer<T>, which vs_5_0 does not compile.
//
// brief-em3d-48 R-em3d48-3b — and an ID offset: an array element draws its prototype's triangles, whose vertices carry
// the prototype's object ids; the element's objects are numbered the same distance after them, so id + mx.id.x is the
// ELEMENT's object — which is what the ID pass must write for a pick to name the element. 0 for every other draw.
//
// 3D editor bugs round 3 — and id.y, a selected object's edge pass (Scene3DFramePlan.EdgePasses): 0 none, else the line
// drawn again (id.y & 1, id.y >> 1) pixels over, so the outline is two pixels wide where no backend draws a wider line.
// brief-em3d-96 — in a FIELD draw id.y is instead the drawn plot's colour block (vs_field reads it; vs never sees a field draw).
//
// brief-em3d-104 R-em3d104-3d — the SHADE stream (Scene3DShadeVertex: the shading normal vec3f, the appearance slot u32; 16
// bytes, parallel to the scene's vertices) is a SECOND vertex buffer that only the realistic pipelines (brief 106) declare, as
// @location(4) normal and @location(5) slot after the four Scene3DVertex attributes. WGSL names no vertex buffer, so the slot is
// each backend's pipeline layout: Metal vertex buffer index 3 (0 is the geometry, 1 the uniforms, 2 this transform — one
// argument table), D3D11 input slot 1 (semantics LOC4, LOC5), Vulkan vertex binding 1. No existing pipeline's layout changes.
//
// brief-em3d-106 R-em3d106-3b — the realistic view's APPEARANCE TABLE is a third uniform binding, @group(0) @binding(2) (array<AP, 256>,
// 12,288 bytes, below Vulkan's guaranteed 16 KB): Metal fragment buffer 4 (3 is the shade stream), D3D11 b2, Vulkan set 0 binding 2.
// Its ENVIRONMENT is @group(2): bindings 0 (the prefiltered map), 1 (its sampler), 2 (the split-sum table) — Metal textures 1, 2 and
// sampler 1, D3D11 t1, t2 and s1, Vulkan set 2. Declared with the realistic entry points at the end of this file.
//
// brief-em3d-107 — shadows, occlusion and the ground are @group(3): the shadow map (0), its comparison sampler (1), the blurred occlusion
// (2), the prepass's depths (3) and the raw occlusion (4) — Metal textures 3, 4, 5, 6 and sampler 2; D3D11 t3, t4, t5, t6 and s2;
// Vulkan set 3. Declared at the end of this file.
struct MX {
    m: mat4x4f,
    id: vec4u,
};
@group(0) @binding(1) var<uniform> mx: MX;

struct VI {
    @location(0) p: vec3f,
    @location(1) id: u32,
    @location(2) col: vec4f,
    @location(3) face: u32,
};

struct VO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
    @location(1) @interpolate(flat) id: u32,
    @location(2) col: vec4f,
    @location(3) @interpolate(flat) face: u32,
};

@vertex fn vs(v: VI) -> VO {
    var o: VO;
    let p = (mx.m * vec4f(v.p, 1.0)).xyz;
    o.pos = u.vp * vec4f(p, 1.0);
    if (mx.id.y != 0u) {
        let px = vec2f(f32(mx.id.y & 1u) * u.ppx, f32((mx.id.y >> 1u) & 1u) * u.ppy);
        o.pos = vec4f(o.pos.xy + px * o.pos.w, o.pos.zw);
    }
    o.world = p;
    o.id = v.id + mx.id.x;
    o.col = v.col;
    o.face = v.face;
    return o;
}

// Flag bits: 1 = clip on, 2 = draw back faces flat (the cut solid's cap).
fn clipped(w: vec3f) -> bool {
    return (u.flags & 1u) != 0u && dot(u.clip.xyz, w) + u.clip.w > 0.0;
}

// brief-em3d-43 R-em3d43-5 — the selection, tested per fragment: a shader state change, never a
// retessellation. In Face mode an entry matches one face; in Object mode the face is not compared.
fn sel_entry(k: u32) -> vec2u {
    let q = u.sel[k / 2u];
    if ((k & 1u) == 0u) { return q.xy; }
    return q.zw;
}

fn is_selected(id: u32, face: u32) -> bool {
    for (var k = 0u; k < u.nsel; k = k + 1u) {
        let e = sel_entry(k);
        if (e.x == id && (u.mode != 1u || e.y == face)) { return true; }
    }
    return false;
}

// Object mode: hover tints the object (its selection is the edge pass's outline). Face mode: hover is a
// lighter fill on the one face, selection a stronger fill (plus its edges). Vertex mode: no fill — the
// dots are the 2D overlay's.
fn highlight(rgb: vec3f, id: u32, face: u32) -> vec3f {
    var c = rgb;
    if (id == 0u) { return c; }
    if (u.mode == 0u) {
        if (id == u.hover) { c = mix(c, vec3f(0.2, 0.9, 1.0), 0.45); }
    } else if (u.mode == 1u) {
        if (id == u.hover && face == u.hover_face) { c = mix(c, vec3f(1.0, 1.0, 1.0), 0.35); }
        if (is_selected(id, face)) { c = mix(c, vec3f(1.0, 0.35, 1.0), 0.6); }
    }
    return c;
}

@fragment fn fs_color(i: VO, @builtin(front_facing) front: bool) -> @location(0) vec4f {
    let n = normalize(cross(dpdx(i.world), dpdy(i.world)));
    if (clipped(i.world)) { discard; }
    // 3D editor bugs round 1 — alpha 0 is a WIREFRAME object's face (no material): drawn only while hovered (Object
    // mode: the whole object; Face mode: the face) or selected (Face mode); its edges are the line pass's.
    if (i.col.a == 0.0) {
        if (i.id != 0u && u.mode == 0u && i.id == u.hover) { return vec4f(0.2, 0.9, 1.0, 0.18); }
        if (i.id != 0u && u.mode == 1u && is_selected(i.id, i.face)) { return vec4f(1.0, 0.35, 1.0, 0.4); }
        if (i.id != 0u && u.mode == 1u && i.id == u.hover && i.face == u.hover_face) { return vec4f(1.0, 1.0, 1.0, 0.25); }
        discard;
    }
    let d = abs(dot(n, normalize(u.eye.xyz - i.world)));
    var rgb = i.col.rgb * (0.3 + 0.7 * d);
    if (!front && (u.flags & 2u) != 0u) { rgb = i.col.rgb * 0.8; }
    // 3D editor bugs round 3 — an object selected in Object mode is drawn faded (Scene3DFramePlan.SelectedAlpha; the plan
    // draws it with the translucent objects), so what it hides shows through.
    var a = i.col.a;
    if (u.mode == 0u && is_selected(i.id, 0u)) { a = min(a, 0.5); }
    return vec4f(highlight(rgb, i.id, i.face), a);
}

@fragment fn fs_line(i: VO) -> @location(0) vec4f {
    return vec4f(i.col.rgb, 1.0);
}

// brief-em3d-43 R-em3d43-4d — the selected face drawn again with the depth test off, at reduced opacity,
// so a face B-cycled behind others is visible through what is in front of it.
@fragment fn fs_top(i: VO) -> @location(0) vec4f {
    if (clipped(i.world) || u.mode != 1u || !is_selected(i.id, i.face)) { discard; }
    return vec4f(1.0, 0.35, 1.0, 0.45);
}

// brief-em3d-43 R-em3d43-5 — a feature edge, drawn only when its object (Object mode) or one of its two
// faces (Face mode) is selected. The edge carries both faces, packed: low 16 bits one, high 16 the other.
@fragment fn fs_edge(i: VO) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    var show = false;
    if (u.mode == 0u) { show = is_selected(i.id, 0u); }
    if (u.mode == 1u) { show = is_selected(i.id, i.face & 0xFFFFu) || is_selected(i.id, i.face >> 16u); }
    if (!show) { discard; }
    return vec4f(1.0, 0.35, 1.0, 1.0);
}

// brief-em3d-43 R-em3d43-3a — the ID pass writes the (object, face) pair.
struct PickOut {
    @location(0) id: vec2u,
    @location(1) world: vec4f,
};

@fragment fn fs_pick(i: VO) -> PickOut {
    if (clipped(i.world)) { discard; }
    var o: PickOut;
    o.id = vec2u(i.id, i.face);
    o.world = vec4f(i.world, 1.0);
    return o;
}

// ── brief-em3d-101: an image ───────────────────────────────────────────────────────────────────────
// An image sheet's surface (and, Phase B, an image mapped onto a face): its own triangles, a texture coordinate per vertex, and the
// texture and sampler of group 1 — MSL [[texture(0)]] / [[sampler(0)]], HLSL t0 / s0, SPIR-V descriptor set 1 bindings 0 and 1
// (tools/ShaderGen's README). Unshaded: the picture is the datum, as a field's colour is. A texel's alpha is multiplied by the
// vertex colour's (the object's transparency), and outside [0, 1] there is no image at all — a face image larger than its face is
// cropped by the face that way, with no texture memory spent on the clip. Picking draws the object's ordinary triangles (fs_pick),
// so a fully transparent texel still picks.

@group(1) @binding(0) var img: texture_2d<f32>;
@group(1) @binding(1) var img_s: sampler;

struct IVI {
    @location(0) p: vec3f,
    @location(1) uv: vec2f,
    @location(2) id: u32,
    @location(3) face: u32,
    @location(4) col: vec4f,
};

struct IVO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
    @location(1) uv: vec2f,
    @location(2) @interpolate(flat) id: u32,
    @location(3) @interpolate(flat) face: u32,
    @location(4) col: vec4f,
};

@vertex fn vs_image(v: IVI) -> IVO {
    var o: IVO;
    let p = (mx.m * vec4f(v.p, 1.0)).xyz;
    o.pos = u.vp * vec4f(p, 1.0);
    o.world = p;
    o.uv = v.uv;
    o.id = v.id + mx.id.x;
    o.face = v.face;
    o.col = v.col;
    return o;
}

@fragment fn fs_image(i: IVO) -> @location(0) vec4f {
    // Sampled first: a sample's derivatives need uniform control flow, so nothing is discarded before it.
    let t = textureSample(img, img_s, i.uv);
    if (clipped(i.world)) { discard; }
    let e = 1e-4;
    if (i.uv.x < -e || i.uv.x > 1.0 + e || i.uv.y < -e || i.uv.y > 1.0 + e) { discard; }
    var a = t.a * i.col.a;
    // An object selected in Object mode is drawn faded, as fs_color draws it.
    if (u.mode == 0u && is_selected(i.id, 0u)) { a = min(a, 0.5); }
    if (a <= 0.0) { discard; }
    return vec4f(highlight(t.rgb, i.id, i.face), a);
}

// ── brief-em3d-29: the field pass ──────────────────────────────────────────────────────────────────
// The value is computed HERE from the vertex's real and imaginary parts and the phase uniform, so an
// animated frame changes one uniform and uploads nothing. Unshaded: the colour is the datum. A value
// outside the range is drawn in the end colour (the clamp), never transparent.

struct FVI {
    @location(0) p: vec3f,
    @location(1) re: vec3f,
    @location(2) im: vec3f,
};

struct FVO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
    @location(1) re: vec3f,
    @location(2) im: vec3f,
    // brief-em3d-96 — the drawn plot's colour block, from the draw's transform slot
    @location(3) @interpolate(flat) layer: u32,
};

@vertex fn vs_field(v: FVI) -> FVO {
    var o: FVO;
    o.pos = u.vp * vec4f(v.p, 1.0);
    o.world = v.p;
    o.re = v.re;
    o.im = v.im;
    o.layer = min(mx.id.y, 3u);
    return o;
}

fn field_value(re: vec3f, im: vec3f, l: u32) -> f32 {
    let c = u.f[l].fphase.x;
    let s = u.f[l].fphase.y;
    let mode = u32(u.f[l].fmode.x + 0.5);
    if (mode == 0u) { return sqrt(dot(re, re) + dot(im, im)); }
    if (mode == 1u) { return length(re * c - im * s); }
    if (mode == 2u) { return re.x; }
    if (mode == 3u) { return re.x * c - im.x * s; }
    return sqrt(re.x * re.x + im.x * im.x);
}

fn colour_map(t: f32, l: u32) -> vec3f {
    let n = u32(u.f[l].fmode.z + 0.5);
    var rgb = u.f[l].stops[0].yzw;
    for (var k = 1u; k < n; k = k + 1u) {
        let a = u.f[l].stops[k - 1u];
        let b = u.f[l].stops[k];
        if (t <= b.x || k == n - 1u) {
            let w = clamp((t - a.x) / max(b.x - a.x, 1e-6), 0.0, 1.0);
            rgb = mix(a.yzw, b.yzw, w);
            break;
        }
    }
    return rgb;
}

@fragment fn fs_field(i: FVO) -> @location(0) vec4f {
    let l = i.layer;
    if (u.f[l].fmode.w < 0.5 && clipped(i.world)) { discard; }
    var v = field_value(i.re, i.im, l);
    if (u.f[l].fmode.y > 0.5) { v = 20.0 * 0.30102999566 * log2(max(abs(v), 1e-30)); }
    let lo = u.f[l].fphase.z;
    let hi = u.f[l].fphase.w;
    let t = clamp((v - lo) / max(hi - lo, 1e-30), 0.0, 1.0);
    return vec4f(colour_map(t, l), 1.0);
}

// ── brief-em3d-45: the drawing grid ────────────────────────────────────────────────────────────────
// An INFINITE plane (3D editor bugs round 1). The vertex shader covers the whole viewport with one quad made from
// six vertex indices — no vertex buffer, so an orbit uploads nothing — and the fragment shader casts the fragment's
// own ray at the plane: the plane is found wherever it is, not only inside a quad around the focus, and never cut
// by the scene's near and far planes (the fragment writes its true depth, clamped into [0, 1]).
//
// The lines are found per fragment at the spacing the FRAGMENT needs: level k is every gs.y^k minor lines, and a
// level's weight grows with how many pixels its cell spans there — nothing under 6 px, the minor lines' faint
// alpha by 14 px (PlaneGrid.MinPixels), full alpha by gs.y times that (3D editor bugs round 3: 4 and 10 px, which
// read as too dense zoomed out). So near the focus the minor lines show with every gs.y-th one
// heavier, and toward the horizon the coarser levels take over one after another until even they would crowd,
// where the grid fades out. A weight depends only on the cell's size on screen, so the change of level is seamless.
// The world origin's lines are drawn in the axis colours. A plane seen nearly edge-on fades.

struct GVO {
    @builtin(position) pos: vec4f,
    @location(0) ndc: vec2f,
};

@vertex fn vs_grid(@builtin(vertex_index) k: u32) -> GVO {
    // Two triangles: (-1,-1) (1,-1) (1,1) and (-1,-1) (1,1) (-1,1). Bit k of 0x16 is x > 0; of 0x34, y > 0.
    let cx = f32((0x16u >> k) & 1u) * 2.0 - 1.0;
    let cy = f32((0x34u >> k) & 1u) * 2.0 - 1.0;
    var o: GVO;
    o.pos = vec4f(cx, cy, 0.5, 1.0);
    o.ndc = vec2f(cx, cy);
    return o;
}

struct GOut {
    @location(0) col: vec4f,
    @builtin(frag_depth) depth: f32,
};

// The alpha of a line of cell size c pixels: 0 under 6 px, the faint minor alpha (0.45) by 14 px, 1 by m times that.
fn grid_weight(c: f32, m: f32) -> f32 {
    return 0.45 * clamp((c - 6.0) / 8.0, 0.0, 1.0) + 0.55 * clamp((c - 6.0 * m) / (8.0 * m), 0.0, 1.0);
}

// How much the fragment at p (metres per pixel pf) lies on a line every s metres, phase ph.
fn grid_on(p: vec2f, pf: vec2f, s: f32, ph: vec2f) -> f32 {
    let a = (p + ph) / s;
    let d = abs(fract(a - 0.5) - 0.5) / max(pf / s, vec2f(1e-12, 1e-12));
    return 1.0 - min(min(d.x, d.y), 1.0);
}

@fragment fn fs_grid(i: GVO) -> GOut {
    let ro = u.gq.xyz + u.gox.xyz * i.ndc.x + u.goy.xyz * i.ndc.y;
    let rd = u.gd.xyz + u.gdx.xyz * i.ndc.x + u.gdy.xyz * i.ndc.y;
    let dn = dot(rd, u.gn.xyz);
    let t = (u.gn.w - dot(ro, u.gn.xyz)) / select(dn, 1e-30, dn == 0.0);
    let w = ro + rd * t;
    // Derivatives before any discard: uniform control flow.
    let pu = dot(w, u.gu.xyz);
    let pv = dot(w, u.gv.xyz);
    let p = vec2f(pu, pv);
    let pf = max(fwidth(p), vec2f(1e-30, 1e-30));
    let cosine = abs(dn) / max(length(rd), 1e-30);
    // 3D editor bugs round 2 — the clip plane cuts the MODEL, not the drawing grid: the grid is a drawing aid, and with
    // the plane at an extreme it vanished across half the view with nothing in it to cut.
    if (cosine < 1e-4 || (u.gq.w > 0.5 && t <= 0.0)) { discard; }

    let mpp = max(pf.x, pf.y);
    let m = max(u.gs.y, 2.0);
    let c0 = u.gs.x / mpp;
    // The finest level whose cell spans 6 px; finer ones weigh nothing. Three levels from it: any coarser one's
    // lines are among the third's, which is already at full weight.
    let k0 = clamp(ceil(log(6.0 / c0) / log(m)), 0.0, 24.0);
    var alpha = 0.0;
    for (var j = 0; j < 3; j = j + 1) {
        let k = k0 + f32(j);
        let s = u.gs.x * pow(m, k);
        // Levels 0 and 1 use the fine phase (world less whole major cells); coarser ones the coarse phase.
        let ph = select(u.gax.zw, vec2f(u.gu.w, u.gv.w), k < 1.5);
        alpha = max(alpha, grid_on(p, pf, s, ph) * grid_weight(s / mpp, m));
    }
    alpha = alpha * u.gcol.w;
    var rgb = u.gcol.rgb;
    // The line u = 0 runs along v, so it is v's colour; v = 0 runs along u.
    let on_v = 1.0 - min(abs(pu - u.gax.x) / pf.x / 1.2, 1.0);
    let on_u = 1.0 - min(abs(pv - u.gax.y) / pf.y / 1.2, 1.0);
    if (on_v * u.gcv.w > alpha) { alpha = on_v * u.gcv.w; rgb = u.gcv.rgb; }
    if (on_u * u.gcu.w > alpha) { alpha = on_u * u.gcu.w; rgb = u.gcu.rgb; }
    // Only the last two degrees or so before the horizon: the levels already thin the lines out where they crowd.
    alpha = alpha * smoothstep(0.0, 0.035, cosine);
    if (alpha <= 0.002) { discard; }
    var o: GOut;
    o.col = vec4f(rgb, alpha);
    let c = u.vp * vec4f(w, 1.0);
    o.depth = clamp(c.z / c.w, 0.0, 1.0);
    return o;
}

// ── brief-em3d-106: the realistic view ─────────────────────────────────────────────────────────────
// Physically based shading, lit by an environment, for pictures (src/Render/Scene3D/Look/Pbr.cs is THE REFERENCE: every function and
// constant below is written once there and once here, and RealisticViewTests' scan holds the constants equal). In LINEAR light: the
// appearance table holds linear base colours (the resolver decodes sRGB once); exposure, the PBR Neutral tone curve and the sRGB encode
// are the last steps of fs_pbr — forward, because the target is UNORM, not sRGB, and so a field fragment never meets the curve
// (overview rule 2). Hover and selection are applied after the encode, so their colours are today's.
//
// Bindings (tools/ShaderGen states them): the appearance table @group(0) @binding(2) — MSL [[buffer(4)]] (index 3 is the shade
// stream's vertex buffer), HLSL register(b2), SPIR-V set 0 binding 2; the environment's prefiltered octahedral map (five roughness
// levels as its five mips) @group(2) @binding(0), its sampler @binding(1), the split-sum table @binding(2) — MSL [[texture(1)]],
// [[sampler(1)]], [[texture(2)]]; HLSL t1, s1, t2; SPIR-V set 2, bindings 0, 1, 2.

// One appearance (overview D16: 256 a scene, three vec4f each, 12,288 bytes — Vulkan guarantees 16 KB of uniform range): base colour
// + metallic; roughness, transmission, IOR, clear coat; clear-coat roughness + attenuation colour.
struct AP {
    a: vec4f,
    b: vec4f,
    c: vec4f,
};
struct APT {
    e: array<AP, 256>,
};
@group(0) @binding(2) var<uniform> ap: APT;

@group(2) @binding(0) var env: texture_2d<f32>;
@group(2) @binding(1) var env_s: sampler;
@group(2) @binding(2) var brdf: texture_2d<f32>;

const PI: f32 = 3.14159265;
const DIELECTRIC_F0: f32 = 0.04;
const MIN_ROUGHNESS: f32 = 0.045;
const MIN_NDOTV: f32 = 1e-4;
// the shade vertex's slot: the appearance row in the low 8 bits, and a bit saying the colour's alpha is a STATED coverage (D12)
const SLOT_MASK: u32 = 0xFFu;
const STATED_ALPHA: u32 = 0x100u;
// the PBR Neutral tone curve (ToneCurve.cs)
const TONE_START: f32 = 0.76;
const TONE_DESAT: f32 = 0.15;
const TONE_TOE_BREAK: f32 = 0.08;
const TONE_TOE_SLOPE: f32 = 6.25;
const TONE_TOE_OFFSET: f32 = 0.04;
// sRGB (IEC 61966-2-1)
const SRGB_BREAK: f32 = 0.0031308;
const SRGB_SLOPE: f32 = 12.92;
const SRGB_SCALE: f32 = 1.055;
const SRGB_OFFSET: f32 = 0.055;
const SRGB_GAMMA: f32 = 2.4;

struct PVI {
    @location(0) p: vec3f,
    @location(1) id: u32,
    @location(2) col: vec4f,
    @location(3) face: u32,
    @location(4) n: vec3f,
    @location(5) slot: u32,
};

struct PVO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
    @location(1) @interpolate(flat) id: u32,
    @location(2) col: vec4f,
    @location(3) @interpolate(flat) face: u32,
    @location(4) n: vec3f,
    @location(5) @interpolate(flat) slot: u32,
};

// vs's work, plus the shading normal rotated by the per-draw transform (rigid: brief 104 §1f) and the slot passed flat.
@vertex fn vs_pbr(v: PVI) -> PVO {
    var o: PVO;
    let p = (mx.m * vec4f(v.p, 1.0)).xyz;
    o.pos = u.vp * vec4f(p, 1.0);
    o.world = p;
    o.id = v.id + mx.id.x;
    o.col = v.col;
    o.face = v.face;
    o.n = (mx.m * vec4f(v.n, 0.0)).xyz;
    o.slot = v.slot;
    return o;
}

// A world direction in the environment's frame: the environment is turned by +rotation about +z, so a lookup turns by -rotation.
fn env_dir(w: vec3f) -> vec3f {
    return vec3f(u.lk.z * w.x + u.lk.w * w.y, -u.lk.w * w.x + u.lk.z * w.y, w.z);
}

// The octahedral map, +z at the centre (Pbr.OctEncode).
fn oct_uv(d: vec3f) -> vec2f {
    let s = abs(d.x) + abs(d.y) + abs(d.z);
    var p = d.xy / s;
    if (d.z < 0.0) {
        p = (vec2f(1.0, 1.0) - abs(p.yx)) * select(vec2f(-1.0, -1.0), vec2f(1.0, 1.0), p >= vec2f(0.0, 0.0));
    }
    return p * 0.5 + vec2f(0.5, 0.5);
}

fn env_radiance(w: vec3f, rough: f32) -> vec3f {
    return textureSampleLevel(env, env_s, oct_uv(env_dir(w)), rough * u.lk1.y).rgb;
}

fn irradiance(n: vec3f) -> vec3f {
    let e = env_dir(n);
    return u.sh[0].rgb + u.sh[1].rgb * e.y + u.sh[2].rgb * e.z + u.sh[3].rgb * e.x + u.sh[4].rgb * (e.x * e.y)
         + u.sh[5].rgb * (e.y * e.z) + u.sh[6].rgb * (3.0 * e.z * e.z - 1.0) + u.sh[7].rgb * (e.x * e.z)
         + u.sh[8].rgb * (e.x * e.x - e.y * e.y);
}

fn brdf_lut(nv: f32, rough: f32) -> vec2f {
    return textureSampleLevel(brdf, env_s, vec2f(clamp(nv, 0.0, 1.0), clamp(rough, 0.0, 1.0)), 0.0).xy;
}

// Trowbridge-Reitz (GGX), alpha = roughness squared.
fn ggx_d(nh: f32, a: f32) -> f32 {
    let a2 = a * a;
    let f = nh * nh * (a2 - 1.0) + 1.0;
    return a2 / (PI * f * f);
}

// Height-correlated Smith visibility, G / (4 N.L N.V).
fn smith_v(nv: f32, nl: f32, a: f32) -> f32 {
    let a2 = a * a;
    let gv = nl * sqrt(nv * nv * (1.0 - a2) + a2);
    let gl = nv * sqrt(nl * nl * (1.0 - a2) + a2);
    return 0.5 / max(gv + gl, 1e-7);
}

fn schlick3(f0: vec3f, vh: f32) -> vec3f {
    let k = pow(1.0 - clamp(vh, 0.0, 1.0), 5.0);
    return f0 + (vec3f(1.0, 1.0, 1.0) - f0) * k;
}

fn schlick1(f0: f32, vh: f32) -> f32 {
    return f0 + (1.0 - f0) * pow(1.0 - clamp(vh, 0.0, 1.0), 5.0);
}

fn pbr_neutral(c0: vec3f) -> vec3f {
    var c = c0;
    let x = min(c.r, min(c.g, c.b));
    let offset = select(TONE_TOE_OFFSET, x - TONE_TOE_SLOPE * x * x, x < TONE_TOE_BREAK);
    c = c - vec3f(offset, offset, offset);
    let peak = max(c.r, max(c.g, c.b));
    if (peak < TONE_START) { return c; }
    let d = 1.0 - TONE_START;
    let np = 1.0 - d * d / (peak + d - TONE_START);
    c = c * (np / peak);
    let g = 1.0 - 1.0 / (TONE_DESAT * (peak - np) + 1.0);
    return mix(c, vec3f(np, np, np), g);
}

fn srgb1(x: f32) -> f32 {
    let c = clamp(x, 0.0, 1.0);
    return select(SRGB_SCALE * pow(c, 1.0 / SRGB_GAMMA) - SRGB_OFFSET, c * SRGB_SLOPE, c <= SRGB_BREAK);
}

// Exposure, the curve, the encode (ToneCurve.Display).
fn display(lin: vec3f) -> vec3f {
    let t = pbr_neutral(max(lin * u.lk.x, vec3f(0.0, 0.0, 0.0)));
    return vec3f(srgb1(t.r), srgb1(t.g), srgb1(t.b));
}

// highlight() on a PREMULTIPLIED colour of coverage a: the tints scaled by a, so an opaque fragment's is exactly highlight()'s.
fn highlight_pm(rgb: vec3f, a: f32, id: u32, face: u32) -> vec3f {
    var c = rgb;
    if (id == 0u) { return c; }
    if (u.mode == 0u) {
        if (id == u.hover) { c = mix(c, vec3f(0.2, 0.9, 1.0) * a, 0.45); }
    } else if (u.mode == 1u) {
        if (id == u.hover && face == u.hover_face) { c = mix(c, vec3f(1.0, 1.0, 1.0) * a, 0.35); }
        if (is_selected(id, face)) { c = mix(c, vec3f(1.0, 0.35, 1.0) * a, 0.6); }
    }
    return c;
}

// One fragment (Pbr.Radiance, then Pbr.Shade): PREMULTIPLIED display colour and coverage. The opaque pipeline's coverage is 1.
// brief-em3d-107 — the opaque pipeline's (fs_pbr) environment light is darkened by the occlusion at its pixel; a translucent one
// (fs_pbr_glass) neither writes the prepass nor receives occlusion (R-em3d107-2c). Both take the key light's shadow on its direct term.
@fragment fn fs_pbr(i: PVO, @builtin(front_facing) front: bool) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    return pbr(i, front, occlusion_at(i.pos.xy));
}

@fragment fn fs_pbr_glass(i: PVO, @builtin(front_facing) front: bool) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    return pbr(i, front, 1.0);
}

fn pbr(i: PVO, front: bool, occ: f32) -> vec4f {
    let m = ap.e[i.slot & SLOT_MASK];
    let v = normalize(u.eye.xyz - i.world);
    var n = normalize(i.n);
    if (!front) {
        // A cut solid's cap (flag 2: the clip is on) is shaded facing the viewer; a sheet's back face (two-sided) flips its normal.
        if ((u.flags & 2u) != 0u) { n = v; } else { n = -n; }
    }
    let nv = max(dot(n, v), MIN_NDOTV);
    let base = m.a.rgb;
    let metal = clamp(m.a.w, 0.0, 1.0);
    let rough = clamp(m.b.x, 0.0, 1.0);
    let t = clamp(m.b.y, 0.0, 1.0);
    let fr0 = (m.b.z - 1.0) / (m.b.z + 1.0);
    let f0 = mix(vec3f(fr0 * fr0, fr0 * fr0, fr0 * fr0), base, metal);
    let lut = brdf_lut(nv, rough);
    let spec_albedo = f0 * lut.x + vec3f(lut.y, lut.y, lut.y);
    let r = 2.0 * dot(n, v) * n - v;
    let reflected = env_radiance(r, rough) * u.lk.y;
    let irr = irradiance(n) * u.lk.y;
    var surface = (reflected * spec_albedo
                + (vec3f(1.0, 1.0, 1.0) - spec_albedo) * (1.0 - metal) * (1.0 - t) * base * irr / PI) * occ;

    // the key light, direct, and its shadow (brief 107)
    let l = u.key.xyz;
    let nl = dot(n, l);
    let keyon = nl > 0.0 && dot(u.keyc.xyz, u.keyc.xyz) > 0.0;
    var vis = 1.0;
    if (keyon) { vis = shadow_vis(i.world, n); }
    var nh = 0.0;
    var vh = 0.0;
    if (keyon) {
        let h = normalize(l + v);
        nh = max(dot(n, h), 0.0);
        vh = max(dot(v, h), 0.0);
        var ad = max(rough, MIN_ROUGHNESS);
        ad = ad * ad;
        let fr = schlick3(f0, vh);
        let lobe = fr * (ggx_d(nh, ad) * smith_v(nv, nl, ad));
        let body = (vec3f(1.0, 1.0, 1.0) - fr) * (1.0 - metal) * (1.0 - t) * base / PI;
        surface = surface + (lobe + body) * u.keyc.xyz * u.lk.y * nl * vis;
    }

    // the clear coat: a dielectric lobe over the body, its energy taken from it
    let cc = clamp(m.b.w, 0.0, 1.0);
    if (cc > 0.0) {
        let ccr = clamp(m.c.x, 0.0, 1.0);
        let lc = brdf_lut(nv, ccr);
        let coat_albedo = DIELECTRIC_F0 * lc.x + lc.y;
        var coat = env_radiance(r, ccr) * u.lk.y * coat_albedo * occ;
        if (keyon) {
            var ac = max(ccr, MIN_ROUGHNESS);
            ac = ac * ac;
            coat = coat + u.keyc.xyz * u.lk.y * (schlick1(DIELECTRIC_F0, vh) * ggx_d(nh, ac) * smith_v(nv, nl, ac) * nl * vis);
        }
        surface = surface * (1.0 - cc * coat_albedo) + coat * cc;
    }

    // transmission, a raster approximation: coverage 1 - T (1 - F) mean(attenuation); the absorbed share glows in the attenuation
    // colour; the reflection is on top. Real refraction and the distance term are glTF export's.
    var alpha = 1.0;
    if (t > 0.0) {
        let att = m.c.yzw;
        let fs = (spec_albedo.r + spec_albedo.g + spec_albedo.b) / 3.0;
        let tau = (att.r + att.g + att.b) / 3.0;
        let through = t * (1.0 - fs);
        alpha = 1.0 - through * tau;
        surface = surface + through * (1.0 - tau) * att * irr / PI * occ;
    }

    // a STATED transparency multiplies the coverage (D12); an object selected in Object mode is drawn faded, as fs_color draws it
    var cov = select(1.0, i.col.a, (i.slot & STATED_ALPHA) != 0u);
    if (u.mode == 0u && is_selected(i.id, 0u)) { cov = min(cov, 0.5); }
    let a = alpha * cov;
    return vec4f(highlight_pm(display(surface) * cov, a, i.id, i.face), a);
}

// The backdrop, drawn first with vs_grid's full-screen triangles: a vertical gradient (top of the view to bottom), or the
// environment seen along each pixel's ray, exposed and tone-mapped as a surface would be.
@fragment fn fs_backdrop(i: GVO) -> @location(0) vec4f {
    if (u.lk1.x < 1.5) {
        let t = clamp(i.ndc.y * 0.5 + 0.5, 0.0, 1.0);
        return vec4f(mix(u.bg1.rgb, u.bg0.rgb, t), 1.0);
    }
    let d = u.bd.xyz + u.bdx.xyz * i.ndc.x + u.bdy.xyz * i.ndc.y;
    let c = textureSampleLevel(env, env_s, oct_uv(env_dir(d)), 0.0).rgb * u.lk.y;
    return vec4f(display(c), 1.0);
}

// ── brief-em3d-107: shadows, contact shading, the ground ──────────────────────────────────────────────────────────────────────
// src/Render/Scene3D/Look/Shadows.cs is THE REFERENCE for every constant below (RealisticViewTests' scan, extended by
// ShadowsOcclusionExportTests, holds them equal). Nothing here is drawn in the default view, and fs_field never reads any of it
// (overview rule 2): a field's colour stays its colour map's.
//
// Bindings (tools/ShaderGen states them): @group(3) — the shadow map (texture_depth_2d) @binding(0), its comparison sampler @binding(1),
// the blurred occlusion @binding(2), the prepass's depths @binding(3), the raw occlusion @binding(4): MSL [[texture(3)]], [[sampler(2)]],
// [[texture(4)]], [[texture(5)]], [[texture(6)]]; HLSL t3, s2, t4, t5, t6; SPIR-V set 3, bindings 0 to 4.

@group(3) @binding(0) var smap: texture_depth_2d;
@group(3) @binding(1) var smap_s: sampler_comparison;
@group(3) @binding(2) var aot: texture_2d<f32>;
@group(3) @binding(3) var aodepth: texture_2d<f32>;
@group(3) @binding(4) var aoraw: texture_2d<f32>;

// Shadows.MinSlopeCos, Shadows.Poisson.Length
const SHADOW_MIN_COS: f32 = 0.2;
const SHADOW_TAPS: u32 = 16u;
// Occlusion.Directions, .Steps, .Bias, .MaxPixels, .Empty
const AO_DIRECTIONS: u32 = 8u;
const AO_STEPS: u32 = 4u;
const AO_BIAS: f32 = 0.1;
const AO_MAX_PIXELS: f32 = 64.0;
const AO_EMPTY: f32 = 3.0e38;
// Ground.FadeStart
const GROUND_FADE: f32 = 0.35;

// Shadows.Poisson: the filter's FIXED taps (no per-frame noise, overview §1e)
var<private> POISSON: array<vec2f, 16> = array<vec2f, 16>(
    vec2f(-0.94201624, -0.39906216), vec2f(0.94558609, -0.76890725), vec2f(-0.094184101, -0.92938870), vec2f(0.34495938, 0.29387760),
    vec2f(-0.91588581, 0.45771432), vec2f(-0.81544232, -0.87912464), vec2f(-0.38277543, 0.27676845), vec2f(0.97484398, 0.75648379),
    vec2f(0.44323325, -0.97511554), vec2f(0.53742981, -0.47373420), vec2f(-0.26496911, -0.41893023), vec2f(0.79197514, 0.19090188),
    vec2f(-0.24188840, 0.99706507), vec2f(-0.81409955, 0.91437590), vec2f(0.19984126, 0.78641367), vec2f(0.14383161, -0.14100790)
);

// How much of the key light reaches world point w on a surface of normal n: percentage-closer filtering of the map, each tap a
// bilinear comparison. A receiver-plane slope from n carries the receiver's own depth to each tap (so a tilted surface does not shadow
// itself across the filter's width), and the point is moved a texel and a half along n. Outside the casters' window nothing shadows.
fn shadow_vis(w: vec3f, n: vec3f) -> f32 {
    if (u.ls.x < 0.5) { return 1.0; }
    let c = u.lvp * vec4f(w + n * u.lr.w, 1.0);
    let uv = vec2f(c.x * 0.5 + 0.5, 0.5 - c.y * 0.5);
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || c.z <= 0.0) { return 1.0; }
    let nf = min(dot(n, u.lf.xyz), -SHADOW_MIN_COS);
    let g = -vec2f(dot(n, u.lr.xyz), dot(n, u.lu.xyz)) / nf;
    let z = min(c.z, 1.0);
    var lit = 0.0;
    for (var k = 0u; k < SHADOW_TAPS; k = k + 1u) {
        let o = POISSON[k] * u.ls.y;
        let t = uv + vec2f(o.x, -o.y) * u.ls.z;
        // a receiver beyond the map's far plane (the ground under it all) compares at 1: shadowed under a caster, lit where none is
        lit = lit + textureSampleCompareLevel(smap, smap_s, t, min(z + dot(g, o) * u.ls.w, 1.0));
    }
    return lit / f32(SHADOW_TAPS);
}

// The occlusion at a fragment's pixel (1 with none).
fn occlusion_at(pos: vec2f) -> f32 {
    if (u.ao.x < 0.5) { return 1.0; }
    return textureLoad(aot, vec2i(pos), 0).r;
}

// ── the depth passes: the shadow map's casters, and the occlusion's prepass ──

struct DVO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
};

// A caster's triangle into the key light's map (the per-draw transform as vs takes it).
@vertex fn vs_shadow(v: VI) -> DVO {
    var o: DVO;
    let p = (mx.m * vec4f(v.p, 1.0)).xyz;
    o.pos = u.lvp * vec4f(p, 1.0);
    o.world = p;
    return o;
}

// Depth only; what the section plane cuts away casts nothing.
@fragment fn fs_depth(i: DVO) {
    if (clipped(i.world)) { discard; }
}

// The prepass (with vs): each opaque material's depth along the view's ray.
@fragment fn fs_prepass(i: VO) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    return vec4f(dot(i.world - u.aro.xyz, u.ard.xyz), 0.0, 0.0, 1.0);
}

// ── the occlusion: horizon-based, from the prepass's depths ──

fn ao_clamp(xy: vec2i, size: vec2i) -> vec2i {
    return clamp(xy, vec2i(0, 0), size - vec2i(1, 1));
}

// Pixel xy's point (w 1), or w 0 where the prepass drew nothing.
fn ao_point(xy: vec2i, size: vec2i) -> vec4f {
    let t = textureLoad(aodepth, xy, 0).r;
    if (t > AO_EMPTY * 0.5) { return vec4f(0.0, 0.0, 0.0, 0.0); }
    let s = vec2f(size);
    let x = (f32(xy.x) + 0.5) / s.x * 2.0 - 1.0;
    let y = 1.0 - (f32(xy.y) + 0.5) / s.y * 2.0;
    let o = u.aro.xyz + u.arox.xyz * x + u.aroy.xyz * y;
    let d = u.ard.xyz + u.ardx.xyz * x + u.ardy.xyz * y;
    return vec4f(o + d * t, 1.0);
}

// The surface's normal at p, from its neighbours: on each axis the nearer one, so a silhouette is not bridged; facing the viewer. A
// neighbour off the picture is no neighbour (clamped, it would be p itself, and the normal would fall back to the view's).
fn ao_normal(xy: vec2i, size: vec2i, p: vec3f) -> vec3f {
    var l = vec4f(0.0, 0.0, 0.0, 0.0);
    var r = l;
    var a = l;
    var b = l;
    if (xy.x > 0) { l = ao_point(xy - vec2i(1, 0), size); }
    if (xy.x < size.x - 1) { r = ao_point(xy + vec2i(1, 0), size); }
    if (xy.y > 0) { a = ao_point(xy - vec2i(0, 1), size); }
    if (xy.y < size.y - 1) { b = ao_point(xy + vec2i(0, 1), size); }
    var dx = vec3f(0.0, 0.0, 0.0);
    if (r.w > 0.0 && (l.w == 0.0 || length(r.xyz - p) < length(p - l.xyz))) { dx = r.xyz - p; } else if (l.w > 0.0) { dx = p - l.xyz; }
    var dy = vec3f(0.0, 0.0, 0.0);
    if (b.w > 0.0 && (a.w == 0.0 || length(b.xyz - p) < length(p - a.xyz))) { dy = b.xyz - p; } else if (a.w > 0.0) { dy = p - a.xyz; }
    var n = cross(dx, dy);
    let ray = u.ard.xyz;
    if (dot(n, n) < 1e-36) { return -ray; }
    n = normalize(n);
    if (dot(n, ray) > 0.0) { n = -n; }
    return n;
}

// With vs_grid's six vertices into an R8 target: 1 open, 0 fully occluded. Each of AO_DIRECTIONS directions is walked out to the radius
// in AO_STEPS steps and keeps its highest horizon (the sine of its elevation above the surface, less AO_BIAS, fading with distance).
// The directions and the steps are turned per pixel by a FIXED 4 × 4 interleave, which fs_ao_blur's 4 × 4 window removes.
@fragment fn fs_ao(i: GVO) -> @location(0) vec4f {
    let size = vec2i(textureDimensions(aodepth));
    let xy = vec2i(i.pos.xy);
    let p4 = ao_point(xy, size);
    if (p4.w == 0.0) { return vec4f(1.0, 1.0, 1.0, 1.0); }
    let p = p4.xyz;
    let n = ao_normal(xy, size, p);
    let t = textureLoad(aodepth, xy, 0).r;
    let wpp = length(u.arox.xyz + u.ardx.xyz * t) * 2.0 / f32(size.x);
    let rpx = clamp(u.ao.y / max(wpp, 1e-30), 1.0, AO_MAX_PIXELS * max(u.ao.w, 1.0));
    let cell = u32(xy.x & 3) * 4u + u32(xy.y & 3);
    let turn = (f32(cell) + 0.5) / 16.0;
    let step0 = (f32((cell * 5u) & 15u) + 0.5) / 16.0;
    let r2 = u.ao.y * u.ao.y;
    var occ = 0.0;
    for (var d = 0u; d < AO_DIRECTIONS; d = d + 1u) {
        let a = (f32(d) + turn) * 2.0 * PI / f32(AO_DIRECTIONS);
        let dir = vec2f(cos(a), sin(a));
        var h = 0.0;
        for (var k = 0u; k < AO_STEPS; k = k + 1u) {
            let off = dir * rpx * ((f32(k) + step0) / f32(AO_STEPS));
            let q = ao_point(ao_clamp(xy + vec2i(round(off)), size), size);
            if (q.w == 0.0) { continue; }
            let v = q.xyz - p;
            let len2 = dot(v, v);
            if (len2 <= 0.0) { continue; }
            let fall = clamp(1.0 - len2 / r2, 0.0, 1.0);
            h = max(h, (dot(n, v) * inverseSqrt(len2) - AO_BIAS) * fall);
        }
        occ = occ + h;
    }
    let open = clamp(1.0 - occ / (f32(AO_DIRECTIONS) * (1.0 - AO_BIAS)), 0.0, 1.0);
    return vec4f(open, open, open, 1.0);
}

// The 4 × 4 depth-aware blur: the window holds each of the interleave's 16 turns once; a neighbour on another surface (farther than the
// blur's reach from this pixel's point) does not mix in.
@fragment fn fs_ao_blur(i: GVO) -> @location(0) vec4f {
    let size = vec2i(textureDimensions(aodepth));
    let xy = vec2i(i.pos.xy);
    let c = ao_point(xy, size);
    if (c.w == 0.0) { return vec4f(1.0, 1.0, 1.0, 1.0); }
    var sum = 0.0;
    var n = 0.0;
    for (var j = -2; j < 2; j = j + 1) {
        for (var k = -2; k < 2; k = k + 1) {
            let q = ao_clamp(xy + vec2i(k, j), size);
            let p = ao_point(q, size);
            if (p.w == 0.0 || length(p.xyz - c.xyz) > u.ao.z) { continue; }
            sum = sum + textureLoad(aoraw, q, 0).r;
            n = n + 1.0;
        }
    }
    let open = select(1.0, sum / n, n > 0.0);
    return vec4f(open, open, open, 1.0);
}

// ── the ground: a shadow catcher ──
// vs_grid's six vertices; each fragment casts the view's ray at the ground's plane, as fs_grid does, so a disc four scene radii across is
// never cut by the near or far plane (its depth is written, clamped). Inside the disc it draws BLACK with the light it loses as alpha:
// the key light's share times its shadow, the rest times the occlusion, fading to nothing at the rim. The section plane does not cut it.

struct GroundHit {
    w: vec3f,
    // the parameter along the view's ray (its depth), and the distance from the centre in radii (1 or more: missed)
    t: f32,
    r: f32,
};

fn ground_hit(ndc: vec2f) -> GroundHit {
    var h: GroundHit;
    h.r = 2.0;
    let o = u.aro.xyz + u.arox.xyz * ndc.x + u.aroy.xyz * ndc.y;
    let d = u.ard.xyz + u.ardx.xyz * ndc.x + u.ardy.xyz * ndc.y;
    if (abs(d.z) < 1e-30 || u.gnd.w <= 0.0) { return h; }
    h.t = (u.gnd.z - o.z) / d.z;
    h.w = o + d * h.t;
    // a perspective ray meets it only ahead of the eye (an orthographic ray's origin is on the view plane, where t may be negative)
    if (dot(u.ardx.xyz, u.ardx.xyz) > 0.0 && h.t <= 0.0) { return h; }
    h.r = length(h.w.xy - u.gnd.xy) / u.gnd.w;
    return h;
}

struct GrOut {
    @location(0) col: vec4f,
    @builtin(frag_depth) depth: f32,
};

@fragment fn fs_ground(i: GVO) -> GrOut {
    let h = ground_hit(i.ndc);
    if (h.r >= 1.0) { discard; }
    let fade = 1.0 - smoothstep(GROUND_FADE, 1.0, h.r);
    let share = u.lu.w;
    let lost = 1.0 - (share * shadow_vis(h.w, vec3f(0.0, 0.0, 1.0)) + (1.0 - share) * occlusion_at(i.pos.xy));
    var o: GrOut;
    o.col = vec4f(0.0, 0.0, 0.0, clamp(lost * fade, 0.0, 1.0));
    let c = u.vp * vec4f(h.w, 1.0);
    o.depth = clamp(c.z / c.w, 0.0, 1.0);
    return o;
}

// The ground in the occlusion's prepass: what rests on it darkens it, and it darkens what rests on it.
@fragment fn fs_ground_prepass(i: GVO) -> GrOut {
    let h = ground_hit(i.ndc);
    if (h.r >= 1.0) { discard; }
    var o: GrOut;
    o.col = vec4f(h.t, 0.0, 0.0, 1.0);
    let c = u.vp * vec4f(h.w, 1.0);
    o.depth = clamp(c.z / c.w, 0.0, 1.0);
    return o;
}
