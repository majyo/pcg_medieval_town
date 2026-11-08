// Painterly ground for Terrain3D, used as Terrain3DMaterial.shader_override.
// Vertex stage, region lookup and per-pixel normals are from Terrain3D's extras/shaders/minimum.gdshader
// (MIT License, Copyright © 2023-2026 Cory Petkovsek, Roope Palmroos, and Contributors).
// The fragment shading and light() are ours.
//
// The color map carries data, not colors, in the default view:
//   R = forest cover, G = height above the river surface (0 at/below water, 1 at 3 m and higher),
//   B = relative elevation (0 lowest .. 1 highest). A is unused.
// With view_mode = 1 the color map holds sRGB analysis colors that are shown as they are.
//
// Look: soft blends between ground types, procedural surface detail (grass blades and tufts, pebbles and grain,
// leaf litter) that drives both color and a bump normal, and soft wrapped lighting with a touch of banding.

shader_type spatial;
render_mode blend_mix,depth_draw_opaque,cull_back,skip_vertex_transform;

// Defined Constants
#define MAX_REGIONS 1024 // Match Terrain3DMaterial.max_regions. Set to 64-256 for mobile

// Inline Functions
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
    #define fma(a, b, c) ((a) * (b) + (c))
    #define dFdxCoarse(a) dFdx(a)
    #define dFdyCoarse(a) dFdy(a)
#endif

// Private uniforms
// Commented uniforms aren't needed for this shader, but are available for your own needs.
group_uniforms private;
uniform vec3 _target_pos = vec3(0.f);
uniform float _mesh_size = 48.f;
uniform float _subdiv = 1.f;
uniform uint _background_mode = 1u; // NONE = 0, FLAT = 1, NOISE = 2
uniform uint _mouse_layer = 0x80000000u; // Layer 32
uniform float _vertex_spacing = 1.0;
uniform float _vertex_density = 1.0; // = 1./_vertex_spacing
uniform float _region_size = 1024.0;
uniform float _region_texel_size = 0.0009765625; // = 1./region_size
uniform int _region_map_size = 32;
uniform int _region_map[1024];
//uniform vec2 _region_locations[MAX_REGIONS];
//uniform float _texture_normal_depth_array[32];
//uniform float _texture_ao_strength_array[32];
//uniform float _texture_ao_affect_array[32];
//uniform float _texture_roughness_mod_array[32];
//uniform float _texture_uv_scale_array[32];
//uniform vec2 _texture_detile_array[32];
//uniform vec4 _texture_color_array[32];
uniform highp sampler2DArray _height_maps : repeat_disable;
uniform highp sampler2DArray _control_maps : repeat_disable;
// Raw data (no source_color): the default view stores masks, not colors.
uniform highp sampler2DArray _color_maps : filter_nearest, repeat_disable;
//uniform highp sampler2DArray _texture_array_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
//uniform highp sampler2DArray _texture_array_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
group_uniforms;

// Public uniforms
group_uniforms general_uniforms;
uniform bool flat_terrain_normals = false;
group_uniforms;

group_uniforms style;
uniform int view_mode = 0; // 0 = painterly ground from masks, 1 = analysis colors from the color map
uniform vec3 meadow_fresh : source_color = vec3(0.494, 0.612, 0.341);
uniform vec3 meadow_light : source_color = vec3(0.580, 0.643, 0.380);
uniform vec3 meadow_dry : source_color = vec3(0.667, 0.651, 0.431);
uniform vec3 grass_tip : source_color = vec3(0.690, 0.737, 0.455);
uniform vec3 forest_floor : source_color = vec3(0.333, 0.420, 0.267);
uniform vec3 leaf_litter : source_color = vec3(0.498, 0.431, 0.278);
uniform vec3 bare_earth : source_color = vec3(0.612, 0.533, 0.388);
uniform vec3 bank_sand : source_color = vec3(0.776, 0.714, 0.541);
uniform vec3 wet_mud : source_color = vec3(0.427, 0.412, 0.302);
uniform float forest_threshold : hint_range(0.0, 1.0) = 0.30;
uniform float sand_threshold : hint_range(0.0, 1.0) = 0.30;
uniform float wet_threshold : hint_range(0.0, 1.0) = 0.09;
uniform float steep_degrees : hint_range(5.0, 60.0) = 24.0;
// Half-width of the blend between ground types, in units of each mask.
uniform float blend_width : hint_range(0.01, 0.5) = 0.16;
uniform float edge_breakup : hint_range(0.0, 1.0) = 0.30;
uniform float macro_variation : hint_range(0.0, 0.3) = 0.08;
uniform float detail_strength : hint_range(0.0, 1.0) = 1.0;
uniform float bump_strength : hint_range(0.0, 4.0) = 2.0;
uniform float detail_far = 140.0;
group_uniforms;

