// Lit smoke puffs: soft noise discs that catch the sun.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, shadows_disabled, diffuse_lambert_wrap, specular_disabled;

#include "res://shaders/fx_common.gdshaderinc"

uniform sampler2D noise_texture : filter_linear_mipmap, repeat_enable;
uniform vec4 tint : source_color = vec4(0.32, 0.31, 0.30, 1.0);
uniform float density = 0.85;
uniform float softness = 1.2;
uniform float erosion = 0.5;
// Underside glow from the fire below, early in the smoke's life.
uniform float ember_glow = 1.5;

varying float seed;
varying float age;

void vertex() {
	fx_billboard(MODELVIEW_MATRIX, VIEW_MATRIX, INV_VIEW_MATRIX, MODEL_MATRIX, INSTANCE_CUSTOM.x);
	seed = fx_hash(float(INSTANCE_ID) + 4.71);
	age = INSTANCE_CUSTOM.y;
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float r = length(p);
	vec2 nuv = UV * 0.55 + vec2(seed * 5.3, seed * 9.7) + vec2(TIME * 0.015, -TIME * 0.03);
	float n = texture(noise_texture, nuv).r;
	float n2 = texture(noise_texture, nuv * 2.7).r;
	float shape = 1.0 - r + (n - 0.5) * 0.8 + (n2 - 0.5) * 0.3;
	float erode = 0.1 + age * erosion;
	float a = smoothstep(erode, erode + 0.45, shape);
	// Fake volume: a normal that bulges toward the camera, so the sun shades the puff.
	vec3 bulge = normalize(vec3(p * 0.9, sqrt(max(0.0, 1.0 - r * r)) + 0.35));
	NORMAL = bulge;
	ALBEDO = tint.rgb * COLOR.rgb * (0.85 + n * 0.3);
	EMISSION = vec3(1.0, 0.38, 0.08) * ember_glow * pow(1.0 - smoothstep(0.0, 0.18, age), 2.0) * (0.5 + n);
	float fade_in = smoothstep(0.0, 0.08, age);
	float fade_out = 1.0 - smoothstep(0.55, 1.0, age);
	float soft = fx_soft_fade(SCREEN_UV, VERTEX, INV_PROJECTION_MATRIX, softness);
	ALPHA = clamp(a * density * fade_in * fade_out * soft * COLOR.a, 0.0, 1.0);
}
