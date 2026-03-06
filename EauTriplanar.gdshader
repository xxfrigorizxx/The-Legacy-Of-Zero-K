shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

// Triplanar Mapping : projection en coordonnées monde (world_pos) pour éviter l'étirement
// quand le mesh d'eau s'agrandit via Marching Cubes
uniform vec4 albedo_color : source_color = vec4(0.1, 0.3, 0.6, 0.6);

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Coordonnées monde disponibles pour projection triplanar (texture future)
	ALBEDO = albedo_color.rgb;
	ALPHA = albedo_color.a;
	ROUGHNESS = 0.1;
}
