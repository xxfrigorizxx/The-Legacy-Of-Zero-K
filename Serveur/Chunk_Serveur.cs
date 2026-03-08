using Godot;
using System;
using System.Collections.Generic;
using System.IO;

/// <summary>Données voxel et logique de génération pour un chunk. Aucun MeshInstance3D.</summary>
public partial class Chunk_Serveur : RefCounted
{
	public int TailleChunk { get; }
	public int HauteurMax { get; }
	public int ChunkOffsetX { get; }
	public int ChunkOffsetZ { get; }
	public Vector3 PositionMonde { get; }

	private float[,,] _densities;
	private float[,,] _densitiesEau;
	private byte[,,] _materials;
	private readonly object _verrouVoxel = new object();

	private FastNoiseLite _noiseSurface;
	private FastNoiseLite _noiseErosion;
	private FastNoiseLite _noiseTemperature;
	private FastNoiseLite _noiseHumidite;
	private FastNoiseLite _noiseCavernes;
	private FastNoiseLite _noiseRivieres;

	private const float Isolevel = 0.0f;
	private const int NiveauEau = 102;
	private const int ProfondeurBase = 104;
	private const int AmplitudeMontagne = 100;
	private const int NiveauPlage = 102;
	/// <summary>Limites altitude flore. Inclut la zone de spawn (herbe haute).</summary>
	private const float NIVEAU_MIN_FLORE = 5f;
	private const float NIVEAU_MAX_FLORE = 260f;

	/// <summary>Registre de flore : position globale → type (1 = Buisson Plein, 2 = Buisson Vide). Jamais de modification voxel.</summary>
	public Dictionary<Vector3I, byte> InventaireFlore { get; } = new Dictionary<Vector3I, byte>();

	private Action<Vector3, byte> _callbackBlocChutant;
	private Func<Vector2I, bool> _chunkEstCharge;
	private Action<Vector3> _reveillerEau;
	private Action<Vector3I, byte> _onVoxelModifie;
	private Action<Vector2I, Dictionary<Vector3I, byte>> _onFlorePurgée;

	/// <summary>Drapeau de souillure : true UNIQUEMENT quand DetruireVoxel ou CreerMatiere sont appelés. On ne sauvegarde JAMAIS un chunk intact.</summary>
	private bool _estModifie;
	/// <summary>True si chargé depuis disque. AUCUNE passe de génération ne doit jamais s'exécuter sur ce chunk.</summary>
	private bool _chargeDepuisDisque;

	public bool EstModifie => _estModifie;
	public bool EstChargeDepuisDisque => _chargeDepuisDisque;

	public void SetOnVoxelModifie(Action<Vector3I, byte> callback) => _onVoxelModifie = callback;
	public void SetOnFlorePurgée(Action<Vector2I, Dictionary<Vector3I, byte>> callback) => _onFlorePurgée = callback;

	public Chunk_Serveur(int chunkOffsetX, int chunkOffsetZ, int tailleChunk, int hauteurMax, int seed,
		Action<Vector3, byte> callbackBlocChutant, Func<Vector2I, bool> chunkEstCharge, Action<Vector3> reveillerEau)
	{
		ChunkOffsetX = chunkOffsetX;
		ChunkOffsetZ = chunkOffsetZ;
		TailleChunk = tailleChunk;
		HauteurMax = hauteurMax;
		PositionMonde = new Vector3(chunkOffsetX * tailleChunk, 0, chunkOffsetZ * tailleChunk);
		_callbackBlocChutant = callbackBlocChutant;
		_chunkEstCharge = chunkEstCharge;
		_reveillerEau = reveillerEau;

		ConfigurerBruit(seed);
	}

