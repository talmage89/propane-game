// The soft ring of light on the ground under an ammo pickup, so it reads from a distance.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

#include "res://shaders/reveal.gdshaderinc"

uniform vec4 color : source_color = vec4(1.0, 0.8, 0.3, 1.0);
instance uniform float strength = 1.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Outside the transition ring it is discarded like the rest of the suburb.
	reveal_apply(world_pos);
	float r = length(UV - vec2(0.5)) * 2.0;
	float ring = smoothstep(0.55, 0.8, r) * (1.0 - smoothstep(0.8, 1.0, r));
	float fill = (1.0 - smoothstep(0.0, 0.8, r)) * 0.35;
	ALBEDO = color.rgb * (ring + fill) * strength * (0.8 + 0.2 * sin(TIME * 3.0));
}
