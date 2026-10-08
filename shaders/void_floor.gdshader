// The infinite white floor of the void: a faint metric grid that fades into the horizon haze.
shader_type spatial;
render_mode cull_disabled;

uniform vec4 floor_color : source_color = vec4(0.80, 0.80, 0.81, 1.0);
uniform vec4 line_color : source_color = vec4(0.70, 0.72, 0.76, 1.0);
uniform vec4 major_line_color : source_color = vec4(0.62, 0.65, 0.71, 1.0);
uniform float minor_spacing = 1.0;
uniform float major_spacing = 10.0;
uniform float minor_width = 0.012;
uniform float major_width = 0.03;
uniform float fade_start = 25.0;
uniform float fade_end = 260.0;

varying vec3 world_pos;

float grid_line(vec2 p, float spacing, float width) {
	vec2 cell = p / spacing;
	vec2 deriv = fwidth(cell);
	vec2 dist = abs(fract(cell - 0.5) - 0.5) * spacing;
	vec2 aa = deriv * spacing * 1.2;
	vec2 line = 1.0 - smoothstep(vec2(width * 0.5), vec2(width * 0.5) + aa, dist);
	// Fade lines out where cells shrink to a few pixels, before they alias into moire.
	float visibility = 1.0 - smoothstep(0.08, 0.3, max(deriv.x, deriv.y));
	return max(line.x, line.y) * visibility;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float d = length(world_pos.xz - CAMERA_POSITION_WORLD.xz);
	float fade = 1.0 - smoothstep(fade_start, fade_end, d);
	float minor = grid_line(world_pos.xz, minor_spacing, minor_width) * 0.45;
	float major = grid_line(world_pos.xz, major_spacing, major_width) * 0.8;
	vec3 c = floor_color.rgb;
	c = mix(c, line_color.rgb, minor * fade);
	c = mix(c, major_line_color.rgb, major * fade);
	ALBEDO = c;
	ROUGHNESS = 0.55;
	SPECULAR = 0.25;
}
