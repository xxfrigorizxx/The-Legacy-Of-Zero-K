using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Détient les Chunk_Client (MeshInstance3D, collision). Reçoit des données et les transforme en triangles. Pas de génération de bruit.</summary>
public partial class Monde_Client : Node3D
{
	[Export] public int TailleChunk = 16;
	[Export] public int HauteurMax = 720;  // Montagnes jusqu'à 700
	[Export] public int RenderDistance = 200;
	[Export] public int MaxChunksParFrame = 4;
	/// <summary>En chunks : au-delà de ce rayon, seule la physique est mise en dormance (BodySetSpace vide). Le visuel reste affiché tant que le chunk est dans RenderDistance.</summary>
	[Export] public int RayonDormancePhysique = 2;

	private ConcurrentQueue<Action> _misesAJourMainThread = new ConcurrentQueue<Action>();
	public ConcurrentQueue<Action> _misesAJourUrgentes = new ConcurrentQueue<Action>();

	/// <summary>File d'attente d'intégration : les chunks (sections + flore) terminés en arrière-plan y sont déposés. Un seul élément est traité par frame pour éviter les micro-freezes.</summary>
	private ConcurrentQueue<Action> _fileIntegrationMainThread = new ConcurrentQueue<Action>();

	/// <summary>Forge restreinte : file des chunks en attente de calcul (maths). Au plus MaxTravailleurs Task.Run actifs.</summary>
	private readonly object _lockFileAttenteMaths = new object();
	private List<(ChunkData data, DonneesChunk donnees)> _fileAttenteMathsData = new List<(ChunkData, DonneesChunk)>();
	private int _chunksEnCoursDeCalcul = 0;
	private const int MaxTravailleurs = 4;

	/// <summary>Chunks au format Data-Oriented (RID). Plus de Node pour le terrain.</summary>
	private Dictionary<Vector2I, ChunkData> _chunksData = new Dictionary<Vector2I, ChunkData>();
	/// <summary>File d'attente de solidification physique : un chunk par frame pour éviter les pics PhysicsServer3D (dilution physique).</summary>
	private List<ChunkData> _fileAttenteSolidification = new List<ChunkData>();

