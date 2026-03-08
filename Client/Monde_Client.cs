using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>Détient les Chunk_Client (MeshInstance3D, collision). Reçoit des données et les transforme en triangles. Pas de génération de bruit.</summary>
public partial class Monde_Client : Node3D
{
	[Export] public int TailleChunk = 16;
	[Export] public int HauteurMax = 512;  // Montagnes jusqu'à ~500
	[Export] public int RenderDistance = 200;
	[Export] public int MaxChunksParFrame = 4;

	private ConcurrentQueue<Action> _misesAJourMainThread = new ConcurrentQueue<Action>();
	public ConcurrentQueue<Action> _misesAJourUrgentes = new ConcurrentQueue<Action>();

	private List<Vector2I> _chunksACharger = new List<Vector2I>();
	private bool _radarEnCours;
	private ConcurrentDictionary<Vector2I, Chunk_Client> _chunks = new ConcurrentDictionary<Vector2I, Chunk_Client>();
	private HashSet<(int cx, int cz, int section)> _sectionsAReconstruire = new HashSet<(int, int, int)>();
	private PackedScene _sceneChunkClient;
	private CharacterBody3D _joueur;
	private Vector2I _ancienChunkJoueur = new Vector2I(-99999, -99999);
	private bool _modificationEnCours;

	private Action<Vector2I> _enregistrerDemandeChunk;
	private Action<Vector3, float> _demanderDestruction;
	private Action<Vector3, Vector3, float, int> _demanderCreation;
	private int _seedTerrain;

	// Références vers l'UI
	private Panel _slotGauche;
	private Panel _slotDroite;

	public override void _Ready()
	{
		// Assure-toi que les chemins correspondent à ton arborescence exacte
		_slotGauche = GetNode<Panel>("../HUD_Inventaire/Conteneur_Ancrage/Boite_Slots/Slot_Main_Gauche");
		_slotDroite = GetNode<Panel>("../HUD_Inventaire/Conteneur_Ancrage/Boite_Slots/Slot_Main_Droite");
	}

	public void Initialiser(CharacterBody3D joueur, int seed, Action<Vector2I> enregistrerDemandeChunk,
		Action<Vector3, float> demanderDestruction, Action<Vector3, Vector3, float, int> demanderCreation)
	{
		_joueur = joueur;
		_seedTerrain = seed;
		_enregistrerDemandeChunk = enregistrerDemandeChunk;
		_demanderDestruction = demanderDestruction;
		_demanderCreation = demanderCreation;

		_sceneChunkClient = GD.Load<PackedScene>("res://Client/Chunk_Client.tscn");
		if (_sceneChunkClient == null)
			_sceneChunkClient = new PackedScene();
	}

	public void EnqueueMiseAJourMainThread(Action action) => _misesAJourMainThread.Enqueue(action);
	public void EnqueueMiseAJourUrgente(Action action) => _misesAJourUrgentes.Enqueue(action);

	/// <summary>Réserve le chunk de spawn (et ses 8 voisins) en tête de file pour éviter chute libre au démarrage.</summary>
	public void ReserverChunkSpawnPrioritaire(Vector2I coordSpawn)
	{
		var prioritaire = new List<Vector2I> { coordSpawn };
		for (int dx = -1; dx <= 1; dx++)
			for (int dz = -1; dz <= 1; dz++)
				if (dx != 0 || dz != 0)
					prioritaire.Add(new Vector2I(coordSpawn.X + dx, coordSpawn.Y + dz));
		_chunksACharger.InsertRange(0, prioritaire);
		_ancienChunkJoueur = coordSpawn;
	}

	private const int MaxMeshesParFrameVisuelles = 4;
	private const int MaxMeshesParFrameModification = 16;
	private float _tempsDepuisNettoyage;
	private const float IntervalleNettoyageChunks = 1.5f;

