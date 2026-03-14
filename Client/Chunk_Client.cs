using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>Paquet pour une section de chunk : uniquement des données C# pures (aucune ressource Godot). Produit par le Task.Run, consommé par le Main Thread.</summary>
public class SectionPayload
{
	public Vector3[] SommetsVisuels;
	public Vector3[] NormalsVisuels;
	public Color[] CouleursVisuels;
	public Vector3[] SommetsEau;
	public Vector3[] NormalsEau;
}

/// <summary>Paquet flore précalculé dans le Task.Run : positions et couleurs pour un seul passage MultiMesh (évite AddChild désynchronisé).</summary>
public class ChunkFlorePayload
{
	public List<(Transform3D T, Color C, Vector3 PosMonde)> Gazon;
	public List<Transform3D> BuissonPlein;
	public List<Transform3D> BuissonVide;
}

/// <summary>Paquet pour cailloux physiques : positions précalculées, spawn dilué (1-2 par frame) sur le Main Thread.</summary>
public class ChunkCaillouxPayload
{
	public List<Transform3D> Positions = new List<Transform3D>();
}

/// <summary>Détient MeshInstance3D, CollisionShape3D. Reçoit des données et les transforme en triangles. Aucun bruit fractal.</summary>
public partial class Chunk_Client : Node3D
{
	[ThreadStatic] private static float[] _valsRecyclables;
	[ThreadStatic] private static Vector3[] _vertsRecyclables;
	[ThreadStatic] private static Vector3[] _vertListRecyclables;
	[ThreadStatic] private static float[] _valsEauRecyclables;
	[ThreadStatic] private static Vector3[] _vertsEauRecyclables;
	[ThreadStatic] private static Vector3[] _vertListEauRecyclables;

	private const int TAILLE_MAX_SECTION = 17 * 17 * 17;

	public int ChunkOffsetX { get; set; }
	public int ChunkOffsetZ { get; set; }
	public int TailleChunk { get; set; }
	public int HauteurMax { get; set; }

	private const int HAUTEUR_SECTION = 16;
	private const int NB_SECTIONS = 45;  // 45×16 = 720 (HauteurMax) — avant: 16 = 256 uniquement
	private const float Isolevel = 0.0f;

	private MeshInstance3D[] _sectionsTerrain;
	private MeshInstance3D[] _sectionsEau;
	private CollisionShape3D[] _sectionsPhysiques;
	private MultiMeshInstance3D _mmGazon;
	private MultiMeshInstance3D _mmBuissonPlein;
	private MultiMeshInstance3D _mmBuissonVide;

	/// <summary>Échelle du gazon (grass.glb) partout sur ID 1. Ajustable pour uniformiser la taille.</summary>
	public static float EchelleGazon = 2f;

	[Export] public Material MaterielTerre;

	private float[] _densitiesFlat;
	private byte[] _materialsFlat;
	private float[] _densitiesEauFlat;
	/// <summary>Dimensions des tableaux plats : tx = TailleChunk+1, ty = HauteurMax+1, tz = TailleChunk+1.</summary>
	private int _tx, _ty, _tz;

	/// <summary>Index 1D aligné sur DonneesChunk (serveur) : x*Ty*Tz + y*Tz + z. Cohérent avec CompresserDensitesPourReseau / DecompresserDensitesFlat.</summary>
	private int Idx(int x, int y, int z) => x * _ty * _tz + y * _tz + z;
	private FastNoiseLite _noiseTemperature;
	private FastNoiseLite _noiseHumidite;
	private Dictionary<Vector3I, byte> _inventaireFloreEnAttente;
	private Dictionary<Vector3I, byte> _inventaireFloreCache;
	private ChunkFlorePayload _payloadFloreCache;
	private int _frameFlore;
	/// <summary>Rayon en chunks : seul le gazon (grass.glb) est visible dans cette zone autour du joueur. Les buissons restent visibles partout.</summary>
	private const int RAYON_GAZON_CHUNKS = 1;
	/// <summary>Au-delà de ce rayon (en chunks), le chunk reste affiché mais est "dormant" : pas de physique ni collision.</summary>
	private const int RAYON_CHUNK_ACTIF_CHUNKS = 2;

	private bool _dormant;

	public override void _Ready()
	{
		SetProcess(true);
		SetPhysicsProcess(false);

		_sectionsTerrain = new MeshInstance3D[NB_SECTIONS];
		_sectionsEau = new MeshInstance3D[NB_SECTIONS];
		_sectionsPhysiques = new CollisionShape3D[NB_SECTIONS];

		var shaderEau = GD.Load<Shader>("res://EauTriplanar.gdshader");
		var matEau = new ShaderMaterial();
		matEau.Shader = shaderEau;
		matEau.SetShaderParameter("albedo_color", new Color(0.1f, 0.3f, 0.6f, 0.6f));

		for (int i = 0; i < NB_SECTIONS; i++)
		{
			var miTerrain = new MeshInstance3D { Name = $"TerrainSection_{i}" };
			AddChild(miTerrain);
			_sectionsTerrain[i] = miTerrain;

			var corps = new StaticBody3D { Name = $"CollisionSection_{i}" };
			var collisionShape = new CollisionShape3D();
			corps.AddChild(collisionShape);
			miTerrain.AddChild(corps);
			_sectionsPhysiques[i] = collisionShape;

			var miEau = new MeshInstance3D { Name = $"EauSection_{i}", MaterialOverride = matEau };
			AddChild(miEau);
			_sectionsEau[i] = miEau;
		}
		_mmGazon = new MultiMeshInstance3D { Name = "Gazon" };
		_mmBuissonPlein = new MultiMeshInstance3D { Name = "BuissonPlein" };
		_mmBuissonVide = new MultiMeshInstance3D { Name = "BuissonVide" };
		AddChild(_mmGazon);
		AddChild(_mmBuissonPlein);
		AddChild(_mmBuissonVide);
	}

	public override void _Process(double delta)
	{
		if (_inventaireFloreCache == null || _inventaireFloreCache.Count == 0) return;
		_frameFlore++;
		if (_frameFlore % 12 == 0)
		{
			ActualiserFloreAvecDistance();
		}
	}

	public void ConfigurerBruitClimat(int seed)
	{
		// Aligné avec serveur : Fbm + octaves = transitions lentes pour couleurs cohérentes
		_noiseTemperature = new FastNoiseLite();
		_noiseTemperature.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noiseTemperature.Seed = seed + 2;
		_noiseTemperature.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		_noiseTemperature.FractalOctaves = 4;
		_noiseTemperature.Frequency = 0.0005f;

		_noiseHumidite = new FastNoiseLite();
		_noiseHumidite.Seed = seed + 3;
		_noiseHumidite.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noiseHumidite.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		_noiseHumidite.FractalOctaves = 4;
		_noiseHumidite.Frequency = 0.0006f;
	}

	/// <summary>Exécute le calcul lourd (décompression + flore + 45 sections) dans le worker. Appelé par Monde_Client depuis Task.Run. Une seule tâche = un chunk entier (pas de sous-tasks).</summary>
	public void ExecuterCalculChunk(DonneesChunk donnees, Action<Action> enqueueIntegration)
	{
		if (donnees.MaterialsFlat == null) return;
		bool formatQuantifie = donnees.DensitiesQuantifiees != null;
		int tx = donnees.TailleChunk + 1, ty = donnees.HauteurMax + 1, tz = donnees.TailleChunk + 1;
		float baseX = donnees.CoordChunk.X * (float)donnees.TailleChunk;
		float baseZ = donnees.CoordChunk.Y * (float)donnees.TailleChunk;

		if (formatQuantifie)
		{
			_densitiesFlat = DonneesChunk.DecompresserDensitesFlat(donnees.DensitiesQuantifiees, tx, ty, tz);
			_densitiesEauFlat = donnees.DensitiesEauQuantifiees != null
				? DonneesChunk.DecompresserDensitesFlat(donnees.DensitiesEauQuantifiees, tx, ty, tz)
				: null;
		}
		else
		{
			_densitiesFlat = (float[])donnees.DensitiesFlat.Clone();
			_densitiesEauFlat = donnees.DensitiesEauFlat != null ? (float[])donnees.DensitiesEauFlat.Clone() : null;
		}
		_tx = tx; _ty = ty; _tz = tz;
		_materialsFlat = (byte[])donnees.MaterialsFlat.Clone();

		_inventaireFloreEnAttente = donnees.InventaireFlore;
		var invFlore = donnees.InventaireFlore;
		var chunkRef = this;
		if (invFlore != null && invFlore.Count > 0)
		{
			var payload = ConstruirePayloadFloreEnBackground(invFlore, (float)(donnees.CoordChunk.X * donnees.TailleChunk), (float)(donnees.CoordChunk.Y * donnees.TailleChunk));
			enqueueIntegration?.Invoke(() => chunkRef.AppliquerPayloadFlore(payload));
		}
		else
			enqueueIntegration?.Invoke(() => chunkRef.AppliquerPayloadFlore(null));

		// 45 sections en séquence dans ce worker (pas de Task.Run par section)
		for (int idxSec = 0; idxSec < NB_SECTIONS; idxSec++)
		{
			SectionPayload payload = ConstruireSectionPayloadEnBackground(idxSec, baseX, baseZ);
			int sec = idxSec;
			enqueueIntegration?.Invoke(() => chunkRef.IntegrerSectionPayload(sec, payload));
		}
	}

	/// <summary>Reçoit les données du serveur. Les travaux lourds sont délégués à la Forge restreinte (file d'attente + MaxTravailleurs). Ne lance plus de Task.Run ici.</summary>
	public void RecevoirDonneesChunk(DonneesChunk donnees, Action<Action> enqueueIntegration)
	{
		// Si le monde utilise la Forge restreinte, il appelle EnqueueChunkGeneration au lieu de ceci. Conservé pour appel direct (ex. tests).
		ExecuterCalculChunk(donnees, enqueueIntegration);
	}

	/// <summary>Met à jour un voxel aux coordonnées locales (pour réplication du padding des voisins).</summary>
	public void SetVoxelLocal(int lx, int ly, int lz, byte id)
	{
		if (_densitiesFlat == null || lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk) return;
		if (id == 0)
		{
			_densitiesFlat[Idx(lx, ly, lz)] = -10f;
			_materialsFlat[Idx(lx, ly, lz)] = 0;
			if (_densitiesEauFlat != null) _densitiesEauFlat[Idx(lx, ly, lz)] = -1f;
			// Purge flore locale : modèle 3D disparaît immédiatement quand le bloc sous lui est détruit
			var posGlobale = new Vector3I(ChunkOffsetX * TailleChunk + lx, ly, ChunkOffsetZ * TailleChunk + lz);
			if (_inventaireFloreCache != null && _inventaireFloreCache.Remove(posGlobale))
				ActualiserFloreAvecDistance();
		}
		else if (id == 4)
		{
			_densitiesFlat[Idx(lx, ly, lz)] = -10f;
			_materialsFlat[Idx(lx, ly, lz)] = 4;
			if (_densitiesEauFlat != null) _densitiesEauFlat[Idx(lx, ly, lz)] = 1f;
		}
		else
		{
			_densitiesFlat[Idx(lx, ly, lz)] = 10f;
			_materialsFlat[Idx(lx, ly, lz)] = id;
			if (_densitiesEauFlat != null) _densitiesEauFlat[Idx(lx, ly, lz)] = -1f;
		}
	}

