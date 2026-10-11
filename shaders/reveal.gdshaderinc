// Map transition: a ring centered on reveal_center. Geometry tagged reveal_side = +1 exists only inside the ring
// (growing it materializes a suburb, shrinking it dissolves one), -1 only outside it, and 0 is unaffected.
instance uniform vec3 reveal_center : instance_index(12) = vec3(0.0);
instance uniform float reveal_radius : instance_index(13) = 100000.0;
instance uniform vec4 reveal_glow : source_color, instance_index(14) = vec4(0.35, 0.75, 1.0, 1.0);
instance uniform float reveal_side : instance_index(15) = 0.0;

const float REVEAL_EDGE = 1.6;

// Discards fragments on the wrong side of the ring and returns the edge glow strength.
float reveal_apply(vec3 world_pos) {
	if (abs(reveal_side) < 0.5) {
		return 0.0;
	}
	float d = distance(world_pos.xz, reveal_center.xz);
	float signed_dist = (reveal_radius - d) * reveal_side;
	if (signed_dist < 0.0) {
		discard;
	}
	return 1.0 - smoothstep(0.0, REVEAL_EDGE, signed_dist);
}
