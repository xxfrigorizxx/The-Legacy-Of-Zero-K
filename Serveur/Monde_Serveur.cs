using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>Détient les chunks serveur (données voxel), la génération, la simulation d'eau. Aucun MeshInstance3D.</summary>
public partial class Monde_Serveur : Node
{
	[Export] public int TailleChunk = 16;
	[Export] public int HauteurMax = 256;
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
	private float _tempsDepuisVerifDecharge;
	private const float IntervalleEvaluationTectonique = 0.5f;

	public void Initialiser(Node parentPourBlocsChutants, Action<Vector2I, List<int>> onChunkModifie, Action<Vector2I, DonneesChunk> onEnvoyerChunk = null, Action<Vector2I, Dictionary<Vector3I, byte>> onFloreModifie = null, Action<Vector3I, byte> onVoxelModifie = null, Action<Vector2I> onOrdonnerDestructionChunk = null, Func<Vector3> obtenirPositionJoueur = null)
	{
		_parentPourBlocsChutants = parentPourBlocsChutants;
		_onChunkModifie = onChunkModifie;
		_onEnvoyerChunk = onEnvoyerChunk;
		_onFloreModifie = onFloreModifie;
		_onVoxelModifie = onVoxelModifie;
		_onOrdonnerDestructionChunk = onOrdonnerDestructionChunk;
		_obtenirPositionJoueur = obtenirPositionJoueur;
		DirAccess.MakeDirRecursiveAbsolute("user://chunks");
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
				SauvegarderChunkSurDisque(coord, chunk);
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

				// BRANCHE COMMUNE : Chunk ressuscité — envoi direct, AUCUNE passe de décoration.
				_chunks[chunkCible] = chunkActuel;
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

		_tempsEcoulement += (float)delta;
		if (_tempsEcoulement < TICK_EAU) return;
		_tempsEcoulement = 0;

		_tempsDepuisVerifDecharge += (float)delta;
		if (_tempsDepuisVerifDecharge >= IntervalleEvaluationTectonique)
		{
			_tempsDepuisVerifDecharge = 0f;
			EvaluerDechargementChunks();
		}

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
		return FileAccess.FileExists(DonneesChunk.ObtenirCheminChunk(coord));
	}

	private static string ObtenirCheminSauvegarde(Vector2I coord) => DonneesChunk.ObtenirCheminChunk(coord);

	/// <summary>Sauvegarde sécurisée (StoreBuffer byte[]). NE sauvegarde QUE si EstModifie.</summary>
	private void SauvegarderChunkSurDisque(Vector2I coord, Chunk_Serveur chunk)
	{
		if (!chunk.EstModifie) return;
		string chemin = ObtenirCheminSauvegarde(coord);
		using var file = FileAccess.Open(chemin, FileAccess.ModeFlags.Write);
		if (file == null)
		{
			GD.PrintErr($"ZERO-K ALERTE : Impossible d'écrire le chunk {coord}");
			return;
		}
		byte[] donneesVoxels = chunk.ObtenirTableauBytes();
		file.Store32((uint)donneesVoxels.Length);
		if (!file.StoreBuffer(donneesVoxels))
		{
			GD.PrintErr($"ZERO-K ALERTE : StoreBuffer a échoué pour le chunk {coord}");
			return;
		}
		file.Flush();
	}

	/// <summary>Chargement via GetBuffer (byte[]). LE SÉRUM DE VÉRITÉ : diagnostics chirurgicaux.</summary>
	private Chunk_Serveur ChargerChunkDepuisDisque(Vector2I coord)
	{
		GD.Print($"ZERO-K DIAG : Tentative chargement Chunk {coord}...");
		string chemin = ObtenirCheminSauvegarde(coord);
		using var file = FileAccess.Open(chemin, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			GD.PrintErr($"ZERO-K REJET : Chunk {coord} — fichier illisible ou inexistant.");
			return null;
		}
		uint tailleLu = file.Get32();
		int voxelCount = (TailleChunk + 1) * (HauteurMax + 1) * (TailleChunk + 1);
		int tailleAttendue = voxelCount * 9;

		if (tailleLu != tailleAttendue)
		{
			GD.PrintErr($"ZERO-K REJET : Chunk {coord} corrompu (taille header {tailleLu} ≠ {tailleAttendue}). Régénération forcée.");
			return null;
		}
		byte[] donneesVoxels = file.GetBuffer((long)tailleLu);
		// LE MOUCHARD ABSOLU :
		if (donneesVoxels == null || donneesVoxels.Length != tailleAttendue)
		{
			GD.PrintErr($"ZERO-K REJET : Chunk {coord} refusé ! Taille lue : {donneesVoxels?.Length ?? 0} | Attendue : {tailleAttendue}. Régénération forcée.");
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

	public void AppliquerCreationGlobale(Vector3 pointImpact, Vector3 normale, float rayon, int peerDemandeur = -1)
	{
		_modificationEnCours = true;
		Vector3 pointCible = pointImpact + (normale * 1.5f);
		int cx = Mathf.FloorToInt(pointCible.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pointCible.Z / (float)TailleChunk);
		Vector2I coord = new Vector2I(cx, cz);

		var chunk = ObtenirOuCreerChunk(coord);
		chunk.CreerMatiere(pointCible, rayon);
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
		int sec = Mathf.Clamp(Mathf.FloorToInt(pos.Y / 16f), 0, 15);
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

		foreach (Vector2I coord in aDecharger)
		{
			if (_chunks.TryGetValue(coord, out var chunk))
			{
				if (chunk.EstModifie) SauvegarderChunkSurDisque(coord, chunk); // Drapeau : sauvegarde uniquement si modifié.
				_chunks.Remove(coord);
				_onOrdonnerDestructionChunk(coord);
			}
		}
	}
}
