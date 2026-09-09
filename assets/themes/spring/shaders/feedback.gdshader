shader_type canvas_item;
render_mode unshaded;

uniform vec4 tint : source_color = vec4(0.95, 0.42, 0.52, 1.0);
uniform bool origin_cursor = false;
uniform bool animated = true;

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float time = animated ? TIME : 0.0;
	float r = length(p);
	float ring = 1.0 - smoothstep(0.035, 0.070, abs(r - 0.66));
	float petals = pow(max(0.0, cos(atan(p.y, p.x) * 5.0 + time * 0.7)), 8.0);
	float bloom = (1.0 - smoothstep(0.05, 0.13, abs(r - 0.76))) * petals;
	float brackets = step(0.48, max(abs(p.x), abs(p.y))) * step(max(abs(p.x), abs(p.y)), 0.72)
		* step(min(abs(p.x), abs(p.y)), 0.23);
	float alpha = origin_cursor ? brackets * 0.82 : max(ring, bloom * 0.72);
	alpha *= animated ? 0.82 + 0.18 * sin(time * 3.8) : 0.9;
	COLOR = vec4(mix(tint.rgb, vec3(1.0, 0.84, 0.88), bloom * 0.55), alpha * tint.a);
}