	public override void _PhysicsProcess(double delta)
	{
		if (!IsInsideTree()) return; // GARROT SPATIAL : pas de manipulation de chunks si l'arbre s'effondre.
		bool hadModifications = _sectionsAReconstruire.Count > 0;
		_modificationEnCours = false;

		// 1. PRIORITÉ ABSOLUE : Reconstruire immédiatement les sections minées (synchrone, pas de ThreadPool)
		if (hadModifications)
		{
			foreach (var cible in _sectionsAReconstruire)
				ExecuterReconstructionPrioritaire(cible);
			_sectionsAReconstruire.Clear();
			// Gel de Production : l'univers s'arrête de naître pendant cette frame.
			return;
		}

		// 2. Tâches de fond : dépiler l'affichage des nouveaux Chunks
		int actionsVisuelles = 0;
		while (actionsVisuelles < MaxMeshesParFrameVisuelles && _misesAJourUrgentes.TryDequeue(out var urgente))
		{
			try { urgente.Invoke(); } catch (ObjectDisposedException) { /* Chunk déjà supprimé */ }
			actionsVisuelles++;
		}
		while (actionsVisuelles < MaxMeshesParFrameVisuelles && _misesAJourMainThread.TryDequeue(out var action))
		{
			try { action.Invoke(); } catch (ObjectDisposedException) { /* Chunk déjà supprimé */ }
			actionsVisuelles++;
		}

		// Position d'observation : Caméra Active (caméra libre) ou corps du joueur — le verrou se base sur la caméra !
		Camera3D cameraActive = GetViewport()?.GetCamera3D();
		Vector3 positionObservation = cameraActive != null ? cameraActive.GlobalPosition : (_joueur?.GlobalPosition ?? Vector3.Zero);
		Vector2I chunkObservationActuel = new Vector2I(
			Mathf.FloorToInt(positionObservation.X / (float)TailleChunk),
			Mathf.FloorToInt(positionObservation.Z / (float)TailleChunk)
		);

		if (chunkObservationActuel != _ancienChunkJoueur)
		{
			_ancienChunkJoueur = chunkObservationActuel;
			ActualiserVisibiliteEtTriChunks(positionObservation);
		}

		if (_modificationEnCours) return;

		// 3. Requêtes : extraction radiale + purge obsolètes
		PurgerChunksObsolètesDeLaFile(positionObservation);
		for (int n = 0; n < MaxChunksParFrame && _chunksACharger.Count > 0; n++)
		{
			Vector2I chunkCible = ExtraireChunkLePlusProche(_chunksACharger, positionObservation);
			float distCarree = DistanceCarreeAuJoueur(chunkCible, positionObservation);
			float rayonMaxCarre = (RenderDistance + 1) * (RenderDistance + 1);
			if (distCarree > rayonMaxCarre)
				continue;
			DemanderChunk(chunkCible);
		}

		_tempsDepuisNettoyage += (float)delta;
		if (_tempsDepuisNettoyage >= IntervalleNettoyageChunks)
		{
			_tempsDepuisNettoyage = 0f;
			NettoyerChunksObsoles(positionObservation);
		}
	}

	private void ExecuterReconstructionPrioritaire((int cx, int cz, int section) cible)
	{
		var coord = new Vector2I(cible.cx, cible.cz);
		if (!_chunks.TryGetValue(coord, out var chunk)) return;
		chunk.ReconstruireSectionSynchrone(cible.section);
	}

	private float DistanceCarreeAuJoueur(Vector2I chunk, Vector3 posObservation)
	{
		int obsCx = Mathf.FloorToInt(posObservation.X / (float)TailleChunk);
		int obsCz = Mathf.FloorToInt(posObservation.Z / (float)TailleChunk);
		int dx = chunk.X - obsCx, dz = chunk.Y - obsCz;
		return dx * dx + dz * dz;
	}

	private void PurgerChunksObsolètesDeLaFile(Vector3 positionObservation)
	{
		float rayonMaxCarre = (RenderDistance + 1) * (RenderDistance + 1);
		_chunksACharger.RemoveAll(c =>
		{
			float d2 = DistanceCarreeAuJoueur(c, positionObservation);
			return d2 > rayonMaxCarre;
		});
	}