	/// <summary>Applique une mise à jour voxel unique (eau/air/solide) depuis le serveur. Met à jour les données et lève le dirty flag — AUCUN Marching Cubes ici.</summary>
	public void AppliquerVoxelGlobal(Vector3I posGlobal, byte id)
	{
		if (_densitiesFlat == null) return;
		Gestionnaire_Monde.WorldToChunkAndLocal(posGlobal.X, posGlobal.Z, TailleChunk, out Vector2I c, out int lx, out int lz);
		if (c.X != ChunkOffsetX || c.Y != ChunkOffsetZ) return;
		int ly = posGlobal.Y;
		if (lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk)
			return;
		if (id == 0)
		{
			_densitiesFlat[Idx(lx, ly, lz)] = -10f;
			_materialsFlat[Idx(lx, ly, lz)] = 0;
			if (_densitiesEauFlat != null) _densitiesEauFlat[Idx(lx, ly, lz)] = -1f;
			// Purge flore : gazon et buissons disparaissent quand le bloc ID 1 (herbe) est détruit
			bool floreModifiee = false;
			if (_inventaireFloreCache != null)
			{
				floreModifiee |= _inventaireFloreCache.Remove(posGlobal);
				// Sensibilité : aussi retirer la flore sur le bloc au-dessus (gazon sur surface)
				var posAuDessus = new Vector3I(posGlobal.X, posGlobal.Y + 1, posGlobal.Z);
				floreModifiee |= _inventaireFloreCache.Remove(posAuDessus);
			}
			if (floreModifiee) ActualiserFloreAvecDistance();
		}
		else if (id == 4)
		{
			_densitiesFlat[Idx(lx, ly, lz)] = -10f;
			_materialsFlat[Idx(lx, ly, lz)] = 4;
			if (_densitiesEauFlat != null) _densitiesEauFlat[Idx(lx, ly, lz)] = 1f;
		}
		else
		{
			_densitiesFlat[Idx(lx, ly, lz)] = 10f;
			_materialsFlat[Idx(lx, ly, lz)] = id;
			if (_densitiesEauFlat != null) _densitiesEauFlat[Idx(lx, ly, lz)] = -1f;
		}
	}

	/// <summary>Appelé par Monde_Client. Reconstruit une section en Task.Run (données pures) puis forge sur le Main Thread.</summary>
	public void DeclencherReconstructionSection(int indexSection)
	{
		if (_densitiesFlat == null) return;
		var monde = GetParent() as Monde_Client;
		if (monde == null) return;
		float baseX = ChunkOffsetX * TailleChunk;
		float baseZ = ChunkOffsetZ * TailleChunk;
		int idx = indexSection;
		var chunkRef = this;
		var enqueue = monde.EnqueueIntegration;
		Task.Run(() =>
		{
			SectionPayload payload = ConstruireSectionPayloadEnBackground(idx, baseX, baseZ);
			enqueue.Invoke(() => chunkRef.IntegrerSectionPayload(idx, payload));
		});
	}

	/// <summary>Reconstruction synchrone sur le Main Thread (Coupe-File VIP). Évite la ThreadPool Starvation.</summary>
	public void ReconstruireSectionSynchrone(int indexSection)
	{
		if (_densitiesFlat == null) return;
		float baseX = ChunkOffsetX * TailleChunk;
		float baseZ = ChunkOffsetZ * TailleChunk;
		var (meshTerrain, meshEau) = ConstruireMeshSection(indexSection, baseX, baseZ);
		AppliquerMeshSection(indexSection, meshTerrain, meshEau);
	}

	/// <summary>Retourne la densité aux coordonnées locales. -10 si hors bornes (pour suture MC aux frontières).</summary>
	public float ObtenirDensiteLocale(int lx, int ly, int lz)
	{
		if (_densitiesFlat == null || lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk)
			return -10f;
		return _densitiesFlat[Idx(lx, ly, lz)];
	}

	/// <summary>Section prête si son CollisionShape3D est construit. Utilisé pour suspendre la gravité au spawn.</summary>
	public bool SectionAPret(int section)
	{
		if (_sectionsPhysiques == null || section < 0 || section >= NB_SECTIONS) return false;
		return _sectionsPhysiques[section]?.Shape != null;
	}

	/// <summary>Active ou désactive la physique du terrain selon la distance au joueur (obsChunkX/Z). Au-delà de RAYON_CHUNK_ACTIF_CHUNKS, le chunk est "dormant" : visuel seul. Protocole d'éveil : réactive Visible, ProcessMode et CollisionShape.Disabled.</summary>
	public void MettreAJourDormance(int obsChunkX, int obsChunkZ)
	{
		int dx = Mathf.Abs(ChunkOffsetX - obsChunkX);
		int dz = Mathf.Abs(ChunkOffsetZ - obsChunkZ);
		bool dormant = dx > RAYON_CHUNK_ACTIF_CHUNKS || dz > RAYON_CHUNK_ACTIF_CHUNKS;
		if (dormant == _dormant) return;
		_dormant = dormant;

		if (dormant)
		{
			Visible = false;
			ProcessMode = ProcessModeEnum.Disabled;
			if (_sectionsPhysiques != null)
			{
				for (int i = 0; i < _sectionsPhysiques.Length; i++)
				{
					var col = _sectionsPhysiques[i];
					if (col != null) col.Disabled = true;
					var corps = col?.GetParent() as StaticBody3D;
					if (corps != null)
					{
						corps.SetCollisionLayerValue(1, false);
						corps.SetCollisionMaskValue(1, false);
					}
				}
			}
		}
		else
		{
			Visible = true;
			ProcessMode = ProcessModeEnum.Inherit;
			SetProcess(true);
			if (_sectionsPhysiques != null)
			{
				for (int i = 0; i < _sectionsPhysiques.Length; i++)
				{
					var col = _sectionsPhysiques[i];
					if (col != null) col.Disabled = false;
					var corps = col?.GetParent() as StaticBody3D;
					if (corps != null)
					{
						corps.SetCollisionLayerValue(1, true);
						corps.SetCollisionMaskValue(1, true);
					}
				}
			}
		}
	}

	/// <summary>Applique le mesh visuel ET le CollisionShape3D. Hitbox créée sur le Main Thread (CreateTrimeshShape) pour éviter la dépendance à PackedVector3Array.</summary>
	private void AppliquerMeshSection(int idx, ArrayMesh meshTerrain, ArrayMesh meshEau)
	{
		try
		{
			if (!IsInsideTree()) return;
			_sectionsTerrain[idx].Mesh = meshTerrain;
			_sectionsTerrain[idx].MaterialOverride = MaterielTerre ?? GD.Load<Material>("res://Manteau_Planetaire.tres");

			var collisionShape = _sectionsPhysiques[idx];
			if (collisionShape != null && meshTerrain != null)
			{
				Shape3D nouveauShape = meshTerrain.GetFaces().Length > 0 ? meshTerrain.CreateTrimeshShape() : null;
				// Appelé depuis la file d'intégration (Main Thread) : pas de CallDeferred.
				if (IsInsideTree() && collisionShape != null)
				{
					if (collisionShape.Shape != null)
					{
						collisionShape.Shape.Dispose();
						collisionShape.Shape = null;
					}
					collisionShape.Shape = nouveauShape;
				}
			}

			if (_densitiesEauFlat != null && meshEau != null)
				_sectionsEau[idx].Mesh = meshEau;
		}
		catch (ObjectDisposedException) { /* Chunk déjà supprimé, ignorer */ }
		catch (System.Exception) when (IsChunkDisposeException()) { /* Godot/natif : objet libéré */ }
	}

	/// <summary>Forge sur le Main Thread : transforme un SectionPayload (données pures) en ArrayMesh + shape, puis applique à la section.</summary>
	private void IntegrerSectionPayload(int idx, SectionPayload payload)
	{
		if (payload == null) return;
		(ArrayMesh meshTerrain, ArrayMesh meshEau) = CreerMeshesDepuisPayload(payload);
		AppliquerMeshSection(idx, meshTerrain, meshEau);
	}

	/// <summary>Construit ArrayMesh terrain et eau à partir des tableaux du payload (à appeler uniquement sur le Main Thread).</summary>
	private static (ArrayMesh terrain, ArrayMesh eau) CreerMeshesDepuisPayload(SectionPayload p)
	{
		ArrayMesh meshTerrain = null;
		ArrayMesh meshEau = null;

		if (p.SommetsVisuels != null && p.SommetsVisuels.Length > 0)
		{
			var st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Triangles);
			for (int i = 0; i < p.SommetsVisuels.Length; i++)
			{
				st.SetNormal(p.NormalsVisuels != null && i < p.NormalsVisuels.Length ? p.NormalsVisuels[i] : Vector3.Up);
				st.SetColor(p.CouleursVisuels != null && i < p.CouleursVisuels.Length ? p.CouleursVisuels[i] : Colors.White);
				st.AddVertex(p.SommetsVisuels[i]);
			}
			st.GenerateNormals();
			meshTerrain = st.Commit();
		}

		if (p.SommetsEau != null && p.SommetsEau.Length > 0)
		{
			var stEau = new SurfaceTool();
			stEau.Begin(Mesh.PrimitiveType.Triangles);
			for (int i = 0; i < p.SommetsEau.Length; i++)
			{
				stEau.SetNormal(p.NormalsEau != null && i < p.NormalsEau.Length ? p.NormalsEau[i] : Vector3.Up);
				stEau.AddVertex(p.SommetsEau[i]);
			}
			stEau.GenerateNormals();
			meshEau = stEau.Commit();
		}

