// brief-em3d-28 R-em3d28-1c — THE one shader source for every 3D-view backend. tools/ShaderGen
// cross-compiles it offline to scene.metal (Metal), scene.hlsl (D3D11) and scene.spv (Vulkan); each
// generated file records this file's SHA-256, and Viewer3DFrameGateTests.Gate1b fails when they disagree.
// Edit this file, then regenerate:  tools/ShaderGen  ->  shadergen src/Ui/Viewer3D/Shaders/scene.wgsl src/Ui/Viewer3D/Shaders
//
// The uniform block is Scene3DFramePlan's: vp, eye, clip, the hovered (object, face), flags, the selection
// mode, the selection (brief-em3d-43 R-em3d43-5: up to 64 (object, face) pairs), then brief 29's field blocks
// (FieldUniforms: phase, range, mode, dB, the colour map's stops — brief-em3d-96: four, one per drawn plot), then
// brief 45's drawing grid (PlaneGrid.Fill), then brief 106's look block (Scene3DFramePlan.FillLook) — 2,304 bytes.
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
@fragment fn fs_pbr(i: PVO, @builtin(front_facing) front: bool) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
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
    var surface = reflected * spec_albedo
                + (vec3f(1.0, 1.0, 1.0) - spec_albedo) * (1.0 - metal) * (1.0 - t) * base * irr / PI;

    // the key light, direct (shadowed in brief 107)
    let l = u.key.xyz;
    let nl = dot(n, l);
    let keyon = nl > 0.0 && dot(u.keyc.xyz, u.keyc.xyz) > 0.0;
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
        surface = surface + (lobe + body) * u.keyc.xyz * u.lk.y * nl;
    }

    // the clear coat: a dielectric lobe over the body, its energy taken from it
    let cc = clamp(m.b.w, 0.0, 1.0);
    if (cc > 0.0) {
        let ccr = clamp(m.c.x, 0.0, 1.0);
        let lc = brdf_lut(nv, ccr);
        let coat_albedo = DIELECTRIC_F0 * lc.x + lc.y;
        var coat = env_radiance(r, ccr) * u.lk.y * coat_albedo;
        if (keyon) {
            var ac = max(ccr, MIN_ROUGHNESS);
            ac = ac * ac;
            coat = coat + u.keyc.xyz * u.lk.y * (schlick1(DIELECTRIC_F0, vh) * ggx_d(nh, ac) * smith_v(nv, nl, ac) * nl);
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
        surface = surface + through * (1.0 - tau) * att * irr / PI;
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
