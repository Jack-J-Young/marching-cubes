# Generates the pencil hatch textures used by the terrain toon shader. Run from the project folder:
#   godot --headless --path . -s res://tools/generate_hatch_textures.gd
# Then open the editor (or run with --import) so Godot imports the PNGs.
#
# Each texture is a seamless 256x256 tile: white paper, dark strokes. Strokes run at +-45 degrees, so a line
# family with a spacing that divides 256 wraps perfectly; along-stroke pressure and gaps come from 1D value noise
# with a period of 256 for the same reason.
extends SceneTree

const SIZE := 256

func _init() -> void:
	DirAccess.make_dir_recursive_absolute("res://textures")
	# Level 1 (light shade): single direction, sparse, light pressure.
	_save(_hatch([{"dir": 1, "spacing": 8, "width": 1.1, "pressure": 0.55, "seed": 1}]), "res://textures/hatch_light.png")
	# Level 2 (dark shade): denser cross-hatch, heavier pressure.
	_save(_hatch([
		{"dir": 1, "spacing": 6, "width": 1.3, "pressure": 0.8, "seed": 2},
		{"dir": -1, "spacing": 8, "width": 1.1, "pressure": 0.65, "seed": 3},
	]), "res://textures/hatch_dark.png")
	quit()

func _save(img: Image, path: String) -> void:
	var err := img.save_png(path)
	print("%s -> %s" % [path, error_string(err)])

# layers: stroke families; dir 1 = along x+y (strokes "/"), -1 = along x-y ("\")
func _hatch(layers: Array) -> Image:
	var ink := PackedFloat32Array()
	ink.resize(SIZE * SIZE)
	for layer in layers:
		var rng := RandomNumberGenerator.new()
		rng.seed = layer["seed"]
		var spacing: int = layer["spacing"]
		var lines := SIZE / spacing
		# Per stroke: pressure, sideways wobble phase, and a 1D noise table along the stroke (period SIZE).
		var pressure := PackedFloat32Array()
		var phase := PackedFloat32Array()
		var tables: Array[PackedFloat32Array] = []
		for i in lines:
			pressure.append(layer["pressure"] * rng.randf_range(0.7, 1.0))
			phase.append(rng.randf() * TAU)
			var t := PackedFloat32Array()
			for k in 32:
				t.append(rng.randf())
			tables.append(t)
		for y in SIZE:
			for x in SIZE:
				# Across-stroke coordinate (wraps every SIZE) and along-stroke coordinate.
				var across := posmod(x + y if layer["dir"] == 1 else x - y, SIZE)
				var along := posmod(x - y if layer["dir"] == 1 else x + y, SIZE)
				var line := int(floor(float(across) / spacing + 0.5)) % lines
				var wobble := 0.35 * sin(along * TAU / SIZE * 2.0 + phase[line])
				var dist: float = abs(float(across) - line * spacing - wobble)
				dist = min(dist, abs(float(across) - SIZE - line * spacing - wobble))
				# Distance is measured along the diagonal: divide by sqrt(2) for pixels across the stroke.
				var w: float = layer["width"]
				var core := clampf(1.0 - (dist / 1.41421 - w * 0.5) / 0.75, 0.0, 1.0)
				if core <= 0.0:
					continue
				var n := _noise(tables[line], along)
				var gap := smoothstep(0.12, 0.3, n) # occasional breaks where the pencil lifts
				var p := pressure[line] * (0.75 + 0.25 * n) * gap
				var i := y * SIZE + x
				ink[i] = 1.0 - (1.0 - ink[i]) * (1.0 - core * p)
	var img := Image.create(SIZE, SIZE, false, Image.FORMAT_RGB8)
	for y in SIZE:
		for x in SIZE:
			var v := 1.0 - ink[y * SIZE + x]
			img.set_pixel(x, y, Color(v, v, v))
	return img

# Smooth 1D value noise over 32 knots, period SIZE.
func _noise(table: PackedFloat32Array, t: float) -> float:
	var u := t / SIZE * table.size()
	var i := int(floor(u))
	var f := u - i
	var a := table[i % table.size()]
	var b := table[(i + 1) % table.size()]
	return lerp(a, b, f * f * (3.0 - 2.0 * f))
