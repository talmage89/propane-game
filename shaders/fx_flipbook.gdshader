// Plays the pre-rendered Blender fireball flipbook on an upright camera-facing quad.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

#include "res://shaders/fx_common.gdshaderinc"

uniform sampler2D flipbook : source_color, filter_linear;
uniform float columns = 8.0;
uniform float rows = 8.0;
uniform float progress = 0.0;
uniform float emission = 3.5;
uniform float softness = 0.5;
uniform float opacity = 1.0;

void vertex() {
	// Y-locked billboard: faces the camera horizontally but stays upright.
	vec3 up = vec3(0.0, 1.0, 0.0);
	vec3 to_cam = CAMERA_POSITION_WORLD - MODEL_MATRIX[3].xyz;
	to_cam.y = 0.0;
	vec3 forward = normalize(to_cam + vec3(0.0001, 0.0, 0.0));
	vec3 right = normalize(cross(up, forward));
	float sx = length(MODEL_MATRIX[0].xyz);
	float sy = length(MODEL_MATRIX[1].xyz);
	vec3 world = MODEL_MATRIX[3].xyz + right * VERTEX.x * sx + up * VERTEX.y * sy;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	float frames = columns * rows;
	float f = clamp(progress, 0.0, 0.9999) * frames;
	float frame_a = floor(f);
	float frame_b = min(frame_a + 1.0, frames - 1.0);
	float blend = fract(f);
	vec2 cell = vec2(1.0 / columns, 1.0 / rows);
	vec2 uv_a = (vec2(mod(frame_a, columns), floor(frame_a / columns)) + UV) * cell;
	vec2 uv_b = (vec2(mod(frame_b, columns), floor(frame_b / columns)) + UV) * cell;
	vec4 a = texture(flipbook, uv_a);
	vec4 b = texture(flipbook, uv_b);
	vec4 c = mix(a, b, blend);
	ALBEDO = c.rgb * emission;
	float soft = fx_soft_fade(SCREEN_UV, VERTEX, INV_PROJECTION_MATRIX, softness);
	// Soften the frame's hard edges: the flat ground cut at the bottom and the sides.
	float edges = smoothstep(0.0, 0.08, 1.0 - UV.y) * smoothstep(0.0, 0.05, UV.x) * smoothstep(0.0, 0.05, 1.0 - UV.x);
	ALPHA = clamp(c.a * opacity * soft * edges, 0.0, 1.0);
}
