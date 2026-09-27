// brief-em3d-28 R-em3d28-1c — THE one shader source for every 3D-view backend. tools/ShaderGen
// cross-compiles it offline to scene.metal (Metal), scene.hlsl (D3D11) and scene.spv (Vulkan); each
// generated file records this file's SHA-256, and Viewer3DFrameGateTests.Gate1b fails when they disagree.
// Edit this file, then regenerate:  tools/ShaderGen  ->  shadergen src/Ui/Viewer3D/Shaders/scene.wgsl src/Ui/Viewer3D/Shaders
//
// The uniform block is Scene3DFramePlan's: vp, eye, clip, the hovered (object, face), flags, the selection
// mode, the selection (brief-em3d-43 R-em3d43-5: up to 64 (object, face) pairs), then brief 29's field block
// (FieldUniforms: phase, range, mode, dB, the colour map's stops), then brief 45's drawing grid (PlaneGrid.Fill)
// — 1,152 bytes.
// A vertex is Scene3DVertex: position, object id, RGBA8 colour, face (24 bytes); a FIELD vertex is
// FieldVertex: position, the value's real part, its imaginary part (36 bytes).

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
    // x cos φ, y sin φ, z range lo, w range hi
    fphase: vec4f,
    // x mode (0 |v| of a vector, 1 |Re{v e^jφ}|, 2 a real scalar, 3 Re{v e^jφ} of a scalar, 4 |v| of a
    // scalar), y dB, z the colour map's stop count
    fmode: vec4f,
    // (t, r, g, b) per stop
    stops: array<vec4f, 16>,
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
};

@vertex fn vs_field(v: FVI) -> FVO {
    var o: FVO;
    o.pos = u.vp * vec4f(v.p, 1.0);
    o.world = v.p;
    o.re = v.re;
    o.im = v.im;
    return o;
}

fn field_value(re: vec3f, im: vec3f) -> f32 {
    let c = u.fphase.x;
    let s = u.fphase.y;
    let mode = u32(u.fmode.x + 0.5);
    if (mode == 0u) { return sqrt(dot(re, re) + dot(im, im)); }
    if (mode == 1u) { return length(re * c - im * s); }
    if (mode == 2u) { return re.x; }
    if (mode == 3u) { return re.x * c - im.x * s; }
    return sqrt(re.x * re.x + im.x * im.x);
}

fn colour_map(t: f32) -> vec3f {
    let n = u32(u.fmode.z + 0.5);
    var rgb = u.stops[0].yzw;
    for (var k = 1u; k < n; k = k + 1u) {
        let a = u.stops[k - 1u];
        let b = u.stops[k];
        if (t <= b.x || k == n - 1u) {
            let w = clamp((t - a.x) / max(b.x - a.x, 1e-6), 0.0, 1.0);
            rgb = mix(a.yzw, b.yzw, w);
            break;
        }
    }
    return rgb;
}

@fragment fn fs_field(i: FVO) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    var v = field_value(i.re, i.im);
    if (u.fmode.y > 0.5) { v = 20.0 * 0.30102999566 * log2(max(abs(v), 1e-30)); }
    let t = clamp((v - u.fphase.z) / max(u.fphase.w - u.fphase.z, 1e-30), 0.0, 1.0);
    return vec4f(colour_map(t), 1.0);
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
