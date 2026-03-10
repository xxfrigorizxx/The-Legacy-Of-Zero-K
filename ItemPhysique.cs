using Godot;
using System.Collections.Generic;

/// <summary>Composition chimique d'une roche. Dicte couleur, rugosité, future résistance et point de fusion.</summary>
public struct ProfilMineral
{
	public string Nom;
	public Color CouleurBase;
	public Color CouleurVeine;
	public Color CouleurTache;
	public float Rugosite;
	public int ResistanceFuture;
}

/// <summary>ADN de l'objet libre : identifie ce que le Raycast du joueur ramasse.
/// 10 = Petite Pierre, 11 = Silex, 12 = Pierre Moyenne, 13 = Grosse Pierre, 14 = Très Grosse Pierre.
/// Banque d'ADN : variations procédurales + composition chimique réelle.</summary>
public partial class ItemPhysique : Node3D
{
	/// <summary>Table géologique : compositions minérales réelles (couleur, rugosité, future résistance).</summary>
	public static readonly ProfilMineral[] TableGeologique = new ProfilMineral[]
	{
		new ProfilMineral { Nom = "Granit", CouleurBase = new Color(0.4f, 0.4f, 0.4f), CouleurVeine = new Color(0.8f, 0.8f, 0.8f), CouleurTache = new Color(0.1f, 0.1f, 0.1f), Rugosite = 0.9f, ResistanceFuture = 80 },
		new ProfilMineral { Nom = "Basalte", CouleurBase = new Color(0.15f, 0.15f, 0.15f), CouleurVeine = new Color(0.1f, 0.1f, 0.1f), CouleurTache = new Color(0.05f, 0.05f, 0.05f), Rugosite = 0.95f, ResistanceFuture = 90 },
		new ProfilMineral { Nom = "Calcaire", CouleurBase = new Color(0.85f, 0.85f, 0.80f), CouleurVeine = new Color(0.9f, 0.9f, 0.85f), CouleurTache = new Color(0.7f, 0.7f, 0.6f), Rugosite = 1.0f, ResistanceFuture = 20 },
		new ProfilMineral { Nom = "Grès", CouleurBase = new Color(0.6f, 0.4f, 0.2f), CouleurVeine = new Color(0.7f, 0.5f, 0.3f), CouleurTache = new Color(0.4f, 0.2f, 0.1f), Rugosite = 0.98f, ResistanceFuture = 40 },
		new ProfilMineral { Nom = "Schiste", CouleurBase = new Color(0.3f, 0.35f, 0.35f), CouleurVeine = new Color(0.2f, 0.25f, 0.25f), CouleurTache = new Color(0.4f, 0.45f, 0.45f), Rugosite = 0.8f, ResistanceFuture = 30 },
		new ProfilMineral { Nom = "Silex", CouleurBase = new Color(0.12f, 0.12f, 0.14f), CouleurVeine = new Color(0.18f, 0.18f, 0.20f), CouleurTache = new Color(0.02f, 0.02f, 0.03f), Rugosite = 0.5f, ResistanceFuture = 85 },
		new ProfilMineral { Nom = "Quartz", CouleurBase = new Color(0.9f, 0.88f, 0.85f), CouleurVeine = new Color(0.95f, 0.95f, 0.95f), CouleurTache = new Color(0.6f, 0.55f, 0.5f), Rugosite = 0.3f, ResistanceFuture = 70 },
		new ProfilMineral { Nom = "Marbre", CouleurBase = new Color(0.85f, 0.85f, 0.9f), CouleurVeine = new Color(0.7f, 0.7f, 0.75f), CouleurTache = new Color(0.95f, 0.95f, 0.98f), Rugosite = 0.2f, ResistanceFuture = 50 },
		new ProfilMineral { Nom = "Obsidienne", CouleurBase = new Color(0.08f, 0.08f, 0.1f), CouleurVeine = new Color(0.05f, 0.05f, 0.06f), CouleurTache = new Color(0.15f, 0.15f, 0.18f), Rugosite = 0.15f, ResistanceFuture = 75 },
		new ProfilMineral { Nom = "Gneiss", CouleurBase = new Color(0.45f, 0.42f, 0.4f), CouleurVeine = new Color(0.55f, 0.5f, 0.48f), CouleurTache = new Color(0.25f, 0.22f, 0.2f), Rugosite = 0.85f, ResistanceFuture = 65 }
	};