group_uniforms lighting;
// Wrapped diffuse: light reaches a little past the terminator for a softer falloff.
uniform float light_wrap : hint_range(0.0, 1.0) = 0.35;
// How much of a single soft light band is mixed into the smooth falloff.
uniform float toon_amount : hint_range(0.0, 1.0) = 0.3;
uniform float band_center : hint_range(-1.0, 1.0) = 0.15;
uniform float band_softness : hint_range(0.01, 1.0) = 0.25;
uniform vec3 shadow_tint : source_color = vec3(0.55, 0.60, 0.85);
uniform float shadow_fill : hint_range(0.0, 1.0) = 0.22;
group_uniforms;

// Varyings & Types
// Some are required for editor functions
varying vec3 v_vertex;
varying float v_vertex_xz_dist;
varying vec3 v_camera_pos;

////////////////////////
// Style helpers
////////////////////////

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

// Smooth value noise in [0, 1].
float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x),
		mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}

vec2 rotate2(vec2 p, float a) {
	float c = cos(a), s = sin(a);
	return vec2(c * p.x - s * p.y, s * p.x + c * p.y);
}

// Cellular noise: x = distance to the nearest jittered feature point, y = that cell's random value.
vec2 cellular(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	float best = 8.0;
	float id = 0.0;
	for (int y = -1; y <= 1; y++) {
		for (int x = -1; x <= 1; x++) {
			vec2 g = vec2(float(x), float(y));
			vec2 r = g + vec2(hash12(i + g), hash12(i + g + 19.19)) - f;
			float d = dot(r, r);
			if (d < best) {
				best = d;
				id = hash12(i + g + 7.7);
			}
		}
	}
	return vec2(sqrt(best), id);
}

// 1 while a feature of this frequency (cycles per meter) spans several pixels, 0 once it would alias.
float lod_fade(float frequency, float pixel) {
	return 1.0 - smoothstep(0.25, 0.6, pixel * frequency);
}

// Detail heights in [0, 1]; each component fades to its mean (0.5) where it would alias.
// Grass: rounded tufts of two sizes with darker gaps between them, fine blades on top, broad clumping below.
float grass_detail(vec2 p, float pixel) {
	vec2 big = cellular(p * 1.4);
	float tufts = (1.0 - smoothstep(0.1, 0.75, big.x)) * (0.7 + 0.3 * big.y);
	tufts = mix(0.5, tufts, lod_fade(1.4, pixel));
	vec2 small = cellular(p * 3.3 + 11.0);
	float sprigs = mix(0.5, (1.0 - smoothstep(0.05, 0.7, small.x)) * (0.6 + 0.4 * small.y), lod_fade(3.3, pixel));
	vec2 q = rotate2(p, value_noise(p / 9.0) * 3.0); // blades lean in a slowly turning direction
	float blades = mix(0.5, value_noise(q * vec2(5.0, 16.0)), lod_fade(16.0, pixel));
	float clumps = value_noise(p * 0.35 + 7.3);
	return tufts * 0.4 + sprigs * 0.25 + blades * 0.15 + clumps * 0.2;
}

float soil_detail(vec2 p, float pixel) {
	vec2 cell = cellular(p * 2.6);
	float pebbles = mix(0.5, 1.0 - smoothstep(0.18, 0.42 + cell.y * 0.12, cell.x), lod_fade(2.6, pixel));
	float grain = mix(0.5, value_noise(p * 11.0), lod_fade(11.0, pixel));
	float lumps = value_noise(p * 0.7 + 1.3);
	return pebbles * 0.45 + grain * 0.25 + lumps * 0.3;
}

