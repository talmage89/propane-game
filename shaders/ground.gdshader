// Ground surfaces of the suburb slab. Patterns are drawn procedurally in world space so huge meshes need no UVs:
// 0 = lawn (soft mottling and faint mowing stripes), 1 = asphalt (speckled), 2 = concrete (joint lines),
// 3 = road paint, 4 = slab edge (the cut side of the diorama).
shader_type spatial;
render_mode cull_back;

#include "res://shaders/reveal.gdshaderinc"

uniform int pattern = 0;
uniform vec4 base_color : source_color = vec4(0.45, 0.62, 0.32, 1.0);
uniform vec4 alt_color : source_color = vec4(0.40, 0.57, 0.29, 1.0);
uniform sampler2D noise_texture : filter_linear_mipmap, repeat_enable;
uniform float joint_spacing = 1.6;
uniform float roughness_value = 0.9;
// Direction of concrete joints (world XZ), so sidewalk joints run across the walk.
uniform vec2 joint_axis = vec2(1.0, 0.0);

varying vec3 world_pos;
varying vec3 world_normal;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	float glow = reveal_apply(world_pos);
	vec2 p = world_pos.xz;
	float n1 = texture(noise_texture, p * 0.013).r;
	float n2 = texture(noise_texture, p * 0.071 + 0.3).r;
	float n3 = texture(noise_texture, p * 0.41 + 0.7).r;
	vec3 c = base_color.rgb;
	float rough = roughness_value;
	if (pattern == 0) {
		c = mix(base_color.rgb, alt_color.rgb, smoothstep(0.35, 0.65, n1));
		c *= 0.9 + 0.2 * n2;
		// Mowing stripes, very faint.
		float stripe = step(0.5, fract(p.x / 3.2 + n1 * 0.15));
		c *= 1.0 + (stripe - 0.5) * 0.06;
	} else if (pattern == 1) {
		c *= 0.85 + 0.25 * n2;
		c *= 0.92 + 0.16 * step(0.72, n3);
	} else if (pattern == 2) {
		c *= 0.94 + 0.1 * n2;
		float f = fract(dot(p, normalize(joint_axis)) / joint_spacing);
		float meters = min(f, 1.0 - f) * joint_spacing;
		float aa = fwidth(meters) + 0.004;
		float joint = 1.0 - smoothstep(0.012, 0.012 + aa, meters);
		c *= mix(1.0, 0.8, joint);
	} else if (pattern == 3) {
		c *= 0.92 + 0.12 * n3;
		rough = 0.6;
	} else if (pattern == 4) {
		// The slab's cut edge: layered like a diorama base.
		float band = step(0.5, fract(world_pos.y * 25.0));
		c = mix(base_color.rgb, alt_color.rgb, band * 0.3);
	}
	ALBEDO = c;
	ROUGHNESS = rough;
	SPECULAR = 0.3;
	EMISSION = reveal_glow.rgb * glow * 6.0;
}
