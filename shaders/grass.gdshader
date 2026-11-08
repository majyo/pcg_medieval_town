// Ground cover placed on the GPU (see GroundCover.cs). Instance transforms are identity; the node sits at the
// cover's center snapped to whole cells. Each instance owns one world-space cell: it jitters inside it, reads
// ground height, ground masks (R forest, G height above water, B elevation) and the exclusion map, and shrinks
// to nothing where it should not grow or near the edge of the covered area.
//   kind 0: grass tufts, taller and straw-colored as reeds near the water
//   kind 1: wild flowers in patches on open meadow
shader_type spatial;
render_mode cull_disabled;

#include "res://shaders/common/painterly.gdshaderinc"

uniform int kind = 0;
uniform int grid_size = 300;
uniform float spacing = 0.33;
uniform float radius = 50.0;
uniform float map_half = 1000.0;
uniform float map_samples = 2001.0;
uniform sampler2D height_map : filter_linear, repeat_disable;
uniform sampler2D mask_map : filter_linear, repeat_disable;
uniform sampler2D exclusion_map : filter_linear, repeat_disable;

uniform vec3 grass_root : source_color = vec3(0.30, 0.42, 0.20);
uniform vec3 grass_tip : source_color = vec3(0.66, 0.73, 0.40);
uniform vec3 dry_tip : source_color = vec3(0.76, 0.72, 0.44);
uniform vec3 reed_tip : source_color = vec3(0.74, 0.70, 0.42);

varying float v_tip;
varying vec3 v_masks;
varying float v_reed;
varying float v_hue;
varying float v_head;

void vertex() {
	vec3 node = MODEL_MATRIX[3].xyz;
	vec2 cell = floor(node.xz / spacing + 0.5)
		+ vec2(float(INSTANCE_ID % grid_size), float(INSTANCE_ID / grid_size)) - float(grid_size) * 0.5;
	vec2 pos = (cell + 0.15 + vec2(pl_hash12(cell), pl_hash12(cell + 17.3)) * 0.7) * spacing;

	// Height-grid samples sit at texel centers.
	vec2 uv = ((pos + map_half) / (2.0 * map_half) * (map_samples - 1.0) + 0.5) / map_samples;
	vec4 masks = texture(mask_map, uv);
	float ground = texture(height_map, uv).r;
	float excluded = texture(exclusion_map, uv).r;

	float density;
	float scale = 0.75 + pl_hash12(cell + 3.1) * 0.6;
	float reed = 0.0;
	if (kind == 0) {
		// Sparse on the sandy bank (mostly reeds), full on the meadow, thinner under trees.
		density = (1.0 - masks.r * 0.7) * smoothstep(0.03, 0.12, masks.g) * mix(0.25, 1.0, smoothstep(0.25, 0.45, masks.g));
		reed = 1.0 - smoothstep(0.12, 0.4, masks.g);
		scale *= 1.0 + reed * 1.6;
	} else {
		float patch_value = smoothstep(0.58, 0.72, pl_noise(pos * 0.07 + 13.0));
		density = patch_value * (1.0 - masks.r) * smoothstep(0.4, 0.6, masks.g) * 0.55;
	}
	density *= 1.0 - smoothstep(0.2, 0.6, excluded);

	float distance_to_camera = length(pos - CAMERA_POSITION_WORLD.xz);
	float edge = 1.0 - smoothstep(radius * 0.55, radius * 0.95, distance_to_camera);
	float keep = step(pl_hash12(cell + 5.7), density) * step(0.001, edge);
	scale *= keep * mix(0.25, 1.0, edge);

	float angle = pl_hash12(cell + 9.1) * 6.2831;
	vec3 v = VERTEX * scale;
	v.xz = mat2(vec2(cos(angle), sin(angle)), vec2(-sin(angle), cos(angle))) * v.xz;
	float tip = UV.y;
	vec2 wind = pl_wind(pos, TIME, 0.22 + reed * 0.25) * tip * tip * scale;
	v.xz += wind;
	v.y -= dot(wind, wind) * 0.5; // bend down a little as the blade leans

	VERTEX = vec3(pos.x, ground, pos.y) + v - node;
	v_tip = tip;
	v_masks = masks.rgb;
	v_reed = reed;
	v_hue = pl_hash12(cell + 23.9);
	v_head = COLOR.r;
}

void fragment() {
	vec3 color;
	if (kind == 0) {
		vec3 tip_color = mix(grass_tip, dry_tip, smoothstep(0.7, 0.95, v_masks.b) * 0.6);
		tip_color = mix(tip_color, reed_tip, v_reed);
		color = mix(grass_root, tip_color, smoothstep(0.0, 1.0, v_tip));
		color *= 1.0 - v_masks.r * 0.25; // a little darker under trees
	} else if (v_head > 0.5) {
		color = v_hue < 0.45 ? vec3(0.95, 0.93, 0.86) : v_hue < 0.8 ? vec3(0.95, 0.80, 0.30) : vec3(0.66, 0.55, 0.85);
	} else {
		color = mix(grass_root, grass_tip, v_tip * 0.6);
	}

	ALBEDO = color;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	// Thin blades: light from behind shows through a little.
	BACKLIGHT = color * 0.35;
}

void light() {
	DIFFUSE_LIGHT += pl_diffuse(NORMAL, LIGHT, LIGHT_COLOR, ATTENUATION);
}