	[Export] public int ID_Objet = 0;
	/// <summary>Sauvegarde de la forme exacte (index dans la banque d'ADN). -1 = tirage aléatoire au spawn.</summary>
	public int IndexCacheMemoire = -1;
	/// <summary>Index dans TableGeologique. -1 = non défini (tirage au spawn).</summary>
	public int IndexChimique = -1;

	/// <summary>Banque d'ADN : accès public pour rendu en main et UI inventaire.</summary>
	public static IReadOnlyList<Mesh> CacheMeshCaillou => _cacheMeshCaillou;
	public static IReadOnlyList<Shape3D> CacheCollisionCaillou => _cacheCollisionCaillou;
	public static IReadOnlyList<Mesh> CacheMeshSilex => _cacheMeshSilex;
	public static IReadOnlyList<Shape3D> CacheCollisionSilex => _cacheCollisionSilex;

	private static readonly List<Mesh> _cacheMeshCaillou = new List<Mesh>();
	private static readonly List<Shape3D> _cacheCollisionCaillou = new List<Shape3D>();
	private static readonly List<Mesh> _cacheMeshSilex = new List<Mesh>();
	private static readonly List<Shape3D> _cacheCollisionSilex = new List<Shape3D>();
	private const int NbVariationsCache = 50;

	public override void _Ready()
	{
		Node parent = GetParent();
		MeshInstance3D visuel = null;
		CollisionShape3D hitbox = null;
		foreach (Node child in parent.GetChildren())
		{
			if (child is MeshInstance3D mi) visuel = mi;
			else if (child is CollisionShape3D cs) hitbox = cs;
		}
		if (visuel == null || hitbox == null) return;

		// Injection génétique : composition chimique (ou selon biome plus tard)
		if (IndexChimique == -1)
			IndexChimique = GD.RandRange(0, TableGeologique.Length - 1);

		AppliquerMateriel(visuel);

		// MODIFICATION CRITIQUE : Si IndexCacheMemoire déjà défini (objet relâché par le joueur), on NE TIRE PAS au hasard.
		if (IndexCacheMemoire == -1)
			IndexCacheMemoire = ID_Objet == 11
				? PreparerCacheEtTirerIndex(true)
				: PreparerCacheEtTirerIndex(false);

		// Appliquer la forme EXACTE depuis le cache
		int idx = Mathf.Clamp(IndexCacheMemoire, 0, int.MaxValue);
		if (ID_Objet == 11)
		{
			if (idx < _cacheMeshSilex.Count)
			{
				visuel.Mesh = _cacheMeshSilex[idx];
				hitbox.Shape = _cacheCollisionSilex[idx];
			}
		}
		else
		{
			if (idx < _cacheMeshCaillou.Count)
			{
				visuel.Mesh = _cacheMeshCaillou[idx];
				hitbox.Shape = _cacheCollisionCaillou[idx];
			}
			// Mise à l'échelle selon la taille (cache = unité 0.15)
			float scale = ID_Objet == 10 ? 1f : ID_Objet == 12 ? 0.25f / 0.15f : ID_Objet == 13 ? 0.4f / 0.15f : 0.6f / 0.15f;
			if (parent is Node3D n3d)
				n3d.Scale = new Vector3(scale, scale, scale);
		}

		RotationDegrees = new Vector3(GD.RandRange(0, 360), GD.RandRange(0, 360), GD.RandRange(0, 360));
	}

