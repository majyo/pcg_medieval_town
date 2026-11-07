shader_type spatial;
render_mode diffuse_burley;

#include "res://shaders/terrain_sampling.gdshaderinc"

uniform sampler2D natural_control : filter_linear_mipmap, repeat_disable;
uniform sampler2D river_control : filter_linear, repeat_disable;
uniform sampler2DArray surface_normals : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float map_size = 2000.0;
uniform vec2 height_range = vec2(0.0, 55.0);
uniform vec3 meadow : source_color;
uniform vec3 dry_grass : source_color;
uniform vec3 forest_floor : source_color;
uniform vec3 soil : source_color;
uniform vec3 wet_mud : source_color;
uniform vec3 gravel : source_color;
uniform float macro_variation = 0.08;
uniform float detail_strength = 0.65;
uniform float normal_strength = 0.3;
uniform float bank_width = 1.0;
uniform int quality = 1;
uniform float planning_fade = 0.0;
// Graded cells (03B): vertex colour r = cut, g = fill, b = road bed (1) / shoulder (0.5).
uniform bool grading_mask = false;
// Cut/fill analysis tint on graded cells: red cut, blue fill, saturating at 0.6 m.
uniform bool grading_heat = false;
varying vec3 world_position;
varying vec3 grading;
varying vec3 world_normal;

void vertex() {
    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    world_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);
    grading = grading_mask ? COLOR.rgb : vec3(0.0);
}

