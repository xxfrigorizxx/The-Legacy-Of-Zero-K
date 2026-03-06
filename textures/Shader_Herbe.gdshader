shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;

varying vec4 instance_color;

void vertex() {
	instance_color = COLOR;
}

void fragment() {
	vec4 tex_color = texture(albedo_texture, UV);
	// SYMBIOSE : multiplie la couleur de la texture par la couleur de l'instance (MultiMesh)
	ALBEDO = tex_color.rgb * instance_color.rgb;
	ROUGHNESS = 0.85;
}
