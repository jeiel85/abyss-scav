shader_type spatial;
render_mode diffuse_lambert, specular_disabled;

// Subtle abyss tint for rock/steel. Geometry stays honest boxes; this only
// grades the albedo toward ink-blue with depth, plus a faint cyan rim so
// corridor walls read under the headlight without harsh flicker.
uniform vec3 tint : source_color = vec3(0.04, 0.10, 0.12);
uniform vec3 rim : source_color = vec3(0.44, 0.85, 0.82);

void fragment() {
	ALBEDO = mix(ALBEDO, tint, 0.35);
	EMISSION = rim * pow(1.0 - abs(dot(NORMAL, VIEW)), 3.0) * 0.08;
	ROUGHNESS = 0.85;
}
