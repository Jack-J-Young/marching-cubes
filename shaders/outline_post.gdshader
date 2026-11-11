// Screen-space outlines with a fixed pixel width, from the depth buffer (works on Forward+ and Compatibility).
//
// Drawn by a full-screen quad parented to the camera (see scenes/node_3d.tscn, Camera3D/OutlinePost). For each
// pixel it looks at neighbours `line_width` pixels away:
//   - silhouettes: a neighbour is much nearer (relative depth jump), so this pixel is just behind an edge. Lines are
//     drawn on the far side only, so they are line_width pixels wide at any distance.
//   - creases: the inverse depth (1 / z, which is linear across the screen for flat surfaces, so planes never trigger)
//     bends sharply, i.e. a ridge or a valley in the terrain.
// Lines fade out with distance so the far terrain doesn't turn into a scribble.
shader_type spatial;
render_mode unshaded, fog_disabled, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled;

uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;

uniform vec4 line_color : source_color = vec4(0.09, 0.08, 0.07, 1.0);
/// Line width in screen pixels.
uniform float line_width : hint_range(0.5, 8.0, 0.5) = 1.5;
/// Silhouette: neighbour nearer by this fraction of this pixel's depth.
uniform float silhouette_threshold : hint_range(0.0, 1.0) = 0.06;
/// Crease: bend in inverse depth relative to its value. Lower = more crease lines.
uniform float crease_threshold : hint_range(0.0, 0.2) = 0.02;
/// Crease lines are a little lighter than silhouettes.
uniform float crease_strength : hint_range(0.0, 1.0) = 0.6;
/// Lines fade out between fade_start and fade_end (world units from the camera).
uniform float fade_start = 250.0;
uniform float fade_end = 600.0;

void vertex() {
	// Cover the screen; z = 1 is the near plane with reversed depth, so the quad is never clipped.
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

float linear_depth(vec2 uv, mat4 inv_projection) {
	float d = texture(depth_texture, uv).r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	vec3 ndc = vec3(uv, d) * 2.0 - 1.0;
#else
	vec3 ndc = vec3(uv * 2.0 - 1.0, d);
#endif
	vec4 view = inv_projection * vec4(ndc, 1.0);
	return -view.z / view.w;
}

void fragment() {
	vec2 o = line_width / VIEWPORT_SIZE;
	mat4 ip = INV_PROJECTION_MATRIX;
	float c = linear_depth(SCREEN_UV, ip);
	float l = linear_depth(SCREEN_UV - vec2(o.x, 0.0), ip);
	float r = linear_depth(SCREEN_UV + vec2(o.x, 0.0), ip);
	float u = linear_depth(SCREEN_UV - vec2(0.0, o.y), ip);
	float d = linear_depth(SCREEN_UV + vec2(0.0, o.y), ip);

	float nearest = min(min(l, r), min(u, d));
	float silhouette = step(silhouette_threshold, (c - nearest) / c);

	// Second difference of 1/z: zero across any plane, large across a fold.
	float ic = 1.0 / c;
	float bend = max(abs(1.0 / l + 1.0 / r - 2.0 * ic), abs(1.0 / u + 1.0 / d - 2.0 * ic)) / ic;
	float crease = step(crease_threshold, bend) * crease_strength;

	float fade = 1.0 - smoothstep(fade_start, fade_end, min(c, nearest));
	ALBEDO = line_color.rgb;
	ALPHA = max(silhouette, crease) * fade * line_color.a;
}
