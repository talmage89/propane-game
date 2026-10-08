// Shared helpers for particle billboards.
// INSTANCE_CUSTOM from ParticleProcessMaterial: x = rotation (radians), y = age / lifetime.

uniform sampler2D fx_depth_texture : hint_depth_texture, filter_linear;

// Camera-facing billboard that keeps the particle's scale and spin.
void fx_billboard(inout mat4 modelview, mat4 view, mat4 inv_view, mat4 model, float spin) {
	mat4 world = mat4(normalize(inv_view[0]), normalize(inv_view[1]), normalize(inv_view[2]), model[3]);
	world = world * mat4(vec4(cos(spin), -sin(spin), 0.0, 0.0), vec4(sin(spin), cos(spin), 0.0, 0.0),
			vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));
	modelview = view * world * mat4(vec4(length(model[0].xyz), 0.0, 0.0, 0.0),
			vec4(0.0, length(model[1].xyz), 0.0, 0.0), vec4(0.0, 0.0, length(model[2].xyz), 0.0),
			vec4(0.0, 0.0, 0.0, 1.0));
}

// 0 where the particle meets opaque geometry, 1 once it is `softness` metres in front of it.
float fx_soft_fade(vec2 screen_uv, vec3 vertex, mat4 inv_projection, float softness) {
	float depth = textureLod(fx_depth_texture, screen_uv, 0.0).r;
	vec4 scene = inv_projection * vec4(screen_uv * 2.0 - 1.0, depth, 1.0);
	scene.xyz /= scene.w;
	// View space looks down -Z, so a positive gap means the scene is behind the particle.
	return clamp((vertex.z - scene.z) / softness, 0.0, 1.0);
}

float fx_hash(float n) {
	return fract(sin(n * 12.9898) * 43758.5453);
}
