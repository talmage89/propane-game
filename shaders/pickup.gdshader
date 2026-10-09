// Ammo pickup: flat colours, plus a build-up from the base when it (re)appears and the suburb transition reveal.
shader_type spatial;
render_mode cull_back, depth_draw_opaque;

#include "res://shaders/reveal.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform float roughness : hint_range(0.0, 1.0) = 0.6;
uniform float metallic : hint_range(0.0, 1.0) = 0.2;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// 0 = nothing shown, 1 = whole. The cut sweeps up from build_base over build_height metres, with a glowing edge.
instance uniform float build = 1.0;
instance uniform float build_base = 0.0;
instance uniform float build_height = 1.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float glow = reveal_apply(world_pos);
	float cut = build_base + build * (build_height + 0.2) - 0.1;
	float below = cut - world_pos.y;
	if (below < 0.0) {
		discard;
	}
	float edge = build < 0.999 ? 1.0 - smoothstep(0.0, 0.12, below) : 0.0;
	ALBEDO = albedo_color.rgb;
	ROUGHNESS = roughness;
	METALLIC = metallic;
	EMISSION = emission_color.rgb + reveal_glow.rgb * (glow + edge) * 6.0;
}
