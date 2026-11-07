shader_type spatial;
render_mode cull_disabled;

#include "res://shaders/terrain_sampling.gdshaderinc"

uniform sampler2D ground_heights : filter_nearest, repeat_disable;
uniform sampler2DArray wave_normals : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray wave_patterns : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float map_size = 2000.0;
uniform int grid_side = 385;
uniform float preview_time = -1.0;
uniform float flow_speed = 0.7;
uniform int quality = 1;
uniform float planning_fade = 0.0;
uniform vec4 shallow_color : source_color = vec4(0.43, 0.47, 0.38, 1.0);
uniform vec4 deep_color : source_color = vec4(0.20, 0.33, 0.33, 1.0);
varying vec3 world_position;

void vertex() { world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }

// Exactly the same two planar triangles as TemperateMapData.HeightAt, including the diagonal.
float terrain_height(vec2 p) {
    vec2 g = clamp((p / map_size + vec2(0.5)) * float(grid_side - 1), vec2(0.0), vec2(float(grid_side - 1)));
    ivec2 cell = clamp(ivec2(floor(g)), ivec2(0), ivec2(grid_side - 2)); vec2 f = g - vec2(cell);
    float a = texelFetch(ground_heights, cell, 0).r;
    float b = texelFetch(ground_heights, cell + ivec2(1, 0), 0).r;
    float c = texelFetch(ground_heights, cell + ivec2(0, 1), 0).r;
    float d = texelFetch(ground_heights, cell + ivec2(1, 1), 0).r;
    return f.x + f.y <= 1.0 ? a + (b - a) * f.x + (c - a) * f.y : d + (c - d) * (1.0 - f.x) + (b - d) * (1.0 - f.y);
}

void fragment() {
    float depth = max(world_position.y - terrain_height(world_position.xz), 0.0);
    float time = preview_time >= 0.0 ? preview_time : TIME;
    vec2 flowing = UV - vec2(0.0, time * flow_speed);
    // Advect the warp together with the waves so their downstream motion stays coherent.
    vec2 warp = vec2(untiled_noise(flowing / vec2(27.0, 47.0)), untiled_noise(flowing / vec2(43.0, 71.0) + vec2(19.7, 7.3)));
    flowing += (warp - vec2(0.5)) * vec2(8.0, 15.0);
    vec2 wave_a = texture(wave_normals, vec3(flowing / vec2(8.0, 15.0), 3.0)).rg * 2.0 - 1.0;
    mat2 rotation = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8));
    vec2 wave_b = transpose(rotation) * (texture(wave_normals, vec3(rotation * flowing / 22.0, 2.0)).rg * 2.0 - 1.0);
    float footprint = max(length(dFdx(world_position.xz)), length(dFdy(world_position.xz)));
    vec2 waves = (wave_a * 0.28 + wave_b * 0.14) * (1.0 - smoothstep(0.6, 2.5, footprint));
    float broad_ripple = texture(wave_patterns, vec3(flowing / vec2(18.0, 48.0), 4.0)).r - 0.45;
    float fine_ripple = texture(wave_patterns, vec3(rotation * flowing / 12.0, 3.0)).r - 0.45;
    vec2 along = normalize(UV2); vec2 across = vec2(along.y, -along.x);
    vec2 perturb = across * waves.x + along * waves.y;
    if (quality == 0) { perturb *= 0.4; }
    NORMAL = normalize(NORMAL + (VIEW_MATRIX * vec4(perturb.x, 0.0, perturb.y, 0.0)).xyz);
    float fresnel = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 4.0);
    vec3 base = mix(shallow_color.rgb, deep_color.rgb, smoothstep(0.05, 3.2, depth));
    float surface_variation = (broad_ripple * 0.052 + fine_ripple * 0.022) * (1.0 - smoothstep(0.8, 3.0, footprint));
    ALBEDO = mix(base, vec3(0.52, 0.60, 0.60), fresnel * 0.25) + vec3(surface_variation);
    ALBEDO = mix(ALBEDO, vec3(0.39, 0.46, 0.45), planning_fade);
    ROUGHNESS = mix(0.43, 0.30, smoothstep(0.1, 2.0, depth));
    METALLIC = 0.0;
    SPECULAR = 0.5;
}
