shader_type spatial;
render_mode diffuse_burley;
#include "res://shaders/terrain_sampling.gdshaderinc"
uniform int view_mode = 0;
uniform bool ground_surface = false;
uniform bool stale = false;
uniform bool gray_study = false;
uniform bool analysis_uses = true;
varying vec3 world_position;
void vertex() { world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
    vec3 color = COLOR.rgb;
    if (gray_study && view_mode == 0) { color = mix(color, vec3(dot(color, vec3(0.3,0.59,0.11))), 0.9); }
    int role = int(round(UV2.x));
    if (view_mode == 1 && analysis_uses) {
        if (role == 1) { color = vec3(0.57, 0.67, 0.63); }
        else if (role == 2) { color = vec3(0.76, 0.63, 0.43); }
        else if (role == 3) { color = vec3(0.65, 0.61, 0.71); }
        else if (role == 4) { color = vec3(0.57, 0.66, 0.71); }
    }
    if (view_mode == 1 && !analysis_uses && !ground_surface) { color = vec3(0.63, 0.65, 0.61); }
    if (view_mode == 2 || stale) { color = mix(color, vec3(dot(color, vec3(0.3,0.59,0.11))), 0.85) * 0.86; }
    if (ground_surface && view_mode == 0) {
        float footprint = max(length(dFdx(world_position.xz)), length(dFdy(world_position.xz)));
        float grain = untiled_noise(world_position.xz * 2.7) - 0.5;
        float patch = untiled_noise(world_position.xz * 0.17) - 0.5;
        color *= 1.0 + patch * 0.035 + grain * 0.045 * (1.0 - smoothstep(0.25, 1.2, footprint));
    }
    ALBEDO = view_mode == 1 ? vec3(0.0) : color;
    EMISSION = view_mode == 1 ? color : vec3(0.0);
    ROUGHNESS = 0.94; SPECULAR = 0.15;
}