float litter_detail(vec2 p, float pixel) {
	vec2 cell = cellular(p * 4.0);
	float leaves = mix(0.5, (1.0 - smoothstep(0.25, 0.55, cell.x)) * (0.6 + 0.4 * cell.y), lod_fade(4.0, pixel));
	float moss = value_noise(p * 0.9 + 5.5);
	return leaves * 0.6 + moss * 0.4;
}

vec3 srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

////////////////////////
// Vertex
////////////////////////

// Takes in world space XZ (UV) coordinates
// Returns ivec3 with:
// XY: (0 to _region_size - 1) coordinates within a region
// Z: layer index used for texturearrays, -1 if not in a region
ivec3 get_index_coord(const vec2 uv) {
	vec2 r_uv = round(uv);
	ivec2 pos = ivec2(floor(r_uv * _region_texel_size)) + (_region_map_size / 2);
	int bounds = int(uint(pos.x | pos.y) < uint(_region_map_size));
	int map_val = _region_map[pos.y * _region_map_size + pos.x];
	int raw_index = map_val - 1;
	// Real regions limited by max_regions
	int is_region = bounds * int(raw_index >= 0) * int(raw_index < MAX_REGIONS);
	// Editor dummies are negative; keep them != -1 so the shader still sees a region
	int is_dummy = bounds * int(map_val < 0);
	int layer_index = is_region * raw_index
	               + is_dummy * (map_val - 1)
	               - (1 - is_region - is_dummy);
	return ivec3(ivec2(mod(r_uv, _region_size)), layer_index);
}

// Takes in world space XZ (UV) and returns if the coordinate should be part of the NONE background.
bool is_none_bg(const vec2 uv) {
	ivec4 regions = ivec4(
		get_index_coord(uv - vec2(0.5, 0.0)).z,
		get_index_coord(uv - vec2(0.0, 0.5)).z,
		get_index_coord(uv + vec2(1.0, 0.0)).z,
		get_index_coord(uv + vec2(0.0, 1.0)).z);
	return any(equal(regions, ivec4(-1)));
}

float interpolated_height(vec2 pos) {
	const vec2 offsets = vec2(0, 1);
	vec2 index_id = floor(pos);
	ivec3 index[4];
	index[0] = get_index_coord(index_id + offsets.xy);
	index[1] = get_index_coord(index_id + offsets.yy);
	index[2] = get_index_coord(index_id + offsets.yx);
	index[3] = get_index_coord(index_id + offsets.xx);
	float h0 = texelFetch(_height_maps, index[0], 0).r;
	float h1 = texelFetch(_height_maps, index[1], 0).r;
	float h2 = texelFetch(_height_maps, index[2], 0).r;
	float h3 = texelFetch(_height_maps, index[3], 0).r;
	vec2 f = fract(pos);
	vec2 i = 1.0 - f;
	vec4 w = vec4(i.x * f.y, f.x * f.y, f.x * i.y, i.x * i.y);
	float h = h0 * w[0] + h1 * w[1] + h2 * w[2] + h3 * w[3];
	return h;
}

