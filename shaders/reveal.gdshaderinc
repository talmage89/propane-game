// Map transition: a ring centered on reveal_center. Geometry tagged reveal_side = +1 exists only inside the ring
// (growing it materializes a suburb, shrinking it dissolves one), -1 only outside it, and 0 is unaffected.
// reveal_side = 2 is the window of a streamed street (Dissolve Run): geometry exists only between two planes across
// the run, set for the whole frame by the global reveal_window: (direction x, direction z, back, ahead) along it.
instance uniform vec3 reveal_center : instance_index(12) = vec3(0.0);
instance uniform float reveal_radius : instance_index(13) = 100000.0;
instance uniform vec4 reveal_glow : source_color, instance_index(14) = vec4(0.35, 0.75, 1.0, 1.0);
instance uniform float reveal_side : instance_index(15) = 0.0;

global uniform vec4 reveal_window;

const float REVEAL_EDGE = 1.6;

// Discards fragments on the wrong side of the ring and returns the edge glow strength.
float reveal_apply(vec3 world_pos) {
	if (abs(reveal_side) < 0.5) {
		return 0.0;
	}
	if (reveal_side > 1.5) {
		float along = dot(world_pos.xz, reveal_window.xy);
		float inside = min(along - reveal_window.z, reveal_window.w - along);
		if (inside < 0.0) {
			discard;
		}
		return 1.0 - smoothstep(0.0, REVEAL_EDGE, inside);
	}
	float d = distance(world_pos.xz, reveal_center.xz);
	float signed_dist = (reveal_radius - d) * reveal_side;
	if (signed_dist < 0.0) {
		discard;
	}
	return 1.0 - smoothstep(0.0, REVEAL_EDGE, signed_dist);
}