	/// <summary>Sénescence : retire de la mémoire les chunks au-delà du rayon + hystérésis. Pas à chaque frame.</summary>
	private void NettoyerChunksObsoles(Vector3 positionObservation)
	{
		float seuilCarree = (RenderDistance + 2) * (RenderDistance + 2);
		var chunksATuer = new List<Vector2I>();
		foreach (var kv in _chunks)
		{
			if (DistanceCarreeAuJoueur(kv.Key, positionObservation) > seuilCarree)
				chunksATuer.Add(kv.Key);
		}
		foreach (Vector2I coord in chunksATuer)
		{
			if (_chunks.TryRemove(coord, out var chunk))
			{
				chunk.QueueFree();
				NettoyerRegistreReconstruction(coord);
			}
		}
	}

	/// <summary>Extraction radiale : le chunk à distance minimale de l'épicentre (caméra/joueur). DistanceSquaredTo évite la racine.</summary>
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

	private void DeclencherReconstructionSection((int cx, int cz, int section) cible)
	{
		var coord = new Vector2I(cible.cx, cible.cz);
		if (!_chunks.TryGetValue(coord, out var chunk)) return;
		chunk.DeclencherReconstructionSection(cible.section);
	}

	public void AppliquerDestructionGlobale(Vector3 pointImpact, float rayon)
	{
		_demanderDestruction?.Invoke(pointImpact, rayon);
	}

	public void AppliquerCreationGlobale(Vector3 pointImpact, Vector3 normale, float rayon, int idMatiere = 1)
	{
		_demanderCreation?.Invoke(pointImpact, normale, rayon, idMatiere);
	}

	/// <summary>Mise à jour flore seule — N'appelle JAMAIS ConstruireMeshSection (évite recréation terrain).</summary>
	public void RecevoirFloreModifie(Vector2I coordChunk, Dictionary<Vector3I, byte> inventaireFlore)
	{
		if (!_chunks.TryGetValue(coordChunk, out var chunk)) return;
		EnqueueMiseAJourMainThread(() => chunk.MettreAJourRenduFlore(inventaireFlore));
	}

	public void RecevoirChunkModifie(Vector2I coordChunk, List<int> sectionsAffectees)
	{
		_modificationEnCours = true;
		if (!_chunks.TryGetValue(coordChunk, out _)) return;
		foreach (int sec in sectionsAffectees)
			if (sec >= 0 && sec < 16) _sectionsAReconstruire.Add((coordChunk.X, coordChunk.Y, sec));
	}

	/// <summary>Micro-RPC : mise à jour voxel unique. Modifie le chunk principal ET la réplique sur le padding des voisins (évite déchirures aux frontières).</summary>
	public void AppliquerVoxel(Vector3I posGlobal, byte id)
	{
		_modificationEnCours = true;
		// Loi FloorToInt : obligatoire pour coordonnées négatives (évite damier indestructible)
		int cx = Mathf.FloorToInt(posGlobal.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(posGlobal.Z / (float)TailleChunk);
		int localX = posGlobal.X - cx * TailleChunk;
		int localZ = posGlobal.Z - cz * TailleChunk;
		int sec = Mathf.FloorToInt(posGlobal.Y / 16f);
		int localY = posGlobal.Y - sec * 16;

		Vector2I coord = new Vector2I(cx, cz);
		if (!_chunks.TryGetValue(coord, out var chunk)) return;
		chunk.AppliquerVoxelGlobal(posGlobal, id);

		// Réplication du padding : le voxel frontalier est l'index 16 du voisin
		if (localX == 0 && _chunks.TryGetValue(new Vector2I(cx - 1, cz), out var vx))
		{
			vx.SetVoxelLocal(TailleChunk, posGlobal.Y, localZ, id);
			_sectionsAReconstruire.Add((cx - 1, cz, sec));
		}
		if (localZ == 0 && _chunks.TryGetValue(new Vector2I(cx, cz - 1), out var vz))
		{
			vz.SetVoxelLocal(localX, posGlobal.Y, TailleChunk, id);
			_sectionsAReconstruire.Add((cx, cz - 1, sec));
		}
		if (localX == 0 && localZ == 0 && _chunks.TryGetValue(new Vector2I(cx - 1, cz - 1), out var vxz))
		{
			vxz.SetVoxelLocal(TailleChunk, posGlobal.Y, TailleChunk, id);
			_sectionsAReconstruire.Add((cx - 1, cz - 1, sec));
		}

		if (sec >= 0 && sec < 16) _sectionsAReconstruire.Add((cx, cz, sec));
		if (localY == 0 && posGlobal.Y > 0 && sec - 1 >= 0) _sectionsAReconstruire.Add((cx, cz, sec - 1));
		if (localY == 15 && sec + 1 < 16) _sectionsAReconstruire.Add((cx, cz, sec + 1));
		if (localX == TailleChunk - 1) _sectionsAReconstruire.Add((cx + 1, cz, sec));
		if (localZ == TailleChunk - 1) _sectionsAReconstruire.Add((cx, cz + 1, sec));
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false)]
	public void AppliquerVoxelRPC(int x, int y, int z, int id)
	{
		AppliquerVoxel(new Vector3I(x, y, z), (byte)id);
	}

