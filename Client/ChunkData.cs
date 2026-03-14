using Godot;
using System;
using System.Collections.Generic;

/// <summary>Chunk au format Data-Oriented (AAA) : données nues + RIDs RenderingServer/PhysicsServer3D. Aucun Node.</summary>
public class ChunkData
{
	public Vector2I Coordonnees { get; set; }
	public int ChunkOffsetX => Coordonnees.X;
	public int ChunkOffsetZ => Coordonnees.Y;
	public int TailleChunk { get; set; }
	public int HauteurMax { get; set; }

	/// <summary>Identifiant GPU de l'instance de rendu (RenderingServer).</summary>
	public Rid VisualInstanceRID { get; set; }
	/// <summary>Identifiant GPU de l'instance de rendu pour l'eau (RenderingServer).</summary>
	public Rid WaterInstanceRID { get; set; }
	/// <summary>Identifiant du corps statique (PhysicsServer3D).</summary>
	public Rid PhysicsBodyRID { get; set; }
	/// <summary>Identifiant de la forme de collision concave.</summary>
	public Rid PhysicsShapeRID { get; set; }

	/// <summary>Références gardées pour éviter que le moteur libère les ressources tant que le chunk est actif. Null après LibérerRids.</summary>
	internal ArrayMesh _meshRef;
	internal Shape3D _shapeRef;
	/// <summary>Référence au mesh eau fusionné (évite GC).</summary>
	internal ArrayMesh _meshEauRef;
	/// <summary>Nœud gazon (MultiMeshInstance3D) créé à l'intégration du chunk. Libéré dans LibérerRids.</summary>
	internal Node _nodeGazon;

	/// <summary>Données voxel (tableaux plats). Remplis par ExecuterCalculChunk.</summary>
	public float[] DensitiesFlat { get; set; }
	public byte[] MaterialsFlat { get; set; }
	public float[] DensitiesEauFlat { get; set; }
	public int Tx { get; set; }
	public int Ty { get; set; }
	public int Tz { get; set; }

	public bool Dormant { get; set; }
	/// <summary>True tant que le chunk attend dans la file de solidification physique (évite doublons).</summary>
	public bool EstEnFileSolidification { get; set; }

	/// <summary>Bruit climat (une seule instance par chunk, réutilisée pour tous les voxels).</summary>
	public FastNoiseLite NoiseTemperature { get; set; }
	public FastNoiseLite NoiseHumidite { get; set; }
	/// <summary>Flore générée à partir de la surface du chunk (gazon + buissons). Rempli au chargement.</summary>
	public Dictionary<Vector3I, byte> InventaireFlore { get; set; }

	public void ConfigurerBruitClimat(int seed)
	{
		NoiseTemperature = new FastNoiseLite();
		NoiseTemperature.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		NoiseTemperature.Seed = seed + 2;
		NoiseTemperature.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		NoiseTemperature.FractalOctaves = 4;
		NoiseTemperature.Frequency = 0.0005f;
		NoiseHumidite = new FastNoiseLite();
		NoiseHumidite.Seed = seed + 3;
		NoiseHumidite.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		NoiseHumidite.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		NoiseHumidite.FractalOctaves = 4;
		NoiseHumidite.Frequency = 0.0006f;
	}

	/// <summary>Index 1D cohérent avec DonneesChunk (serveur) : ordre (x,y,z) avec x le plus lent = x*Ty*Tz + y*Tz + z. Une erreur ici inverse X/Z et tourne le maillage de 90°.</summary>
	public int Idx(int x, int y, int z) => x * Ty * Tz + y * Tz + z;

	public float ObtenirDensiteLocale(int lx, int ly, int lz)
	{
		if (DensitiesFlat == null || lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk)
			return -10f;
		return DensitiesFlat[Idx(lx, ly, lz)];
	}

	public void SetVoxelLocal(int lx, int ly, int lz, byte id)
	{
		if (DensitiesFlat == null || lx < 0 || lx > TailleChunk || ly < 0 || ly > HauteurMax || lz < 0 || lz > TailleChunk) return;
		if (id == 0)
		{
			DensitiesFlat[Idx(lx, ly, lz)] = -10f;
			MaterialsFlat[Idx(lx, ly, lz)] = 0;
			if (DensitiesEauFlat != null) DensitiesEauFlat[Idx(lx, ly, lz)] = -1f;
		}
		else if (id == 4)
		{
			DensitiesFlat[Idx(lx, ly, lz)] = -10f;
			MaterialsFlat[Idx(lx, ly, lz)] = 4;
			if (DensitiesEauFlat != null) DensitiesEauFlat[Idx(lx, ly, lz)] = 1f;
		}
		else
		{
			DensitiesFlat[Idx(lx, ly, lz)] = 10f;
			MaterialsFlat[Idx(lx, ly, lz)] = id;
			if (DensitiesEauFlat != null) DensitiesEauFlat[Idx(lx, ly, lz)] = -1f;
		}
	}

	/// <summary>Libère les RIDs côté RenderingServer et PhysicsServer3D. À appeler quand le chunk sort du rayon (déchargement).</summary>
	public void LibérerRids()
	{
		if (VisualInstanceRID.IsValid)
		{
			RenderingServer.Singleton.FreeRid(VisualInstanceRID);
			VisualInstanceRID = default;
		}
		if (WaterInstanceRID.IsValid)
		{
			RenderingServer.Singleton.FreeRid(WaterInstanceRID);
			WaterInstanceRID = default;
		}
		if (PhysicsBodyRID.IsValid)
		{
			// Retirer la shape du body avant de la libérer, sinon le body la détruit et double free au Dispose().
			PhysicsServer3D.Singleton.BodyRemoveShape(PhysicsBodyRID, 0);
			PhysicsServer3D.Singleton.FreeRid(PhysicsBodyRID);
			PhysicsBodyRID = default;
		}
		if (_shapeRef != null)
		{
			_shapeRef.Dispose();
			_shapeRef = null;
		}
		PhysicsShapeRID = default;
		_meshRef = null;
		_shapeRef = null;
		_meshEauRef = null;
		if (_nodeGazon != null)
		{
			_nodeGazon.QueueFree();
			_nodeGazon = null;
		}
	}
}
