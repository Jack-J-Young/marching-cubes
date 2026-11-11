// Shared terrain chunk shader body (included by terrain.gdshader and terrain_morph.gdshader): vertex colors plus Transvoxel LOD seams.
//
// lod_mask: bit f set when the neighbor across face f is one LOD finer. A plain uniform (not an instance
// uniform, which the Compatibility/web renderer caps at a few thousand slots): ChunkManager shares one
// material per mask value, 64 at most.
//   f = 2 * axis + side, side 0 = negative face, 1 = positive face.
// CUSTOM0.xyz: secondary-position offset for vertices within one cell of a chunk face.
// CUSTOM0.w (integer flags):
//   bits 0-5: which face each offset component belongs to (bit 2*axis = low face, 2*axis+1 = high face)
//   bits 6-8: transition face + 1 (0 = regular cell geometry)
// MORPH variant: CUSTOM1.xyz = offset from the new vertex back to the previously shown surface; the vertex slides
// from there to its real position as morph_t goes 0 -> 1 (ChunkManager animates it per commit).
//
// The toon / pencil look (colour banding, hatching, light()) comes from toon.gdshaderinc.

#include "res://shaders/toon.gdshaderinc"

uniform int lod_mask = 0;

#ifdef MORPH
uniform float morph_t = 1.0;
#endif

void vertex() {
#ifdef MORPH
	VERTEX += CUSTOM1.xyz * (1.0 - morph_t);
#endif
	int flags = int(CUSTOM0.w + 0.5);
	int transition_face = (flags >> 6) & 7;

	if (transition_face != 0 && (lod_mask & (1 << (transition_face - 1))) == 0) {
		// Transition strip for a face with no finer neighbor: collapse it so it's never rasterized.
		VERTEX = vec3(0.0);
	} else {
		int active = flags & lod_mask & 63;
		if (active != 0) {
			vec3 d = vec3(
				(active & 3) != 0 ? CUSTOM0.x : 0.0,
				(active & 12) != 0 ? CUSTOM0.y : 0.0,
				(active & 48) != 0 ? CUSTOM0.z : 0.0);
			// Project onto the tangent plane (Lengyel 4.4) so the surface keeps its shape.
			VERTEX += d - NORMAL * dot(NORMAL, d);
		}
	}

	v_hatch_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_hatch_normal = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
}