	/// <summary>RPC Serveur → Client : ordre de destruction. Le Client n'a pas le droit de discuter.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false)]
	public void OrdonnerDestructionChunkRPC(int coordX, int coordZ)
	{
		var coord = new Vector2I(coordX, coordZ);
		if (_chunks.TryRemove(coord, out var chunkADetruire))
		{
			chunkADetruire.QueueFree();
			NettoyerRegistreReconstruction(coord);
		}
	}

	private void NettoyerRegistreReconstruction(Vector2I coordChunk)
	{
		_sectionsAReconstruire.RemoveWhere(c => c.cx == coordChunk.X && c.cz == coordChunk.Y);
	}

	[Export] public Material MaterielTerrain;

	/// <summary>RPC : le serveur envoie chunk en byte[] uniquement. Ne jamais lancer Marching Cubes ici — Task.Run immédiat.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false)]
	public void RecevoirChunkDuServeurRPC(int coordX, int coordZ, int tailleChunk, int hauteurMax, byte[] densitiesPlates, byte[] materialsFlat, byte[] densitiesEauPlates)
	{
		var donnees = new DonneesChunk
		{
			CoordChunk = new Vector2I(coordX, coordZ),
			TailleChunk = tailleChunk,
			HauteurMax = hauteurMax,
			DensitiesQuantifiees = densitiesPlates,
			DensitiesEauQuantifiees = densitiesEauPlates,
			MaterialsFlat = materialsFlat
		};
		RecevoirDonneesChunk(new Vector2I(coordX, coordZ), donnees);
	}

	public void RecevoirDonneesChunk(Vector2I coordChunk, DonneesChunk donnees)
	{
		if (_chunks.TryGetValue(coordChunk, out var chunk))
		{
			chunk.RecevoirDonneesChunk(donnees, EnqueueMiseAJourMainThread);
			return;
		}

		var ch = _sceneChunkClient?.Instantiate<Chunk_Client>();
		if (ch == null)
			ch = new Chunk_Client();
		ch.ChunkOffsetX = coordChunk.X;
		ch.ChunkOffsetZ = coordChunk.Y;
		ch.TailleChunk = TailleChunk;
		ch.HauteurMax = HauteurMax;
		ch.MaterielTerre = MaterielTerrain ?? GD.Load<Material>("res://Manteau_Planetaire.tres");
		ch.ConfigurerBruitClimat(_seedTerrain);
		var position = new Vector3(coordChunk.X * TailleChunk, 0, coordChunk.Y * TailleChunk);
		_chunks[coordChunk] = ch;
		// RÈGLE GODOT 4 : positionnement et ajout à l'arbre via CallDeferred (thread principal, frame suivante).
		Callable.From(() => AttacherEtPositionnerChunk(ch, position)).CallDeferred();
		ch.RecevoirDonneesChunk(donnees, EnqueueMiseAJourMainThread);
	}

	private void AttacherEtPositionnerChunk(Chunk_Client chunkVisuel, Vector3 position)
	{
		if (!IsInsideTree()) return; // Si le jeu ferme, on annule.
		AddChild(chunkVisuel);
		chunkVisuel.Position = position;
	}

	/// <summary>Position d'observation (caméra ou joueur). Utilisée par le radar et par les chunks pour la visibilité du gazon.</summary>
	public Vector3 ObtenirPositionObservation()
	{
		Camera3D cam = GetViewport()?.GetCamera3D();
		return cam != null ? cam.GlobalPosition : (_joueur?.GlobalPosition ?? Vector3.Zero);
	}

