shader_type canvas_item;
render_mode unshaded;

uniform vec4 paper : source_color = vec4(0.96, 0.94, 0.78, 1.0);
uniform vec4 young_green : source_color = vec4(0.72, 0.84, 0.60, 1.0);
uniform vec4 blossom : source_color = vec4(0.96, 0.56, 0.66, 1.0);
uniform float wash_strength = 0.24;
uniform float decoration_opacity = 0.38;

float petal(vec2 p, vec2 center, float angle) {
	vec2 d = vec2(cos(angle), sin(angle));
	vec2 q = p - center - d * 0.012;
	q = mat2(d, vec2(-d.y, d.x)) * q / vec2(0.016, 0.007);
	return 1.0 - smoothstep(0.72, 1.0, dot(q, q));
}

float flower(vec2 p, vec2 center) {
	float value = 0.0;
	for (int i = 0; i < 5; i++) value = max(value, petal(p, center, float(i) * 1.256637));
	return value;
}

float leaf(vec2 p, vec2 center, float angle) {
	vec2 d = vec2(cos(angle), sin(angle));
	vec2 q = p - center;
	q = mat2(d, vec2(-d.y, d.x)) * q / vec2(0.025, 0.009);
	return 1.0 - smoothstep(0.72, 1.0, dot(q, q));
}

float segment(vec2 p, vec2 a, vec2 b, float width) {
	vec2 pa = p - a;
	vec2 ba = b - a;
	float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
	return 1.0 - smoothstep(width, width * 2.2, length(pa - ba * h));
}

void fragment() {
	vec2 uv = UV;
	float breeze = 0.018 * sin(uv.x * 7.0 + uv.y * 4.0 + TIME * 0.16);
	vec3 color = mix(paper.rgb, young_green.rgb, wash_strength + breeze);
	float edge = smoothstep(0.82, 0.22, length(uv - vec2(0.5)));
	color *= mix(0.98, 1.025, edge);
	float flowers = flower(uv, vec2(0.09, 0.10));
	flowers = max(flowers, flower(uv, vec2(0.91, 0.88)));
	flowers = max(flowers, flower(uv, vec2(0.07, 0.26)) * 0.65);
	color = mix(color, blossom.rgb, flowers * decoration_opacity);
	float sway = sin(TIME * 0.45) * 0.008;
	float leaves = leaf(uv, vec2(0.08 + sway, 0.18), -0.4);
	leaves = max(leaves, leaf(uv, vec2(0.13 + sway, 0.31), 0.55));
	leaves = max(leaves, leaf(uv, vec2(0.91 - sway, 0.69), 2.7));
	leaves = max(leaves, leaf(uv, vec2(0.86 - sway, 0.82), 3.55));
	color = mix(color, vec3(0.22, 0.43, 0.22), leaves * decoration_opacity * 0.7);
	float swallows = max(segment(uv, vec2(0.71, 0.13), vec2(0.745, 0.11), 0.0025),
		segment(uv, vec2(0.745, 0.11), vec2(0.78, 0.135), 0.0025));
	swallows = max(swallows, max(segment(uv, vec2(0.57, 0.20), vec2(0.595, 0.185), 0.0022),
		segment(uv, vec2(0.595, 0.185), vec2(0.62, 0.203), 0.0022)) * 0.7);
	color = mix(color, vec3(0.15, 0.24, 0.18), swallows * decoration_opacity * 0.72);
	COLOR = vec4(color, 1.0);
}