	private void ConfigurerBruit(int seed)
	{
		_noiseSurface = new FastNoiseLite();
		_noiseSurface.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		_noiseSurface.Seed = seed;
		_noiseSurface.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		_noiseSurface.FractalOctaves = 5;
		_noiseSurface.Frequency = 0.002f;

		_noiseErosion = new FastNoiseLite();
		_noiseErosion.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		_noiseErosion.Seed = seed + 1;
		_noiseErosion.FractalOctaves = 5;
		_noiseErosion.Frequency = 0.002f;

		_noiseTemperature = new FastNoiseLite();
		_noiseTemperature.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noiseTemperature.Seed = seed + 2;
		_noiseTemperature.Frequency = 0.001f;

		_noiseHumidite = new FastNoiseLite();
		_noiseHumidite.Seed = seed + 3;
		_noiseHumidite.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noiseHumidite.Frequency = 0.0012f;

		_noiseCavernes = new FastNoiseLite();
		_noiseCavernes.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
		_noiseCavernes.Seed = seed + 4;
		_noiseCavernes.FractalType = FastNoiseLite.FractalTypeEnum.Ridged;
		_noiseCavernes.FractalOctaves = 3;
		_noiseCavernes.Frequency = 0.015f;

		_noiseRivieres = new FastNoiseLite();
		_noiseRivieres.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		_noiseRivieres.FractalType = FastNoiseLite.FractalTypeEnum.Ridged;
		_noiseRivieres.Frequency = 0.003f;
		_noiseRivieres.Seed = seed + 5;
	}

	public bool EstPret => _densities != null;

	/// <summary>TOUTES les passes procédurales (terrain, surface, herbe, eau). NE DOIT JAMAIS s'exécuter sur un chunk chargé du disque.</summary>
	public void GenererDonneesVoxel()
	{
		if (_chargeDepuisDisque) return; // GARDE ABSOLUE : chunk ressuscité du disque — aucune modification mathématique.
		lock (_verrouVoxel)
		{
			_densities = new float[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];
			_materials = new byte[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];
			_densitiesEau = new float[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];

			for (int x = 0; x <= TailleChunk; x++)
			{
				for (int y = 0; y <= HauteurMax; y++)
				{
					for (int z = 0; z <= TailleChunk; z++)
					{
						// Espace GLOBAL du monde — évite le tiling biomique (chaleur/humidité fracturée).
						float xGlobal = ChunkOffsetX * TailleChunk + x;
						float zGlobal = ChunkOffsetZ * TailleChunk + z;
						float globalY = y;

						int hauteurSurface = CalculerHauteurTerrain((int)xGlobal, (int)zGlobal);
						float temperature = _noiseTemperature.GetNoise2D(xGlobal, zGlobal);
						float humidite = _noiseHumidite.GetNoise2D(xGlobal, zGlobal);

						_densitiesEau[x, y, z] = -1.0f;

						if (y <= 2)
						{
							_densities[x, y, z] = 1000.0f;
							_materials[x, y, z] = 2;
						}
						else if (globalY == hauteurSurface)
						{
							byte mat = DeterminerMateriauCroûte((int)globalY, hauteurSurface, temperature, humidite);
							_materials[x, y, z] = mat;
							_densities[x, y, z] = 10.0f;
							// Gazon partout sur herbe (ID 1), buissons à certaines positions — uniquement sur terrain plat
							if (mat == 1 && TerrainAssezPlat((int)xGlobal, (int)zGlobal))
							{
								float altitudeFlore = globalY;
								if (altitudeFlore > NIVEAU_MIN_FLORE && altitudeFlore < NIVEAU_MAX_FLORE)
								{
									var posGlobale = new Vector3I((int)xGlobal, (int)globalY, (int)zGlobal);
									InventaireFlore[posGlobale] = 0; // Gazon seul par défaut
									float humiditeBrute = _noiseHumidite.GetNoise2D(xGlobal * 0.05f, zGlobal * 0.05f);
									float humiditeNorm = (humiditeBrute + 1f) * 0.5f;
									float chanceDePousse = 0f;
									if (humiditeNorm > 0.3f) chanceDePousse = (humiditeNorm - 0.3f) * 0.015f;
									if (chanceDePousse > 0.02f) chanceDePousse = 0.02f;
									if (chanceDePousse > 0f && DeterministicRand(xGlobal, zGlobal) < chanceDePousse)
										InventaireFlore[posGlobale] = (byte)(DeterministicRand(xGlobal + 17f, zGlobal) < 0.5f ? 1 : 2);
								}
							}
						}
						else if (globalY < hauteurSurface && globalY >= hauteurSurface - 4)
						{
							float valeurGrotte = _noiseCavernes.GetNoise3D(xGlobal, globalY, zGlobal);
							if (valeurGrotte > 0.75f)
							{
								_densities[x, y, z] = -10.0f;
								_materials[x, y, z] = 0;
							}
							else
							{
								_densities[x, y, z] = 10.0f;
								_materials[x, y, z] = (humidite > 0.3f) ? (byte)7 : (byte)6;
							}
						}
						else if (globalY < hauteurSurface - 4)
						{
							float valeurGrotte = _noiseCavernes.GetNoise3D(xGlobal, globalY, zGlobal);
							if (valeurGrotte > 0.55f)
							{
								_densities[x, y, z] = -10.0f;
								_materials[x, y, z] = 0;
							}
							else
							{
								_densities[x, y, z] = 10.0f;
								_materials[x, y, z] = 2;
							}
						}
						else if (globalY > hauteurSurface && globalY <= NiveauEau)
						{
							_densities[x, y, z] = -10.0f;
							_materials[x, y, z] = 0;
							_densitiesEau[x, y, z] = (NiveauEau + 1.0f) - y;
						}
						else
						{
							_densities[x, y, z] = -10.0f;
							_materials[x, y, z] = 0;
						}
					}
				}
			}
			// RÈGLE : Chunk procédural non touché par le joueur → jamais sauvegardé (régénération à la demande).
		}
	}

