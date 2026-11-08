// Trees from TreeMeshes: sRGB vertex colors times the per-instance tint, painterly lighting, and wind that bends
// each tree by the square of its height (UV.y holds the height above the base in meters).
shader_type spatial;
render_mode cull_back;

#include "res://shaders/common/painterly.gdshaderinc"

uniform float sway = 0.006;

varying vec3 tint;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	float height = UV.y;
	vec2 wind = pl_wind(origin.xz, TIME, 1.0) * sway * height * height;
	// Wind is a world-space offset; bring it into the instance's local space.
	VERTEX += inverse(mat3(MODEL_MATRIX)) * vec3(wind.x, 0.0, wind.y);
	tint = COLOR.rgb;
}

void fragment() {
	ALBEDO = pl_srgb_to_linear(tint);
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}

void light() {
	DIFFUSE_LIGHT += pl_diffuse(NORMAL, LIGHT, LIGHT_COLOR, ATTENUATION);
}
