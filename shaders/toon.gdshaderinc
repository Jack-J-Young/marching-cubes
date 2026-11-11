// Toon / pencil look shared by the terrain and ships: posterised base colour, hatched shade bands and the
// custom light() function. The including shader's vertex() must set v_hatch_pos and v_hatch_normal: the position
// and normal in the space the strokes are mapped in (world for terrain; object space for ships, so the strokes are
// painted on and move with them).
//
// Toon / pencil shading: the directional "key" light is banded into three levels in light(): lit (plain paper),
// light shade (hatch_light strokes) and dark shade (hatch_dark strokes). Other lights (the small camera lights) add a
// plain two-tone band. Ambient light is off so the shade bands stay readable. Style values live on the template
// material (materials/terrain_toon.tres).
//
// Hatching is surface-anchored by default: the strokes are mapped onto the surface from three axes (triplanar) in
// the hatch space above, so they stay put when the camera moves or turns. To keep strokes about the same size on screen at any distance, the
// texture scale is picked per pixel from how big a screen pixel is there, in powers of two, cross-fading between
// the two nearest scales (a stroke is between 1x and 2x hatch_pixel_scale pixels). hatch_world_space = false
// switches to screen-space hatching (fixed to the screen, exact pixel size).
//
// Colour banding: the base (vertex) colour is posterised in HSV (color_steps brightness levels, saturation_steps,
// hue_steps hues) for flat, cel-style colour regions.

render_mode ambient_light_disabled;

group_uniforms toon;
/// Light shade level: grayscale tile, white = paper, dark = stroke.
uniform sampler2D hatch_light : hint_default_white, repeat_enable, filter_linear_mipmap_anisotropic;
/// Dark shade level.
uniform sampler2D hatch_dark : hint_default_white, repeat_enable, filter_linear_mipmap_anisotropic;
/// Strokes attached to the terrain (true) or to the screen (false).
uniform bool hatch_world_space = true;
/// Screen pixels per hatch texel (2 = strokes twice as big). World-space strokes vary between 1x and 2x this.
uniform float hatch_pixel_scale : hint_range(0.25, 8.0) = 1.0;
/// Triplanar blend sharpness between the three projection axes (higher = harder seams, less stroke mixing).
uniform float triplanar_sharpness : hint_range(1.0, 16.0) = 6.0;
/// Key-light N.L above this is fully lit.
uniform float light_band : hint_range(0.0, 1.0) = 0.45;
/// Key-light N.L below this uses the dark hatch.
uniform float dark_band : hint_range(0.0, 1.0) = 0.15;
uniform float band_softness : hint_range(0.0, 0.2) = 0.02;
/// How dark a full-pressure stroke is (0 = invisible, 1 = black).
uniform float ink_strength : hint_range(0.0, 1.0) = 0.7;
/// Paper brightness under the dark hatch (a little grey reads as deeper shadow).
uniform float dark_paper : hint_range(0.0, 1.0) = 0.85;
/// 0 = monochrome pencil, 1 = full terrain colours.
uniform float color_amount : hint_range(0.0, 1.0) = 1.0;
/// Brightness levels the terrain colour is posterised to (0 = smooth).
uniform int color_steps : hint_range(0, 16) = 4;
/// Saturation levels (0 = smooth).
uniform int saturation_steps : hint_range(0, 16) = 3;
/// Hue steps around the colour wheel (0 = smooth).
uniform int hue_steps : hint_range(0, 36) = 0;
/// Lightens the base colour toward white paper so strokes stay readable on dark ground.
uniform float paper_lift : hint_range(0.0, 1.0) = 0.35;
/// Brightness of the camera's own lights in their lit band.
uniform float fill_light_strength : hint_range(0.0, 2.0) = 0.6;
group_uniforms;

// Hatch-space position and normal (set by the including shader's vertex()) for triplanar hatching in fragment();
// the hatch result goes to light() in v_hatch.
varying vec2 v_hatch;
varying vec3 v_hatch_pos;
varying vec3 v_hatch_normal;