	/// <summary>Réapplique mesh/collision/matériau après réutilisation depuis un pool (ID_Objet ou IndexCache/Chimique changés).</summary>
	public void ReappliquerApparence()
	{
		Node parent = GetParent();
		if (parent == null) return;
		MeshInstance3D visuel = null;
		CollisionShape3D hitbox = null;
		foreach (Node child in parent.GetChildren())
		{
			if (child is MeshInstance3D mi) visuel = mi;
			else if (child is CollisionShape3D cs) hitbox = cs;
		}
		if (visuel == null || hitbox == null) return;
		if (IndexChimique < 0) IndexChimique = GD.RandRange(0, TableGeologique.Length - 1);
		AppliquerMateriel(visuel);
		if (IndexCacheMemoire < 0)
		{
			// -1 = proche du joueur (formes douces, 1re moitié), -2 = loin (formes plus cassées, 2e moitié)
			bool formesCassées = (IndexCacheMemoire == -2);
			IndexCacheMemoire = ID_Objet == 11 ? PreparerCacheEtTirerIndex(true, formesCassées) : PreparerCacheEtTirerIndex(false, formesCassées);
		}
		int idx = Mathf.Clamp(IndexCacheMemoire, 0, int.MaxValue);
		if (ID_Objet == 11)
		{
			if (idx < _cacheMeshSilex.Count) { visuel.Mesh = _cacheMeshSilex[idx]; hitbox.Shape = _cacheCollisionSilex[idx]; }
		}
		else
		{
			if (idx < _cacheMeshCaillou.Count) { visuel.Mesh = _cacheMeshCaillou[idx]; hitbox.Shape = _cacheCollisionCaillou[idx]; }
			float scale = ID_Objet == 10 ? 1f : ID_Objet == 12 ? 0.25f / 0.15f : ID_Objet == 13 ? 0.4f / 0.15f : 0.6f / 0.15f;
			if (parent is Node3D n3d) n3d.Scale = new Vector3(scale, scale, scale);
		}
	}

	private int PreparerCacheEtTirerIndex(bool estSilex, bool formesCassées = false)
	{
		if (estSilex)
		{
			lock (_cacheMeshSilex)
			{
				if (_cacheMeshSilex.Count < NbVariationsCache)
					GenererEtMettreEnCache(true);
				int count = _cacheMeshSilex.Count;
				if (count == 0) return 0;
				if (formesCassées && count > 1) return GD.RandRange(count / 2, count - 1);
				return GD.RandRange(0, Mathf.Max(0, (count / 2) - 1));
			}
		}
		lock (_cacheMeshCaillou)
		{
			if (_cacheMeshCaillou.Count < NbVariationsCache)
				GenererEtMettreEnCache(false);
			int count = _cacheMeshCaillou.Count;
			if (count == 0) return 0;
			if (formesCassées && count > 1) return GD.RandRange(count / 2, count - 1);
			return GD.RandRange(0, Mathf.Max(0, (count / 2) - 1));
		}
	}

	private void AppliquerMateriel(MeshInstance3D visuel)
	{
		visuel.MaterialOverride = CreerMaterielProcedural(ID_Objet == 11, IndexChimique);
	}

	/// <summary>Matériau procédural basé sur la chimie réelle (TableGeologique). Taches, veines, rugosité.</summary>
	public static StandardMaterial3D CreerMaterielProcedural(bool estSilex, int indexChimique)
	{
		var materiel = new StandardMaterial3D();
		var bruitRelief = new FastNoiseLite { Seed = (int)GD.Randi() };

		int idx = Mathf.Clamp(indexChimique, 0, TableGeologique.Length - 1);
		ProfilMineral chimie = TableGeologique[idx];
		materiel.Roughness = chimie.Rugosite;

		// 1. Pigmentation par la chimie (CouleurTache → CouleurBase → CouleurVeine)
		var bruitCouleur = new FastNoiseLite
		{
			Seed = (int)GD.Randi(),
			NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
			Frequency = 0.03f
		};
		var textureCouleur = new NoiseTexture2D { Width = 256, Height = 256, Noise = bruitCouleur };
		var degradeMineral = new Gradient();
		degradeMineral.AddPoint(0f, chimie.CouleurTache);
		degradeMineral.AddPoint(0.5f, chimie.CouleurBase);
		degradeMineral.AddPoint(1f, chimie.CouleurVeine);
		textureCouleur.ColorRamp = degradeMineral;
		materiel.AlbedoTexture = textureCouleur;

		// 2. Micro-relief (Normal Map)
		var textureRelief = new NoiseTexture2D { Width = 256, Height = 256, GenerateMipmaps = true, AsNormalMap = true };
		if (estSilex)
		{
			materiel.Metallic = 0.2f;
			bruitRelief.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
			bruitRelief.Frequency = 0.08f;
			textureRelief.BumpStrength = 3.0f;
		}
		else
		{
			materiel.Metallic = 0.0f;
			bruitRelief.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
			bruitRelief.Frequency = 0.15f;
			textureRelief.BumpStrength = 1.5f;
		}
		textureRelief.Noise = bruitRelief;
		materiel.NormalEnabled = true;
		materiel.NormalTexture = textureRelief;
		return materiel;
	}