void fragment() {
    vec2 map_uv = world_position.xz / map_size + vec2(0.5);
    vec4 control = texture(natural_control, map_uv);
    vec2 river = texture(river_control, map_uv).rg;
    float slope = length(world_normal.xz) / max(world_normal.y, 0.2);
    float above_water = world_position.y - river.r - 0.015;
    float bank_distance = max(above_water, 0.0) / max(slope, 0.12);
    float irregular = mix(0.72, 1.35, control.a) * bank_width;
    float bank = river.g * (1.0 - smoothstep(2.0 * irregular, 9.0 * irregular, bank_distance));
    float wet = bank * (1.0 - smoothstep(0.25, 2.5 * irregular, bank_distance)) * 0.85;
    float sand = bank * smoothstep(0.32, 0.66, control.a) * (1.0 - wet * 0.65) * 0.5;
    float forest = smoothstep(0.08, 0.7, control.r) * 0.72 * (1.0 - bank);
    float relative_height = clamp((world_position.y - height_range.x) / (height_range.y - height_range.x), 0.0, 1.0);
    float dry = smoothstep(0.48, 0.75, control.g) * 0.35 * smoothstep(0.15, 0.75, relative_height) * (1.0 - forest) * (1.0 - bank);
    float bare = control.b * 0.55 * (1.0 - bank);
    float weights[6];
    weights[0] = max(0.0, 1.0 - dry - forest - bare - wet - sand);
    weights[1] = dry; weights[2] = forest; weights[3] = bare; weights[4] = wet; weights[5] = sand;
    float total = 0.0; for (int i = 0; i < 6; i++) { total += weights[i]; }
    vec3 palettes[6]; palettes[0] = meadow; palettes[1] = dry_grass; palettes[2] = forest_floor;
    palettes[3] = soil; palettes[4] = wet_mud; palettes[5] = gravel;
    float footprint = max(length(dFdx(world_position.xz)), length(dFdy(world_position.xz)));
    float detail = (1.0 - smoothstep(0.35, 2.4, footprint)) * detail_strength;
    if (quality == 0) { detail *= 0.25; }
    if (quality == 2) { detail = min(detail * 1.25, 1.0); }
    vec3 color = vec3(0.0); vec2 perturbation = vec2(0.0); float roughness = 0.0;
    vec2 texture_dx = dFdx(world_position.xz / 8.0);
    vec2 texture_dy = dFdy(world_position.xz / 8.0);
    // Three overlapping samples remove the fixed 8 m repeat. Barycentric weights
    // are continuous at cell/diagonal boundaries; offsets never move with the camera.
    vec2 tile_position = world_position.xz / 18.0;
    vec2 cell = floor(tile_position);
    vec2 f = fract(tile_position);
    vec2 anchors[3];
    vec3 blend;
    if (f.x + f.y <= 1.0) {
        anchors[0] = cell; anchors[1] = cell + vec2(1.0, 0.0); anchors[2] = cell + vec2(0.0, 1.0);
        blend = vec3(1.0 - f.x - f.y, f.x, f.y);
    } else {
        anchors[0] = cell + vec2(1.0, 1.0); anchors[1] = cell + vec2(0.0, 1.0); anchors[2] = cell + vec2(1.0, 0.0);
        blend = vec3(f.x + f.y - 1.0, 1.0 - f.x, 1.0 - f.y);
    }
    blend *= blend;
    blend /= dot(blend, vec3(1.0));
    vec2 sample_uv[3]; vec2 sample_dx[3]; vec2 sample_dy[3];
    mat2 rotations[3]; float scales[3];
    for (int j = 0; j < 3; j++) {
        vec3 random = tile_hash(anchors[j]);
        float angle = floor(random.x * 4.0) * 1.570796327;
        rotations[j] = mat2(vec2(cos(angle), sin(angle)), vec2(-sin(angle), cos(angle)));
        scales[j] = mix(0.83, 1.21, random.y);
        sample_uv[j] = rotations[j] * world_position.xz / 8.0 * scales[j] + random.yz * 17.0;
        sample_dx[j] = rotations[j] * texture_dx * scales[j];
        sample_dy[j] = rotations[j] * texture_dy * scales[j];
    }
    for (int i = 0; i < 6; i++) {
        float w = weights[i] / total;
        vec3 pattern = vec3(0.46667);
        vec3 surface = vec3(0.5, 0.5, i == 4 ? 0.58 : (i == 5 ? 0.87 : 0.95));
        // Explicit derivatives keep mip selection valid across the material-weight branch.
        if (detail > 0.001 && w > 0.002) {
            pattern = vec3(0.0); surface = vec3(0.0);
            for (int j = 0; j < 3; j++) {
                vec3 uv = vec3(sample_uv[j] + vec2(float(i) * 0.371, float(i) * 0.619), float(i));
                vec4 packed = textureGrad(surface_normals, uv, sample_dx[j], sample_dy[j]);
                pattern += vec3(packed.a) * blend[j];
                vec2 normal_xz = transpose(rotations[j]) * (packed.rg * 2.0 - 1.0) * scales[j];
                surface += vec3(normal_xz * 0.5 + 0.5, packed.b) * blend[j];
            }
        }
        color += palettes[i] * mix(vec3(1.0), vec3(0.72) + pattern * 0.6, detail) * w;
        perturbation += (surface.rg * 2.0 - 1.0) * w;
        roughness += surface.b * w;
    }
    float broad = (control.g - 0.5) * macro_variation * 2.0;
    float mottling = (control.a - 0.5) * 0.035 * (1.0 - smoothstep(1.5, 7.0, footprint));
    ALBEDO = color * (1.0 + broad + mottling);
    if (grading_mask && grading_heat) {
        ALBEDO = mix(ALBEDO, vec3(0.82, 0.27, 0.24), clamp(grading.r * 1.4, 0.0, 0.92));
        ALBEDO = mix(ALBEDO, vec3(0.18, 0.44, 0.82), clamp(grading.g * 1.4, 0.0, 0.92));
    } else if (grading_mask) {
        float cut = smoothstep(0.05, 0.4, grading.r) * (1.0 - grading.b);
        float fill = smoothstep(0.05, 0.4, grading.g) * (1.0 - grading.b);
        float bed = smoothstep(0.2, 0.9, grading.b);
        ALBEDO = mix(ALBEDO, soil * (0.92 + 0.12 * mottling * 10.0), cut * 0.8);
        ALBEDO = mix(ALBEDO, mix(soil, dry_grass, 0.45) * 0.95, fill * 0.65);
        ALBEDO = mix(ALBEDO, mix(soil, gravel, 0.35) * (0.9 + 0.2 * (control.a - 0.5)), bed * 0.9);
    }
    ALBEDO = mix(ALBEDO, vec3(dot(ALBEDO, vec3(0.3,0.59,0.11))) * 0.45 + vec3(0.28), planning_fade);
    NORMAL = normalize(NORMAL + (VIEW_MATRIX * vec4(perturbation.x, 0.0, perturbation.y, 0.0)).xyz * normal_strength * detail);
    ROUGHNESS = roughness;
    SPECULAR = mix(0.16, 0.28, wet);
    METALLIC = 0.0;
}
