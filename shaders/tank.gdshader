// Propane tank PBR material. The source normal map uses the DirectX convention, so green is flipped here.
shader_type spatial;
render_mode cull_back;

#include "res://shaders/reveal.gdshaderinc"

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D orm_texture : hint_default_white, filter_linear_mipmap_anisotropic;
uniform float normal_strength = 1.0;
// Soot from nearby blasts darkens the tank.
instance uniform float soot = 0.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float glow = reveal_apply(world_pos);
	vec3 orm = texture(orm_texture, UV).rgb;
	vec3 albedo = texture(albedo_texture, UV).rgb;
	ALBEDO = mix(albedo, albedo * vec3(0.08, 0.075, 0.07), clamp(soot, 0.0, 1.0));
	AO = orm.r;
	AO_LIGHT_AFFECT = 0.3;
	ROUGHNESS = mix(orm.g, 0.9, soot);
	METALLIC = orm.b;
	vec3 n = texture(normal_texture, UV).rgb;
	NORMAL_MAP = vec3(n.r, 1.0 - n.g, n.b);
	NORMAL_MAP_DEPTH = normal_strength;
	EMISSION = reveal_glow.rgb * glow * 6.0;
}
