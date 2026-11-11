// Translucent dig-radius preview: a faint fill with a bright fresnel rim, drawn on top of the terrain
// so the whole sphere is readable even where it is buried. `fade` is driven by Player for the fade-out.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_never, depth_test_disabled, blend_mix, shadows_disabled;

uniform vec4 color : source_color = vec4(1.0, 0.82, 0.3, 1.0);
uniform float fade : hint_range(0.0, 1.0) = 1.0;

void fragment() {
	float rim = 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0);
	ALBEDO = color.rgb;
	ALPHA = fade * clamp(0.1 + pow(rim, 3.0) * 0.85, 0.0, 1.0);
}
