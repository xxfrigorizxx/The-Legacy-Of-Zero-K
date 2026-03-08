# Script pour appliquer la structure cumulative du relief
import re

def smoothstep_code():
    return '''		// Structure cumulative : plaine -> colline -> montagne -> grande montagne (logique)
		// Plaine : variation douce 102 (eau) -> 107 (base collines)
		float plaineRamp = (bruitPlaine + 1f) * 0.5f;
		float basePlaine = 102f + plaineRamp * 5f;

		float SmoothT(float a, float b, float x) {
			float t = Mathf.Clamp((x - a) / (b - a), 0f, 1f);
			return t * t * (3f - 2f * t);
		}
		float hCollines = SmoothT(0.35f, 0.6f, relief) * 3f;
		float hMontagnes = SmoothT(0.6f, 0.92f, relief) * 90f;
		float hGrandes = SmoothT(0.92f, 1f, relief) * 300f;

		int hauteurBase;
		if (relief < 0.35f)
			hauteurBase = (int)basePlaine;
		else
			hauteurBase = (int)(107f + hCollines + hMontagnes + hGrandes);'''

# Generateur_Voxel - ObtenirHauteurTerrainMonde (static, uses noiseSurface)
old1 = '''		// Bruit doux ±1 bloc : plaines ET collines (continuité des ondulations)
		float bruitPlaine = noiseSurface.GetNoise2D(worldX * 0.0004f, worldZ * 0.0004f);
		int variationDouce = Mathf.RoundToInt(bruitPlaine * 1.0f);

		int hauteurBase;
		if (relief < 0.35f)
			hauteurBase = ProfondeurBase + variationDouce;  // Plaines 103–105
		else if (relief < 0.6f)
		{
			float t = (relief - 0.35f) / 0.25f;
			float tSmooth = t * t * (3f - 2f * t);  // smoothstep = transition douce
			hauteurBase = ProfondeurBase + (int)(tSmooth * 3.0f) + variationDouce;  // Collines 103–108, même ondulation
		}
		else if (relief < 0.92f)
		{
			float t = (relief - 0.6f) / 0.32f;
			float tSmooth = t * t * (3f - 2f * t);
			hauteurBase = 107 + (int)(tSmooth * 93.0f);
		}
		else
		{
			float t = (relief - 0.92f) / 0.08f;
			float tSmooth = t * t * (3f - 2f * t);
			hauteurBase = 200 + (int)(tSmooth * 300.0f);
		}'''

new1 = '''		float bruitPlaine = noiseSurface.GetNoise2D(worldX * 0.0004f, worldZ * 0.0004f);
		float plaineRamp = (bruitPlaine + 1f) * 0.5f;
		float basePlaine = 102f + plaineRamp * 5f;
		float SmoothT(float a, float b, float x) { float t = Mathf.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }
		float hCollines = SmoothT(0.35f, 0.6f, relief) * 3f;
		float hMontagnes = SmoothT(0.6f, 0.92f, relief) * 90f;
		float hGrandes = SmoothT(0.92f, 1f, relief) * 300f;
		int hauteurBase;
		if (relief < 0.35f)
			hauteurBase = (int)basePlaine;
		else
			hauteurBase = (int)(107f + hCollines + hMontagnes + hGrandes);'''

# C# doesn't allow nested functions - need to use a different approach
# Use inline smoothstep
new1_fixed = '''		float bruitPlaine = noiseSurface.GetNoise2D(worldX * 0.0004f, worldZ * 0.0004f);
		float plaineRamp = (bruitPlaine + 1f) * 0.5f;
		float basePlaine = 102f + plaineRamp * 5f;
		float tC = Mathf.Clamp((relief - 0.35f) / 0.25f, 0f, 1f);
		float tM = Mathf.Clamp((relief - 0.6f) / 0.32f, 0f, 1f);
		float tG = Mathf.Clamp((relief - 0.92f) / 0.08f, 0f, 1f);
		float hCollines = tC * tC * (3f - 2f * tC) * 3f;
		float hMontagnes = tM * tM * (3f - 2f * tM) * 90f;
		float hGrandes = tG * tG * (3f - 2f * tG) * 300f;
		int hauteurBase;
		if (relief < 0.35f)
			hauteurBase = (int)basePlaine;
		else
			hauteurBase = (int)(107f + hCollines + hMontagnes + hGrandes);'''

