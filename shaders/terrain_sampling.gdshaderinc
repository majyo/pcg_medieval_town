// Stable, world-anchored variation. Adjacent triangles share their vertex transforms.
vec3 tile_hash(vec2 cell) {
    vec3 p = vec3(dot(cell, vec2(127.1, 311.7)), dot(cell, vec2(269.5, 183.3)), dot(cell, vec2(419.2, 371.9)));
    return fract(sin(p) * 43758.5453);
}

float untiled_noise(vec2 p) {
    vec2 cell = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(tile_hash(cell).x, tile_hash(cell + vec2(1.0, 0.0)).x, f.x),
        mix(tile_hash(cell + vec2(0.0, 1.0)).x, tile_hash(cell + vec2(1.0, 1.0)).x, f.x), f.y);
}
