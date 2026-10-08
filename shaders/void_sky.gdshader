// Bright sterile sky for the void at golden hour: a sun just above the horizon, with a warm glow on its side.
shader_type sky;

uniform vec4 zenith_color : source_color = vec4(0.8, 0.86, 0.96, 1.0);
uniform vec4 horizon_color : source_color = vec4(1.0, 0.95, 0.9, 1.0);
uniform float brightness = 1.45;
uniform vec4 ground_color : source_color = vec4(0.95, 0.95, 0.955, 1.0);
uniform float horizon_softness = 0.22;
uniform vec4 sun_color : source_color = vec4(1.0, 0.72, 0.42, 1.0);
uniform float sun_size = 0.035;
uniform float sun_glow = 0.35;
// The band of warm light along the horizon toward the sun, and how far round the sky it reaches.
uniform vec4 warm_color : source_color = vec4(1.0, 0.66, 0.38, 1.0);
uniform float horizon_warmth = 0.75;
uniform float warmth_spread = 2.5;

void sky() {
	float y = EYEDIR.y;
	vec3 c = mix(horizon_color.rgb, zenith_color.rgb, smoothstep(0.0, horizon_softness * 3.0, y));
	c = mix(ground_color.rgb, c, smoothstep(-0.04, 0.02, y));
	if (LIGHT0_ENABLED) {
		vec2 eye_flat = EYEDIR.xz / max(length(EYEDIR.xz), 0.0001);
		vec2 sun_flat = LIGHT0_DIRECTION.xz / max(length(LIGHT0_DIRECTION.xz), 0.0001);
		float toward_sun = pow(max(dot(eye_flat, sun_flat) * 0.5 + 0.5, 0.0), warmth_spread);
		float low = 1.0 - smoothstep(-0.02, 0.45, y);
		c = mix(c, warm_color.rgb, toward_sun * low * horizon_warmth);
		float s = dot(EYEDIR, LIGHT0_DIRECTION);
		float disc = smoothstep(1.0 - sun_size * 0.02, 1.0 - sun_size * 0.012, s);
		float glow = pow(max(s, 0.0), 48.0) * sun_glow + pow(max(s, 0.0), 6.0) * sun_glow * 0.25;
		c += sun_color.rgb * (disc * 6.0 + glow) * LIGHT0_ENERGY * 0.4;
	}
	COLOR = c * brightness;
}
