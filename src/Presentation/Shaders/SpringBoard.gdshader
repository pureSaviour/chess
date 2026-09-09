shader_type canvas_item;
render_mode unshaded;

uniform vec4 young_green : source_color = vec4(0.75, 0.84, 0.62, 1.0);
uniform vec4 warm_paper : source_color = vec4(0.94, 0.92, 0.72, 1.0);
uniform vec4 willow_ink : source_color = vec4(0.18, 0.39, 0.20, 0.13);
uniform vec4 blossom_pink : source_color = vec4(0.94, 0.55, 0.62, 0.18);
uniform vec4 swallow_ink : source_color = vec4(0.12, 0.20, 0.17, 0.10);
uniform float green_wash = 0.42;
uniform float decoration_opacity = 0.72;

float segment_distance(vec2 p, vec2 a, vec2 b) {
	vec2 pa = p - a;
	vec2 ba = b - a;
	float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
	return length(pa - ba * h);
}

float petal(vec2 uv, vec2 center, float angle) {
	vec2 direction = vec2(cos(angle), sin(angle));
	vec2 p = uv - center - direction * 0.010;
	p = mat2(direction, vec2(-direction.y, direction.x)) * p;
	p /= vec2(0.012, 0.006);
	return 1.0 - smoothstep(0.82, 1.0, dot(p, p));
}

float blossom(vec2 uv, vec2 center) {
	float value = 0.0;
	for (int i = 0; i < 5; i++) {
		value = max(value, petal(uv, center, float(i) * 1.256637));
	}
	return value;
}

float leaf(vec2 uv, vec2 center, float angle, vec2 size) {
	vec2 direction = vec2(cos(angle), sin(angle));
	vec2 p = uv - center;
	p = mat2(direction, vec2(-direction.y, direction.x)) * p;
	p /= size;
	float body = 1.0 - smoothstep(0.72, 1.0, dot(p, p));
	float vein = 1.0 - smoothstep(0.025, 0.09, abs(p.y));
	return max(body * 0.72, body * vein);
}

void fragment() {
	vec2 uv = UV;
	float light = 0.045 * sin(uv.x * 8.0 + uv.y * 3.0) + 0.025 * cos(uv.y * 13.0);
	// The spring wash is deliberately translucent-looking and pale so the board does not
	// become darker than the classic wood theme.
	vec3 color = mix(warm_paper.rgb, young_green.rgb, green_wash + light);
	float vignette = 1.0 - smoothstep(0.20, 0.78, length(uv - vec2(0.5)));
	color *= mix(0.91, 1.02, vignette);

	// Two quiet willow silhouettes stay close to the corners and away from the playing area.
	float left_branch = 1.0 - smoothstep(0.003, 0.008,
		abs(uv.x - (0.035 + 0.055 * sin(uv.y * 5.2))) + max(uv.y - 0.62, 0.0));
	float right_branch = 1.0 - smoothstep(0.003, 0.008,
		abs(uv.x - (0.965 - 0.045 * sin(uv.y * 4.7))) + max(0.38 - uv.y, 0.0));
	color = mix(color, willow_ink.rgb, willow_ink.a * decoration_opacity * max(left_branch, right_branch));
	float leaves = leaf(uv, vec2(0.070, 0.17), -0.35, vec2(0.020, 0.008));
	leaves = max(leaves, leaf(uv, vec2(0.115, 0.28), 0.55, vec2(0.018, 0.007)));
	leaves = max(leaves, leaf(uv, vec2(0.925, 0.70), 2.75, vec2(0.020, 0.008)));
	leaves = max(leaves, leaf(uv, vec2(0.875, 0.82), 3.65, vec2(0.018, 0.007)));
	color = mix(color, willow_ink.rgb, willow_ink.a * decoration_opacity * leaves * 1.35);

	float flowers = blossom(uv, vec2(0.105, 0.105));
	flowers = max(flowers, blossom(uv, vec2(0.895, 0.875)));
	flowers = max(flowers, blossom(uv, vec2(0.070, 0.245)) * 0.72);
	flowers = max(flowers, blossom(uv, vec2(0.150, 0.065)) * 0.66);
	flowers = max(flowers, blossom(uv, vec2(0.935, 0.760)) * 0.70);
	color = mix(color, blossom_pink.rgb, blossom_pink.a * decoration_opacity * flowers);

	// Minimal paired strokes suggest a distant spring swallow without a literal illustration.
	float wing_a = 1.0 - smoothstep(0.003, 0.009,
		segment_distance(uv, vec2(0.76, 0.12), vec2(0.79, 0.105)));
	float wing_b = 1.0 - smoothstep(0.003, 0.009,
		segment_distance(uv, vec2(0.79, 0.105), vec2(0.82, 0.125)));
	float wing_c = 1.0 - smoothstep(0.003, 0.009,
		segment_distance(uv, vec2(0.57, 0.19), vec2(0.595, 0.178)));
	float wing_d = 1.0 - smoothstep(0.003, 0.009,
		segment_distance(uv, vec2(0.595, 0.178), vec2(0.62, 0.194)));
	color = mix(color, swallow_ink.rgb, swallow_ink.a * decoration_opacity * max(max(wing_a, wing_b), max(wing_c, wing_d) * 0.72));

	COLOR = vec4(color, 1.0);
}
