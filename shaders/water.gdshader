// River surface. Color and opacity follow the water thickness behind each pixel (from the depth buffer):
// clear and light in the shallows, darker and denser where deep, with foam along the shore. Two ripple layers
// drift downstream (UV.y runs along the river, UV.x across it in meters) and tilt the normal for sun glints.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled, specular_schlick_ggx;

#include "res://shaders/common/painterly.gdshaderinc"

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
uniform vec3 shallow_color : source_color = vec3(0.42, 0.62, 0.58);
uniform vec3 deep_color : source_color = vec3(0.12, 0.27, 0.34);
uniform vec3 foam_color : source_color = vec3(0.92, 0.94, 0.90);
uniform vec3 sky_color : source_color = vec3(0.70, 0.80, 0.86);
uniform float deep_thickness = 3.0;   // meters of water (along the view ray) to reach the deep color
uniform float foam_thickness = 0.5;
uniform float flow_speed = 0.7;       // meters per second downstream
uniform float ripple_strength = 0.35;

float ripples(vec2 river_uv, float time) {
	vec2 a = vec2(river_uv.x * 0.22, (river_uv.y - time * flow_speed) * 0.09);
	vec2 b = vec2(river_uv.x * 0.55 + 3.0, (river_uv.y - time * flow_speed * 1.35) * 0.3);
	return pl_noise(a) * 0.65 + pl_noise(b) * 0.35;
}

void fragment() {
	// Water thickness behind this pixel along the view ray.
	float scene_depth = texture(depth_texture, SCREEN_UV).r;
	vec4 scene = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_depth, 1.0);
	scene.xyz /= scene.w;
	float thickness = max(length(scene.xyz) - length(VERTEX), 0.0);
	float depth01 = clamp(thickness / deep_thickness, 0.0, 1.0);

	// Ripple normal from finite differences in river space.
	vec2 uv = UV;
	float e = 0.4;
	float h = ripples(uv, TIME);
	float hx = ripples(uv + vec2(e, 0.0), TIME) - h;
	float hz = ripples(uv + vec2(0.0, e), TIME) - h;
	vec3 world_normal = normalize(vec3(-hx, e / ripple_strength, -hz));
	NORMAL = normalize((VIEW_MATRIX * vec4(world_normal, 0.0)).xyz);

	vec3 color = mix(shallow_color, deep_color, smoothstep(0.0, 1.0, depth01));
	// Grazing angles reflect the sky.
	float fresnel = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
	color = mix(color, sky_color, fresnel * 0.6);

	// Foam where the water is thin, broken up by drifting noise.
	float foam_noise = pl_noise(vec2(uv.x * 1.3, (uv.y - TIME * flow_speed) * 0.8));
	float foam = 1.0 - smoothstep(0.0, foam_thickness, thickness + (foam_noise - 0.5) * 0.35);
	color = mix(color, foam_color, foam * 0.85);

	ALBEDO = color;
	ALPHA = clamp(mix(0.45, 0.95, depth01) + foam * 0.5, 0.0, 1.0);
	ROUGHNESS = 0.08;
	METALLIC = 0.0;
	SPECULAR = 0.5;
}