	private int CalculerHauteurTerrain(int xGlobal, int zGlobal)
	{
		float bruitBrut = _noiseSurface.GetNoise2D(xGlobal, zGlobal);
		float bruitNormalise = (bruitBrut + 1.0f) / 2.0f;
		float relief = Mathf.Pow(bruitNormalise, 3.0f);
		if (relief < 0.05f) relief = 0.0f;
		else relief = relief - 0.05f;

		int hauteurBase = ProfondeurBase + (int)(relief * AmplitudeMontagne);
		float crevasseBrute = _noiseRivieres.GetNoise2D(xGlobal, zGlobal);
		int profondeurEau = 0;
		if (crevasseBrute > 0.15f)
		{
			float intensiteRiviera = (crevasseBrute - 0.15f) / 0.85f;
			profondeurEau = (int)(Mathf.Pow(intensiteRiviera, 0.8f) * 22.0f);
		}
		return hauteurBase - profondeurEau;
	}

	private static float DeterministicRand(float x, float z)
	{
		uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663);
		return ((h % 10000) / 10000f);
	}

	/// <summary>Seuil de pente max (m) : si la hauteur varie de plus sur 1 m, pas de flore (évite lévitation sur bords).</summary>
	private const float SEUIL_PENTE_MAX = 0.8f;

	/// <summary>Loi de l'inclinaison : vrai si le terrain est assez plat pour la flore. Évalue la pente via différence de hauteur du bruit.</summary>
	private bool TerrainAssezPlat(int xGlobal, int zGlobal)
	{
		float h0 = CalculerHauteurTerrain(xGlobal, zGlobal);
		float hauteurNord = CalculerHauteurTerrain(xGlobal, zGlobal + 1);
		float hauteurSud = CalculerHauteurTerrain(xGlobal, zGlobal - 1);
		float hauteurEst = CalculerHauteurTerrain(xGlobal + 1, zGlobal);
		float hauteurOuest = CalculerHauteurTerrain(xGlobal - 1, zGlobal);
		float diffMax = Mathf.Max(
			Mathf.Max(Mathf.Abs(hauteurNord - h0), Mathf.Abs(hauteurSud - h0)),
			Mathf.Max(Mathf.Abs(hauteurEst - h0), Mathf.Abs(hauteurOuest - h0))
		);
		return diffMax < SEUIL_PENTE_MAX;
	}

	/// <summary>Retourne (hauteur surface, matériau) pour ensemencement. (-1, 0) si pas de sol.</summary>
	public (int ySurface, byte mat) ObtenirSurfaceEtMateriau(int lx, int lz)
	{
		int y = ObtenirHauteurSurfaceLocale(lx, lz);
		if (y < 0) return (-1, 0);
		lock (_verrouVoxel)
		{
			byte mat = _materials[lx, y, lz];
			if (mat == 4) mat = 3; // Cécité hydrique : eau → sable
			return (y, mat);
		}
	}

	/// <summary>Hauteur de surface depuis les données chargées (chunks disque). -1 si hors limites ou pas de sol.</summary>
	private int ObtenirHauteurSurfaceLocale(int lx, int lz)
	{
		if (lx < 0 || lx > TailleChunk || lz < 0 || lz > TailleChunk || _densities == null) return -1;
		for (int y = HauteurMax - 1; y >= 2; y--)
			if (_densities[lx, y, lz] > Isolevel && (y + 1 >= HauteurMax + 1 || _densities[lx, y + 1, lz] <= Isolevel))
				return y;
		return -1;
	}

	/// <summary>Loi de l'inclinaison (chunks disque) : vrai si le terrain chargé est assez plat à (lx, lz).</summary>
	private bool TerrainAssezPlatDepuisDonnees(int lx, int lz)
	{
		int h0 = ObtenirHauteurSurfaceLocale(lx, lz);
		if (h0 < 0) return false;
		int hx1 = ObtenirHauteurSurfaceLocale(lx + 1, lz);
		int hx2 = ObtenirHauteurSurfaceLocale(lx - 1, lz);
		int hz1 = ObtenirHauteurSurfaceLocale(lx, lz + 1);
		int hz2 = ObtenirHauteurSurfaceLocale(lx, lz - 1);
		float d1 = hx1 >= 0 ? Mathf.Abs(hx1 - h0) : 0f;
		float d2 = hx2 >= 0 ? Mathf.Abs(hx2 - h0) : 0f;
		float d3 = hz1 >= 0 ? Mathf.Abs(hz1 - h0) : 0f;
		float d4 = hz2 >= 0 ? Mathf.Abs(hz2 - h0) : 0f;
		float diffMax = Mathf.Max(Mathf.Max(d1, d2), Mathf.Max(d3, d4));
		return diffMax < SEUIL_PENTE_MAX;
	}

	private byte DeterminerMateriauCroûte(int globalY, int hauteurSurface, float temperature, float humidite)
	{
		if (globalY > NiveauEau + 60) return 5;
		if (globalY <= NiveauPlage) return (humidite > 0.3f) ? (byte)7 : (byte)3;
		if (temperature > 0.3f)
		{
			if (humidite < -0.2f) return 3;
			if (humidite > 0.3f) return 8;
			return 6;
		}
		if (temperature < -0.3f) return (humidite > 0.2f) ? (byte)9 : (byte)5;
		if (humidite < -0.3f) return 6;
		if (humidite > 0.3f) return 7;
		return 1;
	}

	/// <summary>Tableau C# byte[] pour sauvegarde binaire. Format: densities (4×N) + materials (1×N) + densitiesEau (4×N).</summary>
	public byte[] ObtenirTableauBytes()
	{
		int tx = TailleChunk + 1, ty = HauteurMax + 1, tz = TailleChunk + 1;
		int voxelCount = tx * ty * tz;
		var bytes = new byte[voxelCount * 9];
		lock (_verrouVoxel)
		{
			int idx = 0;
			for (int x = 0; x < tx; x++)
				for (int y = 0; y < ty; y++)
					for (int z = 0; z < tz; z++)
					{
						Buffer.BlockCopy(BitConverter.GetBytes(_densities[x, y, z]), 0, bytes, idx, 4); idx += 4;
						bytes[idx++] = _materials[x, y, z];
						Buffer.BlockCopy(BitConverter.GetBytes(_densitiesEau[x, y, z]), 0, bytes, idx, 4); idx += 4;
					}
		}
		return bytes;
	}

	/// <summary>Sauvegarde binaire sur disque. NE sauvegarde QUE si EstModifie (touché par DetruireVoxel/CreerMatiere).</summary>
	public void SauvegarderChunkSurDisque()
	{
		if (!_estModifie) return;
		string nom = GameState.Instance?.NomMondeActuel ?? "MonMonde";
		string dossierSave = ProjectSettings.GlobalizePath($"user://saves/{nom}/chunks/");
		Directory.CreateDirectory(dossierSave);
		string cheminFichier = Path.Combine(dossierSave, $"chunk_{ChunkOffsetX}_{ChunkOffsetZ}.bin");
		byte[] donnees = ObtenirTableauBytes();
		using (var writer = new BinaryWriter(File.Open(cheminFichier, FileMode.Create)))
		{
			writer.Write((byte)1);
			writer.Write(donnees.Length);
			writer.Write(donnees);
		}
		GD.Print($"ZERO-K : Cicatrice mémorisée. Chunk {ChunkOffsetX}_{ChunkOffsetZ} gravé sur le disque.");
	}

	/// <summary>Désérialise depuis byte[] (GetBuffer). Chunk chargé = pas modifié.</summary>
	public bool AppliquerTableauBytes(byte[] donnees)
	{
		int tx = TailleChunk + 1, ty = HauteurMax + 1, tz = TailleChunk + 1;
		int voxelCount = tx * ty * tz;
		int tailleAttendue = voxelCount * 9;
		if (donnees == null || donnees.Length != tailleAttendue) return false;

		lock (_verrouVoxel)
		{
			_densities = new float[tx, ty, tz];
			_materials = new byte[tx, ty, tz];
			_densitiesEau = new float[tx, ty, tz];
			int idx = 0;
			for (int x = 0; x < tx; x++)
				for (int y = 0; y < ty; y++)
					for (int z = 0; z < tz; z++)
					{
						_densities[x, y, z] = BitConverter.ToSingle(donnees, idx); idx += 4;
						_materials[x, y, z] = donnees[idx++];
						_densitiesEau[x, y, z] = BitConverter.ToSingle(donnees, idx); idx += 4;
					}
		}
		_estModifie = false;
		_chargeDepuisDisque = true; // MARQUER : ce chunk vient du disque — GenererDonneesVoxel ne doit JAMAIS le toucher.
		GenererInventaireFloreDepuisSurface(); // Flore pour chunks chargés (InventaireFlore non sauvegardé)
		return true;
	}

	/// <summary>Scanne la surface chargée et remplit InventaireFlore (chunks du disque). Gazon partout sur ID 1.</summary>
	private void GenererInventaireFloreDepuisSurface()
	{
		for (int x = 0; x < TailleChunk; x++)
			for (int z = 0; z < TailleChunk; z++)
			{
				int ySurface = -1;
				for (int y = HauteurMax - 1; y >= 2; y--)
					if (_densities[x, y, z] > Isolevel && (y + 1 >= HauteurMax + 1 || _densities[x, y + 1, z] <= Isolevel))
					{ ySurface = y; break; }
				if (ySurface < 0) continue;
				byte mat = _materials[x, ySurface, z];
				if (mat != 1) continue;
				if (!TerrainAssezPlatDepuisDonnees(x, z)) continue;
				float xGlobal = ChunkOffsetX * TailleChunk + x;
				float zGlobal = ChunkOffsetZ * TailleChunk + z;
				float altitudeFlore = ySurface;
				if (altitudeFlore <= NIVEAU_MIN_FLORE || altitudeFlore >= NIVEAU_MAX_FLORE) continue;
				var posGlobale = new Vector3I((int)xGlobal, ySurface, (int)zGlobal);
				InventaireFlore[posGlobale] = 0;
				float humiditeBrute = _noiseHumidite.GetNoise2D(xGlobal * 0.05f, zGlobal * 0.05f);
				float humiditeNorm = (humiditeBrute + 1f) * 0.5f;
				float chanceDePousse = 0f;
				if (humiditeNorm > 0.3f) chanceDePousse = (humiditeNorm - 0.3f) * 0.015f;
				if (chanceDePousse > 0.02f) chanceDePousse = 0.02f;
				if (chanceDePousse > 0f && DeterministicRand(xGlobal, zGlobal) < chanceDePousse)
					InventaireFlore[posGlobale] = (byte)(DeterministicRand(xGlobal + 17f, zGlobal) < 0.5f ? 1 : 2);
			}
	}

	/// <summary>Crible gravitationnel : purger les buissons dont le bloc support a été miné (évite lévitation).</summary>
	public void AuditerGraviteFlore()
	{
		if (InventaireFlore.Count == 0) return;
		var floreMorte = new List<Vector3I>();
		lock (_verrouVoxel)
		{
			foreach (var kv in InventaireFlore)
			{
				Vector3I posGlobale = kv.Key;
				int lx = posGlobale.X - ChunkOffsetX * TailleChunk;
				int ly = posGlobale.Y;
				int lz = posGlobale.Z - ChunkOffsetZ * TailleChunk;
				if (!EstDansLimitesChunk(lx, ly, lz)) continue;
				if (_densities[lx, ly, lz] <= Isolevel) floreMorte.Add(posGlobale);
			}
		}
		if (floreMorte.Count == 0) return;
		foreach (var mort in floreMorte) InventaireFlore.Remove(mort);
		_onFlorePurgée?.Invoke(new Vector2I(ChunkOffsetX, ChunkOffsetZ), new Dictionary<Vector3I, byte>(InventaireFlore));
	}

	/// <summary>Copie les données du chunk pour envoi au client. Quantification byte[] pour RPC (divise poids par 4).</summary>
	public DonneesChunk ObtenirDonneesPourClient()
	{
		lock (_verrouVoxel)
		{
			int tx = TailleChunk + 1, ty = HauteurMax + 1, tz = TailleChunk + 1;
			var d = new DonneesChunk
			{
				CoordChunk = new Vector2I(ChunkOffsetX, ChunkOffsetZ),
				TailleChunk = TailleChunk,
				HauteurMax = HauteurMax,
				DensitiesQuantifiees = DonneesChunk.CompresserDensitesPourReseau(_densities, tx, ty, tz),
				DensitiesEauQuantifiees = DonneesChunk.CompresserDensitesPourReseau(_densitiesEau, tx, ty, tz),
				MaterialsFlat = new byte[tx * ty * tz],
				InventaireFlore = new Dictionary<Vector3I, byte>(InventaireFlore)
			};
			int idx = 0;
			for (int x = 0; x < tx; x++)
				for (int y = 0; y < ty; y++)
					for (int z = 0; z < tz; z++)
						d.MaterialsFlat[idx++] = _materials[x, y, z];
			return d;
		}
	}

	private const byte ID_ITEM_BUISSON_PLEIN = 10;
	private const byte ID_ITEM_BUISSON_VIDE = 11;

	public void DetruireVoxel(Vector3 pointImpactGlobal, float rayonExplosion, Action<List<int>> onSectionsAffectees = null)
	{
		Vector3 pointLocal = pointImpactGlobal - PositionMonde;
		var positionsDetruites = new List<Vector3I>();

		// Destruction radiale : flore dans le rayon de la pioche (2 m) — atomiser AVANT de modifier la densité
		const float rayonDestructionFlore = 2.0f;
		var floreDetruite = new List<KeyValuePair<Vector3I, byte>>();
		foreach (var kv in InventaireFlore)
		{
			Vector3 posFlore = new Vector3(kv.Key.X + 0.5f, kv.Key.Y + 0.5f, kv.Key.Z + 0.5f);
			if (posFlore.DistanceTo(pointImpactGlobal) <= rayonDestructionFlore) floreDetruite.Add(kv);
		}
		foreach (var kv in floreDetruite)
		{
			InventaireFlore.Remove(kv.Key);
			// Gazon (0) : disparaît seulement, pas d'entité. Buissons (1,2) : spawn BlocChutant.
			if (kv.Value == 1 || kv.Value == 2)
			{
				Vector3 posSpawn = new Vector3(kv.Key.X + 0.5f, kv.Key.Y + 0.5f, kv.Key.Z + 0.5f);
				byte idItem = (byte)(kv.Value == 1 ? ID_ITEM_BUISSON_PLEIN : ID_ITEM_BUISSON_VIDE);
				_callbackBlocChutant?.Invoke(posSpawn, idItem);
			}
		}
		if (floreDetruite.Count > 0)
			_onFlorePurgée?.Invoke(new Vector2I(ChunkOffsetX, ChunkOffsetZ), new Dictionary<Vector3I, byte>(InventaireFlore));

		lock (_verrouVoxel)
		{
			float rayon2 = rayonExplosion * rayonExplosion;
			bool modifie = false;

			for (int x = 0; x <= TailleChunk; x++)
				for (int y = 0; y <= HauteurMax; y++)
					for (int z = 0; z <= TailleChunk; z++)
					{
						if (y <= 2) continue;
						float dx = pointLocal.X - x, dy = pointLocal.Y - y, dz = pointLocal.Z - z;
						if (dx * dx + dy * dy + dz * dz <= rayon2)
						{
							bool etaitSolide = _densities[x, y, z] > Isolevel;
							_densities[x, y, z] = Mathf.Max(_densities[x, y, z] - 5.0f, -1.0f); // Plancher absolu : le voxel ne peut pas être "plus que vide"
							modifie = true;
							if (etaitSolide) positionsDetruites.Add(new Vector3I(x, y, z));
						}
					}
			if (!modifie) return;
			_estModifie = true; // Joueur a miné → sauvegarde obligatoire au déchargement.
			foreach (var pos in positionsDetruites) VerifierStabilite(pos);
		}

		foreach (var pos in positionsDetruites)
		{
			_reveillerEau?.Invoke(PositionMonde + new Vector3(pos.X, pos.Y, pos.Z));
			var posGlobal = new Vector3I((int)PositionMonde.X + pos.X, pos.Y, (int)PositionMonde.Z + pos.Z);
			_onVoxelModifie?.Invoke(posGlobal, 0);
		}
		AuditerGraviteFlore();
	}

	public void CreerMatiere(Vector3 pointCibleGlobal, float rayon, byte idMatiere = 1, Action<List<int>> onSectionsAffectees = null)
	{
		Vector3 pointLocal = pointCibleGlobal - PositionMonde;
		var positionsModifiees = new List<Vector3I>();

		lock (_verrouVoxel)
		{
			float rayon2 = rayon * rayon;
			for (int x = 0; x <= TailleChunk; x++)
				for (int y = 0; y <= HauteurMax; y++)
					for (int z = 0; z <= TailleChunk; z++)
					{
						if (y <= 2) continue;
						float dx = pointLocal.X - x, dy = pointLocal.Y - y, dz = pointLocal.Z - z;
						if (dx * dx + dy * dy + dz * dz <= rayon2)
						{
							_densities[x, y, z] = Mathf.Min(_densities[x, y, z] + 5.0f, 1.0f); // Plafond absolu : le voxel ne peut pas être "plus que plein"
							_materials[x, y, z] = idMatiere; // Injection couleur : le Shader lit ce tableau
							positionsModifiees.Add(new Vector3I(x, y, z));
						}
					}
			if (positionsModifiees.Count == 0) return;
			_estModifie = true; // Joueur a placé des blocs → sauvegarde obligatoire.
		}

		foreach (var pos in positionsModifiees)
		{
			_reveillerEau?.Invoke(PositionMonde + new Vector3(pos.X, pos.Y, pos.Z));
			var posGlobal = new Vector3I((int)PositionMonde.X + pos.X, pos.Y, (int)PositionMonde.Z + pos.Z);
			_onVoxelModifie?.Invoke(posGlobal, idMatiere);
		}
		AuditerGraviteFlore();
	}

	private List<int> ObtenirSectionsAffectees(List<Vector3I> positions)
	{
		const int HAUTEUR_SECTION = 16, NB_SECTIONS = 16;
		var sections = new HashSet<int>();
		foreach (var pos in positions)
		{
			int idx = Mathf.FloorToInt(pos.Y / (float)HAUTEUR_SECTION);
			if (idx >= 0 && idx < NB_SECTIONS) sections.Add(idx);
			if (pos.Y % HAUTEUR_SECTION == 0 && pos.Y > 0 && idx - 1 >= 0) sections.Add(idx - 1);
		}
		return new List<int>(sections);
	}

	private bool EstDansLimitesChunk(int x, int y, int z) =>
		x >= 0 && x <= TailleChunk && y >= 0 && y <= HauteurMax && z >= 0 && z <= TailleChunk;

	private Vector2I? ObtenirChunkVoisinSiHorsLimites(int x, int y, int z)
	{
		if (y < 0 || y > HauteurMax) return null;
		if (x >= 0 && x <= TailleChunk && z >= 0 && z <= TailleChunk) return null;
		int dx = x < 0 ? -1 : (x > TailleChunk ? 1 : 0);
		int dz = z < 0 ? -1 : (z > TailleChunk ? 1 : 0);
		return new Vector2I(ChunkOffsetX + dx, ChunkOffsetZ + dz);
	}

	private bool EstSolide(int x, int y, int z) =>
		EstDansLimitesChunk(x, y, z) && _densities[x, y, z] > Isolevel;

	/// <summary>Lecture directe de l'ADN : retourne l'ID matière exact du voxel (aligné avec le Shader). 1 si air ou hors limites. CÉCITÉ HYDRIQUE : jamais 4 (eau).</summary>
	public byte ObtenirMatiereAtLocal(int lx, int ly, int lz)
	{
		if (!EstDansLimitesChunk(lx, ly, lz) || _densities == null) return 1;
		lock (_verrouVoxel)
		{
			if (_densities[lx, ly, lz] <= Isolevel) return 1; // Air : fallback terre
			byte mat = _materials[lx, ly, lz];
			// L'EAU (ID 4) est un fluide géré ailleurs. Le Marching Cubes sous l'eau = SABLE ou TERRE. Jamais retourner 4.
			if (mat == 4) return 3; // Fond marin = Sable
			return mat;
		}
	}

	private static int ObtenirResistanceMateriau(byte id)
	{
		if (id == 3) return 0;
		if (id == 2) return 2;
		return 1;
	}

	private bool AUnSupport(int bx, int by, int bz, byte mat)
	{
		int r = ObtenirResistanceMateriau(mat);
		if (r == 0) return EstSolide(bx, by - 1, bz);
		for (int x = -r; x <= r; x++)
			for (int z = -r; z <= r; z++)
				if (Mathf.Abs(x) + Mathf.Abs(z) <= r &&
					EstSolide(bx + x, by, bz + z) && EstSolide(bx + x, by - 1, bz + z))
					return true;
		return false;
	}

	private void VerifierStabilite(Vector3I pos)
	{
		int xu = pos.X, yu = pos.Y + 1, zu = pos.Z;
		if (yu < 0 || yu > HauteurMax) return;
		if (!EstDansLimitesChunk(xu, yu, zu))
		{
			var v = ObtenirChunkVoisinSiHorsLimites(xu, yu, zu);
			if (v == null || _chunkEstCharge == null || !_chunkEstCharge(v.Value)) return;
			return;
		}
		if (!EstSolide(xu, yu, zu)) return;
		byte mat = _materials[xu, yu, zu];
		if (mat == 0) mat = 2;
		if (AUnSupport(xu, yu, zu, mat)) return;

		lock (_verrouVoxel)
		{
			_densities[xu, yu, zu] = -10.0f;
			if (_densitiesEau != null) _densitiesEau[xu, yu, zu] = -1.0f;
		}

		_reveillerEau?.Invoke(PositionMonde + new Vector3(xu, yu, zu));
		_callbackBlocChutant?.Invoke(PositionMonde + new Vector3(xu + 0.5f, yu + 0.5f, zu + 0.5f), mat);

		AuditerGraviteFlore();
		VerifierStabilite(new Vector3I(xu, yu, zu));
		VerifierStabilite(new Vector3I(xu - 1, yu - 1, zu));
		VerifierStabilite(new Vector3I(xu + 1, yu - 1, zu));
		VerifierStabilite(new Vector3I(xu, yu - 1, zu - 1));
		VerifierStabilite(new Vector3I(xu, yu - 1, zu + 1));
	}

	public bool EstVoxelEau(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return false;
		lock (_verrouVoxel) return _densitiesEau != null && _densitiesEau[x, y, z] > Isolevel;
	}

	public bool EstVoxelAir(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return false;
		lock (_verrouVoxel)
		{
			bool sol = _densities[x, y, z] > Isolevel;
			bool eau = _densitiesEau != null && _densitiesEau[x, y, z] > Isolevel;
			return !sol && !eau;
		}
	}

	public bool EstVoxelSolide(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return false;
		lock (_verrouVoxel) return _densities[x, y, z] > Isolevel;
	}

	public void DefinirVoxelEau(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z) || y <= 2) return;
		lock (_verrouVoxel)
		{
			_densities[x, y, z] = -10.0f;
			_materials[x, y, z] = 4;
			if (_densitiesEau != null) _densitiesEau[x, y, z] = 1.0f;
		}
		AuditerGraviteFlore();
	}

	public void DefinirVoxelAir(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return;
		lock (_verrouVoxel)
		{
			_densities[x, y, z] = -10.0f;
			_materials[x, y, z] = 0;
			if (_densitiesEau != null) _densitiesEau[x, y, z] = -1.0f;
		}
		AuditerGraviteFlore();
	}

	/// <summary>Met à jour un voxel aux coords locales (réplication du padding des voisins).</summary>
	public void SetVoxelLocal(int lx, int ly, int lz, byte id)
	{
		if (!EstDansLimitesChunk(lx, ly, lz)) return;
		lock (_verrouVoxel)
		{
			if (id == 0)
			{
				_densities[lx, ly, lz] = -10.0f;
				_materials[lx, ly, lz] = 0;
				if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = -1.0f;
			}
			else if (id == 4)
			{
				_densities[lx, ly, lz] = -10.0f;
				_materials[lx, ly, lz] = 4;
				if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = 1.0f;
			}
			else
			{
				_densities[lx, ly, lz] = 10.0f;
				_materials[lx, ly, lz] = id;
				if (_densitiesEau != null) _densitiesEau[lx, ly, lz] = -1.0f;
			}
		}
		AuditerGraviteFlore();
	}
}