	private List<Vector2I> _chunksACharger = new List<Vector2I>();
	private bool _radarEnCours;
	private HashSet<(int cx, int cz, int section)> _sectionsAReconstruire = new HashSet<(int, int, int)>();
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
	}

	public void EnqueueMiseAJourMainThread(Action action) => _misesAJourMainThread.Enqueue(action);
	public void EnqueueMiseAJourUrgente(Action action) => _misesAJourUrgentes.Enqueue(action);

	/// <summary>Dépose un travail d'intégration (mesh, collision, flore) à exécuter sur le Main Thread. Au plus un par frame (goulot d'étranglement).</summary>
	public void EnqueueIntegration(Action action) => _fileIntegrationMainThread.Enqueue(action);

	/// <summary>Enfile un chunk pour calcul en arrière-plan (Forge restreinte). Ne lance pas de Task.Run : le lancement est limité à MaxTravailleurs dans _PhysicsProcess.</summary>
	public void EnqueueChunkGeneration(ChunkData data, DonneesChunk donnees)
	{
		if (data == null || donnees == null) return;
		lock (_lockFileAttenteMaths)
			_fileAttenteMathsData.Add((data, donnees));
	}

	/// <summary>Architecture AAA : fusionne les 45 SectionPayload en un mesh + shape, crée les RIDs RenderingServer/PhysicsServer3D, attache au monde. À appeler sur le Main Thread.</summary>
	internal void IntegrerChunkDataRIDs(ChunkData data, List<SectionPayload> payloads)
	{
		if (data == null || payloads == null || payloads.Count == 0 || !IsInsideTree()) return;
		World3D world = GetWorld3D();
		if (world == null) return;

		// 1. Fusion des payloads en un seul ArrayMesh (terrain)
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		foreach (var p in payloads)
		{
			if (p?.SommetsVisuels == null || p.SommetsVisuels.Length == 0) continue;
			for (int i = 0; i < p.SommetsVisuels.Length; i++)
			{
				st.SetNormal(p.NormalsVisuels != null && i < p.NormalsVisuels.Length ? p.NormalsVisuels[i] : Vector3.Up);
				st.SetColor(p.CouleursVisuels != null && i < p.CouleursVisuels.Length ? p.CouleursVisuels[i] : Colors.White);
				st.AddVertex(p.SommetsVisuels[i]);
			}
		}
		st.GenerateNormals();
		ArrayMesh mergedMesh = st.Commit();
		if (mergedMesh.GetSurfaceCount() == 0) return;

		Material matTerrain = MaterielTerrain ?? GD.Load<Material>("res://Manteau_Planetaire.tres");
		if (matTerrain != null)
			mergedMesh.SurfaceSetMaterial(0, matTerrain);

		// RÈGLE CAS B (espace local) : les sommets du mesh sont en [0, TailleChunk] x [0, HauteurMax] x [0, TailleChunk].
		// Une SEULE application du décalage chunk : position monde = origine parent + (coordChunk * TailleChunk).
		// Pas de double translation (ne pas ajouter d'offset si les vertices étaient déjà en monde).
		Vector3 positionVraie = GlobalPosition + new Vector3(data.Coordonnees.X * TailleChunk, 0, data.Coordonnees.Y * TailleChunk);
		Transform3D transformChunk = new Transform3D(Basis.Identity, positionVraie);

		// 2. RenderingServer : instance visuelle sans Node
		Rid meshRid = mergedMesh.GetRid();
		Rid instanceRid = RenderingServer.Singleton.InstanceCreate();
		RenderingServer.Singleton.InstanceSetBase(instanceRid, meshRid);
		RenderingServer.Singleton.InstanceSetScenario(instanceRid, world.Scenario);
		RenderingServer.Singleton.InstanceSetTransform(instanceRid, transformChunk);

		// 3. PhysicsServer3D : corps statique + forme (trimesh depuis le mesh fusionné)
		Vector3[] faces = mergedMesh.GetFaces();
		Shape3D shape = null;
		if (faces != null && faces.Length > 0)
		{
			try { shape = mergedMesh.CreateTrimeshShape(); }
			catch (Exception) { shape = null; }
		}
		if (shape == null) { RenderingServer.Singleton.FreeRid(instanceRid); return; }
		Rid shapeRid = shape.GetRid();
		Rid bodyRid = PhysicsServer3D.Singleton.BodyCreate();
		PhysicsServer3D.Singleton.BodySetMode(bodyRid, PhysicsServer3D.BodyMode.Static);
		// Ne pas insérer dans l'espace ici : dilution physique (un chunk par frame dans _PhysicsProcess).
		PhysicsServer3D.Singleton.BodySetSpace(bodyRid, default(Rid));
		PhysicsServer3D.Singleton.BodyAddShape(bodyRid, shapeRid);
		PhysicsServer3D.Singleton.BodySetState(bodyRid, PhysicsServer3D.BodyState.Transform, transformChunk);
		PhysicsServer3D.Singleton.BodySetCollisionLayer(bodyRid, 1);
		PhysicsServer3D.Singleton.BodySetCollisionMask(bodyRid, 1);

		data.VisualInstanceRID = instanceRid;
		data.PhysicsBodyRID = bodyRid;
		data.PhysicsShapeRID = shapeRid;
		data._meshRef = mergedMesh;
		data._shapeRef = shape;
		// Mise en file de solidification : BodySetSpace sera appelé dans _PhysicsProcess (1 chunk/frame).
		if (!data.EstEnFileSolidification)
		{
			_fileAttenteSolidification.Add(data);
			data.EstEnFileSolidification = true;
		}
		// Chunk sous les pieds du joueur : solidifier tout de suite pour ne jamais traverser la map.
		if (_joueur != null)
		{
			Vector2I cJoueur = Gestionnaire_Monde.WorldToChunkCoord(_joueur.GlobalPosition, TailleChunk);
			if (data.Coordonnees == cJoueur && data.PhysicsBodyRID.IsValid)
			{
				_fileAttenteSolidification.Remove(data);
				data.EstEnFileSolidification = false;
				PhysicsServer3D.Singleton.BodySetSpace(data.PhysicsBodyRID, world.Space);
			}
			else
			{
				// Solidifier les chunks à distance 2 (couronne autour du joueur) pour ne pas passer à travers au loin
				int dx = Mathf.Abs(data.Coordonnees.X - cJoueur.X);
				int dz = Mathf.Abs(data.Coordonnees.Y - cJoueur.Y);
				if (dx <= 2 && dz <= 2 && data.PhysicsBodyRID.IsValid)
				{
					_fileAttenteSolidification.Remove(data);
					data.EstEnFileSolidification = false;
					PhysicsServer3D.Singleton.BodySetSpace(data.PhysicsBodyRID, world.Space);
				}
			}
		}

		// 4. Eau : fusion des SommetsEau/NormalsEau de toutes les sections, même transform que le terrain
		var stEau = new SurfaceTool();
		stEau.Begin(Mesh.PrimitiveType.Triangles);
		foreach (var p in payloads)
		{
			if (p?.SommetsEau == null || p.SommetsEau.Length == 0) continue;
			for (int i = 0; i < p.SommetsEau.Length; i++)
			{
				stEau.SetNormal(p.NormalsEau != null && i < p.NormalsEau.Length ? p.NormalsEau[i] : Vector3.Up);
				stEau.AddVertex(p.SommetsEau[i]);
			}
		}
		stEau.GenerateNormals();
		ArrayMesh meshEau = stEau.Commit();
		if (meshEau.GetFaces().Length > 0)
		{
			Rid waterRid = RenderingServer.Singleton.InstanceCreate();
			RenderingServer.Singleton.InstanceSetBase(waterRid, meshEau.GetRid());
			RenderingServer.Singleton.InstanceSetScenario(waterRid, world.Scenario);
			RenderingServer.Singleton.InstanceSetTransform(waterRid, transformChunk);
			var gestionnaire = GetParent() as Gestionnaire_Monde;
			if (gestionnaire != null && gestionnaire.MaterielEau != null)
				RenderingServer.Singleton.InstanceGeometrySetMaterialOverride(waterRid, gestionnaire.MaterielEau.GetRid());
			else
				GD.PrintErr("CRITIQUE: MaterielEau non assigné (Gestionnaire_Monde._Ready n'a pas créé le matériau ou parent absent).");
			data.WaterInstanceRID = waterRid;
			data._meshEauRef = meshEau;
		}
	}

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

		// GOULOT DE SOLIDIFICATION PHYSIQUE : priorité au chunk sous les pieds du joueur pour éviter de traverser la map.
		if (_fileAttenteSolidification.Count > 0)
		{
			Vector2I coordObsSolidif = ObtenirCoordonneesChunkJoueur();
			_fileAttenteSolidification.Sort((a, b) =>
			{
				int da = (a.Coordonnees.X - coordObsSolidif.X) * (a.Coordonnees.X - coordObsSolidif.X) + (a.Coordonnees.Y - coordObsSolidif.Y) * (a.Coordonnees.Y - coordObsSolidif.Y);
				int db = (b.Coordonnees.X - coordObsSolidif.X) * (b.Coordonnees.X - coordObsSolidif.X) + (b.Coordonnees.Y - coordObsSolidif.Y) * (b.Coordonnees.Y - coordObsSolidif.Y);
				return da.CompareTo(db);
			});
			// Solidifier plusieurs chunks autour du joueur pour éviter de passer à travers (surtout en s'éloignant du spawn).
			const int MaxSolidificationsProche = 6;
			int solidifies = 0;
			World3D w = GetWorld3D();
			while (_fileAttenteSolidification.Count > 0 && solidifies < MaxSolidificationsProche)
			{
				ChunkData chunkASolidifier = _fileAttenteSolidification[0];
				_fileAttenteSolidification.RemoveAt(0);
				chunkASolidifier.EstEnFileSolidification = false;
				int dx = Mathf.Abs(chunkASolidifier.Coordonnees.X - coordObsSolidif.X);
				int dz = Mathf.Abs(chunkASolidifier.Coordonnees.Y - coordObsSolidif.Y);
				if (dx <= RayonDormancePhysique && dz <= RayonDormancePhysique && chunkASolidifier.PhysicsBodyRID.IsValid && w != null)
					PhysicsServer3D.Singleton.BodySetSpace(chunkASolidifier.PhysicsBodyRID, w.Space);
				solidifies++;
				// Ne pas limiter à "distance 1" : vider la file jusqu'à 6 chunks pour que le sol soit prêt dès qu'on s'éloigne du spawn
			}
		}

		// GOULOT D'ÉTRANGLEMENT : au plus UNE lourde opération d'intégration par frame (mesh, collision, flore). Garantit 60 FPS même si 10 chunks sont prêts.
		if (_fileIntegrationMainThread.TryDequeue(out var integration))
		{
			try { integration.Invoke(); }
			catch (ObjectDisposedException) { /* Chunk déjà supprimé */ }
			catch (System.Exception ex) { GD.PrintErr("Monde_Client intégration: ", ex.Message); }
			return;
		}

		// FORGE RESTREINTE : lancer au plus MaxTravailleurs calculs en arrière-plan (tri par distance au joueur).
		Vector2I obsChunk = ObtenirCoordonneesChunkJoueur();
		while (Thread.VolatileRead(ref _chunksEnCoursDeCalcul) < MaxTravailleurs)
		{
			ChunkData chunkData = null;
			DonneesChunk donnees = null;
			lock (_lockFileAttenteMaths)
			{
				if (_fileAttenteMathsData.Count == 0) break;
				_fileAttenteMathsData.Sort((a, b) =>
				{
					float da = (a.data.Coordonnees.X - obsChunk.X) * (a.data.Coordonnees.X - obsChunk.X) + (a.data.Coordonnees.Y - obsChunk.Y) * (a.data.Coordonnees.Y - obsChunk.Y);
					float db = (b.data.Coordonnees.X - obsChunk.X) * (b.data.Coordonnees.X - obsChunk.X) + (b.data.Coordonnees.Y - obsChunk.Y) * (b.data.Coordonnees.Y - obsChunk.Y);
					return da.CompareTo(db);
				});
				var job = _fileAttenteMathsData[0];
				_fileAttenteMathsData.RemoveAt(0);
				chunkData = job.data;
				donnees = job.donnees;
			}
			if (chunkData == null || donnees == null) break;
			Interlocked.Increment(ref _chunksEnCoursDeCalcul);
			var mondeRef = this;
			var enqueueIntegration = EnqueueIntegration;
			Task.Run(() =>
			{
				try
				{
					var payloads = Chunk_Client.RemplirEtConstruirePayloads(chunkData, donnees);
					if (payloads != null)
						enqueueIntegration(() => mondeRef.IntegrerChunkDataRIDs(chunkData, payloads));
				}
				finally
				{
					Interlocked.Decrement(ref _chunksEnCoursDeCalcul);
				}
			});
		}

		bool hadModifications = _sectionsAReconstruire.Count > 0;
		_modificationEnCours = false;

		// 1. PRIORITÉ ABSOLUE : Reconstruire les chunks modifiés (minage/pose) pour que le terrain se mette à jour
		if (hadModifications)
		{
			var chunksUniques = new HashSet<Vector2I>();
			foreach (var cible in _sectionsAReconstruire)
				chunksUniques.Add(new Vector2I(cible.cx, cible.cz));
			_sectionsAReconstruire.Clear();
			foreach (Vector2I coord in chunksUniques)
				ExecuterReconstructionPrioritaire(coord);
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
		Vector2I chunkObservationActuel = Gestionnaire_Monde.WorldToChunkCoord(positionObservation, TailleChunk);

		if (chunkObservationActuel != _ancienChunkJoueur)
		{
			_ancienChunkJoueur = chunkObservationActuel;
			ActualiserVisibiliteEtTriChunks(positionObservation);
			ActualiserDormanceChunks(chunkObservationActuel.X, chunkObservationActuel.Y);
		}

		// Priorité : si le chunk sous le joueur n'est pas encore chargé, le mettre en tête pour qu'il s'affiche (évite "noir" au-delà du spawn)
		Vector2I chunkPieds = Gestionnaire_Monde.WorldToChunkCoord(positionObservation, TailleChunk);
		if (!_chunksData.ContainsKey(chunkPieds))
		{
			var prioritaire = new List<Vector2I> { chunkPieds };
			for (int dx = -1; dx <= 1; dx++)
				for (int dz = -1; dz <= 1; dz++)
					if ((dx != 0 || dz != 0))
					{
						var v = new Vector2I(chunkPieds.X + dx, chunkPieds.Y + dz);
						if (!_chunksData.ContainsKey(v)) prioritaire.Add(v);
					}
			_chunksACharger.RemoveAll(c => prioritaire.Contains(c));
			_chunksACharger.InsertRange(0, prioritaire);
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

	private void ExecuterReconstructionPrioritaire(Vector2I coord)
	{
		if (!_chunksData.TryGetValue(coord, out var data)) return;
		if (data.DensitiesFlat == null || data.MaterialsFlat == null) return;
		// Libérer l'ancien mesh et la collision avant de recréer (sinon fuite RID)
		data.LibérerRids();
		var payloads = Chunk_Client.ReconstruirePayloadsDepuisData(data);
		if (payloads != null && payloads.Count > 0)
			IntegrerChunkDataRIDs(data, payloads);
	}

	private float DistanceCarreeAuJoueur(Vector2I chunk, Vector3 posObservation)
	{
		Vector2I obs = Gestionnaire_Monde.WorldToChunkCoord(posObservation, TailleChunk);
		int dx = chunk.X - obs.X, dz = chunk.Y - obs.Y;
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

	/// <summary>Sénescence : retire de la mémoire les chunks au-delà du rayon + hystérésis. Libère les RIDs (RenderingServer/PhysicsServer3D).</summary>
	private void NettoyerChunksObsoles(Vector3 positionObservation)
	{
		float seuilCarree = (RenderDistance + 2) * (RenderDistance + 2);
		var chunksATuer = new List<Vector2I>();
		foreach (var kv in _chunksData)
		{
			if (DistanceCarreeAuJoueur(kv.Key, positionObservation) > seuilCarree)
				chunksATuer.Add(kv.Key);
		}
		foreach (Vector2I coord in chunksATuer)
		{
			if (_chunksData.TryGetValue(coord, out var data))
			{
				_chunksData.Remove(coord);
				data.LibérerRids();
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
		if (!_chunksData.TryGetValue(coord, out _)) return;
		// AAA : pas de reconstruction par section ; on pourrait re-demander le chunk.
	}

	public void AppliquerDestructionGlobale(Vector3 pointImpact, float rayon)
	{
		_demanderDestruction?.Invoke(pointImpact, rayon);
	}

	public void AppliquerCreationGlobale(Vector3 pointImpact, Vector3 normale, float rayon, int idMatiere = 1)
	{
		_demanderCreation?.Invoke(pointImpact, normale, rayon, idMatiere);
	}

	/// <summary>Mise à jour flore seule — AAA : ChunkData n'a pas de flore (MultiMesh). No-op.</summary>
	public void RecevoirFloreModifie(Vector2I coordChunk, Dictionary<Vector3I, byte> inventaireFlore)
	{
		// ChunkData ne gère pas la flore (pas de Node MultiMesh).
	}

	public void RecevoirChunkModifie(Vector2I coordChunk, List<int> sectionsAffectees)
	{
		_modificationEnCours = true;
		if (!_chunksData.TryGetValue(coordChunk, out _)) return;
		foreach (int sec in sectionsAffectees)
			if (sec >= 0 && sec < 45) _sectionsAReconstruire.Add((coordChunk.X, coordChunk.Y, sec));
	}

	/// <summary>Micro-RPC : mise à jour voxel unique. Modifie le chunk principal ET la réplique sur le padding des voisins.</summary>
	public void AppliquerVoxel(Vector3I posGlobal, byte id)
	{
		_modificationEnCours = true;
		Gestionnaire_Monde.WorldToChunkAndLocal(posGlobal.X, posGlobal.Z, TailleChunk, out Vector2I c, out int localX, out int localZ);
		int cx = c.X;
		int cz = c.Y;
		int sec = Mathf.FloorToInt(posGlobal.Y / 16f);
		int localY = posGlobal.Y - sec * 16;

		if (!_chunksData.TryGetValue(new Vector2I(cx, cz), out var data)) return;
		data.SetVoxelLocal(localX, (int)posGlobal.Y, localZ, id);

		if (localX == 0 && _chunksData.TryGetValue(new Vector2I(cx - 1, cz), out var vx))
		{
			vx.SetVoxelLocal(TailleChunk, (int)posGlobal.Y, localZ, id);
			_sectionsAReconstruire.Add((cx - 1, cz, sec));
		}
		if (localZ == 0 && _chunksData.TryGetValue(new Vector2I(cx, cz - 1), out var vz))
		{
			vz.SetVoxelLocal(localX, (int)posGlobal.Y, TailleChunk, id);
			_sectionsAReconstruire.Add((cx, cz - 1, sec));
		}
		if (localX == 0 && localZ == 0 && _chunksData.TryGetValue(new Vector2I(cx - 1, cz - 1), out var vxz))
		{
			vxz.SetVoxelLocal(TailleChunk, (int)posGlobal.Y, TailleChunk, id);
			_sectionsAReconstruire.Add((cx - 1, cz - 1, sec));
		}

		if (sec >= 0 && sec < 45) _sectionsAReconstruire.Add((cx, cz, sec));
		if (localY == 0 && posGlobal.Y > 0 && sec - 1 >= 0) _sectionsAReconstruire.Add((cx, cz, sec - 1));
		if (localY == 15 && sec + 1 < 45) _sectionsAReconstruire.Add((cx, cz, sec + 1));
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
		if (_chunksData.TryGetValue(coord, out var data))
		{
			_chunksData.Remove(coord);
			data.LibérerRids();
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
		// Architecture AAA : ChunkData (RID) uniquement, plus de Node.
		if (_chunksData.TryGetValue(coordChunk, out var existing))
		{
			EnqueueChunkGeneration(existing, donnees);
			return;
		}

		var data = new ChunkData
		{
			Coordonnees = coordChunk,
			TailleChunk = TailleChunk,
			HauteurMax = HauteurMax
		};
		data.ConfigurerBruitClimat(_seedTerrain);
		_chunksData[coordChunk] = data;
		EnqueueChunkGeneration(data, donnees);
	}

	private void AttacherEtPositionnerChunk(Chunk_Client chunkVisuel, Vector3 position)
	{
		if (!IsInsideTree()) return; // Si le jeu ferme, on annule.
		AddChild(chunkVisuel);
		chunkVisuel.Position = position;
		Vector2I obs = ObtenirCoordonneesChunkJoueur();
		chunkVisuel.MettreAJourDormance(obs.X, obs.Y);
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
		return Gestionnaire_Monde.WorldToChunkCoord(pos, TailleChunk);
	}

	private void ActualiserVisibiliteEtTriChunks(Vector3 positionObservation)
	{
		if (_radarEnCours) return;

		_radarEnCours = true;
		Vector2 posObsV2 = new Vector2(positionObservation.X / (float)TailleChunk, positionObservation.Z / (float)TailleChunk);
		int cjX = Gestionnaire_Monde.WorldToChunkCoord(positionObservation.X, positionObservation.Z, TailleChunk).X;
		int cjZ = Gestionnaire_Monde.WorldToChunkCoord(positionObservation.X, positionObservation.Z, TailleChunk).Y;
		HashSet<Vector2I> chunksCharges = new HashSet<Vector2I>(_chunksData.Keys);
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

	/// <summary>Dormance physique uniquement : le visuel (RenderingServer) ne doit JAMAIS être désactivé tant que le chunk est dans RenderDistance. Au-delà de RayonDormancePhysique, on retire uniquement le corps physique du space. Au réveil, le chunk est mis en file de solidification (pas d'éveil massif).</summary>
	private void ActualiserDormanceChunks(int obsChunkX, int obsChunkZ)
	{
		World3D world = GetWorld3D();
		if (world == null) return;
		Rid space = world.Space;
		foreach (var kv in _chunksData)
		{
			var data = kv.Value;
			int dx = Mathf.Abs(data.Coordonnees.X - obsChunkX);
			int dz = Mathf.Abs(data.Coordonnees.Y - obsChunkZ);
			bool dormant = dx > RayonDormancePhysique || dz > RayonDormancePhysique;
			if (data.Dormant == dormant) continue;
			data.Dormant = dormant;
			if (data.PhysicsBodyRID.IsValid)
			{
				if (dormant)
				{
					PhysicsServer3D.Singleton.BodySetSpace(data.PhysicsBodyRID, default(Rid));
					if (data.EstEnFileSolidification)
					{
						_fileAttenteSolidification.Remove(data);
						data.EstEnFileSolidification = false;
					}
				}
				else
				{
					// Réveil : ne pas donner l'espace immédiatement (évite pic Broadphase). Mise en file.
					if (!data.EstEnFileSolidification)
					{
						_fileAttenteSolidification.Add(data);
						data.EstEnFileSolidification = true;
					}
				}
			}
		}
	}

	private void DemanderChunk(Vector2I coord)
	{
		_enregistrerDemandeChunk?.Invoke(coord);
	}

	/// <summary>Interroge la densité à une position globale (chunk en RAM uniquement). Plus utilisé pour Marching Cubes (rembourrage 17³).</summary>
	public (float valeur, bool trouve) ObtenirDensiteGlobaleEx(Vector3I posGlobale)
	{
		Gestionnaire_Monde.WorldToChunkAndLocal(posGlobale.X, posGlobale.Z, TailleChunk, out Vector2I c, out int lx, out int lz);
		if (!_chunksData.TryGetValue(c, out var data)) return (-10f, false);
		return (data.ObtenirDensiteLocale(lx, posGlobale.Y, lz), true);
	}

	/// <summary>Vrai si le chunk sous les pieds du joueur a sa collision active (body inséré dans l'espace physique, pas seulement en file).</summary>
	public bool ChunkSousPiedsAPret()
	{
		if (_joueur == null) return false;
		Vector2I c = Gestionnaire_Monde.WorldToChunkCoord(_joueur.GlobalPosition, TailleChunk);
		if (!_chunksData.TryGetValue(c, out var data)) return false;
		return data.PhysicsBodyRID.IsValid && !data.Dormant && !data.EstEnFileSolidification;
	}
}
