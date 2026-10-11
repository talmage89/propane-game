// A subtle contrast edge only for near-black/white body tints. Presets have no next pass.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_back;

uniform vec4 edge_color : source_color = vec4(1.0);
uniform float strength = 0.0;
uniform float rim_power = 3.0;

void fragment() {
	float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), rim_power);
	ALBEDO = edge_color.rgb;
	ALPHA = rim * strength;
}
