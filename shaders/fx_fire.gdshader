// Fireball and flame billows: noise-eroded soft discs with internal structure. Colour follows a temperature
// ramp that stays saturated orange under the AgX tonemapper; only the hottest cores approach yellow-white.
shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_never, cull_disabled, shadows_disabled;

#include "res://shaders/fx_common.gdshaderinc"

uniform sampler2D noise_texture : filter_linear_mipmap, repeat_enable;
uniform float emission = 1.7;
// Lifetime fraction at which the flame has cooled into smoke.
uniform float burnout = 0.55;
uniform vec4 smoke_color : source_color = vec4(0.17, 0.155, 0.145, 1.0);
uniform float erosion = 0.55;
uniform float softness = 0.6;
uniform float alpha_scale = 1.0;
// Width of the eroded edge: small is a crisp billow, large a soft flame lick.
uniform float edge_softness = 0.42;
// 0 = orange fire, 1 = propane-blue core for the vent jet.
uniform float blue_core = 0.0;

varying float seed;
varying float age;

vec3 fire_ramp(float t) {
	// t: 0 = cooling dull red, 1 = hottest yellow.
	vec3 c = mix(vec3(0.22, 0.05, 0.02), vec3(0.95, 0.24, 0.03), smoothstep(0.0, 0.4, t));
	c = mix(c, vec3(1.0, 0.5, 0.08), smoothstep(0.4, 0.75, t));
	c = mix(c, vec3(1.0, 0.82, 0.45), smoothstep(0.82, 1.0, t));
	return c;
}

void vertex() {
	fx_billboard(MODELVIEW_MATRIX, VIEW_MATRIX, INV_VIEW_MATRIX, MODEL_MATRIX, INSTANCE_CUSTOM.x);
	seed = fx_hash(float(INSTANCE_ID) + 1.37);
	age = INSTANCE_CUSTOM.y;
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float r = length(p);
	vec2 drift = vec2(seed * 7.1, seed * 3.3) + vec2(0.0, -TIME * 0.12 - age * 0.4);
	float n = texture(noise_texture, UV * 0.6 + drift).r;
	float n2 = texture(noise_texture, UV * 1.7 + drift * 1.3 + vec2(TIME * 0.07, 0.0)).r;
	float n3 = texture(noise_texture, UV * 3.9 - drift * 0.7).r;
	float detail = n * 0.62 + n2 * 0.28 + n3 * 0.1;
	float shape = 1.0 - r * 1.05 + (detail - 0.5) * 0.85;
	float erode = age * erosion;
	float a = smoothstep(erode, erode + edge_softness, shape);

	float life_heat = clamp(1.0 - age / burnout, 0.0, 1.0);
	// Cores of each puff stay hotter than their ragged edges; noise breaks it into licks.
	float local_heat = clamp(life_heat * (0.45 + 0.9 * smoothstep(0.0, 0.7, shape - erode)) + (n3 - 0.5) * 0.25, 0.0, 1.0);
	vec3 fire = fire_ramp(local_heat) * emission * (0.25 + 1.1 * local_heat * local_heat + 0.25 * detail);
	if (blue_core > 0.0) {
		vec3 blue = vec3(0.18, 0.35, 1.0) * emission;
		fire = mix(fire, blue, blue_core * smoothstep(0.85, 1.0, life_heat) * smoothstep(0.3, 0.8, shape));
	}
	float smoke_mix = smoothstep(burnout * 0.6, burnout * 1.05, age + (seed - 0.5) * 0.15);
	vec3 smoke = smoke_color.rgb * (0.7 + detail * 0.6);
	ALBEDO = mix(fire, smoke, smoke_mix);
	float fade_in = smoothstep(0.0, 0.03, age);
	float fade_out = 1.0 - smoothstep(0.65, 1.0, age);
	float soft = fx_soft_fade(SCREEN_UV, VERTEX, INV_PROJECTION_MATRIX, softness);
	// Cooled puffs thin out and hand over to the lit smoke particles, rather than lingering as flat dark blobs.
	float smoke_thin = 1.0 - smoke_mix * 0.55;
	ALPHA = clamp(a * fade_in * fade_out * soft * smoke_thin * COLOR.a * alpha_scale, 0.0, 1.0);
}