		return (meshTerrain, meshEau);
	}

	private static bool IsChunkDisposeException() => true; // Placeholder pour filtre when

	/// <summary>Version différée sans paramètre — lit _inventaireFloreEnAttente pour éviter Variant/CallDeferred.</summary>
	private void AppliquerInventaireFloreEnAttente()
	{
		try
		{
			var inv = _inventaireFloreEnAttente;
			_inventaireFloreEnAttente = null;
			if (inv != null) MettreAJourRenduFlore(inv);
		}
		catch (ObjectDisposedException) { /* Chunk déjà supprimé */ }
	}

	/// <summary>Construit le paquet flore (transforms + couleurs) dans le Task.Run. N'appelle aucun nœud Godot.</summary>
	private ChunkFlorePayload ConstruirePayloadFloreEnBackground(Dictionary<Vector3I, byte> inventaire, float originX, float originZ)
	{
		var payload = new ChunkFlorePayload
		{
			Gazon = new List<(Transform3D T, Color C, Vector3 PosMonde)>(),
			BuissonPlein = new List<Transform3D>(),
			BuissonVide = new List<Transform3D>()
		};
		Vector3 chunkOrigin = new Vector3(originX, 0, originZ);
		foreach (var kv in inventaire)
		{
			Vector3 positionLocale = new Vector3(kv.Key.X, kv.Key.Y + 0.5f, kv.Key.Z) - chunkOrigin + new Vector3(0.5f, 0f, 0.5f);
			float angle = (float)((kv.Key.X * 73856093 ^ kv.Key.Z * 19349663) % 10000) / 10000f * Mathf.Tau;
			Vector3 posMonde = new Vector3(kv.Key.X + 0.5f, kv.Key.Y + 0.5f, kv.Key.Z + 0.5f);

			Color couleurSol = ObtenirCouleurTerrainApproxThreadSafe(kv.Key.X, kv.Key.Y, kv.Key.Z);
			Color couleurHerbe = new Color(couleurSol.R * 0.8f, couleurSol.G * 0.8f, couleurSol.B * 0.8f, 1f).Lerp(new Color(0.7f, 0.8f, 1f), 0.1f);
			uint hashBase = (uint)(kv.Key.X * 73856093) ^ (uint)(kv.Key.Z * 19349663);
			int densiteGazon = 14;
			for (int i = 0; i < densiteGazon; i++)
			{
				uint h_brin = hashBase ^ (uint)(i * 83492791);
				float offsetX = ((h_brin % 100) / 100f) - 0.5f;
				float offsetZ = (((h_brin / 100) % 100) / 100f) - 0.5f;
				float echelleAlea = 0.5f + ((h_brin % 50) / 100f);
				float angleBrin = (h_brin % 360) * Mathf.Pi / 180f;
				var tGazon = Transform3D.Identity;
				tGazon.Origin = positionLocale + new Vector3(offsetX, 0, offsetZ);
				tGazon.Basis = Basis.Identity.Scaled(new Vector3(EchelleGazon * echelleAlea, EchelleGazon * echelleAlea, EchelleGazon * echelleAlea)).Rotated(Vector3.Up, angleBrin);
				payload.Gazon.Add((tGazon, couleurHerbe, posMonde + new Vector3(offsetX, 0, offsetZ)));
			}

			if (kv.Value == 1 || kv.Value == 2)
			{
				uint h = (uint)(kv.Key.X * 73856093) ^ (uint)(kv.Key.Z * 19349663) ^ (uint)(kv.Key.Y * 83492791);
				float echelleBuis = 0.009f + (h % 500) / 500f * 0.004f;
				var tBuis = Transform3D.Identity;
				tBuis.Origin = positionLocale;
				tBuis.Basis = Basis.Identity.Scaled(new Vector3(echelleBuis, echelleBuis, echelleBuis)).Rotated(Vector3.Up, angle);
				if (kv.Value == 1) payload.BuissonPlein.Add(tBuis);
				else payload.BuissonVide.Add(tBuis);
			}
		}
		return payload;
	}

	/// <summary>Version thread-safe de la couleur terrain (utilisée dans Task.Run, pas d'accès nœud).</summary>
	private Color ObtenirCouleurTerrainApproxThreadSafe(int xGlobal, int yGlobal, int zGlobal)
	{
		if (_materialsFlat == null || _noiseTemperature == null || _noiseHumidite == null) return new Color(0.5f, 0.6f, 0.5f);
		int lx = xGlobal - ChunkOffsetX * TailleChunk;
		int lz = zGlobal - ChunkOffsetZ * TailleChunk;
		if (lx < 0 || lx > TailleChunk || yGlobal < 0 || yGlobal > HauteurMax || lz < 0 || lz > TailleChunk)
			return new Color(0.5f, 0.6f, 0.5f);
		byte idMat = _materialsFlat[Idx(lx, yGlobal, lz)];
		float temp = _noiseTemperature.GetNoise2D(xGlobal, zGlobal);
		float hum = _noiseHumidite.GetNoise2D(xGlobal, zGlobal);
		float facteurHum = Mathf.Clamp((hum + 1f) * 0.5f, 0f, 1f);
		Color sec = new Color(1.3f, 0.9f, 0.35f);
		Color normal = new Color(0.45f, 0.75f, 0.4f);
		Color humide = new Color(0.25f, 0.55f, 0.3f);
		Color couleurBase = facteurHum < 0.35f
			? sec.Lerp(normal, facteurHum / 0.35f)
			: normal.Lerp(humide, (facteurHum - 0.35f) / 0.65f);
		return couleurBase;
	}

	/// <summary>Applique le paquet flore sur le Main Thread : un seul passage MultiMesh (1 Draw Call pour toute la végétation du chunk). Filtre le gazon par distance.</summary>
	private void AppliquerPayloadFlore(ChunkFlorePayload payload)
	{
		try
		{
			_payloadFloreCache = payload;
			if (!IsInsideTree())
			{
				if (_mmGazon != null) _mmGazon.Multimesh = null;
				if (_mmBuissonPlein != null) _mmBuissonPlein.Multimesh = null;
				if (_mmBuissonVide != null) _mmBuissonVide.Multimesh = null;
				return;
			}
			if (payload == null || (payload.Gazon.Count == 0 && payload.BuissonPlein.Count == 0 && payload.BuissonVide.Count == 0))
			{
				if (_mmGazon != null) _mmGazon.Multimesh = null;
				if (_mmBuissonPlein != null) _mmBuissonPlein.Multimesh = null;
				if (_mmBuissonVide != null) _mmBuissonVide.Multimesh = null;
				return;
			}
			Vector3 posObs = (GetParent() as Monde_Client)?.ObtenirPositionObservation() ?? GlobalPosition;
			float rayonCarre = (RAYON_GAZON_CHUNKS * TailleChunk) * (RAYON_GAZON_CHUNKS * TailleChunk);
			var gazonFiltre = new List<(Transform3D t, Color c)>();
			foreach (var item in payload.Gazon)
			{
				if (item.PosMonde.DistanceSquaredTo(posObs) <= rayonCarre)
					gazonFiltre.Add((item.T, item.C));
			}
			RemplirMultiMeshGazon(gazonFiltre);
			RemplirMultiMeshBuissons(payload.BuissonPlein, payload.BuissonVide);
		}
		catch (ObjectDisposedException) { /* Chunk déjà supprimé */ }
	}

	private void RemplirMultiMeshGazon(List<(Transform3D t, Color c)> gazonInstances)
	{
		if (gazonInstances == null || gazonInstances.Count == 0)
		{
			if (_mmGazon != null) _mmGazon.Multimesh = null;
			return;
		}

		// FORÇAGE ABSOLU : On utilise le générateur C#, on ignore tout fichier externe.
		if (_cacheMeshGazon == null)
		{
			_cacheMeshGazon = GenererMeshGazonProcedural();
		}

		Mesh meshGazon = _cacheMeshGazon;

		var mm = new MultiMesh();
		mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
		mm.UseColors = true; // Le Shader a besoin du canal couleur
		mm.Mesh = meshGazon;
		mm.InstanceCount = gazonInstances.Count;

		for (int i = 0; i < gazonInstances.Count; i++)
		{
			mm.SetInstanceTransform(i, gazonInstances[i].t);
			mm.SetInstanceColor(i, gazonInstances[i].c);
		}

		_mmGazon.Multimesh = mm;
		_mmGazon.MaterialOverride = ObtenirMaterielGazonSymbiotique();
		_mmGazon.Visible = true;
	}

	private void RemplirMultiMeshBuissons(List<Transform3D> pleins, List<Transform3D> vides)
	{
		Mesh meshPlein = ChargerMeshFlore("res://Modeles/Botanique/Buisson_Plein.glb");
		if (meshPlein != null && pleins != null && pleins.Count > 0)
		{
			var mm = new MultiMesh();
			mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
			mm.Mesh = meshPlein;
			mm.InstanceCount = pleins.Count;
			for (int i = 0; i < pleins.Count; i++) mm.SetInstanceTransform(i, pleins[i]);
			_mmBuissonPlein.Multimesh = mm;
		}
		else _mmBuissonPlein.Multimesh = null;
		Mesh meshVide = ChargerMeshFlore("res://Modeles/Botanique/Buisson_Vide.glb");
		if (meshVide != null && vides != null && vides.Count > 0)
		{
			var mm = new MultiMesh();
			mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
			mm.Mesh = meshVide;
			mm.InstanceCount = vides.Count;
			for (int i = 0; i < vides.Count; i++) mm.SetInstanceTransform(i, vides[i]);
			_mmBuissonVide.Multimesh = mm;
		}
		else _mmBuissonVide.Multimesh = null;
	}

	/// <summary>Met à jour UNIQUEMENT les MultiMesh (buissons). N'appelle JAMAIS ConstruireMeshSection — isolement absolu du terrain.</summary>
	public void MettreAJourRenduFlore(Dictionary<Vector3I, byte> inventaire)
	{
		_inventaireFloreCache = inventaire;
		_payloadFloreCache = null; // Force recalcul depuis inventaire si pas encore de payload
		ActualiserFloreAvecDistance();
	}

	/// <summary>Lissage temporel : ajoute les cailloux physiques 1-2 par frame pour éviter le Main Thread Blocking (pas de boucle AddChild massive).</summary>
	private async void AppliquerCaillouxPhysiques(List<Transform3D> positions)
	{
		if (positions == null || positions.Count == 0 || !IsInsideTree()) return;
		const int caillouxParFrame = 2;
		int compteur = 0;
		foreach (var pos in positions)
		{
			GenererCaillouPhysique(pos);
			compteur++;
			if (compteur >= caillouxParFrame)
			{
				compteur = 0;
				await ToSignal(GetTree(), "process_frame");
				if (!IsInsideTree()) return;
			}
		}
	}

	/// <summary>À surcharger ou appeler depuis le monde : crée un RigidBody3D (caillou/silex) à la position. Par défaut no-op (côté client les pierres sont gérées par le serveur).</summary>
	protected virtual void GenererCaillouPhysique(Transform3D pos)
	{
		// Côté client : les cailloux sont répliqués par le serveur. Pour du spawn local, override ou utiliser un callback depuis Monde_Client.
	}

	private void ActualiserFloreAvecDistance()
	{
		try
		{
			if (!IsInsideTree()) return;
			if (_payloadFloreCache != null)
			{
				AppliquerPayloadFlore(_payloadFloreCache);
				return;
			}
			var inventaire = _inventaireFloreCache;
			if (inventaire == null || inventaire.Count == 0)
			{
				if (_mmGazon != null) _mmGazon.Multimesh = null;
				if (_mmBuissonPlein != null) _mmBuissonPlein.Multimesh = null;
				if (_mmBuissonVide != null) _mmBuissonVide.Multimesh = null;
				return;
			}
		Vector3 chunkOrigin = GlobalPosition;
		var pleins = new List<Transform3D>();
		var vides = new List<Transform3D>();
		var gazonInstances = new List<(Transform3D t, Color c)>();

		Vector3 posObs = (GetParent() as Monde_Client)?.ObtenirPositionObservation() ?? chunkOrigin;
		float rayonCarre = (RAYON_GAZON_CHUNKS * TailleChunk) * (RAYON_GAZON_CHUNKS * TailleChunk);

		foreach (var kv in inventaire)
		{
			Vector3 positionLocale = new Vector3(kv.Key.X, kv.Key.Y + 0.5f, kv.Key.Z) - chunkOrigin + new Vector3(0.5f, 0f, 0.5f);
			float angle = (float)((kv.Key.X * 73856093 ^ kv.Key.Z * 19349663) % 10000) / 10000f * Mathf.Tau;
			Vector3 posMonde = new Vector3(kv.Key.X + 0.5f, kv.Key.Y + 0.5f, kv.Key.Z + 0.5f);

			if (posMonde.DistanceSquaredTo(posObs) <= rayonCarre)
			{
				Color couleurSol = ObtenirCouleurTerrainApprox(kv.Key.X, kv.Key.Y, kv.Key.Z);
				Color couleurHerbe = new Color(couleurSol.R * 0.8f, couleurSol.G * 0.8f, couleurSol.B * 0.8f, 1f).Lerp(new Color(0.7f, 0.8f, 1f), 0.1f);
				uint hashBase = (uint)(kv.Key.X * 73856093) ^ (uint)(kv.Key.Z * 19349663);
				int densiteGazon = 14;
				for (int i = 0; i < densiteGazon; i++)
				{
					uint h_brin = hashBase ^ (uint)(i * 83492791);
					float offsetX = ((h_brin % 100) / 100f) - 0.5f;
					float offsetZ = (((h_brin / 100) % 100) / 100f) - 0.5f;
					float echelleAlea = 0.5f + ((h_brin % 50) / 100f);
					float angleBrin = (h_brin % 360) * Mathf.Pi / 180f;
					var tGazon = Transform3D.Identity;
					tGazon.Origin = positionLocale + new Vector3(offsetX, 0, offsetZ);
					tGazon.Basis = Basis.Identity.Scaled(new Vector3(EchelleGazon * echelleAlea, EchelleGazon * echelleAlea, EchelleGazon * echelleAlea)).Rotated(Vector3.Up, angleBrin);
					gazonInstances.Add((tGazon, couleurHerbe));
				}
			}
			if (kv.Value == 1 || kv.Value == 2)
			{
				uint h = (uint)(kv.Key.X * 73856093) ^ (uint)(kv.Key.Z * 19349663) ^ (uint)(kv.Key.Y * 83492791);
				float echelleBuis = 0.009f + (h % 500) / 500f * 0.004f;
				var tBuis = Transform3D.Identity;
				tBuis.Origin = positionLocale;
				tBuis.Basis = Basis.Identity.Scaled(new Vector3(echelleBuis, echelleBuis, echelleBuis)).Rotated(Vector3.Up, angle);
				if (kv.Value == 1) pleins.Add(tBuis);
				else vides.Add(tBuis);
			}
		}
		RemplirMultiMeshGazon(gazonInstances);
		RemplirMultiMeshBuissons(pleins, vides);
		}
		catch (ObjectDisposedException) { /* Chunk déjà supprimé */ }
	}

	private static Mesh _cacheMeshGazon;
	private static Mesh _cacheMeshPlein;
	private static Mesh _cacheMeshVide;
	private static Material _cacheMaterielGazonSymbiotique;

	/// <summary>Couleur approximative du terrain à (x,y,z) — même formule que TerrainVoxel (temp/hum). Pour herbe symbiotique.</summary>
	private Color ObtenirCouleurTerrainApprox(int xGlobal, int yGlobal, int zGlobal)
	{
		if (_materialsFlat == null || _noiseTemperature == null || _noiseHumidite == null) return new Color(0.5f, 0.6f, 0.5f);
		int lx = xGlobal - ChunkOffsetX * TailleChunk;
		int lz = zGlobal - ChunkOffsetZ * TailleChunk;
		if (lx < 0 || lx > TailleChunk || yGlobal < 0 || yGlobal > HauteurMax || lz < 0 || lz > TailleChunk)
			return new Color(0.5f, 0.6f, 0.5f);
		byte idMat = _materialsFlat[Idx(lx, yGlobal, lz)];
		float temp = _noiseTemperature.GetNoise2D(xGlobal, zGlobal);
		float hum = _noiseHumidite.GetNoise2D(xGlobal, zGlobal);
		float facteurHum = Mathf.Clamp((hum + 1f) * 0.5f, 0f, 1f);
		// Gazon 3D : sec = jaunâtre, normal = vert, humide = vert foncé (comme shader terrain)
		Color sec = new Color(1.3f, 0.9f, 0.35f);
		Color normal = new Color(0.45f, 0.75f, 0.4f);
		Color humide = new Color(0.25f, 0.55f, 0.3f);
		Color couleurBase = facteurHum < 0.35f
			? sec.Lerp(normal, facteurHum / 0.35f)
			: normal.Lerp(humide, (facteurHum - 0.35f) / 0.65f);
		return couleurBase;
	}

	/// <summary>ShaderMaterial procédural : gazon mat et organique (pas de plastique), vent, dégradé naturel.</summary>
	private static Material ObtenirMaterielGazonSymbiotique()
	{
		if (_cacheMaterielGazonSymbiotique != null) return _cacheMaterielGazonSymbiotique;
		var shader = new Shader();
		shader.Code = @"
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform vec3 couleur_pointe = vec3(0.38, 0.52, 0.18);
uniform float force_vent = 0.15;
uniform float vitesse_vent = 2.0;

void vertex() {
	vec3 pos_monde = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float influence = 1.0 - UV.y;
	float vent = sin(TIME * vitesse_vent + pos_monde.x * 0.5 + pos_monde.z * 0.5);
	VERTEX.x += vent * force_vent * influence;
	VERTEX.z += cos(TIME * vitesse_vent + pos_monde.x * 0.5) * force_vent * influence;
}

void fragment() {
	vec3 couleur_base = COLOR.rgb;
	vec3 couleur_racine = couleur_base * 0.55;
	float mix_hauteur = 1.0 - UV.y;
	mix_hauteur = pow(mix_hauteur, 1.4);
	vec3 couleur_finale = mix(couleur_racine, couleur_pointe * couleur_base * 1.25, mix_hauteur);
	float bruit = fract(sin(dot(FRAGCOORD.xy, vec2(12.9898, 78.233))) * 43758.5453);
	couleur_finale *= 0.92 + bruit * 0.16;
	ALBEDO = couleur_finale;
	ROUGHNESS = 0.94;
	SPECULAR = 0.0;
	BACKLIGHT = couleur_finale * 0.12;
}
";
		var mat = new ShaderMaterial { Shader = shader };
		_cacheMaterielGazonSymbiotique = mat;
		return mat;
	}

	private static Mesh ChargerMeshFlore(string path)
	{
		if (path.Contains("Plein") && _cacheMeshPlein != null) return _cacheMeshPlein;
		if (path.Contains("Vide") && _cacheMeshVide != null) return _cacheMeshVide;
		Mesh mesh = ChargerMeshDepuisScene(path);
		if (mesh == null) { GD.PrintErr($"Chunk_Client: Échec extraction mesh depuis {path}"); return null; }
		if (path.Contains("Plein")) _cacheMeshPlein = mesh;
		else if (path.Contains("Vide")) _cacheMeshVide = mesh;
		return mesh;
	}

	private static Mesh ChargerMeshDepuisScene(string path)
	{
		var scene = GD.Load<PackedScene>(path);
		if (scene == null) return null;
		Node racine = scene.Instantiate();
		Mesh mesh = ExtraireMeilleurMeshRecursif(racine);
		racine.QueueFree();
		return mesh;
	}

	/// <summary>Collecte tous les meshes et choisit le plus gros (grass.glb peut avoir structure différente des buissons).</summary>
	private static Mesh ExtraireMeilleurMeshRecursif(Node noeud)
	{
		var liste = new List<Mesh>();
		CollecterMeshes(noeud, liste);
		if (liste.Count == 0) return null;
		if (liste.Count == 1) return liste[0];
		Mesh meilleur = liste[0];
		int maxSurfaces = MeshSurfaceCount(meilleur);
		foreach (var m in liste)
		{
			int s = MeshSurfaceCount(m);
			if (s > maxSurfaces) { meilleur = m; maxSurfaces = s; }
		}
		return meilleur;
	}

	private static void CollecterMeshes(Node noeud, List<Mesh> liste)
	{
		if (noeud is MeshInstance3D mi && mi.Mesh != null && MeshSurfaceCount(mi.Mesh) > 0)
			liste.Add(mi.Mesh);
		foreach (Node enfant in noeud.GetChildren())
			CollecterMeshes(enfant, liste);
	}

	private static int MeshSurfaceCount(Mesh m)
	{
		if (m == null) return 0;
		if (m is ArrayMesh am) return am.GetSurfaceCount();
		return 1;
	}

	/// <summary>Génère 3 lames triangulaires (effilées) croisées à 60°. Normales biaisées vers le ciel pour éclairage unifié. Canal COLOR requis pour MultiMesh UseColors.</summary>
	private static Mesh GenererMeshGazonProcedural()
	{
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		// FIX CRITIQUE : Création du canal de couleur pour autoriser le MultiMesh à peindre !
		st.SetColor(new Color(1f, 1f, 1f, 1f));

		float w = 0.06f;
		float h = 0.175f;

		void CreerLame(Vector3 centre, float angleY)
		{
			Vector3 dirX = new Vector3(Mathf.Cos(angleY), 0, Mathf.Sin(angleY)) * (w / 2f);
			Vector3 p0 = centre - dirX;
			Vector3 p1 = centre + dirX;
			Vector3 pTop = centre + new Vector3(0, h, 0);

			// Normale biaisée vers le ciel (80% Up, 20% plan) : unifie l'éclairage avec le terrain, plus d'effet "X"
			Vector3 normal = (Vector3.Up * 0.8f + new Vector3(-dirX.Z, 0, dirX.X).Normalized() * 0.2f).Normalized();

			st.SetNormal(normal); st.SetUV(new Vector2(0, 1)); st.AddVertex(p0);
			st.SetNormal(normal); st.SetUV(new Vector2(1, 1)); st.AddVertex(p1);
			st.SetNormal(normal); st.SetUV(new Vector2(0.5f, 0)); st.AddVertex(pTop);
		}

		CreerLame(Vector3.Zero, 0f);
		CreerLame(Vector3.Zero, Mathf.Pi / 3f);
		CreerLame(Vector3.Zero, 2f * Mathf.Pi / 3f);

		st.GenerateTangents();
		return st.Commit();
	}

	private static IEnumerable<Node> ObtenirTousLesNoeuds(Node n)
	{
		yield return n;
		foreach (Node enfant in n.GetChildren())
			foreach (Node descendant in ObtenirTousLesNoeuds(enfant))
				yield return descendant;
	}

	private (ArrayMesh terrain, ArrayMesh eau) ConstruireMeshSection(int indexSection, float baseX, float baseZ)
	{
		int yDebut = indexSection * HAUTEUR_SECTION;
		int yFin = Math.Min(yDebut + HAUTEUR_SECTION, HauteurMax);
		int tailleY = yFin - yDebut + 1;
		int tc = TailleChunk;
		int tx = tc + 1, tz = tc + 1;

		// Rembourrage 17³ : tableau padded, aucune interrogation voisin. Lookup local pur.
		float DensitePourMesh(int x, int y, int z) => _densitiesFlat[Idx(x, yDebut + y, z)];

		if (_valsRecyclables == null) _valsRecyclables = new float[8];
		if (_vertsRecyclables == null) _vertsRecyclables = new Vector3[8];
		if (_vertListRecyclables == null) _vertListRecyclables = new Vector3[12];

		var bufferDensities = ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION);
		var bufferMaterials = ArrayPool<byte>.Shared.Rent(TAILLE_MAX_SECTION);
		float[] bufferEau = _densitiesEauFlat != null ? ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION) : null;
		ArrayMesh meshTerrain = null;
		ArrayMesh meshEau = null;
		try
		{
		int stride = tailleY * tz;
		for (int x = 0; x < tx; x++)
			for (int y = 0; y < tailleY; y++)
				for (int z = 0; z < tz; z++)
				{
					int idx = x * stride + y * tz + z;
					bufferDensities[idx] = DensitePourMesh(x, y, z);
					bufferMaterials[idx] = _materialsFlat[Idx(x, yDebut + y, z)];
					if (bufferEau != null) bufferEau[idx] = _densitiesEauFlat[Idx(x, yDebut + y, z)];
				}

		float ValD(int x, int y, int z) => bufferDensities[x * stride + y * tz + z];
		byte MatD(int x, int y, int z) => bufferMaterials[x * stride + y * tz + z];
		float EauD(int x, int y, int z) => bufferEau[x * stride + y * tz + z];

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		float[] vals = _valsRecyclables;
		Vector3[] verts = _vertsRecyclables;
		var edgeTable = ConstantesMarchingCubes.EdgeTable;
		var triTable = ConstantesMarchingCubes.TriTable;

		for (int x = 0; x < tc; x++)
			for (int y = 0; y < yFin - yDebut; y++)
			{
				int yG = yDebut + y;
				for (int z = 0; z < tc; z++)
				{
					verts[0] = new Vector3(x, yG, z);
					verts[1] = new Vector3(x + 1, yG, z);
					verts[2] = new Vector3(x + 1, yG + 1, z);
					verts[3] = new Vector3(x, yG + 1, z);
					verts[4] = new Vector3(x, yG, z + 1);
					verts[5] = new Vector3(x + 1, yG, z + 1);
					verts[6] = new Vector3(x + 1, yG + 1, z + 1);
					verts[7] = new Vector3(x, yG + 1, z + 1);

					vals[0] = ValD(x, y, z);
					vals[1] = ValD(x + 1, y, z);
					vals[2] = ValD(x + 1, y + 1, z);
					vals[3] = ValD(x, y + 1, z);
					vals[4] = ValD(x, y, z + 1);
					vals[5] = ValD(x + 1, y, z + 1);
					vals[6] = ValD(x + 1, y + 1, z + 1);
					vals[7] = ValD(x, y + 1, z + 1);

					int cubeIndex = 0;
					for (int i = 0; i < 8; i++)
						if (vals[i] > Isolevel) cubeIndex |= 1 << i;
					if (edgeTable[cubeIndex] == 0) continue;

					Vector3[] vertList = _vertListRecyclables;
					vertList[0] = Interp(verts[0], verts[1], vals[0], vals[1]);
					vertList[1] = Interp(verts[1], verts[2], vals[1], vals[2]);
					vertList[2] = Interp(verts[2], verts[3], vals[2], vals[3]);
					vertList[3] = Interp(verts[3], verts[0], vals[3], vals[0]);
					vertList[4] = Interp(verts[4], verts[5], vals[4], vals[5]);
					vertList[5] = Interp(verts[5], verts[6], vals[5], vals[6]);
					vertList[6] = Interp(verts[6], verts[7], vals[6], vals[7]);
					vertList[7] = Interp(verts[7], verts[4], vals[7], vals[4]);
					vertList[8] = Interp(verts[0], verts[4], vals[0], vals[4]);
					vertList[9] = Interp(verts[1], verts[5], vals[1], vals[5]);
					vertList[10] = Interp(verts[2], verts[6], vals[2], vals[6]);
					vertList[11] = Interp(verts[3], verts[7], vals[3], vals[7]);

					byte idMat = MatD(x, y, z);
					if (idMat == 0)
					{
						int sy = y;
						while (sy > 0 && MatD(x, sy, z) == 0) sy--;
						idMat = MatD(x, sy, z);
						if (idMat == 0) idMat = 2;
					}

					// Espace GLOBAL du monde — évite le tiling biomique (couleurs temp/hum).
					float xGlobal = baseX + x;
					float zGlobal = baseZ + z;
					float temp = _noiseTemperature?.GetNoise2D(xGlobal, zGlobal) ?? 0f;
					float hum = _noiseHumidite?.GetNoise2D(xGlobal, zGlobal) ?? 0f;
					Color couleurId = new Color(idMat / 255f, (temp + 1f) * 0.5f, (hum + 1f) * 0.5f, 1f);

					for (int i = 0; triTable[cubeIndex, i] != -1; i += 3)
					{
						Vector3 v0 = vertList[triTable[cubeIndex, i]];
						Vector3 v1 = vertList[triTable[cubeIndex, i + 1]];
						Vector3 v2 = vertList[triTable[cubeIndex, i + 2]];
						Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
						st.SetNormal(n);
						st.SetColor(couleurId);
						st.AddVertex(v0);
						st.SetNormal(n);
						st.SetColor(couleurId);
						st.AddVertex(v1);
						st.SetNormal(n);
						st.SetColor(couleurId);
						st.AddVertex(v2);
					}
				}
			}

		st.GenerateNormals();
		meshTerrain = st.Commit();

		if (bufferEau != null)
		{
			if (_valsEauRecyclables == null) _valsEauRecyclables = new float[8];
			if (_vertsEauRecyclables == null) _vertsEauRecyclables = new Vector3[8];
			if (_vertListEauRecyclables == null) _vertListEauRecyclables = new Vector3[12];

			var stEau = new SurfaceTool();
			stEau.Begin(Mesh.PrimitiveType.Triangles);
			float[] valsEau = _valsEauRecyclables;
			Vector3[] vertsEau = _vertsEauRecyclables;
			for (int x = 0; x < tc; x++)
				for (int y = 0; y < yFin - yDebut; y++)
				{
					int yG = yDebut + y;
					for (int z = 0; z < tc; z++)
					{
						vertsEau[0] = new Vector3(x, yG, z);
						vertsEau[1] = new Vector3(x + 1, yG, z);
						vertsEau[2] = new Vector3(x + 1, yG + 1, z);
						vertsEau[3] = new Vector3(x, yG + 1, z);
						vertsEau[4] = new Vector3(x, yG, z + 1);
						vertsEau[5] = new Vector3(x + 1, yG, z + 1);
						vertsEau[6] = new Vector3(x + 1, yG + 1, z + 1);
						vertsEau[7] = new Vector3(x, yG + 1, z + 1);
						valsEau[0] = EauD(x, y, z);
						valsEau[1] = EauD(x + 1, y, z);
						valsEau[2] = EauD(x + 1, y + 1, z);
						valsEau[3] = EauD(x, y + 1, z);
						valsEau[4] = EauD(x, y, z + 1);
						valsEau[5] = EauD(x + 1, y, z + 1);
						valsEau[6] = EauD(x + 1, y + 1, z + 1);
						valsEau[7] = EauD(x, y + 1, z + 1);
						int ci = 0;
						for (int i = 0; i < 8; i++)
							if (valsEau[i] > Isolevel) ci |= 1 << i;
						if (edgeTable[ci] == 0) continue;
						Vector3[] vl = _vertListEauRecyclables;
						vl[0] = Interp(vertsEau[0], vertsEau[1], valsEau[0], valsEau[1]);
						vl[1] = Interp(vertsEau[1], vertsEau[2], valsEau[1], valsEau[2]);
						vl[2] = Interp(vertsEau[2], vertsEau[3], valsEau[2], valsEau[3]);
						vl[3] = Interp(vertsEau[3], vertsEau[0], valsEau[3], valsEau[0]);
						vl[4] = Interp(vertsEau[4], vertsEau[5], valsEau[4], valsEau[5]);
						vl[5] = Interp(vertsEau[5], vertsEau[6], valsEau[5], valsEau[6]);
						vl[6] = Interp(vertsEau[6], vertsEau[7], valsEau[6], valsEau[7]);
						vl[7] = Interp(vertsEau[7], vertsEau[4], valsEau[7], valsEau[4]);
						vl[8] = Interp(vertsEau[0], vertsEau[4], valsEau[0], valsEau[4]);
						vl[9] = Interp(vertsEau[1], vertsEau[5], valsEau[1], valsEau[5]);
						vl[10] = Interp(vertsEau[2], vertsEau[6], valsEau[2], valsEau[6]);
						vl[11] = Interp(vertsEau[3], vertsEau[7], valsEau[3], valsEau[7]);
						for (int i = 0; triTable[ci, i] != -1; i += 3)
						{
							Vector3 v0 = vl[triTable[ci, i]], v1 = vl[triTable[ci, i + 1]], v2 = vl[triTable[ci, i + 2]];
							Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
							stEau.SetNormal(n);
							stEau.AddVertex(v0);
							stEau.SetNormal(n);
							stEau.AddVertex(v1);
							stEau.SetNormal(n);
							stEau.AddVertex(v2);
						}
					}
				}
			stEau.GenerateNormals();
			meshEau = stEau.Commit();
		}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(bufferDensities);
			ArrayPool<byte>.Shared.Return(bufferMaterials);
			if (bufferEau != null) ArrayPool<float>.Shared.Return(bufferEau);
		}
		return (meshTerrain, meshEau);
	}

	/// <summary>Construit les données de mesh/collision en arrière-plan sans aucune ressource Godot (listes C# uniquement). Consommé par le Main Thread via CreerMeshesDepuisPayload.</summary>
	private SectionPayload ConstruireSectionPayloadEnBackground(int indexSection, float baseX, float baseZ)
	{
		int yDebut = indexSection * HAUTEUR_SECTION;
		int yFin = Math.Min(yDebut + HAUTEUR_SECTION, HauteurMax);
		int tailleY = yFin - yDebut + 1;
		int tc = TailleChunk;
		int tx = tc + 1, tz = tc + 1;

		float DensitePourMesh(int x, int y, int z) => _densitiesFlat[Idx(x, yDebut + y, z)];

		if (_valsRecyclables == null) _valsRecyclables = new float[8];
		if (_vertsRecyclables == null) _vertsRecyclables = new Vector3[8];
		if (_vertListRecyclables == null) _vertListRecyclables = new Vector3[12];

		var bufferDensities = ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION);
		var bufferMaterials = ArrayPool<byte>.Shared.Rent(TAILLE_MAX_SECTION);
		float[] bufferEau = _densitiesEauFlat != null ? ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION) : null;

		var vertsT = new List<Vector3>();
		var normsT = new List<Vector3>();
		var colsT = new List<Color>();
		List<Vector3> vertsE = bufferEau != null ? new List<Vector3>() : null;
		List<Vector3> normsE = bufferEau != null ? new List<Vector3>() : null;

		try
		{
			int stride = tailleY * tz;
			for (int x = 0; x < tx; x++)
				for (int y = 0; y < tailleY; y++)
					for (int z = 0; z < tz; z++)
					{
						int idx = x * stride + y * tz + z;
						bufferDensities[idx] = DensitePourMesh(x, y, z);
						bufferMaterials[idx] = _materialsFlat[Idx(x, yDebut + y, z)];
						if (bufferEau != null) bufferEau[idx] = _densitiesEauFlat[Idx(x, yDebut + y, z)];
					}

			float ValD(int x, int y, int z) => bufferDensities[x * stride + y * tz + z];
			byte MatD(int x, int y, int z) => bufferMaterials[x * stride + y * tz + z];
			float EauD(int x, int y, int z) => bufferEau[x * stride + y * tz + z];

			float[] vals = _valsRecyclables;
			Vector3[] verts = _vertsRecyclables;
			var edgeTable = ConstantesMarchingCubes.EdgeTable;
			var triTable = ConstantesMarchingCubes.TriTable;

			for (int x = 0; x < tc; x++)
				for (int y = 0; y < yFin - yDebut; y++)
				{
					int yG = yDebut + y;
					for (int z = 0; z < tc; z++)
					{
						verts[0] = new Vector3(x, yG, z);
						verts[1] = new Vector3(x + 1, yG, z);
						verts[2] = new Vector3(x + 1, yG + 1, z);
						verts[3] = new Vector3(x, yG + 1, z);
						verts[4] = new Vector3(x, yG, z + 1);
						verts[5] = new Vector3(x + 1, yG, z + 1);
						verts[6] = new Vector3(x + 1, yG + 1, z + 1);
						verts[7] = new Vector3(x, yG + 1, z + 1);

						vals[0] = ValD(x, y, z);
						vals[1] = ValD(x + 1, y, z);
						vals[2] = ValD(x + 1, y + 1, z);
						vals[3] = ValD(x, y + 1, z);
						vals[4] = ValD(x, y, z + 1);
						vals[5] = ValD(x + 1, y, z + 1);
						vals[6] = ValD(x + 1, y + 1, z + 1);
						vals[7] = ValD(x, y + 1, z + 1);

						int cubeIndex = 0;
						for (int i = 0; i < 8; i++)
							if (vals[i] > Isolevel) cubeIndex |= 1 << i;
						if (edgeTable[cubeIndex] == 0) continue;

						Vector3[] vertList = _vertListRecyclables;
						vertList[0] = Interp(verts[0], verts[1], vals[0], vals[1]);
						vertList[1] = Interp(verts[1], verts[2], vals[1], vals[2]);
						vertList[2] = Interp(verts[2], verts[3], vals[2], vals[3]);
						vertList[3] = Interp(verts[3], verts[0], vals[3], vals[0]);
						vertList[4] = Interp(verts[4], verts[5], vals[4], vals[5]);
						vertList[5] = Interp(verts[5], verts[6], vals[5], vals[6]);
						vertList[6] = Interp(verts[6], verts[7], vals[6], vals[7]);
						vertList[7] = Interp(verts[7], verts[4], vals[7], vals[4]);
						vertList[8] = Interp(verts[0], verts[4], vals[0], vals[4]);
						vertList[9] = Interp(verts[1], verts[5], vals[1], vals[5]);
						vertList[10] = Interp(verts[2], verts[6], vals[2], vals[6]);
						vertList[11] = Interp(verts[3], verts[7], vals[3], vals[7]);

						byte idMat = MatD(x, y, z);
						if (idMat == 0)
						{
							int sy = y;
							while (sy > 0 && MatD(x, sy, z) == 0) sy--;
							idMat = MatD(x, sy, z);
							if (idMat == 0) idMat = 2;
						}

						float xGlobal = baseX + x;
						float zGlobal = baseZ + z;
						float temp = _noiseTemperature?.GetNoise2D(xGlobal, zGlobal) ?? 0f;
						float hum = _noiseHumidite?.GetNoise2D(xGlobal, zGlobal) ?? 0f;
						Color couleurId = new Color(idMat / 255f, (temp + 1f) * 0.5f, (hum + 1f) * 0.5f, 1f);

						for (int i = 0; triTable[cubeIndex, i] != -1; i += 3)
						{
							Vector3 v0 = vertList[triTable[cubeIndex, i]];
							Vector3 v1 = vertList[triTable[cubeIndex, i + 1]];
							Vector3 v2 = vertList[triTable[cubeIndex, i + 2]];
							Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
							vertsT.Add(v0); vertsT.Add(v1); vertsT.Add(v2);
							normsT.Add(n); normsT.Add(n); normsT.Add(n);
							colsT.Add(couleurId); colsT.Add(couleurId); colsT.Add(couleurId);
						}
					}
				}

			if (bufferEau != null)
			{
				if (_valsEauRecyclables == null) _valsEauRecyclables = new float[8];
				if (_vertsEauRecyclables == null) _vertsEauRecyclables = new Vector3[8];
				if (_vertListEauRecyclables == null) _vertListEauRecyclables = new Vector3[12];

				float[] valsEau = _valsEauRecyclables;
				Vector3[] vertsEau = _vertsEauRecyclables;
				for (int x = 0; x < tc; x++)
					for (int y = 0; y < yFin - yDebut; y++)
					{
						int yG = yDebut + y;
						for (int z = 0; z < tc; z++)
						{
							vertsEau[0] = new Vector3(x, yG, z);
							vertsEau[1] = new Vector3(x + 1, yG, z);
							vertsEau[2] = new Vector3(x + 1, yG + 1, z);
							vertsEau[3] = new Vector3(x, yG + 1, z);
							vertsEau[4] = new Vector3(x, yG, z + 1);
							vertsEau[5] = new Vector3(x + 1, yG, z + 1);
							vertsEau[6] = new Vector3(x + 1, yG + 1, z + 1);
							vertsEau[7] = new Vector3(x, yG + 1, z + 1);
							valsEau[0] = EauD(x, y, z);
							valsEau[1] = EauD(x + 1, y, z);
							valsEau[2] = EauD(x + 1, y + 1, z);
							valsEau[3] = EauD(x, y + 1, z);
							valsEau[4] = EauD(x, y, z + 1);
							valsEau[5] = EauD(x + 1, y, z + 1);
							valsEau[6] = EauD(x + 1, y + 1, z + 1);
							valsEau[7] = EauD(x, y + 1, z + 1);
							int ci = 0;
							for (int i = 0; i < 8; i++)
								if (valsEau[i] > Isolevel) ci |= 1 << i;
							if (edgeTable[ci] == 0) continue;
							Vector3[] vl = _vertListEauRecyclables;
							vl[0] = Interp(vertsEau[0], vertsEau[1], valsEau[0], valsEau[1]);
							vl[1] = Interp(vertsEau[1], vertsEau[2], valsEau[1], valsEau[2]);
							vl[2] = Interp(vertsEau[2], vertsEau[3], valsEau[2], valsEau[3]);
							vl[3] = Interp(vertsEau[3], vertsEau[0], valsEau[3], valsEau[0]);
							vl[4] = Interp(vertsEau[4], vertsEau[5], valsEau[4], valsEau[5]);
							vl[5] = Interp(vertsEau[5], vertsEau[6], valsEau[5], valsEau[6]);
							vl[6] = Interp(vertsEau[6], vertsEau[7], valsEau[6], valsEau[7]);
							vl[7] = Interp(vertsEau[7], vertsEau[4], valsEau[7], valsEau[4]);
							vl[8] = Interp(vertsEau[0], vertsEau[4], valsEau[0], valsEau[4]);
							vl[9] = Interp(vertsEau[1], vertsEau[5], valsEau[1], valsEau[5]);
							vl[10] = Interp(vertsEau[2], vertsEau[6], valsEau[2], valsEau[6]);
							vl[11] = Interp(vertsEau[3], vertsEau[7], valsEau[3], valsEau[7]);
							for (int i = 0; triTable[ci, i] != -1; i += 3)
							{
								Vector3 v0 = vl[triTable[ci, i]], v1 = vl[triTable[ci, i + 1]], v2 = vl[triTable[ci, i + 2]];
								Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
								vertsE.Add(v0); vertsE.Add(v1); vertsE.Add(v2);
								normsE.Add(n); normsE.Add(n); normsE.Add(n);
							}
						}
					}
			}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(bufferDensities);
			ArrayPool<byte>.Shared.Return(bufferMaterials);
			if (bufferEau != null) ArrayPool<float>.Shared.Return(bufferEau);
		}

		return new SectionPayload
		{
			SommetsVisuels = vertsT.ToArray(),
			NormalsVisuels = normsT.ToArray(),
			CouleursVisuels = colsT.ToArray(),
			SommetsEau = vertsE?.Count > 0 ? vertsE.ToArray() : null,
			NormalsEau = normsE?.Count > 0 ? normsE.ToArray() : null
		};
	}

	/// <summary>Remplit ChunkData depuis DonneesChunk et construit les 45 SectionPayload (pour architecture AAA / RID). Appelé depuis le worker.</summary>
	public static List<SectionPayload> RemplirEtConstruirePayloads(ChunkData data, DonneesChunk donnees)
	{
		if (donnees?.MaterialsFlat == null) return null;
		bool formatQuantifie = donnees.DensitiesQuantifiees != null;
		int tx = donnees.TailleChunk + 1, ty = donnees.HauteurMax + 1, tz = donnees.TailleChunk + 1;
		float baseX = donnees.CoordChunk.X * (float)donnees.TailleChunk;
		float baseZ = donnees.CoordChunk.Y * (float)donnees.TailleChunk;

		if (formatQuantifie)
		{
			data.DensitiesFlat = DonneesChunk.DecompresserDensitesFlat(donnees.DensitiesQuantifiees, tx, ty, tz);
			data.DensitiesEauFlat = donnees.DensitiesEauQuantifiees != null
				? DonneesChunk.DecompresserDensitesFlat(donnees.DensitiesEauQuantifiees, tx, ty, tz)
				: null;
		}
		else
		{
			data.DensitiesFlat = (float[])donnees.DensitiesFlat.Clone();
			data.DensitiesEauFlat = donnees.DensitiesEauFlat != null ? (float[])donnees.DensitiesEauFlat.Clone() : null;
		}
		data.Tx = tx; data.Ty = ty; data.Tz = tz;
		data.MaterialsFlat = (byte[])donnees.MaterialsFlat.Clone();
		data.TailleChunk = donnees.TailleChunk;
		data.HauteurMax = donnees.HauteurMax;

		// Génération flore à la création du chunk : surface = voxel solide avec vide au-dessus (gazon partout où c'est possible)
		data.InventaireFlore = GenererInventaireFloreDepuisSurface(data);

		var payloads = new List<SectionPayload>(NB_SECTIONS);
		for (int i = 0; i < NB_SECTIONS; i++)
			payloads.Add(ConstruireSectionPayloadEnBackgroundFromData(data, i, baseX, baseZ));
		return payloads;
	}

	/// <summary>Crée le nœud MultiMeshInstance3D de gazon pour un ChunkData (architecture AAA). À ajouter au monde et à libérer dans data.LibérerRids.</summary>
	public static MultiMeshInstance3D CreerNoeudGazonPourChunkData(ChunkData data, Vector3 positionObservation, int tailleChunk)
	{
		var instances = ConstruireGazonInstancesPourChunkData(data, positionObservation);
		if (instances == null || instances.Count == 0) return null;
		if (_cacheMeshGazon == null) _cacheMeshGazon = GenererMeshGazonProcedural();
		Mesh meshGazon = _cacheMeshGazon;
		if (meshGazon == null) return null;
		var mm = CreerMultiMeshGazon(instances, meshGazon);
		var node = new MultiMeshInstance3D { Name = "Gazon" };
		node.Multimesh = mm;
		node.MaterialOverride = ObtenirMaterielGazonSymbiotique();
		node.Position = new Vector3(data.Coordonnees.X * tailleChunk, 0, data.Coordonnees.Y * tailleChunk);
		node.Visible = true;
		return node;
	}

	/// <summary>Met à jour le MultiMesh du nœud gazon quand la flore a été purgée côté serveur (minage, gravité, fauchage). Les brins disparaissent visuellement.</summary>
	public static void MettreAJourGazonPourChunkData(ChunkData data, Vector3 positionObservation, MultiMeshInstance3D nodeGazon)
	{
		if (data == null || nodeGazon == null) return;
		var instances = ConstruireGazonInstancesPourChunkData(data, positionObservation);
		if (_cacheMeshGazon == null) _cacheMeshGazon = GenererMeshGazonProcedural();
		Mesh meshGazon = _cacheMeshGazon;
		if (meshGazon == null) return;
		nodeGazon.Multimesh = instances.Count == 0 ? null : CreerMultiMeshGazon(instances, meshGazon);
	}

	private static MultiMesh CreerMultiMeshGazon(List<(Transform3D t, Color c)> instances, Mesh meshGazon)
	{
		var mm = new MultiMesh();
		mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
		mm.UseColors = true;
		mm.Mesh = meshGazon;
		mm.InstanceCount = instances.Count;
		for (int i = 0; i < instances.Count; i++)
		{
			mm.SetInstanceTransform(i, instances[i].t);
			mm.SetInstanceColor(i, instances[i].c);
		}
		return mm;
	}

	/// <summary>Construit la liste (transform, couleur) pour le gazon d'un ChunkData. Tout le gazon du chunk est ajouté dès l'intégration (pas de filtre distance).</summary>
	public static List<(Transform3D t, Color c)> ConstruireGazonInstancesPourChunkData(ChunkData data, Vector3 positionObservation)
	{
		var liste = new List<(Transform3D t, Color c)>();
		if (data?.InventaireFlore == null || data.InventaireFlore.Count == 0) return liste;
		float originX = data.Coordonnees.X * (float)data.TailleChunk;
		float originZ = data.Coordonnees.Y * (float)data.TailleChunk;
		Vector3 chunkOrigin = new Vector3(originX, 0, originZ);
		foreach (var kv in data.InventaireFlore)
		{
			if (kv.Value != 0) continue; // gazon uniquement
			Vector3 positionLocale = new Vector3(kv.Key.X, kv.Key.Y + 0.5f, kv.Key.Z) - chunkOrigin + new Vector3(0.5f, 0f, 0.5f);
			Color couleurSol = ObtenirCouleurTerrainDepuisChunkData(data, kv.Key.X, kv.Key.Y, kv.Key.Z);
			Color couleurHerbe = new Color(couleurSol.R * 0.8f, couleurSol.G * 0.8f, couleurSol.B * 0.8f, 1f).Lerp(new Color(0.7f, 0.8f, 1f), 0.1f);
			uint hashBase = (uint)(kv.Key.X * 73856093) ^ (uint)(kv.Key.Z * 19349663);
			int densiteGazon = 14;
			for (int i = 0; i < densiteGazon; i++)
			{
				uint h_brin = hashBase ^ (uint)(i * 83492791);
				float offsetX = ((h_brin % 100) / 100f) - 0.5f;
				float offsetZ = (((h_brin / 100) % 100) / 100f) - 0.5f;
				float echelleAlea = 0.5f + ((h_brin % 50) / 100f);
				float angleBrin = (h_brin % 360) * Mathf.Pi / 180f;
				var t = Transform3D.Identity;
				t.Origin = positionLocale + new Vector3(offsetX, 0, offsetZ);
				t.Basis = Basis.Identity.Scaled(new Vector3(EchelleGazon * echelleAlea, EchelleGazon * echelleAlea, EchelleGazon * echelleAlea)).Rotated(Vector3.Up, angleBrin);
				liste.Add((t, couleurHerbe));
			}
		}
		return liste;
	}

	private static Color ObtenirCouleurTerrainDepuisChunkData(ChunkData data, int xGlobal, int yGlobal, int zGlobal)
	{
		if (data?.MaterialsFlat == null || data.NoiseTemperature == null || data.NoiseHumidite == null) return new Color(0.5f, 0.6f, 0.5f);
		int lx = xGlobal - data.Coordonnees.X * data.TailleChunk;
		int lz = zGlobal - data.Coordonnees.Y * data.TailleChunk;
		if (lx < 0 || lx > data.TailleChunk || yGlobal < 0 || yGlobal > data.HauteurMax || lz < 0 || lz > data.TailleChunk)
			return new Color(0.5f, 0.6f, 0.5f);
		float temp = data.NoiseTemperature.GetNoise2D(xGlobal, zGlobal);
		float hum = data.NoiseHumidite.GetNoise2D(xGlobal, zGlobal);
		float facteurHum = Mathf.Clamp((hum + 1f) * 0.5f, 0f, 1f);
		Color sec = new Color(1.3f, 0.9f, 0.35f);
		Color normal = new Color(0.45f, 0.75f, 0.4f);
		Color humide = new Color(0.25f, 0.55f, 0.3f);
		return facteurHum < 0.35f ? sec.Lerp(normal, facteurHum / 0.35f) : normal.Lerp(humide, (facteurHum - 0.35f) / 0.65f);
	}

	/// <summary>Génère l'inventaire flore (gazon) à partir de la surface du chunk. Appelé au chargement pour afficher l'herbe.</summary>
	private static Dictionary<Vector3I, byte> GenererInventaireFloreDepuisSurface(ChunkData data)
	{
		var inv = new Dictionary<Vector3I, byte>();
		if (data?.DensitiesFlat == null || data.MaterialsFlat == null || data.TailleChunk <= 0 || data.HauteurMax <= 0) return inv;
		const float isolevel = 0.0f;
		int tc = data.TailleChunk;
		int ox = data.Coordonnees.X * tc;
		int oz = data.Coordonnees.Y * tc;
		for (int lx = 0; lx < tc; lx++)
			for (int lz = 0; lz < tc; lz++)
			{
				int ySurface = -1;
				for (int y = data.HauteurMax - 1; y >= 0; y--)
				{
					float d = data.DensitiesFlat[data.Idx(lx, y, lz)];
					if (d <= isolevel) continue;
					bool videAuDessus = y + 1 > data.HauteurMax || data.DensitiesFlat[data.Idx(lx, y + 1, lz)] <= isolevel;
					if (videAuDessus) { ySurface = y; break; }
				}
				if (ySurface < 2) continue;
				byte mat = data.MaterialsFlat[data.Idx(lx, ySurface, lz)];
				if (mat != 1) continue; // gazon uniquement sur terre (id 1)
				float dy = data.DensitiesFlat[data.Idx(lx, Math.Min(ySurface + 1, data.HauteurMax), lz)] - data.DensitiesFlat[data.Idx(lx, Math.Max(0, ySurface - 1), lz)];
				float dx = data.DensitiesFlat[data.Idx(Math.Min(lx + 1, tc), ySurface, lz)] - data.DensitiesFlat[data.Idx(Math.Max(0, lx - 1), ySurface, lz)];
				float dz = data.DensitiesFlat[data.Idx(lx, ySurface, Math.Min(lz + 1, tc))] - data.DensitiesFlat[data.Idx(lx, ySurface, Math.Max(0, lz - 1))];
				Vector3 grad = new Vector3(dx, dy, dz);
				if (grad.LengthSquared() < 0.0001f) continue;
				Vector3 normal = (-grad).Normalized();
				if (normal.Y < 0.75f) continue; // seulement si la surface est plate (pas de brin en angle)
				var posGlobale = new Vector3I(ox + lx, ySurface, oz + lz);
				inv[posGlobale] = 0; // gazon
			}
		return inv;
	}

	/// <summary>Reconstruit les 45 SectionPayload à partir d'un ChunkData déjà rempli (minage/pose). Pour mise à jour visuelle après AppliquerVoxel.</summary>
	public static List<SectionPayload> ReconstruirePayloadsDepuisData(ChunkData data)
	{
		if (data?.DensitiesFlat == null || data.MaterialsFlat == null) return null;
		float baseX = data.Coordonnees.X * (float)data.TailleChunk;
		float baseZ = data.Coordonnees.Y * (float)data.TailleChunk;
		var payloads = new List<SectionPayload>(NB_SECTIONS);
		for (int i = 0; i < NB_SECTIONS; i++)
			payloads.Add(ConstruireSectionPayloadEnBackgroundFromData(data, i, baseX, baseZ));
		return payloads;
	}

	private const int TAILLE_MAX_SECTION_DATA = 17 * 17 * 17;
	private const float IsolevelData = 0.0f;

	/// <summary>Construit un SectionPayload à partir de ChunkData (sans Node). Utilise les tableaux plats et le bruit du data.</summary>
	private static SectionPayload ConstruireSectionPayloadEnBackgroundFromData(ChunkData data, int indexSection, float baseX, float baseZ)
	{
		// CAS B : tous les sommets sont en ESPACE LOCAL (x,z dans [0, TailleChunk]). Le placement
		// de l'instance (Monde_Client.IntegrerChunkDataRIDs) applique UNE SEULE FOIS (cx*TailleChunk, 0, cz*TailleChunk).
		const int hauteurSection = 16;
		int yDebut = indexSection * hauteurSection;
		int yFin = Math.Min(yDebut + hauteurSection, data.HauteurMax);
		int tailleY = yFin - yDebut + 1;
		int tc = data.TailleChunk;
		int tx = tc + 1, tz = tc + 1;

		float DensitePourMesh(int x, int y, int z) => data.DensitiesFlat[data.Idx(x, yDebut + y, z)];

		var bufferDensities = ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION_DATA);
		var bufferMaterials = ArrayPool<byte>.Shared.Rent(TAILLE_MAX_SECTION_DATA);
		float[] bufferEau = data.DensitiesEauFlat != null ? ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION_DATA) : null;
		var vertsT = new List<Vector3>();
		var normsT = new List<Vector3>();
		var colsT = new List<Color>();
		List<Vector3> vertsE = bufferEau != null ? new List<Vector3>() : null;
		List<Vector3> normsE = bufferEau != null ? new List<Vector3>() : null;
		float[] vals = new float[8];
		Vector3[] verts = new Vector3[8];
		var vertList = new Vector3[12];

		try
		{
			int stride = tailleY * tz;
			for (int x = 0; x < tx; x++)
				for (int y = 0; y < tailleY; y++)
					for (int z = 0; z < tz; z++)
					{
						int idx = x * stride + y * tz + z;
						bufferDensities[idx] = DensitePourMesh(x, y, z);
						bufferMaterials[idx] = data.MaterialsFlat[data.Idx(x, yDebut + y, z)];
						if (bufferEau != null) bufferEau[idx] = data.DensitiesEauFlat[data.Idx(x, yDebut + y, z)];
					}

			float ValD(int x, int y, int z) => bufferDensities[x * stride + y * tz + z];
			byte MatD(int x, int y, int z) => bufferMaterials[x * stride + y * tz + z];
			float EauD(int x, int y, int z) => bufferEau[x * stride + y * tz + z];

			var edgeTable = ConstantesMarchingCubes.EdgeTable;
			var triTable = ConstantesMarchingCubes.TriTable;
			var noiseT = data.NoiseTemperature;
			var noiseH = data.NoiseHumidite;

			for (int x = 0; x < tc; x++)
				for (int y = 0; y < yFin - yDebut; y++)
				{
					int yG = yDebut + y;
					for (int z = 0; z < tc; z++)
					{
						verts[0] = new Vector3(x, yG, z);
						verts[1] = new Vector3(x + 1, yG, z);
						verts[2] = new Vector3(x + 1, yG + 1, z);
						verts[3] = new Vector3(x, yG + 1, z);
						verts[4] = new Vector3(x, yG, z + 1);
						verts[5] = new Vector3(x + 1, yG, z + 1);
						verts[6] = new Vector3(x + 1, yG + 1, z + 1);
						verts[7] = new Vector3(x, yG + 1, z + 1);
						vals[0] = ValD(x, y, z); vals[1] = ValD(x + 1, y, z); vals[2] = ValD(x + 1, y + 1, z); vals[3] = ValD(x, y + 1, z);
						vals[4] = ValD(x, y, z + 1); vals[5] = ValD(x + 1, y, z + 1); vals[6] = ValD(x + 1, y + 1, z + 1); vals[7] = ValD(x, y + 1, z + 1);
						int cubeIndex = 0;
						for (int i = 0; i < 8; i++)
							if (vals[i] > IsolevelData) cubeIndex |= 1 << i;
						if (edgeTable[cubeIndex] == 0) continue;
						vertList[0] = Interp(verts[0], verts[1], vals[0], vals[1]);
						vertList[1] = Interp(verts[1], verts[2], vals[1], vals[2]);
						vertList[2] = Interp(verts[2], verts[3], vals[2], vals[3]);
						vertList[3] = Interp(verts[3], verts[0], vals[3], vals[0]);
						vertList[4] = Interp(verts[4], verts[5], vals[4], vals[5]);
						vertList[5] = Interp(verts[5], verts[6], vals[5], vals[6]);
						vertList[6] = Interp(verts[6], verts[7], vals[6], vals[7]);
						vertList[7] = Interp(verts[7], verts[4], vals[7], vals[4]);
						vertList[8] = Interp(verts[0], verts[4], vals[0], vals[4]);
						vertList[9] = Interp(verts[1], verts[5], vals[1], vals[5]);
						vertList[10] = Interp(verts[2], verts[6], vals[2], vals[6]);
						vertList[11] = Interp(verts[3], verts[7], vals[3], vals[7]);
						byte idMat = MatD(x, y, z);
						if (idMat == 0)
						{
							int sy = y;
							while (sy > 0 && MatD(x, sy, z) == 0) sy--;
							idMat = MatD(x, sy, z);
							if (idMat == 0) idMat = 2;
						}
						float xGlobal = baseX + x, zGlobal = baseZ + z;
						float temp = noiseT?.GetNoise2D(xGlobal, zGlobal) ?? 0f;
						float hum = noiseH?.GetNoise2D(xGlobal, zGlobal) ?? 0f;
						Color couleurId = new Color(idMat / 255f, (temp + 1f) * 0.5f, (hum + 1f) * 0.5f, 1f);
						for (int i = 0; triTable[cubeIndex, i] != -1; i += 3)
						{
							Vector3 v0 = vertList[triTable[cubeIndex, i]], v1 = vertList[triTable[cubeIndex, i + 1]], v2 = vertList[triTable[cubeIndex, i + 2]];
							Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
							vertsT.Add(v0); vertsT.Add(v1); vertsT.Add(v2);
							normsT.Add(n); normsT.Add(n); normsT.Add(n);
							colsT.Add(couleurId); colsT.Add(couleurId); colsT.Add(couleurId);
						}
					}
				}

			if (bufferEau != null)
			{
				float[] valsEau = new float[8];
				Vector3[] vertsEau = new Vector3[8];
				for (int x = 0; x < tc; x++)
					for (int y = 0; y < yFin - yDebut; y++)
					{
						int yG = yDebut + y;
						for (int z = 0; z < tc; z++)
						{
							vertsEau[0] = new Vector3(x, yG, z);
							vertsEau[1] = new Vector3(x + 1, yG, z);
							vertsEau[2] = new Vector3(x + 1, yG + 1, z);
							vertsEau[3] = new Vector3(x, yG + 1, z);
							vertsEau[4] = new Vector3(x, yG, z + 1);
							vertsEau[5] = new Vector3(x + 1, yG, z + 1);
							vertsEau[6] = new Vector3(x + 1, yG + 1, z + 1);
							vertsEau[7] = new Vector3(x, yG + 1, z + 1);
							valsEau[0] = EauD(x, y, z); valsEau[1] = EauD(x + 1, y, z); valsEau[2] = EauD(x + 1, y + 1, z); valsEau[3] = EauD(x, y + 1, z);
							valsEau[4] = EauD(x, y, z + 1); valsEau[5] = EauD(x + 1, y, z + 1); valsEau[6] = EauD(x + 1, y + 1, z + 1); valsEau[7] = EauD(x, y + 1, z + 1);
							int ci = 0;
							for (int i = 0; i < 8; i++)
								if (valsEau[i] > IsolevelData) ci |= 1 << i;
							if (edgeTable[ci] == 0) continue;
							vertList[0] = Interp(vertsEau[0], vertsEau[1], valsEau[0], valsEau[1]);
							vertList[1] = Interp(vertsEau[1], vertsEau[2], valsEau[1], valsEau[2]);
							vertList[2] = Interp(vertsEau[2], vertsEau[3], valsEau[2], valsEau[3]);
							vertList[3] = Interp(vertsEau[3], vertsEau[0], valsEau[3], valsEau[0]);
							vertList[4] = Interp(vertsEau[4], vertsEau[5], valsEau[4], valsEau[5]);
							vertList[5] = Interp(vertsEau[5], vertsEau[6], valsEau[5], valsEau[6]);
							vertList[6] = Interp(vertsEau[6], vertsEau[7], valsEau[6], valsEau[7]);
							vertList[7] = Interp(vertsEau[7], vertsEau[4], valsEau[7], valsEau[4]);
							vertList[8] = Interp(vertsEau[0], vertsEau[4], valsEau[0], valsEau[4]);
							vertList[9] = Interp(vertsEau[1], vertsEau[5], valsEau[1], valsEau[5]);
							vertList[10] = Interp(vertsEau[2], vertsEau[6], valsEau[2], valsEau[6]);
							vertList[11] = Interp(vertsEau[3], vertsEau[7], valsEau[3], valsEau[7]);
							for (int i = 0; triTable[ci, i] != -1; i += 3)
							{
								Vector3 v0 = vertList[triTable[ci, i]], v1 = vertList[triTable[ci, i + 1]], v2 = vertList[triTable[ci, i + 2]];
								Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
								vertsE.Add(v0); vertsE.Add(v1); vertsE.Add(v2);
								normsE.Add(n); normsE.Add(n); normsE.Add(n);
							}
						}
					}
			}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(bufferDensities);
			ArrayPool<byte>.Shared.Return(bufferMaterials);
			if (bufferEau != null) ArrayPool<float>.Shared.Return(bufferEau);
		}

		return new SectionPayload
		{
			SommetsVisuels = vertsT.ToArray(),
			NormalsVisuels = normsT.ToArray(),
			CouleursVisuels = colsT.ToArray(),
			SommetsEau = vertsE?.Count > 0 ? vertsE.ToArray() : null,
			NormalsEau = normsE?.Count > 0 ? normsE.ToArray() : null
		};
	}

	private static Vector3 Interp(Vector3 p1, Vector3 p2, float v1, float v2)
	{
		if (Mathf.Abs(Isolevel - v1) < 0.00001f) return p1;
		if (Mathf.Abs(Isolevel - v2) < 0.00001f) return p2;
		if (Mathf.Abs(v1 - v2) < 0.00001f) return p1;
		float t = (Isolevel - v1) / (v2 - v1);
		return p1 + t * (p2 - p1);
	}
}