vec3 rgb_to_hsv(vec3 c) {
	vec4 k = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
	vec4 p = mix(vec4(c.bg, k.wz), vec4(c.gb, k.xy), step(c.b, c.g));
	vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
	float d = q.x - min(q.w, q.y);
	float e = 1.0e-10;
	return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 hsv_to_rgb(vec3 c) {
	vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
	return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

// Snap to the centre of one of n levels over [0, 1] (n = 0 leaves it alone).
float posterize(float v, int n) {
	if (n <= 0) return v;
	float f = float(n);
	return clamp((floor(v * f) + 0.5) / f, 0.0, 1.0);
}

// Hatch values (x = light texture, y = dark; 1 = paper, 0 = full stroke) at this pixel. Sampled in fragment(), where
// mip selection is automatic, and handed to light() in v_hatch.
vec2 hatch_values(vec2 frag, float pixel_world) {
	if (!hatch_world_space) {
		vec2 suv = frag / (vec2(textureSize(hatch_light, 0)) * hatch_pixel_scale);
		return vec2(texture(hatch_light, suv).r, texture(hatch_dark, suv).r);
	}
	vec3 w = pow(abs(normalize(v_hatch_normal)), vec3(triplanar_sharpness));
	w /= (w.x + w.y + w.z);
	// Texel world size = (world size of a screen pixel here) * (wanted pixels per texel), snapped to powers of two
	// and cross-faded, so strokes stay 1x-2x hatch_pixel_scale pixels at any distance while staying on the terrain.
	float octave = log2(pixel_world * hatch_pixel_scale);
	float o = floor(octave);
	float scale_mix = octave - o;
	vec2 inv0 = 1.0 / (vec2(textureSize(hatch_light, 0)) * exp2(o));
	vec2 inv1 = inv0 * 0.5;
	vec3 q = v_hatch_pos;
	float l0 = texture(hatch_light, q.zy * inv0).r * w.x + texture(hatch_light, q.xz * inv0).r * w.y + texture(hatch_light, q.xy * inv0).r * w.z;
	float l1 = texture(hatch_light, q.zy * inv1).r * w.x + texture(hatch_light, q.xz * inv1).r * w.y + texture(hatch_light, q.xy * inv1).r * w.z;
	float d0 = texture(hatch_dark, q.zy * inv0).r * w.x + texture(hatch_dark, q.xz * inv0).r * w.y + texture(hatch_dark, q.xy * inv0).r * w.z;
	float d1 = texture(hatch_dark, q.zy * inv1).r * w.x + texture(hatch_dark, q.xz * inv1).r * w.y + texture(hatch_dark, q.xy * inv1).r * w.z;
	return vec2(mix(l0, l1, scale_mix), mix(d0, d1, scale_mix));
}

void fragment() {
	vec3 hsv = rgb_to_hsv(COLOR.rgb);
	float hue = hue_steps > 0 ? floor(hsv.x * float(hue_steps) + 0.5) / float(hue_steps) : hsv.x;
	vec3 col = hsv_to_rgb(vec3(hue, posterize(hsv.y, saturation_steps), posterize(hsv.z, color_steps)));
	float luma = dot(col, vec3(0.299, 0.587, 0.114));
	ALBEDO = mix(mix(vec3(luma), col, color_amount), vec3(1.0), paper_lift);
	ROUGHNESS = 1.0;

	// World size of one screen pixel at this depth. abs(): the projection's Y scale is negative on Vulkan (flipped Y).
	float pixel_world = 2.0 * max(-VERTEX.z, 1e-3) / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y);
	v_hatch = hatch_values(FRAGCOORD.xy, pixel_world);
}

void light() {
	float ndl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * ATTENUATION;
	if (LIGHT_IS_DIRECTIONAL) {
		// Paper everywhere; strokes darken it in the shade bands. Even fully unlit surfaces get paper + ink, so
		// shadows read as cross-hatching rather than black.
		float lit = smoothstep(light_band - band_softness, light_band + band_softness, ndl);
		float mid = smoothstep(dark_band - band_softness, dark_band + band_softness, ndl);
		vec2 h = v_hatch;
		float shade_light = mix(1.0 - ink_strength, 1.0, h.x);
		float shade_dark = mix(1.0 - ink_strength, 1.0, h.y) * dark_paper;
		float shade = mix(shade_dark, mix(shade_light, 1.0, lit), mid);
		DIFFUSE_LIGHT += LIGHT_COLOR * shade / PI;
	} else {
		DIFFUSE_LIGHT += LIGHT_COLOR * fill_light_strength * smoothstep(0.1, 0.12, ndl) / PI;
	}
}
