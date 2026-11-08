// Rocks: sRGB vertex colors with soft moss on upward-facing surfaces and painterly lighting.
shader_type spatial;
render_mode cull_back;

#include "res://shaders/common/painterly.gdshaderinc"

uniform vec3 moss_color : source_color = vec3(0.40, 0.48, 0.27);

varying vec3 v_world_normal;
varying vec3 v_world_position;

void vertex() {
	v_world_normal = normalize(mat3(MODEL_MATRIX) * NORMAL);
	v_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 stone = pl_srgb_to_linear(COLOR.rgb);
	float grain = pl_noise(v_world_position.xz * 3.0 + v_world_position.y * 2.0);
	stone *= 0.85 + grain * 0.3;
	float moss = smoothstep(0.55, 0.85, v_world_normal.y + (pl_noise(v_world_position.xz * 0.8) - 0.5) * 0.4);
	ALBEDO = mix(stone, moss_color, moss * 0.8);
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}

void light() {
	DIFFUSE_LIGHT += pl_diffuse(NORMAL, LIGHT, LIGHT_COLOR, ATTENUATION);
}
