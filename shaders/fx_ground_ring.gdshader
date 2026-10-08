// Bright ring that races out along the ground at the moment of the blast.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform float progress = 0.0;
uniform vec4 color : source_color = vec4(1.0, 0.8, 0.6, 1.0);
uniform float intensity = 0.7;
uniform sampler2D noise_texture : filter_linear_mipmap, repeat_enable;

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float r = length(p);
	float angle = atan(p.y, p.x);
	float n = texture(noise_texture, vec2(angle * 0.3, progress * 0.2)).r;
	float width = 0.035 + 0.06 * progress;
	float ring = 1.0 - smoothstep(0.0, width, abs(r - 0.92 + n * 0.08));
	float life = 1.0 - smoothstep(0.15, 1.0, progress);
	ALBEDO = color.rgb * intensity * ring * life;
	ALPHA = ring * life * 0.5;
}
