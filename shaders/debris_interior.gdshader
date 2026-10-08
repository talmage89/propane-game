// Sooty inside of the burst tank shell. Glows ember-orange for a moment after the blast.
shader_type spatial;

#include "res://shaders/reveal.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(0.035, 0.03, 0.028, 1.0);
instance uniform float heat = 0.0;

varying vec3 world_pos;
varying vec3 local_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	local_pos = VERTEX;
}

void fragment() {
	float glow = reveal_apply(world_pos);
	ALBEDO = albedo_color.rgb;
	ROUGHNESS = 0.75;
	METALLIC = 0.5;
	float flicker = 0.6 + 0.4 * sin(local_pos.x * 90.0 + local_pos.y * 70.0 + TIME * 3.0);
	EMISSION = vec3(1.0, 0.35, 0.08) * heat * heat * 8.0 * flicker + reveal_glow.rgb * glow * 6.0;
}
