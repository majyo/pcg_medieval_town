// Shared helpers for the environment shaders: hashing, value noise, wind and the painterly diffuse term.
// The lighting matches the ground shader (shaders/ground.gdshader): wrapped diffuse with a touch of one soft
// band, and a cool fill instead of black on unlit sides.

global uniform vec2 wind_direction;
global uniform float wind_strength;
// Painterly light, set by Atmosphere (rendering/environment/Atmosphere.cs); the tint is linear.
global uniform float style_toon;
global uniform float style_light_wrap;
global uniform float style_shadow_fill;
global uniform vec3 style_shadow_tint;

float pl_hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

// Smooth value noise in [0, 1].
float pl_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(pl_hash12(i), pl_hash12(i + vec2(1.0, 0.0)), u.x),
		mix(pl_hash12(i + vec2(0.0, 1.0)), pl_hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}

// Horizontal wind displacement at a world position, for something of the given flexibility (0 = rigid).
// Slow gusts travel along the wind direction; a faster flutter rides on top.
vec2 pl_wind(vec2 world_xz, float time, float flexibility) {
	vec2 dir = normalize(wind_direction + vec2(1e-5));
	float along = dot(world_xz, dir);
	float gust = pl_noise(vec2(along * 0.04 - time * 0.35, dot(world_xz, vec2(-dir.y, dir.x)) * 0.04));
	float flutter = sin(time * 2.3 + along * 0.6 + pl_hash12(floor(world_xz)) * 6.28);
	return dir * wind_strength * flexibility * (gust * 0.8 + flutter * 0.15);
}

// Diffuse light for light(): add the result to DIFFUSE_LIGHT.
vec3 pl_diffuse(vec3 normal, vec3 light_dir, vec3 light_color, float attenuation) {
	float ndl = dot(normal, light_dir);
	float wrapped = clamp((ndl + style_light_wrap) / (1.0 + style_light_wrap), 0.0, 1.0);
	float banded = smoothstep(-0.1, 0.4, ndl);
	float lit = mix(wrapped, banded, style_toon) * smoothstep(0.2, 0.8, attenuation);
	return light_color / PI * (lit + style_shadow_tint * style_shadow_fill * (1.0 - lit));
}

// Colors authored in sRGB (vertex colors, uniforms without source_color) to linear.
vec3 pl_srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}