for path in ['Generateur_Voxel.cs', 'Serveur/Chunk_Serveur.cs']:
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # Replace for Generateur_Voxel - two occurrences (ObtenirHauteurTerrainMonde and CalculerHauteurTerrain)
    # ObtenirHauteurTerrainMonde uses noiseSurface
    old_gen1 = old1.replace('noiseSurface', 'noiseSurface').replace('worldX', 'worldX').replace('worldZ', 'worldZ')
    # CalculerHauteurTerrain uses _noiseErosion
    old_gen2 = old1.replace('noiseSurface.GetNoise2D', '_noiseErosion.GetNoise2D').replace('worldX', 'xGlobal').replace('worldZ', 'zGlobal')
    
    # For Generateur_Voxel: first replace ObtenirHauteurTerrainMonde (noiseSurface, worldX, worldZ)
    new_gen1 = new1_fixed.replace('noiseSurface', 'noiseSurface').replace('xGlobal', 'worldX').replace('zGlobal', 'worldZ')
    content = content.replace(old1.replace('noiseSurface', 'noiseSurface').replace('worldX', 'worldX').replace('worldZ', 'worldZ'), 
                              new1_fixed.replace('xGlobal', 'worldX').replace('zGlobal', 'worldZ'), 1)
    
    # For Generateur_Voxel CalculerHauteurTerrain
    old_gen2 = '''		// Bruit doux ±1 bloc : plaines ET collines (continuité des ondulations)
		float bruitPlaine = _noiseErosion.GetNoise2D(xGlobal * 0.0004f, zGlobal * 0.0004f);
		int variationDouce = Mathf.RoundToInt(bruitPlaine * 1.0f);

		int hauteurBase;
		if (relief < 0.35f)
			hauteurBase = ProfondeurBase + variationDouce;  // Plaines 103–105
		else if (relief < 0.6f)
		{
			float t = (relief - 0.35f) / 0.25f;
			float tSmooth = t * t * (3f - 2f * t);  // smoothstep = transition douce
			hauteurBase = ProfondeurBase + (int)(tSmooth * 3.0f) + variationDouce;  // Collines 103–108, même ondulation
		}
		else if (relief < 0.92f)
		{
			float t = (relief - 0.6f) / 0.32f;
			float tSmooth = t * t * (3f - 2f * t);
			hauteurBase = 107 + (int)(tSmooth * 93.0f);
		}
		else
		{
			float t = (relief - 0.92f) / 0.08f;
			float tSmooth = t * t * (3f - 2f * t);
			hauteurBase = 200 + (int)(tSmooth * 300.0f);
		}'''
    
    new_gen2 = '''		float bruitPlaine = _noiseErosion.GetNoise2D(xGlobal * 0.0004f, zGlobal * 0.0004f);
		float plaineRamp = (bruitPlaine + 1f) * 0.5f;
		float basePlaine = 102f + plaineRamp * 5f;
		float tC = Mathf.Clamp((relief - 0.35f) / 0.25f, 0f, 1f);
		float tM = Mathf.Clamp((relief - 0.6f) / 0.32f, 0f, 1f);
		float tG = Mathf.Clamp((relief - 0.92f) / 0.08f, 0f, 1f);
		float hCollines = tC * tC * (3f - 2f * tC) * 3f;
		float hMontagnes = tM * tM * (3f - 2f * tM) * 90f;
		float hGrandes = tG * tG * (3f - 2f * tG) * 300f;
		int hauteurBase;
		if (relief < 0.35f)
			hauteurBase = (int)basePlaine;
		else
			hauteurBase = (int)(107f + hCollines + hMontagnes + hGrandes);'''
    
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    
    count = content.count(old_gen2)
    content = content.replace(old_gen2, new_gen2)
    if path == 'Generateur_Voxel.cs':
        # Also replace ObtenirHauteurTerrainMonde version (noiseSurface, worldX, worldZ)
        old_gen1 = old_gen2.replace('_noiseErosion', 'noiseSurface').replace('xGlobal', 'worldX').replace('zGlobal', 'worldZ')
        new_gen1 = new_gen2.replace('_noiseErosion', 'noiseSurface').replace('xGlobal', 'worldX').replace('zGlobal', 'worldZ')
        content = content.replace(old_gen1, new_gen1)
    
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)
    print(f'Updated {path}')
