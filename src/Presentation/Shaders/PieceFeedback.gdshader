shader_type canvas_item;
render_mode unshaded;

uniform vec4 tint : source_color = vec4(1.0, 0.12, 0.16, 1.0);
uniform bool origin_cursor = false;
uniform bool animated = true;

// The geometry is entirely local to a square control. Board type, ownership, and world
// coordinates are intentionally absent: any board projection can reuse this feedback.
void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float aa = max(fwidth(length(p)), 0.005);
	float time = animated ? TIME : 0.0;
	float pulse = animated ? 0.80 + 0.20 * sin(time * 4.0) : 1.0;
	float distance_to_shape;
	float intensity = 1.0;
	if (origin_cursor) {
		// Four corner brackets leave the vacated intersection visible.
		vec2 q = abs(p);
		float horizontal = length(vec2(max(abs(q.x - 0.47) - 0.15, 0.0), q.y - 0.62));
		float vertical = length(vec2(q.x - 0.62, max(abs(q.y - 0.47) - 0.15, 0.0)));
		distance_to_shape = min(horizontal, vertical);
	} else {
		distance_to_shape = abs(length(p) - 0.66);
		float angle = atan(p.y, p.x);
		float comet = pow(0.5 + 0.5 * cos(angle - time * 2.4), 10.0);
		float partner = pow(0.5 + 0.5 * cos(angle - time * 2.4 + PI), 16.0);
		intensity = animated ? 0.38 + 0.62 * max(comet, partner * 0.75) : 0.80;
	}
	float core = 1.0 - smoothstep(0.017, 0.017 + aa, distance_to_shape);
	float glow = exp(-distance_to_shape * distance_to_shape / 0.0030);
	// A restrained dark rim keeps white readable against both wood and spring paper.
	float rim = (1.0 - smoothstep(0.030, 0.050 + aa, distance_to_shape)) * 0.48;
	float light_alpha = clamp((core * 0.90 + glow * 0.35) * intensity * pulse, 0.0, 1.0);
	float alpha = max(rim, light_alpha) * tint.a;
	vec3 color = mix(vec3(0.14, 0.10, 0.08), tint.rgb,
		clamp(light_alpha / max(alpha, 0.001), 0.0, 1.0));
	COLOR = vec4(color, alpha);
}
