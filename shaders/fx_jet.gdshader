// The solid part of a vent jet: an axis-aligned flame ribbon along the node's +Z, turned to face the camera.
// Blue and narrow at the nozzle, swelling into turbulent orange and breaking up at the tip.
shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_never, cull_disabled, shadows_disabled;

uniform sampler2D noise_texture : filter_linear_mipmap, repeat_enable;
uniform float length_m = 0.7;
uniform float width_m = 0.24;
uniform float emission = 2.0;
uniform float flicker = 1.0;

varying float along;
varying float facing;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	vec3 dir = normalize(MODEL_MATRIX[2].xyz);
	vec3 to_cam = normalize(CAMERA_POSITION_WORLD - origin);
	vec3 side = cross(dir, to_cam);
	float side_len = length(side);
	side = side_len > 0.001 ? side / side_len : normalize(cross(dir, vec3(0.0, 1.0, 0.1)));
	// Viewed end-on the ribbon collapses, so fade it out and let the nozzle glow carry the look.
	facing = clamp(side_len * 2.5, 0.0, 1.0);
	along = VERTEX.y + 0.5;
	float len = length_m * flicker;
	vec3 world = origin + dir * along * len + side * VERTEX.x * width_m;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	float y = UV.y;
	y = 1.0 - y;
	float x = (UV.x - 0.5) * 2.0;
	// Width profile: tight throat, bulge, then taper.
	float profile = mix(0.18, 1.0, smoothstep(0.0, 0.35, y)) * (1.0 - smoothstep(0.55, 1.0, y) * 0.55);
	float n = texture(noise_texture, vec2(UV.x * 0.8, y * 0.9 - TIME * 3.2)).r;
	float n2 = texture(noise_texture, vec2(UV.x * 1.9 + 0.3, y * 2.1 - TIME * 5.1)).r;
	float turb = (n * 0.65 + n2 * 0.35 - 0.5);
	float edge = abs(x + turb * 0.5 * y) / max(profile, 0.001);
	float body = 1.0 - smoothstep(0.35, 1.0, edge + turb * 0.6 * y);
	float tip = 1.0 - smoothstep(0.55, 1.0, y + turb * 0.35);
	float a = body * tip * smoothstep(0.0, 0.04, y);
	float heat = (1.0 - y) * (1.0 - edge * 0.6);
	vec3 blue = vec3(0.2, 0.38, 1.0);
	vec3 white = vec3(1.0, 0.86, 0.6);
	vec3 orange = vec3(1.0, 0.42, 0.06);
	vec3 red = vec3(0.6, 0.1, 0.02);
	vec3 c = mix(red, orange, smoothstep(0.1, 0.45, heat));
	c = mix(c, white, smoothstep(0.55, 0.85, heat));
	c = mix(c, blue, (1.0 - smoothstep(0.05, 0.2, y)) * 0.85);
	ALBEDO = c * emission * (0.7 + heat * 0.8);
	ALPHA = clamp(a * facing, 0.0, 1.0);
}