	/// <summary>Position utilisée par le radar (chunk le plus proche). Utilise la caméra active si disponible (caméra libre), sinon le corps du joueur.</summary>
	private Vector2I ObtenirCoordonneesChunkJoueur()
	{
		Vector3 pos = ObtenirPositionObservation();
		return new Vector2I(Mathf.FloorToInt(pos.X / (float)TailleChunk), Mathf.FloorToInt(pos.Z / (float)TailleChunk));
	}

	private void ActualiserVisibiliteEtTriChunks(Vector3 positionObservation)
	{
		if (_radarEnCours) return;

		_radarEnCours = true;
		Vector2 posObsV2 = new Vector2(positionObservation.X / (float)TailleChunk, positionObservation.Z / (float)TailleChunk);
		int cjX = Mathf.FloorToInt(positionObservation.X / (float)TailleChunk);
		int cjZ = Mathf.FloorToInt(positionObservation.Z / (float)TailleChunk);
		HashSet<Vector2I> chunksCharges = new HashSet<Vector2I>(_chunks.Keys);
		List<Vector2I> copieChunksACharger = new List<Vector2I>(_chunksACharger);

		Task.Run(() =>
		{
			HashSet<Vector2I> dejaVu = new HashSet<Vector2I>(copieChunksACharger);
			foreach (var c in chunksCharges) dejaVu.Add(c);

			for (int dx = -RenderDistance; dx <= RenderDistance; dx++)
				for (int dz = -RenderDistance; dz <= RenderDistance; dz++)
				{
					Vector2I coord = new Vector2I(cjX + dx, cjZ + dz);
					if (dejaVu.Add(coord))
						copieChunksACharger.Add(coord);
				}

			// Tri radial strict : distance au carré depuis l'épicentre (évite racine carrée)
			Vector2 posObs = posObsV2;
			copieChunksACharger.Sort((a, b) =>
			{
				float da = new Vector2(a.X, a.Y).DistanceSquaredTo(posObs);
				float db = new Vector2(b.X, b.Y).DistanceSquaredTo(posObs);
				return da.CompareTo(db);
			});

			Callable.From(() => AppliquerNouveauTriRadar(copieChunksACharger.ToArray())).CallDeferred();
		});
	}

	private void AppliquerNouveauTriRadar(Vector2I[] nouvelleListeTriee)
	{
		_chunksACharger = new List<Vector2I>(nouvelleListeTriee);
		_radarEnCours = false;
		// Le dépilage est fait dans _PhysicsProcess (usine en continu, 60 TPS)
	}

	private void DemanderChunk(Vector2I coord)
	{
		_enregistrerDemandeChunk?.Invoke(coord);
	}

	/// <summary>Interroge la densité à une position globale (chunk en RAM uniquement). Plus utilisé pour Marching Cubes (rembourrage 17³).</summary>
	public (float valeur, bool trouve) ObtenirDensiteGlobaleEx(Vector3I posGlobale)
	{
		int cx = Mathf.FloorToInt(posGlobale.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(posGlobale.Z / (float)TailleChunk);
		if (!_chunks.TryGetValue(new Vector2I(cx, cz), out var chunk)) return (-10f, false);
		int lx = posGlobale.X - cx * TailleChunk;
		int lz = posGlobale.Z - cz * TailleChunk;
		return (chunk.ObtenirDensiteLocale(lx, posGlobale.Y, lz), true);
	}

	/// <summary>Vrai si le chunk sous les pieds du joueur a construit sa collision (évite chute libre au spawn).</summary>
	public bool ChunkSousPiedsAPret()
	{
		if (_joueur == null) return false;
		Vector3 pos = _joueur.GlobalPosition;
		int cx = Mathf.FloorToInt(pos.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pos.Z / (float)TailleChunk);
		if (!_chunks.TryGetValue(new Vector2I(cx, cz), out var chunk)) return false;
		int sec = Mathf.Clamp(Mathf.FloorToInt(pos.Y / 16f), 0, 15);
		return chunk.SectionAPret(sec);
	}
}
