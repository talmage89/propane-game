// Soft glow sprite (blast core flash, muzzle flash, vent nozzle). Premultiplied alpha: `coverage` 0 is purely
// additive, higher values also hide what is behind, so the glow still reads against the white void and pale walls.
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled, shadows_disabled;

#include "res://shaders/fx_common.gdshaderinc"

uniform vec4 color : source_color = vec4(1.0, 0.8, 0.5, 1.0);
uniform float intensity = 8.0;
uniform float opacity = 1.0;
uniform float coverage = 0.0;
// Whitens the centre of the sprite, for a hot core inside a coloured halo.
uniform float core_heat = 0.0;
uniform sampler2D dot_texture : source_color, filter_linear_mipmap;

void vertex() {
	fx_billboard(MODELVIEW_MATRIX, VIEW_MATRIX, INV_VIEW_MATRIX, MODEL_MATRIX, 0.0);
}

void fragment() {
	float a = texture(dot_texture, UV).a;
	vec3 c = mix(color.rgb, vec3(1.0, 0.96, 0.88), smoothstep(0.45, 0.95, a) * core_heat);
	ALBEDO = c * intensity * a * opacity;
	ALPHA = clamp(a * opacity * coverage, 0.0, 1.0);
}
