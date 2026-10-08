// Expanding heat-distortion shell: refracts the screen behind a sphere's silhouette band.
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_back, shadows_disabled, blend_mix;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;
uniform float progress = 0.0;
uniform float strength = 0.045;

void fragment() {
	float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
	// Strongest at the rim of the sphere, where the shell is seen edge-on.
	float band = smoothstep(0.0, 0.45, 1.0 - facing) * (1.0 - smoothstep(0.75, 1.0, 1.0 - facing));
	float life = (1.0 - progress) * smoothstep(0.0, 0.08, progress);
	vec2 offset = NORMAL.xy * band * strength * life;
	ALBEDO = textureLod(screen_texture, SCREEN_UV - offset, 0.0).rgb;
	ALPHA = clamp(band * life * 4.0, 0.0, 1.0);
}
