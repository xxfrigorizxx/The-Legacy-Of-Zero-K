shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;
uniform sampler2DArray texture_array : source_color, filter_linear_mipmap, repeat_enable;
uniform float triplanar_scale = 0.2;

varying vec3 uv1_power_normal;
varying vec3 uv1_triplanar_pos;
varying float raw_id;
varying vec3 world_pos;
varying float v_temperature;
varying float v_humidite;

// Fonction mathématique de pseudo-aléatoire très légère
float random(vec2 st) {
	return fract(sin(dot(st.xy, vec2(12.9898,78.233))) * 43758.5453123);
}

// Bruit de Valeur (Value Noise) 2D ultra-rapide pour le GPU
float noise(vec2 st) {
	vec2 i = floor(st);
	vec2 f = fract(st);
	float a = random(i);
	float b = random(i + vec2(1.0, 0.0));
	float c = random(i + vec2(0.0, 1.0));
	float d = random(i + vec2(1.0, 1.0));
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

void vertex() {
	vec3 normal = abs(NORMAL);
	normal = pow(normal, vec3(4.0));
	uv1_power_normal = normal / dot(normal, vec3(1.0));

	// Récupération de la position globale pour le calcul mathématique
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	uv1_triplanar_pos = world_pos * triplanar_scale;
	// On garde l'ID brut sans l'arrondir ici
	raw_id = COLOR.r * 255.0;
	v_temperature = (COLOR.g * 2.0) - 1.0; // Retour à -1.0 -> 1.0
	v_humidite = (COLOR.b * 2.0) - 1.0;    // Retour à -1.0 -> 1.0
}

void fragment() {
	// 1. PERTURBATION DES TRANSITIONS (Bruit à haute fréquence)
	// On crée un chaos de +/- 0.4 sur l'ID aux frontières des matériaux
	float edge_noise = (noise(world_pos.xz * 4.0) - 0.5) * 0.8;
	float material_id = round(raw_id + edge_noise);
	material_id = clamp(material_id, 0.0, 9.0); // Sécurité anti-crash

	// Projection Triplanaire standard
	vec2 uv_x = uv1_triplanar_pos.zy;
	vec2 uv_y = uv1_triplanar_pos.xz;
	vec2 uv_z = uv1_triplanar_pos.xy;
	vec4 tex_x = texture(texture_array, vec3(uv_x, material_id));
	vec4 tex_y = texture(texture_array, vec3(uv_y, material_id));
	vec4 tex_z = texture(texture_array, vec3(uv_z, material_id));
	vec4 albedo_tex = tex_x * uv1_power_normal.x + tex_y * uv1_power_normal.y + tex_z * uv1_power_normal.z;
	// 2. MACRO-VARIATION ANTI-DAMIER (Bruit à basse fréquence)
	// Crée des zones d'ombres et de lumières géantes sur le terrain
	float macro_noise = noise(world_pos.xz * 0.05);
	vec3 final_color = albedo_tex.rgb * (0.65 + macro_noise * 0.5); // Modulation de la luminosité

	// --- MUTATION SÉMANTIQUE (FERTILITÉ & CLIMAT) —---
	float facteur_humidite = clamp((v_humidite + 1.0) * 0.5, 0.0, 1.0);

	// Herbe (ID 1) : sec = jaunâtre, normal = vert, humide = vert foncé
	if (material_id == 1.0) {
		vec3 couleur_seche = vec3(1.3, 0.9, 0.35);   // Jaunâtre / paille
		vec3 couleur_normal = vec3(0.45, 0.75, 0.4); // Vert normal
		vec3 couleur_humide = vec3(0.25, 0.55, 0.3); // Vert foncé
		vec3 teinte_flore;
		if (facteur_humidite < 0.35) teinte_flore = mix(couleur_seche, couleur_normal, facteur_humidite / 0.35);
		else teinte_flore = mix(couleur_normal, couleur_humide, (facteur_humidite - 0.35) / 0.65);
		final_color *= teinte_flore;
	}
	// Boue (ID 7) : sec = grisâtre, humide = de plus en plus brun
	if (material_id == 7.0) {
		vec3 gris_sec = vec3(0.7, 0.68, 0.65);   // Grisâtre
		vec3 brun_humide = vec3(0.45, 0.32, 0.22); // Brun
		vec3 teinte_boue = mix(gris_sec, brun_humide, facteur_humidite);
		final_color *= teinte_boue;
	}
	// Terre aride (ID 6) : sec = grisâtre, humide = assombri
	if (material_id == 6.0) {
		float assombrissement = mix(1.0, 0.5, facteur_humidite);
		vec3 gris_sec = vec3(0.95, 0.9, 0.85);
		vec3 brun = vec3(0.6, 0.5, 0.4);
		final_color *= mix(gris_sec, brun, facteur_humidite * 0.7);
	}

	ALBEDO = final_color;
	ROUGHNESS = 0.85;
}