void vertex() {
	// Save Camera Position to varying for access in later functions
	v_camera_pos = MAIN_CAM_INV_VIEW_MATRIX[3].xyz;

	// Get vertex of flat plane in world coordinates and set world UV
	v_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;

	// Distance from target node to vertex on a flat plane
	v_vertex_xz_dist = length(v_vertex.xz - _target_pos.xz);

	// Geomorph vertex across clipmap LODs, set end and start for linear height interpolate
	float scale = MODEL_MATRIX[0][0];
	float inv_scale = 1.0 / scale;
	float max_xz = max(abs(v_vertex.x - _target_pos.x), abs(v_vertex.z - _target_pos.z));
	float vertex_lerp = smoothstep(0.0, 1.0, (max_xz * inv_scale - _mesh_size - 4.0) / (_mesh_size - 4.0));
	vec2 vertex_fract = fract(VERTEX.xz * 0.5) * 2.0;
	// For LOD0 morph from a regular grid to an alternating grid to align with LOD1+
	vec2 shift = (scale < _vertex_spacing / _subdiv + 1e-6) ? // LOD0 or not
		// Shift from regular to symmetric
		mix(vertex_fract, vec2(vertex_fract.x, -vertex_fract.y),
			round(fract(round(mod(v_vertex.z * inv_scale, 4.0)) *
			round(mod(v_vertex.x * inv_scale, 4.0)) * 0.25))) :
		// Symmetric shift
		vertex_fract * round((fract(v_vertex.xz * 0.25 * inv_scale) - 0.5) * 4.0);
	vec2 start_pos = v_vertex.xz * _vertex_density;
	vec2 end_pos = (v_vertex.xz - shift * scale) * _vertex_density;
	v_vertex.xz -= shift * scale * vertex_lerp;

	// UV coordinates in region space. 0-1 covers 1 region, 1-2 is the next region, etc.
	UV = v_vertex.xz * _vertex_density;

	// UV coordinates in region space + texel offset. Values are 0 to 1 within regions
	UV2 = fma(UV, vec2(_region_texel_size), vec2(0.5 * _region_texel_size));

	// Discard vertices for Holes. 1 lookup
	ivec3 v_region = get_index_coord(start_pos);
	uint control = floatBitsToUint(texelFetch(_control_maps, v_region, 0)).r;
	bool hole = bool(control >>2u & 0x1u);

	// Show holes to all cameras except mouse camera (on exactly 1 layer)
	if ( !(CAMERA_VISIBLE_LAYERS == _mouse_layer) &&
			(hole || (_background_mode == 0u && is_none_bg(UV)))) {
		v_vertex.x = 0. / 0.;
	} else {
		// Set final vertex height.
		float h;
		// This branch is static for each of the clipmap segments
		// Interpolated reads only occur where sub-texel values are required.
		if (scale < _vertex_spacing) {
			h = interpolated_height(UV);
		} else {
			ivec3 coord_a = get_index_coord(start_pos);
			ivec3 coord_b = get_index_coord(end_pos);
			h = mix(texelFetch(_height_maps, coord_a, 0).r,texelFetch(_height_maps, coord_b, 0).r,vertex_lerp);
		}
		v_vertex.y = h;
	}

	// Convert model space to view space w/ skip_vertex_transform render mode
	VERTEX = (VIEW_MATRIX * vec4(v_vertex, 1.0)).xyz;
	NORMAL = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	BINORMAL = normalize((MODELVIEW_MATRIX * vec4(BINORMAL, 0.0)).xyz);
	TANGENT = normalize((MODELVIEW_MATRIX * vec4(TANGENT, 0.0)).xyz);
}

////////////////////////
// Fragment
////////////////////////

