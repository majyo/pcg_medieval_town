shader_type spatial;
render_mode cull_back;
uniform sampler2DArray pavements : source_color, filter_linear_mipmap, repeat_enable;
uniform vec3 edge_soil : source_color = vec3(0.55, 0.48, 0.36);
varying vec3 world_pos;
varying float paving;
varying float edge_distance;
void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    paving = UV2.x;
    edge_distance = UV2.y;
}
void fragment() {
    float layer = floor(paving + 0.5);
    vec2 uv = layer >= 3.0 ? world_pos.xz / 2.5 : vec2(UV.y, UV.x / 3.0);
    vec4 surface = texture(pavements, vec3(uv, layer));
    float macro = sin(world_pos.x * 0.17 + sin(world_pos.z * 0.23)) * 0.035;
    ALBEDO = surface.rgb * (1.0 + macro);
    ALBEDO = mix(edge_soil, ALBEDO, smoothstep(0.0, 0.25, edge_distance));
    ROUGHNESS = surface.a;
    // The kennel follows the profile's central drainage line.
    if (layer == 3.0) ALBEDO *= 1.0 - 0.14 * (1.0 - smoothstep(0.012, 0.035, abs(UV.y)));
    SPECULAR = 0.16;
}
