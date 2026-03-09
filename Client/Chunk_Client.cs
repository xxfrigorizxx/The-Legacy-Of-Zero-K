using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;

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

	private float[,,] _densities;
	private byte[,,] _materials;
	private float[,,] _densitiesEau;
	private FastNoiseLite _noiseTemperature;
	private FastNoiseLite _noiseHumidite;
	private Dictionary<Vector3I, byte> _inventaireFloreEnAttente;
	private Dictionary<Vector3I, byte> _inventaireFloreCache;
	private int _frameFlore;
	/// <summary>Rayon en chunks : seul le gazon (grass.glb) est visible dans cette zone autour du joueur. Les buissons restent visibles partout.</summary>
	private const int RAYON_GAZON_CHUNKS = 2;

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

	/// <summary>Reçoit les données du serveur. Stocke _densities/_materials en RAM AVANT toute construction visuelle. Meshes en goutte-à-goutte (MaxMeshesParFrame) pour éviter Upload Stall VRAM.</summary>
	public void RecevoirDonneesChunk(DonneesChunk donnees, Action<System.Action> enqueueMainThread)
	{
		if (donnees.MaterialsFlat == null) return;
		bool formatQuantifie = donnees.DensitiesQuantifiees != null;

		int tx = donnees.TailleChunk + 1, ty = donnees.HauteurMax + 1, tz = donnees.TailleChunk + 1;
		int tc = donnees.TailleChunk, hm = donnees.HauteurMax;
		// Position GLOBALE du chunk (évite tiling) : coordChunk * tailleChunk
		float baseX = donnees.CoordChunk.X * tc;
		float baseZ = donnees.CoordChunk.Y * tc;

		// CRITIQUE : Décompression + Marching Cubes dans Task.Run, jamais sur Main Thread
		Task.Run(() =>
		{
			if (formatQuantifie)
			{
				_densities = DonneesChunk.DecompresserDensites(donnees.DensitiesQuantifiees, tx, ty, tz);
				_densitiesEau = donnees.DensitiesEauQuantifiees != null
					? DonneesChunk.DecompresserDensites(donnees.DensitiesEauQuantifiees, tx, ty, tz)
					: null;
			}
			else
			{
				_densities = new float[tx, ty, tz];
				_densitiesEau = donnees.DensitiesEauFlat != null ? new float[tx, ty, tz] : null;
				int idx = 0;
				for (int x = 0; x < tx; x++)
					for (int y = 0; y < ty; y++)
						for (int z = 0; z < tz; z++)
						{
							_densities[x, y, z] = donnees.DensitiesFlat[idx];
							if (_densitiesEau != null) _densitiesEau[x, y, z] = donnees.DensitiesEauFlat[idx];
							idx++;
						}
			}

			int idxMat = 0;
			_materials = new byte[tx, ty, tz];
			for (int x = 0; x < tx; x++)
				for (int y = 0; y < ty; y++)
					for (int z = 0; z < tz; z++)
						_materials[x, y, z] = donnees.MaterialsFlat[idxMat++];

			_inventaireFloreEnAttente = donnees.InventaireFlore;
			// CallDeferred : s'exécute APRÈS AttacherEtPositionnerChunk (fin de frame) — évite race IsInsideTree
			enqueueMainThread?.Invoke(() => CallDeferred("AppliquerInventaireFloreEnAttente"));

			for (int i = 0; i < NB_SECTIONS; i++)
			{
				int idxSec = i;
				System.Threading.Tasks.Task.Run(() =>
				{
					var (meshTerrain, meshEau) = ConstruireMeshSection(idxSec, baseX, baseZ);
					enqueueMainThread?.Invoke(() => AppliquerMeshSection(idxSec, meshTerrain, meshEau));
				});
			}
		});
	}

	/// <summary>Met à jour un voxel aux coordonnées locales (pour réplication du padding des voisins).</summary>
	public void SetVoxelLocal(int lx, int ly, int lz, byte id)
	{
		if (_densities == null || lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk) return;
		if (id == 0)
		{
			_densities[lx, ly, lz] = -10f;
			_materials[lx, ly, lz] = 0;
			if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = -1f;
			// Purge flore locale : modèle 3D disparaît immédiatement quand le bloc sous lui est détruit
			var posGlobale = new Vector3I(ChunkOffsetX * TailleChunk + lx, ly, ChunkOffsetZ * TailleChunk + lz);
			if (_inventaireFloreCache != null && _inventaireFloreCache.Remove(posGlobale))
				ActualiserFloreAvecDistance();
		}
		else if (id == 4)
		{
			_densities[lx, ly, lz] = -10f;
			_materials[lx, ly, lz] = 4;
			if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = 1f;
		}
		else
		{
			_densities[lx, ly, lz] = 10f;
			_materials[lx, ly, lz] = id;
			if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = -1f;
		}
	}

	/// <summary>Applique une mise à jour voxel unique (eau/air/solide) depuis le serveur. Met à jour les données et lève le dirty flag — AUCUN Marching Cubes ici.</summary>
	public void AppliquerVoxelGlobal(Vector3I posGlobal, byte id)
	{
		if (_densities == null) return;
		int cx = Mathf.FloorToInt(posGlobal.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(posGlobal.Z / (float)TailleChunk);
		if (cx != ChunkOffsetX || cz != ChunkOffsetZ) return;
		int lx = posGlobal.X - ChunkOffsetX * TailleChunk;
		int lz = posGlobal.Z - ChunkOffsetZ * TailleChunk;
		int ly = posGlobal.Y;
		if (lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk)
			return;
		if (id == 0)
		{
			_densities[lx, ly, lz] = -10f;
			_materials[lx, ly, lz] = 0;
			if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = -1f;
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
			_densities[lx, ly, lz] = -10f;
			_materials[lx, ly, lz] = 4;
			if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = 1f;
		}
		else
		{
			_densities[lx, ly, lz] = 10f;
			_materials[lx, ly, lz] = id;
			if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = -1f;
		}
	}

	/// <summary>Appelé par Monde_Client. Reconstruit une section en Task.Run (tâches de fond).</summary>
	public void DeclencherReconstructionSection(int indexSection)
	{
		if (_densities == null) return;
		var monde = GetParent() as Monde_Client;
		if (monde == null) return;
		float baseX = ChunkOffsetX * TailleChunk;
		float baseZ = ChunkOffsetZ * TailleChunk;
		int idx = indexSection;
		var enqueue = monde.EnqueueMiseAJourMainThread;
		Task.Run(() =>
		{
			var (meshTerrain, meshEau) = ConstruireMeshSection(idx, baseX, baseZ);
			enqueue.Invoke(() => AppliquerMeshSection(idx, meshTerrain, meshEau));
		});
	}

	/// <summary>Reconstruction synchrone sur le Main Thread (Coupe-File VIP). Évite la ThreadPool Starvation.</summary>
	public void ReconstruireSectionSynchrone(int indexSection)
	{
		if (_densities == null) return;
		float baseX = ChunkOffsetX * TailleChunk;
		float baseZ = ChunkOffsetZ * TailleChunk;
		var (meshTerrain, meshEau) = ConstruireMeshSection(indexSection, baseX, baseZ);
		AppliquerMeshSection(indexSection, meshTerrain, meshEau);
	}

	/// <summary>Retourne la densité aux coordonnées locales. -10 si hors bornes (pour suture MC aux frontières).</summary>
	public float ObtenirDensiteLocale(int lx, int ly, int lz)
	{
		if (_densities == null || lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk)
			return -10f;
		return _densities[lx, ly, lz];
	}

	/// <summary>Section prête si son CollisionShape3D est construit. Utilisé pour suspendre la gravité au spawn.</summary>
	public bool SectionAPret(int section)
	{
		if (_sectionsPhysiques == null || section < 0 || section >= NB_SECTIONS) return false;
		return _sectionsPhysiques[section]?.Shape != null;
	}

	/// <summary>Applique le mesh visuel ET le CollisionShape3D (CRITIQUE : sans ça, le terrain posé serait indestructible).</summary>
	private void AppliquerMeshSection(int idx, ArrayMesh meshTerrain, ArrayMesh meshEau)
	{
		try
		{
			if (!IsInsideTree()) return; // Chunk libéré ou arbre en destruction — pas de modification spatiale.
			_sectionsTerrain[idx].Mesh = meshTerrain;
			_sectionsTerrain[idx].MaterialOverride = MaterielTerre ?? GD.Load<Material>("res://Manteau_Planetaire.tres");

			// ANNIHILATION DU FANTÔME PHYSIQUE : recalcul OBLIGATOIRE de la collision après chaque modification de densité.
			// CreateTrimeshShape génère un ConcavePolygonShape3D pour le terrain complexe. Sans ça, le RayCast passe au travers.
			var collisionShape = _sectionsPhysiques[idx];
			if (collisionShape != null)
			{
				Shape3D nouveauShape = (meshTerrain != null && meshTerrain.GetFaces().Length > 0)
					? meshTerrain.CreateTrimeshShape()
					: null;
				Callable.From(() =>
				{
					if (!IsInsideTree() || collisionShape == null) return;
					if (collisionShape.Shape != null)
					{
						collisionShape.Shape.Dispose();
						collisionShape.Shape = null;
					}
					collisionShape.Shape = nouveauShape;
				}).CallDeferred();
			}

			if (_densitiesEau != null && meshEau != null)
				_sectionsEau[idx].Mesh = meshEau;
		}
		catch (ObjectDisposedException) { /* Chunk déjà supprimé, ignorer */ }
		catch (System.Exception) when (IsChunkDisposeException()) { /* Godot/natif : objet libéré */ }
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

	/// <summary>Met à jour UNIQUEMENT les MultiMesh (buissons). N'appelle JAMAIS ConstruireMeshSection — isolement absolu du terrain.</summary>
	public void MettreAJourRenduFlore(Dictionary<Vector3I, byte> inventaire)
	{
		_inventaireFloreCache = inventaire;
		ActualiserFloreAvecDistance();
	}

	private void ActualiserFloreAvecDistance()
	{
		try
		{
			var inventaire = _inventaireFloreCache;
			if (!IsInsideTree() || inventaire == null || inventaire.Count == 0)
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
			// Y+0.5 : gazon et buissons sur le dessus du bloc, descendus de 0,5 m (évite gazon sous le sol)
			Vector3 positionLocale = new Vector3(kv.Key.X, kv.Key.Y + 0.5f, kv.Key.Z) - chunkOrigin + new Vector3(0.5f, 0f, 0.5f);
			float angle = (float)((kv.Key.X * 73856093 ^ kv.Key.Z * 19349663) % 10000) / 10000f * Mathf.Tau;
			Vector3 posMonde = new Vector3(kv.Key.X + 0.5f, kv.Key.Y + 0.5f, kv.Key.Z + 0.5f);

			// Gazon : visible uniquement dans 2 chunks du joueur, densité divisée par 2
			if (posMonde.DistanceSquaredTo(posObs) <= rayonCarre && ((kv.Key.X + kv.Key.Z) & 1) == 0)
			{
				var tGazon = Transform3D.Identity;
				tGazon.Origin = positionLocale;
				tGazon.Basis = Basis.Identity.Scaled(new Vector3(EchelleGazon, EchelleGazon, EchelleGazon)).Rotated(Vector3.Up, angle);
				Color couleurSol = ObtenirCouleurTerrainApprox(kv.Key.X, kv.Key.Y, kv.Key.Z);
				Color couleurHerbe = new Color(couleurSol.R * 0.8f, couleurSol.G * 0.8f, couleurSol.B * 0.8f, 1f).Lerp(new Color(0.7f, 0.8f, 1f), 0.1f);
				gazonInstances.Add((tGazon, couleurHerbe));
			}

			// Buissons : échelle déterministe (évite "grossissement" à chaque rafraîchissement)
			if (kv.Value == 1 || kv.Value == 2)
			{
				uint h = (uint)(kv.Key.X * 73856093) ^ (uint)(kv.Key.Z * 19349663) ^ (uint)(kv.Key.Y * 83492791);
				float echelleBuis = 0.009f + (h % 500) / 500f * 0.004f; // 0.009–0.013 stable
				var tBuis = Transform3D.Identity;
				tBuis.Origin = positionLocale;
				tBuis.Basis = Basis.Identity.Scaled(new Vector3(echelleBuis, echelleBuis, echelleBuis)).Rotated(Vector3.Up, angle);
				if (kv.Value == 1) pleins.Add(tBuis);
				else vides.Add(tBuis);
			}
		}
		Mesh meshGazon = ChargerMeshFlore("res://Modeles/Botanique/grass.glb");
		if (meshGazon == null && gazonInstances.Count > 0)
			meshGazon = CreerMeshGazonFallback();
		if (meshGazon != null && gazonInstances.Count > 0)
		{
			var mm = new MultiMesh();
			mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
			mm.UseColors = true;
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
		else
		{
			_mmGazon.Multimesh = null;
		}
		Mesh meshPlein = ChargerMeshFlore("res://Modeles/Botanique/Buisson_Plein.glb");
		if (meshPlein != null && pleins.Count > 0)
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
		if (meshVide != null && vides.Count > 0)
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
		catch (ObjectDisposedException) { /* Chunk déjà supprimé */ }
	}

	private static Mesh _cacheMeshGazon;
	private static Mesh _cacheMeshPlein;
	private static Mesh _cacheMeshVide;
	private static Material _cacheMaterielGazonAssombri;
	private static Material _cacheMaterielGazonSymbiotique;

	/// <summary>Couleur approximative du terrain à (x,y,z) — même formule que TerrainVoxel (temp/hum). Pour herbe symbiotique.</summary>
	private Color ObtenirCouleurTerrainApprox(int xGlobal, int yGlobal, int zGlobal)
	{
		if (_materials == null || _noiseTemperature == null || _noiseHumidite == null) return new Color(0.5f, 0.6f, 0.5f);
		int lx = xGlobal - ChunkOffsetX * TailleChunk;
		int lz = zGlobal - ChunkOffsetZ * TailleChunk;
		if (lx < 0 || lx > TailleChunk || yGlobal < 0 || yGlobal > HauteurMax || lz < 0 || lz > TailleChunk)
			return new Color(0.5f, 0.6f, 0.5f);
		byte idMat = _materials[lx, yGlobal, lz];
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

	/// <summary>ShaderMaterial symbiotique : albedo * COLOR (instance). Pour herbe qui épouse le sol givré.</summary>
	private static Material ObtenirMaterielGazonSymbiotique()
	{
		if (_cacheMaterielGazonSymbiotique != null) return _cacheMaterielGazonSymbiotique;
		var shader = GD.Load<Shader>("res://textures/Shader_Herbe.gdshader");
		if (shader == null) return ObtenirMaterielGazonAssombri();
		var mat = new ShaderMaterial();
		mat.Shader = shader;
		Texture2D texAlbedo = ExtraireTextureAlbedoDepuisGrass() ?? GD.Load<Texture2D>("res://textures/terrain/01_herbe.jpg");
		if (texAlbedo != null) mat.SetShaderParameter("albedo_texture", texAlbedo);
		_cacheMaterielGazonSymbiotique = mat;
		return mat;
	}

	private static Texture2D ExtraireTextureAlbedoDepuisGrass()
	{
		var scene = GD.Load<PackedScene>("res://Modeles/Botanique/grass.glb");
		if (scene == null) return null;
		Node racine = scene.Instantiate();
		Material m = TrouverMaterielSurMeshInstance(racine);
		racine.QueueFree();
		if (m is StandardMaterial3D s && s.AlbedoTexture != null) return s.AlbedoTexture;
		return null;
	}

	private static Material ObtenirMaterielGazonAssombri()
	{
		if (_cacheMaterielGazonAssombri != null) return _cacheMaterielGazonAssombri;
		var scene = GD.Load<PackedScene>("res://Modeles/Botanique/grass.glb");
		if (scene != null)
		{
			Node racine = scene.Instantiate();
			Material orig = TrouverMaterielSurMeshInstance(racine);
			racine.QueueFree();
			if (orig != null)
			{
				var dupl = orig.Duplicate() as Material;
				if (dupl is StandardMaterial3D s)
				{
					Color c = s.AlbedoColor;
					s.AlbedoColor = new Color(c.R * 0.55f, c.G * 0.55f, c.B * 0.55f);
					_cacheMaterielGazonAssombri = s;
					return s;
				}
			}
		}
		var mat = new StandardMaterial3D();
		mat.AlbedoColor = new Color(0.45f, 0.5f, 0.4f);
		_cacheMaterielGazonAssombri = mat;
		return mat;
	}

	private static Material TrouverMaterielSurMeshInstance(Node n)
	{
		if (n is MeshInstance3D mi)
		{
			Material m = mi.MaterialOverride ?? (mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0 ? mi.Mesh.SurfaceGetMaterial(0) : null);
			if (m != null) return m;
		}
		foreach (Node enfant in n.GetChildren())
		{
			var r = TrouverMaterielSurMeshInstance(enfant);
			if (r != null) return r;
		}
		return null;
	}

	private static Mesh ChargerMeshFlore(string path)
	{
		if (path.Contains("grass") && _cacheMeshGazon != null) return _cacheMeshGazon;
		if (path.Contains("Plein") && _cacheMeshPlein != null) return _cacheMeshPlein;
		if (path.Contains("Vide") && _cacheMeshVide != null) return _cacheMeshVide;
		Mesh mesh = ChargerMeshDepuisScene(path);
		if (mesh == null) { GD.PrintErr($"Chunk_Client: Échec extraction mesh depuis {path}"); return null; }
		if (path.Contains("grass")) _cacheMeshGazon = mesh;
		else if (path.Contains("Plein")) _cacheMeshPlein = mesh;
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

	/// <summary>Si grass.glb échoue (structure différente), réessaie avec parcours complet ou utilise Buisson_Plein.</summary>
	private static Mesh CreerMeshGazonFallback()
	{
		var scene = GD.Load<PackedScene>("res://Modeles/Botanique/grass.glb");
		if (scene == null) return null;
		Node racine = scene.Instantiate();
		foreach (Node n in ObtenirTousLesNoeuds(racine))
		{
			if (n is MeshInstance3D mi && mi.Mesh != null && MeshSurfaceCount(mi.Mesh) > 0)
			{
				Mesh mesh = mi.Mesh;
				racine.QueueFree();
				_cacheMeshGazon = mesh;
				GD.Print("Chunk_Client: grass.glb fallback OK — mesh depuis ", mi.Name);
				return mesh;
			}
		}
		racine.QueueFree();
		GD.PrintErr("Chunk_Client: grass.glb sans MeshInstance3D valide. Fallback Buisson_Plein.");
		_cacheMeshGazon = ChargerMeshFlore("res://Modeles/Botanique/Buisson_Plein.glb");
		return _cacheMeshGazon;
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
		float DensitePourMesh(int x, int y, int z) => _densities[x, yDebut + y, z];

		if (_valsRecyclables == null) _valsRecyclables = new float[8];
		if (_vertsRecyclables == null) _vertsRecyclables = new Vector3[8];
		if (_vertListRecyclables == null) _vertListRecyclables = new Vector3[12];

		var bufferDensities = ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION);
		var bufferMaterials = ArrayPool<byte>.Shared.Rent(TAILLE_MAX_SECTION);
		float[] bufferEau = _densitiesEau != null ? ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION) : null;
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
					bufferMaterials[idx] = _materials[x, yDebut + y, z];
					if (bufferEau != null) bufferEau[idx] = _densitiesEau[x, yDebut + y, z];
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

	private static Vector3 Interp(Vector3 p1, Vector3 p2, float v1, float v2)
	{
		if (Mathf.Abs(Isolevel - v1) < 0.00001f) return p1;
		if (Mathf.Abs(Isolevel - v2) < 0.00001f) return p2;
		if (Mathf.Abs(v1 - v2) < 0.00001f) return p1;
		float t = (Isolevel - v1) / (v2 - v1);
		return p1 + t * (p2 - p1);
	}
}