void fragment() {
	// Recover UVs
	vec2 uv = UV;
	//vec2 uv2 = UV2;

	// Lookup offsets, ID and blend weight
	const vec3 offsets = vec3(0, 1, 2);
	vec2 index_id = floor(uv);
	vec2 weight = min(fract(uv), 1.0 - 1e-7);
	vec2 invert = 1.0 - weight;
	vec4 weights = vec4(
		invert.x * weight.y, // 0
		weight.x * weight.y, // 1
		weight.x * invert.y, // 2
		invert.x * invert.y  // 3
	);

	vec3 base_ddx = dFdxCoarse(v_vertex);
	vec3 base_ddy = dFdyCoarse(v_vertex);
	//vec4 base_derivatives = vec4(base_ddx.xz, base_ddy.xz);
	// Calculate the effective mipmap for regionspace, and if less than 0,
	// skip all extra lookups required for bilinear blend.
	float region_mip = log2(max(length(base_ddx.xz), length(base_ddy.xz)) * _vertex_density);
	bool bilerp = region_mip < 4.0;

	ivec3 index[4];
	// control map lookups, used for some normal lookups as well
	index[0] = get_index_coord(index_id + offsets.xy);
	index[1] = get_index_coord(index_id + offsets.yy);
	index[2] = get_index_coord(index_id + offsets.yx);
	index[3] = get_index_coord(index_id + offsets.xx);

	// Terrain normals
	vec3 index_normal[4];
	float h[4];
	// allows additional derivatives, eg world noise, brush previews etc
	float u = 0.0;
	float v = 0.0;

	// Re-use index[] for the first lookups, skipping some math. 3 lookups
	h[3] = texelFetch(_height_maps, index[3], 0).r; // 0 (0,0)
	h[2] = texelFetch(_height_maps, index[2], 0).r; // 1 (1,0)
	h[0] = texelFetch(_height_maps, index[0], 0).r; // 2 (0,1)
	index_normal[3] = normalize(vec3(h[3] - h[2] + u, _vertex_spacing, h[3] - h[0] + v));

	// Set flat world normal - overriden if bilerp is true
	vec3 w_normal = index_normal[3];

	// Branching smooth normals must be done seperatley for correct normals at all 4 index ids
	if (bilerp) {
		// 5 lookups
		// Fetch the additional required height values for smooth normals
		h[1] = texelFetch(_height_maps, index[1], 0).r; // 3 (1,1)
		float h_4 = texelFetch(_height_maps, get_index_coord(index_id + offsets.yz), 0).r; // 4 (1,2)
		float h_5 = texelFetch(_height_maps, get_index_coord(index_id + offsets.zy), 0).r; // 5 (2,1)
		float h_6 = texelFetch(_height_maps, get_index_coord(index_id + offsets.zx), 0).r; // 6 (2,0)
		float h_7 = texelFetch(_height_maps, get_index_coord(index_id + offsets.xz), 0).r; // 7 (0,2)

		// Calculate the normal for the remaining index ids.
		index_normal[0] = normalize(vec3(h[0] - h[1] + u, _vertex_spacing, h[0] - h_7 + v));
		index_normal[1] = normalize(vec3(h[1] - h_5 + u, _vertex_spacing, h[1] - h_4 + v));
		index_normal[2] = normalize(vec3(h[2] - h_6 + u, _vertex_spacing, h[2] - h[1] + v));

		// Set interpolated world normal
		w_normal =
			index_normal[0] * weights[0] +
			index_normal[1] * weights[1] +
			index_normal[2] * weights[2] +
			index_normal[3] * weights[3] ;
	}

	// Apply terrain normals
	vec3 w_tangent = normalize(cross(w_normal, vec3(0.0, 0.0, 1.0)));
	vec3 w_binormal = normalize(cross(w_normal, w_tangent));
	NORMAL = mat3(VIEW_MATRIX) * w_normal;
	TANGENT = mat3(VIEW_MATRIX) * w_tangent;
	BINORMAL = mat3(VIEW_MATRIX) * w_binormal;

	if (flat_terrain_normals) {
		NORMAL = normalize(cross(dFdyCoarse(VERTEX),dFdxCoarse(VERTEX)));
		TANGENT = normalize(cross(NORMAL, VIEW_MATRIX[2].xyz));
		BINORMAL = normalize(cross(NORMAL, TANGENT));
	}

	// Data masks, bilinear between the four surrounding vertices.
	vec4 masks =
		texelFetch(_color_maps, index[0], 0) * weights[0] +
		texelFetch(_color_maps, index[1], 0) * weights[1] +
		texelFetch(_color_maps, index[2], 0) * weights[2] +
		texelFetch(_color_maps, index[3], 0) * weights[3];

	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	if (view_mode == 1) {
		ALBEDO = srgb_to_linear(masks.rgb);
	} else {
		vec2 p = v_vertex.xz;
		// Ground footprint of one screen pixel, in meters; drives detail filtering.
		float pixel = max(length(fwidth(p)), 1e-4);
		float distance_to_camera = length(v_camera_pos - v_vertex);
		float near = (1.0 - smoothstep(detail_far * 0.5, detail_far, distance_to_camera)) * detail_strength;

		// Low-frequency offsets keep the boundaries between ground types irregular without making them busy.
		float edge = ((value_noise(p / 23.0) - 0.5) * 0.7 + (value_noise(p / 7.0 + 17.0) - 0.5) * 0.3) * edge_breakup;
		float grass_h = grass_detail(p, pixel);
		float soil_h = soil_detail(p, pixel);
		float litter_h = litter_detail(p, pixel);
		float w = blend_width;

		// Ground-type weights. Each boundary is a soft band, nudged by the detail on either side so that,
		// for example, tufts of grass reach into the sand.
		float forest = smoothstep(forest_threshold - w, forest_threshold + w, masks.r + edge + (litter_h - 0.5) * 0.15);
		float steep_cos = cos(radians(steep_degrees));
		float steep = 1.0 - smoothstep(steep_cos - w * 0.15, steep_cos + w * 0.15, w_normal.y - edge * 0.05);
		float sand = 1.0 - smoothstep(sand_threshold - w, sand_threshold + w, masks.g + edge * 0.5 + (grass_h - soil_h) * 0.2);
		float wet = 1.0 - smoothstep(wet_threshold - w * 0.5, wet_threshold + w * 0.5, masks.g + edge * 0.1);

		// Meadow: broad soft patches of two tones, drier on high ground, with grass detail on top.
		float patch_value = value_noise(p / 55.0 + 5.0) * 0.7 + value_noise(p / 17.0 + 9.0) * 0.3;
		vec3 meadow = mix(meadow_fresh, meadow_light, smoothstep(0.35, 0.75, patch_value + edge * 0.3));
		meadow = mix(meadow, meadow_dry, smoothstep(0.7, 0.95, masks.b + edge * 0.4) * 0.6);
		// Gaps between tufts sit in shade, tuft tops catch light.
		meadow *= mix(1.0, 0.68 + 0.5 * grass_h, near);
		meadow = mix(meadow, grass_tip, smoothstep(0.55, 0.85, grass_h) * 0.45 * near);

		vec3 floor_color = mix(forest_floor, leaf_litter, smoothstep(0.5, 0.85, litter_h) * 0.6 * (0.4 + 0.6 * near));
		vec3 soil = bare_earth * mix(1.0, 0.62 + 0.75 * soil_h, near);
		vec3 sand_color = bank_sand * mix(1.0, 0.74 + 0.5 * soil_h, near);
		vec3 mud_color = wet_mud * mix(1.0, 0.85 + 0.3 * soil_h, near);

		vec3 color = mix(meadow, floor_color, forest);
		color = mix(color, soil, steep);
		color = mix(color, sand_color, sand);
		color = mix(color, mud_color, wet);
		// Very broad, gentle value variation so large fields are not flat.
		color *= 1.0 + (value_noise(p / 140.0 + 2.0) - 0.5) * 2.0 * macro_variation;

		// Bump: the same detail heights, weighted like the colors, tilt the normal. World-space finite
		// differences with a step no smaller than the pixel footprint.
		if (near > 0.001) {
			float grass_w = (1.0 - forest) * (1.0 - steep) * (1.0 - sand) * (1.0 - wet);
			float soil_w = max(max(steep, sand), wet);
			float litter_w = forest * (1.0 - soil_w);
			float e = max(0.04, pixel);
			vec2 ex = vec2(e, 0.0);
			vec2 ez = vec2(0.0, e);
			float hx = grass_w * (grass_detail(p + ex, pixel) - grass_detail(p - ex, pixel)) * 0.06
				+ soil_w * (soil_detail(p + ex, pixel) - soil_detail(p - ex, pixel)) * 0.05
				+ litter_w * (litter_detail(p + ex, pixel) - litter_detail(p - ex, pixel)) * 0.04;
			float hz = grass_w * (grass_detail(p + ez, pixel) - grass_detail(p - ez, pixel)) * 0.06
				+ soil_w * (soil_detail(p + ez, pixel) - soil_detail(p - ez, pixel)) * 0.05
				+ litter_w * (litter_detail(p + ez, pixel) - litter_detail(p - ez, pixel)) * 0.04;
			vec3 n = normalize(w_normal + vec3(-hx, 0.0, -hz) / (2.0 * e) * bump_strength * near);
			NORMAL = mat3(VIEW_MATRIX) * n;
		}

		ALBEDO = color;
	}
}

// Soft lighting: wrapped diffuse mixed with a single soft band. Unlit parts get a cool fill instead of black.
void light() {
	float ndl = dot(NORMAL, LIGHT);
	float wrapped = clamp((ndl + light_wrap) / (1.0 + light_wrap), 0.0, 1.0);
	float banded = smoothstep(band_center - band_softness, band_center + band_softness, ndl);
	float lit = mix(wrapped, banded, toon_amount) * smoothstep(0.2, 0.8, ATTENUATION);
	DIFFUSE_LIGHT += LIGHT_COLOR / PI * (lit + shadow_tint * shadow_fill * (1.0 - lit));
}
