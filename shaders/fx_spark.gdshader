// Embers and sparks: velocity-stretched hot streaks.
shader_type spatial;
render_mode blend_premul_alpha, unshaded, depth_draw_never, cull_disabled, shadows_disabled;

uniform float emission = 8.0;
uniform float stretch = 0.35;
uniform float width = 0.025;
// Premultiplied alpha: 0 is purely additive; higher values make sparks read against bright backgrounds.
uniform float coverage = 0.6;
uniform vec4 hot_color : source_color = vec4(1.0, 0.75, 0.35, 1.0);
uniform vec4 cool_color : source_color = vec4(1.0, 0.25, 0.05, 1.0);

varying float age;

void vertex() {
	// The process material aligns each particle's Y axis with its velocity; the axis length is its scale.
	vec3 axis = MODEL_MATRIX[1].xyz;
	float scale = max(length(axis), 0.0001);
	vec3 dir = axis / scale;
	vec3 origin = MODEL_MATRIX[3].xyz;
	vec3 to_cam = normalize(CAMERA_POSITION_WORLD - origin);
	vec3 side = normalize(cross(dir, to_cam));
	vec3 world = origin + dir * VERTEX.y * stretch * scale + side * VERTEX.x * width * scale;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
	age = INSTANCE_CUSTOM.y;
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float core = (1.0 - smoothstep(0.0, 1.0, abs(p.x))) * (1.0 - smoothstep(0.6, 1.0, abs(p.y)));
	vec3 c = mix(hot_color.rgb, cool_color.rgb, smoothstep(0.0, 0.7, age));
	float fade = 1.0 - smoothstep(0.6, 1.0, age);
	ALBEDO = c * emission * fade * core * COLOR.a;
	ALPHA = clamp(core * COLOR.a * fade * coverage, 0.0, 1.0);
}
