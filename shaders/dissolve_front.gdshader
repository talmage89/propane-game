// Dissolve Run's front: a sheet of the reveal glow standing across the street where everything behind is gone,
// brightest at the ground and fading upward, with faint scan bands rising through it.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 glow : source_color = vec4(0.35, 0.75, 1.0, 1.0);
uniform float strength = 1.0;
uniform float band_speed = 0.6;

void fragment() {
	float height = 1.0 - UV.y;
	float fade = pow(1.0 - height, 3.0);
	float bands = 0.75 + 0.25 * sin((height * 18.0 - TIME * band_speed * 6.2832) );
	ALBEDO = glow.rgb * fade * bands * strength * 1.6;
	ALPHA = clamp(fade * strength, 0.0, 1.0);
}
