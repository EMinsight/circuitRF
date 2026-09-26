// brief-em3d-28 R-em3d28-1c — THE one shader source for every 3D-view backend. tools/ShaderGen
// cross-compiles it offline to scene.metal (Metal), scene.hlsl (D3D11) and scene.spv (Vulkan); each
// generated file records this file's SHA-256, and Viewer3DFrameGateTests.Gate1b fails when they disagree.
// Edit this file, then regenerate:  tools/ShaderGen  ->  shadergen src/Ui/Viewer3D/Shaders/scene.wgsl src/Ui/Viewer3D/Shaders
//
// The uniform block is Scene3DFramePlan's: vp, eye, clip, the hovered (object, face), flags, the selection
// mode, the selection (brief-em3d-43 R-em3d43-5: up to 64 (object, face) pairs), then brief 29's field block
// (FieldUniforms: phase, range, mode, dB, the colour map's stops), then brief 45's drawing grid (PlaneGrid.Fill)
// — 1,072 bytes.
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
    pad0: u32,
    pad1: u32,
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
    // brief-em3d-45 — the drawing grid (PlaneGrid.Fill): the plane's u and v axes with each one's phase, its
    // normal and offset, the quad's centre and half-size, (minor, major every, fade radius, on), the line
    // colour, where the world origin's lines are (local u, v), and the colours of the lines along u and v.
    gu: vec4f,
    gv: vec4f,
    gn: vec4f,
    gq: vec4f,
    gs: vec4f,
    gcol: vec4f,
    gax: vec4f,
    gcu: vec4f,
    gcv: vec4f,
};
@group(0) @binding(0) var<uniform> u: U;

// brief-em3d-46 R-em3d46-1a — the per-draw transform (Scene3DFramePlan.Transforms): the identity, or a drag's
// preview moving what is already drawn, so a drag uploads no geometry. A second uniform binding, set per draw:
// Metal setVertexBytes at buffer 2, a D3D11 cbuffer at b1, a Vulkan dynamic offset into binding 1. Not an
// immediate (push constant): naga writes those to HLSL as ConstantBuffer<T>, which vs_5_0 does not compile.
struct MX {
    m: mat4x4f,
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
    o.world = p;
    o.id = v.id;
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
    if (clipped(i.world)) { discard; }
    let n = normalize(cross(dpdx(i.world), dpdy(i.world)));
    let d = abs(dot(n, normalize(u.eye.xyz - i.world)));
    var rgb = i.col.rgb * (0.3 + 0.7 * d);
    if (!front && (u.flags & 2u) != 0u) { rgb = i.col.rgb * 0.8; }
    return vec4f(highlight(rgb, i.id, i.face), i.col.a);
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
// One quad on the plane, made here from six vertex indices and the uniform block — no vertex buffer, so an
// orbit uploads nothing. The lines are found per fragment: minor lines where the plane coordinate is a
// multiple of the spacing, major ones every gs.y of them, the origin's axis lines in the axis colours. Minor
// lines fade where a cell would be under a few pixels (perspective's distance); everything fades with the
// distance from the focus and with obliqueness, so a plane seen nearly edge-on is a faint line, not a slab.

struct GVO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
};

@vertex fn vs_grid(@builtin(vertex_index) k: u32) -> GVO {
    // Two triangles: (-1,-1) (1,-1) (1,1) and (-1,-1) (1,1) (-1,1). Bit k of 0x16 is x > 0; of 0x34, y > 0.
    let cx = f32((0x16u >> k) & 1u) * 2.0 - 1.0;
    let cy = f32((0x34u >> k) & 1u) * 2.0 - 1.0;
    let p = u.gq.xyz + (u.gu.xyz * cx + u.gv.xyz * cy) * u.gq.w;
    var o: GVO;
    o.pos = u.vp * vec4f(p, 1.0);
    o.world = p;
    return o;
}

@fragment fn fs_grid(i: GVO) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    let pu = dot(i.world, u.gu.xyz);
    let pv = dot(i.world, u.gv.xyz);
    let a = (vec2f(pu, pv) + vec2f(u.gu.w, u.gv.w)) / u.gs.x;
    let fw = max(fwidth(a), vec2f(1e-7, 1e-7));
    let cell_px = 1.0 / max(fw.x, fw.y);
    let minor_fade = clamp((cell_px - 4.0) / 6.0, 0.0, 1.0);
    let dm = abs(fract(a - 0.5) - 0.5) / fw;
    let line_minor = 1.0 - min(min(dm.x, dm.y), 1.0);
    let am = a / u.gs.y;
    let dmaj = abs(fract(am - 0.5) - 0.5) / (fw / u.gs.y);
    let line_major = 1.0 - min(min(dmaj.x, dmaj.y), 1.0);
    let r = length(i.world - u.gq.xyz);
    let view = normalize(u.eye.xyz - i.world);
    let fade = (1.0 - smoothstep(0.45 * u.gs.z, u.gs.z, r)) * smoothstep(0.03, 0.35, abs(dot(u.gn.xyz, view)));
    var alpha = max(line_minor * 0.45 * minor_fade, line_major) * u.gcol.w;
    var rgb = u.gcol.rgb;
    // The line u = 0 runs along v, so it is v's colour; v = 0 runs along u.
    let on_v = 1.0 - min(abs(pu - u.gax.x) / max(fwidth(pu), 1e-12) / 1.2, 1.0);
    let on_u = 1.0 - min(abs(pv - u.gax.y) / max(fwidth(pv), 1e-12) / 1.2, 1.0);
    if (on_v * u.gcv.w > alpha) { alpha = on_v * u.gcv.w; rgb = u.gcv.rgb; }
    if (on_u * u.gcu.w > alpha) { alpha = on_u * u.gcu.w; rgb = u.gcu.rgb; }
    alpha = alpha * fade;
    if (alpha <= 0.002) { discard; }
    return vec4f(rgb, alpha);
}
