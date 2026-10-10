// Shared material for all suburb geometry: flat color or colormap texture, plus the transition reveal.
shader_type spatial;
render_mode cull_back, depth_draw_opaque;

#include "res://shaders/reveal.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_texture = false;
uniform float roughness : hint_range(0.0, 1.0) = 0.85;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float specular : hint_range(0.0, 1.0) = 0.35;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// Per-instance tint for cheap color variation. Only surfaces marked tintable take it fully; the rest take a
// faint share so instances still vary slightly.
instance uniform vec4 instance_tint : source_color = vec4(1.0);
uniform bool tintable = false;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float glow = reveal_apply(world_pos);
	vec3 base = albedo_color.rgb;
	if (use_texture) {
		base *= texture(albedo_texture, UV).rgb;
	}
	ALBEDO = base * mix(vec3(1.0), instance_tint.rgb, tintable ? 1.0 : 0.0);
	ROUGHNESS = roughness;
	METALLIC = metallic;
	SPECULAR = specular;
	EMISSION = emission_color.rgb + reveal_glow.rgb * glow * 6.0;
}
