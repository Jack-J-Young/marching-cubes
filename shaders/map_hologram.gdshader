// Map hologram: low-poly terrain drawn as a glowing green grid on dark green facets (Deep Rock Galactic style).
// Facet normals come from screen derivatives, so the coarse mesh reads as flat-shaded low poly whatever its
// vertex normals are. Grid lines are world-space and triplanar, anti-aliased with fwidth. Works on Forward+ and
// Compatibility.
shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled, depth_draw_opaque;

/// Map centre and radius (map-world space): the terrain dissolves over the last `fade` units.
uniform vec3 map_center = vec3(0.0);
uniform float radius = 512.0;
uniform float fade = 64.0;

/// Cutaway: terrain nearer the camera than cut_distance dissolves (over cut_soft units), so rock between the
/// camera and the player never hides the player's surroundings.
uniform float cut_distance = 0.0;
uniform float cut_soft = 30.0;

uniform vec4 line_color : source_color = vec4(0.35, 1.0, 0.45, 1.0);
uniform vec4 fill_color : source_color = vec4(0.02, 0.16, 0.06, 1.0);
/// World units between grid lines.
uniform float grid = 16.0;
/// Line width in pixels.
uniform float line_px = 1.1;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// 1 on a grid line, 0 between (anti-aliased, constant pixel width).
float grid_line(vec2 p) {
	vec2 q = p / grid;
	vec2 d = abs(fract(q - 0.5) - 0.5) / max(fwidth(q), vec2(1e-5));
	return 1.0 - clamp(min(d.x, d.y) / line_px, 0.0, 1.0);
}

void fragment() {
	// Dissolve the outer edge with interleaved-gradient-noise dithering (stays opaque: no sorting issues).
	float dist = length(world_pos - map_center);
	float edge = 1.0 - smoothstep(radius - fade, radius, dist);
	float cut = smoothstep(cut_distance - cut_soft, cut_distance, length(world_pos - CAMERA_POSITION_WORLD));
	float noise = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
	if (edge * cut <= noise) {
		discard;
	}

	vec3 n = normalize(cross(dFdx(world_pos), dFdy(world_pos)));
	vec3 w = pow(abs(n), vec3(4.0));
	w /= (w.x + w.y + w.z);
	float line = grid_line(world_pos.yz) * w.x + grid_line(world_pos.xz) * w.y + grid_line(world_pos.xy) * w.z;

	vec3 to_cam = normalize(CAMERA_POSITION_WORLD - world_pos);
	float facing = abs(dot(n, to_cam));
	float rim = pow(1.0 - facing, 3.0);
	// Facets lit a little by a fixed "up and over" direction plus how directly they face the camera.
	float key = 0.5 + 0.5 * abs(dot(n, normalize(vec3(0.3, 1.0, 0.2))));
	vec3 base = fill_color.rgb * (0.55 + 0.45 * facing) * key;

	ALBEDO = base + line_color.rgb * clamp(line * 0.9 + rim * 0.35, 0.0, 1.0);
}
