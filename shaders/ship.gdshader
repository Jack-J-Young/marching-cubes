// Ship blocks: the same toon / pencil look as the terrain (toon.gdshaderinc), with plain geometry.
// The pencil strokes are mapped in the ship's own (object) space, so they are painted on the hull and travel with
// it instead of sliding over it as it flies. Which faces are lit or shaded still follows the sun (light()).
shader_type spatial;

#include "res://shaders/toon.gdshaderinc"

void vertex() {
	v_hatch_pos = VERTEX;
	v_hatch_normal = NORMAL;
}
