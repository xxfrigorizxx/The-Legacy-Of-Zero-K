using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FileAccess = Godot.FileAccess;

/// <summary>Détient les chunks serveur (données voxel), la génération, la simulation d'eau. Aucun MeshInstance3D.</summary>
public partial class Monde_Serveur : Node
{
	[Export] public int TailleChunk = 16;
	[Export] public int HauteurMax = 720;  // Montagnes jusqu'à 700
	[Export] public int SeedTerrain = 19847;
	[Export] public int RayonMondeChunks = 1000;
	[Export] public int RenderDistance = 200;

	/// <summary>Matériel du terrain pour les débris (BlocChutant). Assigné par Gestionnaire_Monde.</summary>
	public Material MaterielTerrain;

	/// <summary>Fuseau horaire de la dimension en heures. Monde 1 = 0, Monde 2 = +6, Monde 3 = +12, Monde 4 = +18.</summary>
	[Export] public double FuseauHoraireHeures = 0.0;

	private Dictionary<Vector2I, Chunk_Serveur> _chunks = new Dictionary<Vector2I, Chunk_Serveur>();
	private Queue<Vector3I> _fileEau = new Queue<Vector3I>();
	private HashSet<Vector3I> _eauActive = new HashSet<Vector3I>();
	private float _tempsEcoulement;
	private const float TICK_EAU = 0.05f;
	private const int MaxEauParTick = 32;
	private static readonly Vector3I[] DirEauHoriz = { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 0, -1), new Vector3I(0, 0, 1) };
	private static readonly Vector3I[] DirVoisins = { new Vector3I(0, 1, 0), new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 0, -1), new Vector3I(0, 0, 1) };
	private static readonly Vector3I[] DirReveil = { new Vector3I(0, 1, 0), new Vector3I(0, -1, 0), new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) };

	private Node _parentPourBlocsChutants;
	private Action<Vector2I, List<int>> _onChunkModifie;
	private Action<Vector2I, DonneesChunk> _onEnvoyerChunk;
	private Action<Vector2I, Dictionary<Vector3I, byte>> _onFloreModifie;
	private Action<Vector3I, byte> _onVoxelModifie;
	private Action<Vector2I> _onOrdonnerDestructionChunk;
	private Func<Vector3> _obtenirPositionJoueur;

	private List<Vector2I> _chunksEnAttenteEnvoi = new List<Vector2I>();
	private Queue<ColisChunk> _fileEnvoiReseau = new Queue<ColisChunk>();
	private HashSet<Vector2I> _chunksEnCoursGeneration = new HashSet<Vector2I>();
	private int _chunksEnGenerationActive;
	private static readonly int MaxThreadsGeneration = 4;
	[Export] public int MultiplicateurCharge = 16;
	private int LancerMaxTaches => MaxThreadsGeneration * MultiplicateurCharge;
	private const int MaxChunksEnvoiParTick = 16;
	private bool _modificationEnCours;
	private readonly object _verrouGeneration = new object();
	private ConcurrentQueue<(Vector2I coord, Chunk_Serveur chunk, DonneesChunk donnees)> _chunksGeneres = new ConcurrentQueue<(Vector2I, Chunk_Serveur, DonneesChunk)>();

	private struct ColisChunk
	{
		public Vector2I Coord;
		public DonneesChunk Donnees;
	}

	/// <summary>Pierres chargées depuis disque → instanciation goutte-à-goutte (quand chunk dessiné à l'écran).</summary>
	private Queue<(Vector3 pos, int id, int indexCache, int indexChimique)> _filePierresAInstancier = new Queue<(Vector3, int, int, int)>();
	private const int MaxPierresParFrame = 12;

	/// <summary>Pools de roches par taille (ID 10–14). Limite 50 par catégorie. Plus loin du joueur → formes plus cassées (2e moitié du cache).</summary>
	private Dictionary<int, List<RigidBody3D>> _poolsRochesParTaille = new Dictionary<int, List<RigidBody3D>>();
	private const int TaillePoolParType = 50;
	/// <summary>En deçà de cette distance au niveau d'eau (Y=103) : formes douces. Au-delà (hautes montagnes ou profondeur) : formes plus cassées.</summary>
	private const float SeuilDistanceEauFormesCassées = 25f;

	private float _tempsDepuisVerifDecharge;
	private const float IntervalleEvaluationTectonique = 0.5f;
	/// <summary>Tapis roulant décharge : au plus N chunks sauvegardés/déchargés par frame (évite lag).</summary>
	private const int MaxChunksDechargeParTick = 2;
	private List<Vector2I> _chunksEnAttenteDecharge = new List<Vector2I>();

	public void Initialiser(Node parentPourBlocsChutants, Action<Vector2I, List<int>> onChunkModifie, Action<Vector2I, DonneesChunk> onEnvoyerChunk = null, Action<Vector2I, Dictionary<Vector3I, byte>> onFloreModifie = null, Action<Vector3I, byte> onVoxelModifie = null, Action<Vector2I> onOrdonnerDestructionChunk = null, Func<Vector3> obtenirPositionJoueur = null)
	{
		_parentPourBlocsChutants = parentPourBlocsChutants;
		_onChunkModifie = onChunkModifie;
		_onEnvoyerChunk = onEnvoyerChunk;
		_onFloreModifie = onFloreModifie;
		_onVoxelModifie = onVoxelModifie;
		_onOrdonnerDestructionChunk = onOrdonnerDestructionChunk;
		_obtenirPositionJoueur = obtenirPositionJoueur;
		string nom = GameState.Instance?.NomMondeActuel ?? "MonMonde";
		DirAccess.MakeDirRecursiveAbsolute($"user://saves/{nom}/chunks");
		GD.Print($"ZERO-K : Dossier chunks actif = user://saves/{nom}/chunks/ (lecture ET écriture)");
		CreerPoolsRochesParTaille();
	}

	/// <summary>Sauvegarde d'urgence : sauvegarde uniquement les chunks modifiés (EstModifie).</summary>
	public void SauvegarderMondeEntier()
	{
		GD.Print("ZERO-K : Lancement du Râle d'Agonie. Sauvegarde des Chunks modifiés...");
		int chunksSauves = 0;
		foreach (var kvp in _chunks)
		{
			Vector2I coord = kvp.Key;
			Chunk_Serveur chunk = kvp.Value;
			if (chunk.EstModifie)
			{
				chunk.SauvegarderChunkSurDisque();
				SauvegarderPierresChunk(coord);
				chunksSauves++;
			}
		}
		GD.Print($"ZERO-K : Râle d'Agonie terminé. {chunksSauves} Chunks gravés sur le disque.");
	}

	public override void _ExitTree()
	{
		SauvegarderMondeEntier();
	}

	public override void _Notification(int what)
	{
		// Utilisation stricte de Node.NotificationWMCloseRequest (WM en majuscules)
		if (what == Node.NotificationWMCloseRequest)
		{
			SauvegarderMondeEntier();
			GetTree().Quit();
		}
	}

	/// <summary>Enregistre une demande de chunk. Tri par proximité du joueur (Préemption Absolue).</summary>
	public void EnregistrerDemandeChunk(Vector2I coord)
	{
		if (!_chunksEnAttenteEnvoi.Contains(coord))
			_chunksEnAttenteEnvoi.Add(coord);
	}

	public override void _PhysicsProcess(double delta)
	{
		bool hadModifications = _modificationEnCours;
		_modificationEnCours = false;

		// Récupérer les chunks générés par les workers (Main Thread uniquement)
		// SÉGRÉGATION : ne JAMAIS écraser un chunk chargé depuis le disque avec un chunk procédural.
		while (_chunksGeneres.TryDequeue(out var result))
		{
			_chunksEnCoursGeneration.Remove(result.coord);
			_chunksEnGenerationActive--;
			if (_chunks.TryGetValue(result.coord, out var existant) && existant.EstChargeDepuisDisque)
				continue; // Chunk déjà ressuscité du disque — ignorer le résultat procédural.
			if (!_chunks.ContainsKey(result.coord))
				_chunks[result.coord] = result.chunk;
			DeclencherEnsemencement(result.coord, result.chunk, TailleChunk);
			var donnees = _chunks[result.coord].ObtenirDonneesPourClient();
			_fileEnvoiReseau.Enqueue(new ColisChunk { Coord = result.coord, Donnees = donnees });
		}

		// Manufacture parallèle : purge des obsolètes puis extraction radiale
		if (!hadModifications)
		{
			Vector3 posObs = _obtenirPositionJoueur?.Invoke() ?? Vector3.Zero;
			float rayonMaxCarrePurge = (RenderDistance + 1) * (RenderDistance + 1);
			_chunksEnAttenteEnvoi.RemoveAll(c =>
			{
				float d2 = DistanceCarreeAuJoueur(c, posObs);
				return d2 > rayonMaxCarrePurge;
			});
			Vector3 posObservation = posObs;
			while (_chunksEnAttenteEnvoi.Count > 0 && _chunksEnGenerationActive < LancerMaxTaches)
			{
				Vector2I chunkCible = ExtraireChunkLePlusProche(_chunksEnAttenteEnvoi, posObservation);

				float distCarree = DistanceCarreeAuJoueur(chunkCible, posObservation);
				float rayonMaxCarre = (RenderDistance + 1) * (RenderDistance + 1);
				if (distCarree > rayonMaxCarre)
					continue;

				if (_chunks.TryGetValue(chunkCible, out var existant))
				{
					_fileEnvoiReseau.Enqueue(new ColisChunk { Coord = chunkCible, Donnees = existant.ObtenirDonneesPourClient() });
					continue;
				}

				Chunk_Serveur chunkActuel = null;

				// BRANCHE 1 : RÉSURRECTION PURE — AUCUN appel de génération. Le chunk part directement au Mesh.
				if (FichierChunkExiste(chunkCible))
				{
					chunkActuel = ChargerChunkDepuisDisque(chunkCible);
					// RÈGLE D'ARCHITECTURE : GenererTerrainDeBase, GenererCoucheSurface, GenererEau, GenererArbres
					// ne sont JAMAIS appelés ici. Le chunk chargé est final.
				}

				// BRANCHE 2 : CRÉATION PROCÉDURALE — TOUTES les passes (terrain, surface, eau) UNIQUEMENT ici.
				if (chunkActuel == null)
				{
					lock (_verrouGeneration)
					{
						if (!_chunksEnCoursGeneration.Add(chunkCible))
							continue;
						_chunksEnGenerationActive++;
					}
					Vector2I coord = chunkCible;
					Task.Run(() =>
					{
						var chunk = CreerChunkServeur(coord);
						// TOUTES les passes : GenererTerrainDeBase, GenererCoucheSurface, GenererEau — encapsulées dans GenererDonneesVoxel.
						chunk.GenererDonneesVoxel();
						var donnees = chunk.ObtenirDonneesPourClient();
						_chunksGeneres.Enqueue((coord, chunk, donnees));
					});
					continue;
				}

				// BRANCHE COMMUNE : Chunk ressuscité. Pierres : chargement sauvegardées si fichier existe, sinon procédural. Spawn uniquement quand chunk demandé (visible écran).
				_chunks[chunkCible] = chunkActuel;
				if (!ChargerEtSpawnerPierresChunk(chunkCible))
					DeclencherEnsemencement(chunkCible, chunkActuel, TailleChunk);
				_fileEnvoiReseau.Enqueue(new ColisChunk { Coord = chunkCible, Donnees = chunkActuel.ObtenirDonneesPourClient() });
			}
		}

		// Tapis roulant : 1 envoi au client par Tick (60 TPS)
		int envoisCeTick = 0;
		while (_fileEnvoiReseau.Count > 0 && envoisCeTick < MaxChunksEnvoiParTick)
		{
			ColisChunk colis = _fileEnvoiReseau.Dequeue();
			_onEnvoyerChunk?.Invoke(colis.Coord, colis.Donnees);
			envoisCeTick++;
		}

		// Réveil des pierres dormantes : quand joueur dans 2 chunks, le terrain est chargé → on dégèle
		ReveillerPierresDansRayon();

		// Goutte-à-goutte : pierres chargées depuis disque, instanciées quand chunk dessiné à l'écran
		int nPierres = 0;
		while (nPierres < MaxPierresParFrame && _filePierresAInstancier.Count > 0)
		{
			var (pos, id, idx, chim) = _filePierresAInstancier.Dequeue();
			// Plus la roche est loin du niveau d'eau (Y=103), plus elle peut prendre une forme cassée (2e moitié du cache)
			if (idx < 0)
			{
				float distEau = Mathf.Abs(pos.Y - NIVEAU_EAU);
				bool formesCassées = distEau > SeuilDistanceEauFormesCassées;
				idx = formesCassées ? -2 : -1;
			}
			GenererItemPhysique(pos, id, idx, chim);
			nPierres++;
		}

		_tempsEcoulement += (float)delta;
		if (_tempsEcoulement < TICK_EAU) return;
		_tempsEcoulement = 0;

		_tempsDepuisVerifDecharge += (float)delta;
		if (_tempsDepuisVerifDecharge >= IntervalleEvaluationTectonique)
		{
			_tempsDepuisVerifDecharge = 0f;
			EvaluerDechargementChunks();
		}

		// Tapis roulant décharge : N chunks par frame (sauvegarde + décharge progressifs)
		ProcesserDechargeProgressive();

		int n = Math.Min(_fileEau.Count, MaxEauParTick);
		for (int i = 0; i < n; i++)
		{
			Vector3I pos = _fileEau.Dequeue();
			_eauActive.Remove(pos);
			if (!EstVoxelEau(pos)) continue;

			Vector3I posBas = pos + new Vector3I(0, -1, 0);
			if (posBas.Y < 0) { DefinirVoxel(pos, 0); continue; }

			if (EstVoxelAir(posBas))
			{
				DefinirVoxel(posBas, 4);
				DefinirVoxel(pos, 0);
				ActiverEau(posBas);
				ReveillerVoisins(pos);
				continue;
			}

			bool aPression = EstVoxelEau(pos + new Vector3I(0, 1, 0));
			foreach (var d in DirEauHoriz)
			{
				Vector3I pc = pos + d, pcb = pc + new Vector3I(0, -1, 0);
				if (!EstVoxelAir(pc)) continue;
				bool auBord = EstVoxelAir(pcb);
				if (aPression || auBord)
				{
					DefinirVoxel(pc, 4);
					DefinirVoxel(pos, 0);
					ActiverEau(pc);
					ReveillerVoisins(pos);
					break;
				}
			}
		}
	}

	private void ActiverEau(Vector3I pos)
	{
		if (_eauActive.Add(pos)) _fileEau.Enqueue(pos);
	}

	private void ReveillerVoisins(Vector3I pos)
	{
		foreach (var d in DirVoisins)
			if (EstVoxelEau(pos + d)) ActiverEau(pos + d);
	}

	public void ReveillerEauAdjacente(Vector3 pointGlobal)
	{
		int gx = Mathf.FloorToInt(pointGlobal.X), gy = Mathf.FloorToInt(pointGlobal.Y), gz = Mathf.FloorToInt(pointGlobal.Z);
		var basePos = new Vector3I(gx, gy, gz);
		foreach (var d in DirReveil)
			if (EstVoxelEau(basePos + d)) ActiverEau(basePos + d);
	}

	public bool ChunkEstCharge(Vector2I coord) => _chunks.ContainsKey(coord);

	public Chunk_Serveur ObtenirOuCreerChunk(Vector2I coord)
	{
		if (_chunks.TryGetValue(coord, out var c)) return c;

		Chunk_Serveur chunkActuel = null;
		// BRANCHE 1 : RÉSURRECTION — AUCUNE génération.
		if (FichierChunkExiste(coord))
			chunkActuel = ChargerChunkDepuisDisque(coord);
		// BRANCHE 2 : CRÉATION PROCÉDURALE — TOUTES les passes ici.
		if (chunkActuel == null)
		{
			chunkActuel = CreerChunkServeur(coord);
			chunkActuel.GenererDonneesVoxel(); // GenererTerrainDeBase, Surface, Eau — UNIQUEMENT pour chunks ex nihilo.
		}
		_chunks[coord] = chunkActuel;
		return chunkActuel;
	}

	private static bool FichierChunkExiste(Vector2I coord)
	{
		return File.Exists(ProjectSettings.GlobalizePath(DonneesChunk.ObtenirCheminChunk(coord)));
	}

	private static string ObtenirCheminSauvegarde(Vector2I coord) => DonneesChunk.ObtenirCheminChunk(coord);

	/// <summary>Délègue au chunk la sauvegarde binaire. NE sauvegarde QUE si EstModifie.</summary>
	private void SauvegarderChunkSurDisque(Vector2I coord, Chunk_Serveur chunk)
	{
		chunk.SauvegarderChunkSurDisque();
	}

	/// <summary>Résurrection : chargement binaire via BinaryReader. Si fichier absent ou corrompu → régénération procédurale.</summary>
	private Chunk_Serveur ChargerChunkDepuisDisque(Vector2I coord)
	{
		GD.Print($"ZERO-K DIAG : Tentative chargement Chunk {coord}...");
		string cheminGodot = ObtenirCheminSauvegarde(coord);
		string cheminAbsolu = ProjectSettings.GlobalizePath(cheminGodot);
		if (!File.Exists(cheminAbsolu))
		{
			GD.PrintErr($"ZERO-K REJET : Chunk {coord} — fichier inexistant.");
			return null;
		}
		int voxelCount = (TailleChunk + 1) * (HauteurMax + 1) * (TailleChunk + 1);
		int tailleAttendue = voxelCount * 9;
		byte[] donneesVoxels;
		try
		{
			using (var reader = new BinaryReader(File.Open(cheminAbsolu, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read)))
			{
				byte version = reader.ReadByte();
				if (version != 1)
				{
					GD.PrintErr($"ZERO-K REJET : Chunk {coord} — version {version} non supportée.");
					return null;
				}
				int tailleLu = reader.ReadInt32();
				if (tailleLu != tailleAttendue)
				{
					GD.PrintErr($"ZERO-K REJET : Chunk {coord} corrompu (taille {tailleLu} ≠ {tailleAttendue}). Régénération forcée.");
					return null;
				}
				donneesVoxels = reader.ReadBytes(tailleLu);
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"ZERO-K REJET : Chunk {coord} — erreur lecture : {ex.Message}");
			return null;
		}
		if (donneesVoxels == null || donneesVoxels.Length != tailleAttendue)
		{
			GD.PrintErr($"ZERO-K REJET : Chunk {coord} refusé ! Taille lue : {donneesVoxels?.Length ?? 0} | Attendue : {tailleAttendue}.");
			return null;
		}
		GD.Print($"ZERO-K SUCCÈS : Chunk {coord} chargé depuis le disque ({donneesVoxels.Length} bytes).");
		var chunk = CreerChunkServeur(coord);
		if (!chunk.AppliquerTableauBytes(donneesVoxels))
		{
			GD.PrintErr($"ZERO-K REJET : Chunk {coord} — AppliquerTableauBytes a échoué. Régénération forcée.");
			return null;
		}
		return chunk;
	}

	private Chunk_Serveur CreerChunkServeur(Vector2I coord)
	{
		var chunk = new Chunk_Serveur(
			coord.X, coord.Y, TailleChunk, HauteurMax, SeedTerrain,
			(pos, mat) => { SpawnBlocChutant(pos, mat); },
			ChunkEstCharge,
			ReveillerEauAdjacente
		);
		chunk.SetOnVoxelModifie((pos, id) => _onVoxelModifie?.Invoke(pos, id));
		chunk.SetOnFlorePurgée((c, inventaire) => _onFloreModifie?.Invoke(c, inventaire));
		return chunk;
	}

	private void SpawnBlocChutant(Vector3 pos, byte mat)
	{
		if (_parentPourBlocsChutants == null) return;
		var matTerrain = MaterielTerrain ?? GD.Load<Material>("res://Manteau_Planetaire.tres");
		var bloc = BlocChutant.Creer(pos, mat, matTerrain);
		_parentPourBlocsChutants.AddChild(bloc);
		bloc.GlobalPosition = pos;
	}

	private const float NIVEAU_EAU = 103f;  // +1 m
	private const float DECALAGE_SPAWN_VERTICAL = 1.2f; // Légèrement au-dessus du terrain à la génération, tombe quand réveillé
	/// <summary>Rayon en chunks : pierres gelées s'activent quand le joueur entre dans cette zone (comme le gazon). Chunk garanti chargé.</summary>
	private const int RAYON_ACTIVATION_PIERRES_CHUNKS = 2;
	private const int ID_PETITE_PIERRE = 10;

	/// <summary>Délai de synchronisation : attend 2 frames physiques, puis enfile sur le tapis roulant (ordre spatial logique).</summary>
	private async void DeclencherEnsemencement(Vector2I chunkCoord, Chunk_Serveur chunk, float tailleChunk)
	{
		await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
		var positionsFiltrees = CollecterPositionsEnsemencement(chunkCoord, chunk, tailleChunk);
		var aEnfiler = new List<(Vector3 pos, int id, int indexCache, int indexChimique)>();
		foreach (var (pos, id) in positionsFiltrees)
			aEnfiler.Add((pos, id, -1, -1));
		EnfilerPierresSurTapisRoulant(aEnfiler);
	}

	private const int ID_SILEX = 11;
	private const int ID_PIERRE_MOYENNE = 12;
	private const int ID_GROSSE_PIERRE = 13;
	private const int ID_TRES_GROSSE_PIERRE = 14;

	/// <summary>Pré-crée les pools par taille (chunk en génération lance le dé → on prend une du pool de cette taille).</summary>
	private void CreerPoolsRochesParTaille()
	{
		if (_parentPourBlocsChutants == null) return;
		int[] ids = { ID_PETITE_PIERRE, ID_SILEX, ID_PIERRE_MOYENNE, ID_GROSSE_PIERRE, ID_TRES_GROSSE_PIERRE };
		foreach (int id in ids)
		{
			_poolsRochesParTaille[id] = new List<RigidBody3D>();
			for (int i = 0; i < TaillePoolParType; i++)
			{
				var rb = CreerNouvelleRoche(id, -1, -1);
				_poolsRochesParTaille[id].Add(rb);
			}
		}
		GD.Print($"ZERO-K : Pools roches par taille créés ({ids.Length} x {TaillePoolParType}).");
	}

	/// <summary>Collecte les positions et IDs à ensemencer (sans instancier).</summary>
	private List<(Vector3 pos, int id)> CollecterPositionsEnsemencement(Vector2I chunkCoord, Chunk_Serveur chunk, float tailleChunk)
	{
		var liste = new List<(Vector3 pos, int id)>();
		var rng = new RandomNumberGenerator();
		rng.Seed = (ulong)(chunkCoord.X * 73856093 + chunkCoord.Y * 19349663 + SeedTerrain);

		for (float x = 0; x < tailleChunk; x += 3f)
		{
			for (float z = 0; z < tailleChunk; z += 3f)
			{
				if (rng.Randf() > 0.02f) continue;
				int lx = Mathf.Clamp(Mathf.FloorToInt(x), 0, (int)tailleChunk);
				int lz = Mathf.Clamp(Mathf.FloorToInt(z), 0, (int)tailleChunk);
				var (ySurface, idMatiere) = chunk.ObtenirSurfaceEtMateriau(lx, lz);
				if (ySurface < 0) continue;

				Vector3 pointImpact = new Vector3(
					chunkCoord.X * tailleChunk + x + 0.5f,
					ySurface + 0.5f,
					chunkCoord.Y * tailleChunk + z + 0.5f
				);
				Vector3 pointDeSpawnSecurise = pointImpact + new Vector3(0, DECALAGE_SPAWN_VERTICAL, 0);

				if (idMatiere == 3 && pointImpact.Y < NIVEAU_EAU)
				{
					liste.Add((pointDeSpawnSecurise, ID_SILEX));
					continue;
				}

				int idTailleChoisie = 0;
				float proba = rng.Randf();
				if (idMatiere == 1 || idMatiere == 3) idTailleChoisie = ID_PETITE_PIERRE;
				else if (idMatiere == 7 || idMatiere == 8) idTailleChoisie = (proba > 0.4f) ? ID_PETITE_PIERRE : ID_PIERRE_MOYENNE;
				else if (idMatiere == 5 || idMatiere == 6) idTailleChoisie = (proba > 0.5f) ? ID_PETITE_PIERRE : ID_PIERRE_MOYENNE;
				else if (idMatiere == 2)
				{
					if (proba < 0.40f) idTailleChoisie = ID_PETITE_PIERRE;
					else if (proba < 0.70f) idTailleChoisie = ID_PIERRE_MOYENNE;
					else if (proba < 0.90f) idTailleChoisie = ID_GROSSE_PIERRE;
					else idTailleChoisie = ID_TRES_GROSSE_PIERRE;
				}

				if (idTailleChoisie != 0)
					liste.Add((pointDeSpawnSecurise, idTailleChoisie));
			}
		}
		return liste;
	}

	/// <summary>Enfile cailloux et silex sur le tapis roulant en ordre spatial logique (X, Z, Y) : terrain cohérent.</summary>
	private void EnfilerPierresSurTapisRoulant(List<(Vector3 pos, int id, int indexCache, int indexChimique)> pierres)
	{
		if (pierres.Count == 0) return;
		pierres.Sort((a, b) =>
		{
			int cmpX = a.pos.X.CompareTo(b.pos.X);
			if (cmpX != 0) return cmpX;
			int cmpZ = a.pos.Z.CompareTo(b.pos.Z);
			if (cmpZ != 0) return cmpZ;
			return a.pos.Y.CompareTo(b.pos.Y);
		});
		foreach (var p in pierres)
			_filePierresAInstancier.Enqueue((p.pos, p.id, p.indexCache, p.indexChimique));
	}

	/// <summary>Roches liées au chunk : à la génération le chunk lance le dé → taille → on prend une du pool de cette taille (sinon on en crée une). IndexCache -1 = proche (formes douces), -2 = loin (formes cassées).</summary>
	private void GenererItemPhysique(Vector3 position, int idObjet, int indexCache = -1, int indexChimique = -1)
	{
		if (_parentPourBlocsChutants == null) return;
		RigidBody3D rb = null;
		if (_poolsRochesParTaille.TryGetValue(idObjet, out var pool) && pool.Count > 0)
		{
			rb = pool[pool.Count - 1];
			pool.RemoveAt(pool.Count - 1);
			var item = rb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
			if (item != null)
			{
				item.ID_Objet = idObjet;
				item.IndexCacheMemoire = indexCache;
				item.IndexChimique = indexChimique;
				item.ReappliquerApparence();
			}
			rb.Freeze = true;
		}
		else
			rb = CreerNouvelleRoche(idObjet, indexCache, indexChimique);
		_parentPourBlocsChutants.AddChild(rb);
		rb.GlobalPosition = position;
	}

	/// <summary>Crée une roche neuve (mesh/collision selon id). N'est pas ajoutée au parent.</summary>
	private RigidBody3D CreerNouvelleRoche(int idObjet, int indexCache, int indexChimique)
	{
		float rayon = idObjet == ID_SILEX ? 0.12f
			: idObjet == ID_PETITE_PIERRE ? 0.15f
			: idObjet == ID_PIERRE_MOYENNE ? 0.25f
			: idObjet == ID_GROSSE_PIERRE ? 0.4f
			: 0.6f;
		float hauteur = idObjet == ID_SILEX ? 0.24f : rayon * 2f;

		var rb = new RigidBody3D();
		rb.Mass = 1.0f;
		rb.PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.6f, Bounce = 0.1f };
		rb.Freeze = true;

		Mesh meshBase = idObjet == ID_SILEX ? new PrismMesh { Size = new Vector3(0.2f, 0.15f, 0.25f) } : new SphereMesh { Radius = rayon, Height = hauteur };
		Shape3D shapeBase = idObjet == ID_SILEX ? new BoxShape3D { Size = new Vector3(0.2f, 0.15f, 0.25f) } : new SphereShape3D { Radius = rayon };
		rb.AddChild(new MeshInstance3D { Mesh = meshBase });
		rb.AddChild(new CollisionShape3D { Shape = shapeBase });
		var item = new ItemPhysique { ID_Objet = idObjet, IndexCacheMemoire = indexCache, IndexChimique = indexChimique, Name = "ItemPhysique" };
		rb.AddChild(item);
		return rb;
	}

	/// <summary>Rayon en unités : pierres gelées se réveillent quand joueur entre (2 chunks = terrain chargé).</summary>
	private float RayonActivationPierres => RAYON_ACTIVATION_PIERRES_CHUNKS * TailleChunk;

	/// <summary>Réveille les pierres dans le rayon, endort immédiatement celles hors rayon ou qui se sont mises en sommeil physique.</summary>
	private void ReveillerPierresDansRayon()
	{
		if (_parentPourBlocsChutants == null || _obtenirPositionJoueur == null) return;
		Vector3 posJoueur = _obtenirPositionJoueur();
		float rayonCarre = RayonActivationPierres * RayonActivationPierres;
		foreach (Node child in _parentPourBlocsChutants.GetChildren())
		{
			if (child is not RigidBody3D rb) continue;
			var item = rb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
			if (item == null) continue;
			int id = item.ID_Objet;
			if (id != ID_PETITE_PIERRE && id != ID_PIERRE_MOYENNE && id != ID_GROSSE_PIERRE && id != ID_TRES_GROSSE_PIERRE && id != ID_SILEX) continue;
			float distCarre = rb.GlobalPosition.DistanceSquaredTo(posJoueur);
			if (distCarre <= rayonCarre)
				rb.Freeze = false;
			else
				rb.Freeze = true; // Endormir immédiatement dès qu'il sort du rayon ou se simplifie (sommeil physique)
		}
	}

	/// <summary>Sauvegarde les pierres et silex (IDs 10-14) avec IndexCacheMemoire et IndexChimique.</summary>
	private void SauvegarderPierresChunk(Vector2I coord)
	{
		if (_parentPourBlocsChutants == null) return;
		float xMin = coord.X * TailleChunk;
		float xMax = (coord.X + 1) * TailleChunk;
		float zMin = coord.Y * TailleChunk;
		float zMax = (coord.Y + 1) * TailleChunk;
		var pierres = new List<(Vector3 pos, int id, int index, int chimique)>();
		foreach (Node child in _parentPourBlocsChutants.GetChildren())
		{
			var item = child.GetNodeOrNull<ItemPhysique>("ItemPhysique");
			if (item == null) continue;
			int id = item.ID_Objet;
			if (id < 10 || id > 14) continue;
			Vector3 pos = (child as Node3D)?.GlobalPosition ?? Vector3.Zero;
			if (pos.X >= xMin && pos.X < xMax && pos.Z >= zMin && pos.Z < zMax)
				pierres.Add((pos, id, Mathf.Max(0, item.IndexCacheMemoire), Mathf.Max(0, item.IndexChimique)));
		}
		if (pierres.Count == 0) return;
		string nom = GameState.Instance?.NomMondeActuel ?? "MonMonde";
		string dossier = ProjectSettings.GlobalizePath($"user://saves/{nom}/chunks/");
		Directory.CreateDirectory(dossier);
		string chemin = Path.Combine(dossier, $"chunk_{coord.X}_{coord.Y}_items.bin");
		try
		{
			using (var w = new BinaryWriter(File.Open(chemin, FileMode.Create)))
			{
				w.Write(0x5A4B324A); // Magic v3 = IndexCacheMemoire + IndexChimique
				w.Write(pierres.Count);
				foreach (var (pos, id, index, chimique) in pierres)
				{
					w.Write(pos.X); w.Write(pos.Y); w.Write(pos.Z);
					w.Write((byte)id);
					w.Write((byte)index);
					w.Write((byte)chimique);
				}
			}
		}
		catch (Exception ex) { GD.PrintErr($"ZERO-K : Erreur sauvegarde pierres chunk {coord} : {ex.Message}"); }
	}

	/// <summary>Charge et enfile les pierres sur le tapis roulant (ordre spatial logique X,Z,Y). v1/v2/v3.</summary>
	private bool ChargerEtSpawnerPierresChunk(Vector2I coord)
	{
		if (_parentPourBlocsChutants == null) return false;
		string nom = GameState.Instance?.NomMondeActuel ?? "MonMonde";
		string chemin = ProjectSettings.GlobalizePath($"user://saves/{nom}/chunks/chunk_{coord.X}_{coord.Y}_items.bin");
		if (!File.Exists(chemin)) return false;
		try
		{
			var pierres = new List<(Vector3 pos, int id, int indexCache, int indexChimique)>();
			using (var stream = File.Open(chemin, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read))
			using (var r = new BinaryReader(stream))
			{
				int magicOrCount = r.ReadInt32();
				bool formatV3 = (magicOrCount == 0x5A4B324A);
				bool formatV2 = (magicOrCount == 0x5A4B3249) || formatV3;
				int count = formatV2 || formatV3 ? r.ReadInt32() : magicOrCount;
				for (int i = 0; i < count; i++)
				{
					float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
					int id = r.ReadByte();
					int indexCache = formatV2 || formatV3 ? r.ReadByte() : -1;
					int indexChimique = formatV3 ? r.ReadByte() : -1;
					if (id >= 10 && id <= 14)
						pierres.Add((new Vector3(x, y, z), id, indexCache, indexChimique));
				}
			}
			EnfilerPierresSurTapisRoulant(pierres);
			return true;
		}
		catch (Exception ex) { GD.PrintErr($"ZERO-K : Erreur chargement pierres chunk {coord} : {ex.Message}"); return false; }
	}

	/// <summary>Retire du monde les pierres/silex dont la position est dans le chunk ; remet dans le pool de la taille si possible.</summary>
	private void RetirerPierresChunk(Vector2I coord)
	{
		if (_parentPourBlocsChutants == null) return;
		float xMin = coord.X * TailleChunk;
		float xMax = (coord.X + 1) * TailleChunk;
		float zMin = coord.Y * TailleChunk;
		float zMax = (coord.Y + 1) * TailleChunk;
		var aRetirer = new List<Node>();
		foreach (Node child in _parentPourBlocsChutants.GetChildren())
		{
			var item = child.GetNodeOrNull<ItemPhysique>("ItemPhysique");
			if (item == null) continue;
			if (item.ID_Objet < 10 || item.ID_Objet > 14) continue;
			Vector3 pos = (child as Node3D)?.GlobalPosition ?? Vector3.Zero;
			if (pos.X >= xMin && pos.X < xMax && pos.Z >= zMin && pos.Z < zMax)
				aRetirer.Add(child);
		}
		foreach (var n in aRetirer)
		{
			var item = n.GetNodeOrNull<ItemPhysique>("ItemPhysique");
			int id = item?.ID_Objet ?? 0;
			_parentPourBlocsChutants.RemoveChild(n);
			if (n is RigidBody3D rb && id >= 10 && id <= 14 && _poolsRochesParTaille.TryGetValue(id, out var pool) && pool.Count < TaillePoolParType)
			{
				rb.Freeze = true;
				pool.Add(rb);
			}
			else
				n.QueueFree();
		}
	}

	public void AppliquerDestructionGlobale(Vector3 pointImpact, float rayon, int peerDemandeur = -1)
	{
		_modificationEnCours = true;
		int cxMin = Mathf.FloorToInt((pointImpact.X - rayon) / (float)TailleChunk);
		int cxMax = Mathf.FloorToInt((pointImpact.X + rayon) / (float)TailleChunk);
		int czMin = Mathf.FloorToInt((pointImpact.Z - rayon) / (float)TailleChunk);
		int czMax = Mathf.FloorToInt((pointImpact.Z + rayon) / (float)TailleChunk);

		for (int cx = cxMin; cx <= cxMax; cx++)
			for (int cz = czMin; cz <= czMax; cz++)
			{
				Vector2I coord = new Vector2I(cx, cz);
				var chunk = ObtenirOuCreerChunk(coord);
				chunk.DetruireVoxel(pointImpact, rayon);
			}
	}

	public void AppliquerCreationGlobale(Vector3 pointImpact, Vector3 normale, float rayon, int idMatiere = 1)
	{
		_modificationEnCours = true;
		Vector3 pointCible = pointImpact + (normale * 0.1f); // Réduit pour éviter les blocs flottants
		int cx = Mathf.FloorToInt(pointCible.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pointCible.Z / (float)TailleChunk);
		Vector2I coord = new Vector2I(cx, cz);

		var chunk = ObtenirOuCreerChunk(coord);
		chunk.CreerMatiere(pointCible, rayon, (byte)Mathf.Clamp(idMatiere, 0, 255));
	}

	public DonneesChunk ObtenirDonneesChunkPourClient(Vector2I coord)
	{
		var chunk = ObtenirOuCreerChunk(coord);
		return chunk.ObtenirDonneesPourClient();
	}

	private (Chunk_Serveur chunk, Vector3I local)? ObtenirChunkEtLocal(Vector3I pos)
	{
		if (pos.Y < 0 || pos.Y > HauteurMax) return null;
		int cx = Mathf.FloorToInt(pos.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pos.Z / (float)TailleChunk);
		Vector2I coord = new Vector2I(cx, cz);
		if (!_chunks.TryGetValue(coord, out var ch)) return null;

		int lx = pos.X - cx * TailleChunk;
		int lz = pos.Z - cz * TailleChunk;
		if (lx < 0 || lx > TailleChunk || lz < 0 || lz > TailleChunk) return null;

		return (ch, new Vector3I(lx, pos.Y, lz));
	}

	private bool EstVoxelEau(Vector3I pos)
	{
		var r = ObtenirChunkEtLocal(pos);
		return r.HasValue && r.Value.chunk.EstVoxelEau(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
	}

	private bool EstVoxelAir(Vector3I pos)
	{
		var r = ObtenirChunkEtLocal(pos);
		return r.HasValue && r.Value.chunk.EstVoxelAir(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
	}

	private void DefinirVoxel(Vector3I pos, byte id)
	{
		var r = ObtenirChunkEtLocal(pos);
		if (!r.HasValue) return;
		if (id == 4) r.Value.chunk.DefinirVoxelEau(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
		else if (id == 0) r.Value.chunk.DefinirVoxelAir(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
		_onVoxelModifie?.Invoke(pos, id);
	}

	/// <summary>Réplique la modification sur le padding des chunks voisins (évite déchirures quand chunk envoyé plus tard).</summary>
	public void RepliquerPaddingVoisins(Vector3I posGlobal, byte id)
	{
		int cx = Mathf.FloorToInt(posGlobal.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(posGlobal.Z / (float)TailleChunk);
		int localX = posGlobal.X - cx * TailleChunk;
		int localZ = posGlobal.Z - cz * TailleChunk;

		if (localX == 0 && _chunks.TryGetValue(new Vector2I(cx - 1, cz), out var vx))
			vx.SetVoxelLocal(TailleChunk, posGlobal.Y, localZ, id);
		if (localZ == 0 && _chunks.TryGetValue(new Vector2I(cx, cz - 1), out var vz))
			vz.SetVoxelLocal(localX, posGlobal.Y, TailleChunk, id);
		if (localX == 0 && localZ == 0 && _chunks.TryGetValue(new Vector2I(cx - 1, cz - 1), out var vxz))
			vxz.SetVoxelLocal(TailleChunk, posGlobal.Y, TailleChunk, id);
	}

	private void DemanderMiseAJourMesh(Vector3I pos)
	{
		var r = ObtenirChunkEtLocal(pos);
		if (!r.HasValue) return;
		int cx = Mathf.FloorToInt(pos.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pos.Z / (float)TailleChunk);
		int sec = Mathf.Clamp(Mathf.FloorToInt(pos.Y / 16f), 0, 44);  // 45 sections (0-44) pour HauteurMax 720
		int lx = pos.X - cx * TailleChunk;
		int lz = pos.Z - cz * TailleChunk;
		_onChunkModifie?.Invoke(new Vector2I(cx, cz), new List<int> { sec });
		if (lx == 0) _onChunkModifie?.Invoke(new Vector2I(cx - 1, cz), new List<int> { sec });
		if (lx == TailleChunk - 1) _onChunkModifie?.Invoke(new Vector2I(cx + 1, cz), new List<int> { sec });
		if (lz == 0) _onChunkModifie?.Invoke(new Vector2I(cx, cz - 1), new List<int> { sec });
		if (lz == TailleChunk - 1) _onChunkModifie?.Invoke(new Vector2I(cx, cz + 1), new List<int> { sec });
	}

	public static int ObtenirHauteurTerrainMonde(int worldX, int worldZ, int seed)
	{
		return Generateur_Voxel.ObtenirHauteurTerrainMonde(worldX, worldZ, seed);
	}

	/// <summary>Oracle géologique : lecture directe de l'ADN (_materials) au lieu de deviner. Aligné avec le Shader.</summary>
	public int ObtenirMatiereExacte(Vector3 positionGlobale)
	{
		int gx = Mathf.FloorToInt(positionGlobale.X);
		int gy = Mathf.FloorToInt(positionGlobale.Y);
		int gz = Mathf.FloorToInt(positionGlobale.Z);
		var r = ObtenirChunkEtLocal(new Vector3I(gx, gy, gz));
		if (!r.HasValue) return 1;
		byte mat = r.Value.chunk.ObtenirMatiereAtLocal(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
		return mat > 0 ? mat : 1;
	}

	private float DistanceCarreeAuJoueur(Vector2I chunk, Vector3 posObservation)
	{
		int obsCx = Mathf.FloorToInt(posObservation.X / (float)TailleChunk);
		int obsCz = Mathf.FloorToInt(posObservation.Z / (float)TailleChunk);
		int dx = chunk.X - obsCx, dz = chunk.Y - obsCz;
		return dx * dx + dz * dz;
	}

	/// <summary>Extraction radiale : le chunk à distance minimale de l'épicentre. DistanceSquaredTo évite la racine carrée.</summary>
	private Vector2I ExtraireChunkLePlusProche(List<Vector2I> liste, Vector3 positionObservation)
	{
		if (liste.Count == 0) return Vector2I.Zero;
		Vector2 posObsV2 = new Vector2(positionObservation.X / (float)TailleChunk, positionObservation.Z / (float)TailleChunk);
		Vector2I chunkCible = liste[0];
		float distanceMin = float.MaxValue;
		int indexASupprimer = 0;
		for (int i = 0; i < liste.Count; i++)
		{
			Vector2 posChunk = new Vector2(liste[i].X, liste[i].Y);
			float dist = posObsV2.DistanceSquaredTo(posChunk);
			if (dist < distanceMin)
			{
				distanceMin = dist;
				chunkCible = liste[i];
				indexASupprimer = i;
			}
		}
		liste.RemoveAt(indexASupprimer);
		return chunkCible;
	}

	private void EvaluerDechargementChunks()
	{
		if (_obtenirPositionJoueur == null || _onOrdonnerDestructionChunk == null) return;
		Vector3 posJoueur = _obtenirPositionJoueur();
		int cjX = Mathf.FloorToInt(posJoueur.X / (float)TailleChunk);
		int cjZ = Mathf.FloorToInt(posJoueur.Z / (float)TailleChunk);

		var aDecharger = new List<Vector2I>();
		foreach (var kv in _chunks)
		{
			int dx = Mathf.Abs(kv.Key.X - cjX);
			int dz = Mathf.Abs(kv.Key.Y - cjZ);
			if (dx > RenderDistance || dz > RenderDistance)
				aDecharger.Add(kv.Key);
		}
		// Enfiler sur le tapis roulant : le déchargement sera fait progressivement par ProcesserDechargeProgressive
		_chunksEnAttenteDecharge = aDecharger;
	}

	/// <summary>Traite au plus MaxChunksDechargeParTick chunks : sauvegarde (voxels + pierres) puis décharge (retrait pierres, Remove chunk, notif client).</summary>
	private void ProcesserDechargeProgressive()
	{
		if (_chunksEnAttenteDecharge.Count == 0 || _onOrdonnerDestructionChunk == null) return;
		int traites = 0;
		while (traites < MaxChunksDechargeParTick && _chunksEnAttenteDecharge.Count > 0)
		{
			Vector2I coord = _chunksEnAttenteDecharge[0];
			_chunksEnAttenteDecharge.RemoveAt(0);
			if (_chunks.TryGetValue(coord, out var chunk))
			{
				chunk.SauvegarderChunkSurDisque();
				SauvegarderPierresChunk(coord);
				RetirerPierresChunk(coord);
				_chunks.Remove(coord);
				_onOrdonnerDestructionChunk(coord);
				traites++;
			}
		}
	}
}