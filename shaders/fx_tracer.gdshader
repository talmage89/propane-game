// Bullet tracer: a camera-facing ribbon along the node's Y axis, hot at the head and fading to the tail.
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 color : source_color = vec4(1.0, 0.82, 0.55, 1.0);
uniform float intensity = 3.0;
uniform float coverage = 0.75;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	vec3 axis = MODEL_MATRIX[1].xyz;
	float len = length(axis);
	vec3 dir = axis / max(len, 0.0001);
	float width = length(MODEL_MATRIX[0].xyz);
	vec3 to_cam = normalize(CAMERA_POSITION_WORLD - origin);
	vec3 side = normalize(cross(dir, to_cam));
	vec3 world = origin + dir * VERTEX.y * len + side * VERTEX.x * width;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	float across = 1.0 - abs(UV.x * 2.0 - 1.0);
	float along = 1.0 - UV.y;
	float a = pow(across, 2.0) * pow(along, 1.5);
	ALBEDO = color.rgb * intensity * a;
	ALPHA = clamp(a * coverage, 0.0, 1.0);
}