	private void GenererEtMettreEnCache(bool estSilex)
	{
		ArrayMesh arrayMesh;
		float forceDeformation;

		if (estSilex)
		{
			var primitive = new SphereMesh { Radius = 0.12f, Height = 0.24f }; // Forme de base sphère (déformation inchangée)
			arrayMesh = new ArrayMesh();
			arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, primitive.GetMeshArrays());
			forceDeformation = 0.3f;
		}
		else
		{
			var primitive = new SphereMesh { Radius = 0.15f, Height = 0.3f };
			arrayMesh = new ArrayMesh();
			arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, primitive.GetMeshArrays());
			forceDeformation = 0.15f;
		}

		var bruit = new FastNoiseLite();
		bruit.Seed = (int)GD.Randi();
		if (estSilex)
		{
			bruit.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
			bruit.CellularDistanceFunction = FastNoiseLite.CellularDistanceFunctionEnum.Euclidean;
			bruit.CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue;
		}
		else
			bruit.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;

		var mdt = new MeshDataTool();
		if (mdt.CreateFromSurface(arrayMesh, 0) != Error.Ok) return;

		// Génétique des proportions : vecteur d'écrasement/étirement procédural unique par modèle
		Vector3 adnMorphologique;
		if (!estSilex)
		{
			// CAILLOU : X et Z varient un peu, Y varie énormément (galette 0.3 → patate ronde 1.0)
			adnMorphologique = new Vector3(
				0.7f + (float)GD.Randf() * 0.5f,
				0.3f + (float)GD.Randf() * 0.7f,
				0.7f + (float)GD.Randf() * 0.5f
			);
		}
		else
		{
			// SILEX : étirement sur un axe pour forme de lame ou d'éclat
			adnMorphologique = new Vector3(
				0.6f + (float)GD.Randf() * 0.4f,
				0.6f + (float)GD.Randf() * 0.4f,
				1.0f + (float)GD.Randf() * 0.8f
			);
		}

		for (int i = 0; i < mdt.GetVertexCount(); i++)
		{
			Vector3 pos = mdt.GetVertex(i);
			Vector3 n = mdt.GetVertexNormal(i);
			float b = bruit.GetNoise3D(pos.X * 10f, pos.Y * 10f, pos.Z * 10f);
			Vector3 positionNouvelle = pos + (n * b * forceDeformation);
			// Écrase/étire le sommet selon l'ADN morphologique de ce modèle
			positionNouvelle.X *= adnMorphologique.X;
			positionNouvelle.Y *= adnMorphologique.Y;
			positionNouvelle.Z *= adnMorphologique.Z;
			mdt.SetVertex(i, positionNouvelle);
		}

		// Recalcul des normales (MeshDataTool n'a pas GenerateNormals) : moyenne des normales des faces adjacentes
		for (int i = 0; i < mdt.GetVertexCount(); i++)
		{
			int[] faces = mdt.GetVertexFaces(i);
			Vector3 sum = Vector3.Zero;
			foreach (int faceIdx in faces)
				sum += mdt.GetFaceNormal(faceIdx);
			if (sum.LengthSquared() > 0.0001f)
				mdt.SetVertexNormal(i, sum.Normalized());
		}

		var nouveauMesh = new ArrayMesh();
		mdt.CommitToSurface(nouveauMesh);

		// Caillou ET Silex : hitbox convexe précise qui épouse les bosses (dormance physique désirée)
		Shape3D nouvelleCollision = nouveauMesh.CreateConvexShape(true, false);

		if (estSilex)
		{
			_cacheMeshSilex.Add(nouveauMesh);
			_cacheCollisionSilex.Add(nouvelleCollision);
		}
		else
		{
			_cacheMeshCaillou.Add(nouveauMesh);
			_cacheCollisionCaillou.Add(nouvelleCollision);
		}
	}
}
