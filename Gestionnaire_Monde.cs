using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>Orchestre Monde_Serveur (données) et Monde_Client (visuel). Support Solo (Host local) et MMORPG.</summary>
public partial class Gestionnaire_Monde : Node3D
{
	[Export] public int TailleChunk = 16;
	[Export] public int HauteurMax = 720;  // Montagnes jusqu'à 700
	[Export] public int SeedTerrain = 19847;
	[Export] public int RenderDistance = 200;
	[Export] public int MaxChunksParFrame = 4;
	/// <summary>Fuseau horaire du Monde 1. Québec = -5, Paris = +1, UTC = 0.</summary>
	[Export] public double FuseauHoraireHeures = -5;
	[Export] public bool PreGenererAuDemarrage = false;
	[Export] public int RayonPreGeneration = 2;
	[Export] public Material MaterielTerrain;
	/// <summary>Échelle du gazon (grass.glb) sur ID 1. Modifier pour ajuster la taille partout.</summary>
	[Export] public float EchelleGazon = 2f;
	public int RayonMondeChunks = 1000;

	/// <summary>Si true, utilise Monde_Serveur + Monde_Client (Solo/MMORPG). Si false, legacy Generateur_Voxel.</summary>
	[Export] public bool UseArchitectureReseau = true;

	// Files pour le mode legacy (Generateur_Voxel)
	private ConcurrentQueue<System.Action> _misesAJourMainThread = new ConcurrentQueue<System.Action>();
	public ConcurrentQueue<System.Action> _misesAJourUrgentes = new ConcurrentQueue<System.Action>();

	private CharacterBody3D _joueur;
	private Monde_Serveur _mondeServeur;
	private Monde_Client _mondeClient;
	private NetworkManager _networkManager;
	private Label _labelCoords;

	// Legacy
	private List<Vector2I> _chunksACharger = new List<Vector2I>();
	private bool _radarLegacyEnCours;
	private Dictionary<Vector2I, Node3D> _chunks = new Dictionary<Vector2I, Node3D>();
	private PackedScene _sceneChunk;
	private Vector2I _ancienChunkJoueur = new Vector2I(-99999, -99999);

	public void EnqueueMiseAJourMainThread(System.Action action) => _misesAJourMainThread.Enqueue(action);
	public void EnqueueMiseAJourUrgente(System.Action action) => _misesAJourUrgentes.Enqueue(action);

	/// <summary>Vrai si le chunk sous les pieds du joueur a sa collision construite (évite chute libre au spawn).</summary>
	public bool EstSpawnPret()
	{
		if (_joueur == null) return false;
		if (UseArchitectureReseau) return _mondeClient?.ChunkSousPiedsAPret() ?? false;
		Vector3 pos = _joueur.GlobalPosition;
		int cx = Mathf.FloorToInt(pos.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pos.Z / (float)TailleChunk);
		if (!_chunks.TryGetValue(new Vector2I(cx, cz), out var n)) return false;
		var ch = n as Generateur_Voxel;
		if (ch == null) return false;
		int sec = Mathf.Clamp(Mathf.FloorToInt(pos.Y / 16f), 0, 15);
		return ch.SectionAPret(sec);
	}

	/// <summary>Utilisé par Generateur_Voxel (legacy) et Monde_Serveur.</summary>
	public bool ChunkEstCharge(Vector2I coord)
	{
		if (UseArchitectureReseau) return _mondeServeur?.ChunkEstCharge(coord) ?? false;
		return _chunks.ContainsKey(coord);
	}

	/// <summary>Utilisé par Generateur_Voxel (legacy). En mode réseau, Monde_Serveur gère l'eau.</summary>
	public void ReveillerEauAdjacente(Vector3 pointGlobal)
	{
		if (UseArchitectureReseau) { _mondeServeur?.ReveillerEauAdjacente(pointGlobal); return; }
		ReveillerEauAdjacenteLegacy(pointGlobal);
	}

	private Queue<Vector3I> _fileEau = new Queue<Vector3I>();
	private HashSet<Vector3I> _eauActive = new HashSet<Vector3I>();
	private float _tempsEcoulement;
	private const float TICK_EAU = 0.05f;
	private const int MaxEauParTick = 32;
	private static readonly Vector3I[] DirReveilEau = { new Vector3I(0, 1, 0), new Vector3I(0, -1, 0), new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 0, 1), new Vector3I(0, 0, -1) };
	private static readonly Vector3I[] DirEauHorizLegacy = { new Vector3I(1, 0, 0), new Vector3I(-1, 0, 0), new Vector3I(0, 0, -1), new Vector3I(0, 0, 1) };

	private void ActiverEauLegacy(Vector3I pos) { if (_eauActive.Add(pos)) _fileEau.Enqueue(pos); }

	private void ReveillerEauAdjacenteLegacy(Vector3 pointGlobal)
	{
		int gx = Mathf.FloorToInt(pointGlobal.X), gy = Mathf.FloorToInt(pointGlobal.Y), gz = Mathf.FloorToInt(pointGlobal.Z);
		var basePos = new Vector3I(gx, gy, gz);
		foreach (var d in DirReveilEau)
			if (EstVoxelEauLegacy(basePos + d)) ActiverEauLegacy(basePos + d);
	}

	private (Generateur_Voxel chunk, Vector3I local)? ObtenirChunkEtLocalLegacy(Vector3I pos)
	{
		if (pos.Y < 0 || pos.Y > HauteurMax) return null;
		int cx = Mathf.FloorToInt(pos.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pos.Z / (float)TailleChunk);
		Vector2I coord = new Vector2I(cx, cz);
		if (!_chunks.TryGetValue(coord, out var n)) return null;
		int lx = pos.X - cx * TailleChunk;
		int lz = pos.Z - cz * TailleChunk;
		if (lx < 0 || lx > TailleChunk || lz < 0 || lz > TailleChunk) return null;
		var ch = n as Generateur_Voxel;
		return ch != null ? (ch, new Vector3I(lx, pos.Y, lz)) : null;
	}

	private bool EstVoxelEauLegacy(Vector3I pos)
	{
		var r = ObtenirChunkEtLocalLegacy(pos);
		return r.HasValue && r.Value.chunk.EstVoxelEau(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
	}

	private bool EstVoxelAirLegacy(Vector3I pos)
	{
		var r = ObtenirChunkEtLocalLegacy(pos);
		return r.HasValue && r.Value.chunk.EstVoxelAir(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
	}

	private void DefinirVoxelLegacy(Vector3I pos, byte id)
	{
		var r = ObtenirChunkEtLocalLegacy(pos);
		if (!r.HasValue) return;
		if (id == 4) r.Value.chunk.DefinirVoxelEau(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
		else if (id == 0) r.Value.chunk.DefinirVoxelAir(r.Value.local.X, r.Value.local.Y, r.Value.local.Z);
	}

	private void DemanderMiseAJourMeshLegacy(Vector3I pos)
	{
		var r = ObtenirChunkEtLocalLegacy(pos);
		if (!r.HasValue) return;
		r.Value.chunk.ActualiserMesh();
		int cx = Mathf.FloorToInt(pos.X / (float)TailleChunk);
		int cz = Mathf.FloorToInt(pos.Z / (float)TailleChunk);
		int lx = pos.X - cx * TailleChunk;
		int lz = pos.Z - cz * TailleChunk;
		if (lx == 0 && _chunks.TryGetValue(new Vector2I(cx - 1, cz), out var vx)) (vx as Generateur_Voxel)?.ActualiserMesh();
		if (lx == TailleChunk - 1 && _chunks.TryGetValue(new Vector2I(cx + 1, cz), out var vxp)) (vxp as Generateur_Voxel)?.ActualiserMesh();
		if (lz == 0 && _chunks.TryGetValue(new Vector2I(cx, cz - 1), out var vz)) (vz as Generateur_Voxel)?.ActualiserMesh();
		if (lz == TailleChunk - 1 && _chunks.TryGetValue(new Vector2I(cx, cz + 1), out var vzp)) (vzp as Generateur_Voxel)?.ActualiserMesh();
	}

	public override void _Ready()
	{
		DirAccess.MakeDirRecursiveAbsolute("user://chunks");
		_joueur = GetParent().GetNode<CharacterBody3D>("Joueur");
		Chunk_Client.EchelleGazon = EchelleGazon;

		// Affichage des coordonnées en haut au centre
		var canvas = new CanvasLayer { Layer = 10 };
		var panel = new PanelContainer();
		panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop, false);
		panel.OffsetLeft = -70;
		panel.OffsetTop = 8;
		panel.OffsetRight = 70;
		panel.OffsetBottom = 36;
		var style = new StyleBoxFlat();
		style.BgColor = new Color(0, 0, 0, 0.6f);
		style.SetCornerRadiusAll(4);
		style.SetContentMarginAll(6);
		panel.AddThemeStyleboxOverride("panel", style);
		_labelCoords = new Label();
		_labelCoords.AddThemeFontSizeOverride("font_size", 14);
		_labelCoords.HorizontalAlignment = HorizontalAlignment.Center;
		panel.AddChild(_labelCoords);
		AddChild(canvas);
		canvas.AddChild(panel);

		// Position : chargée si monde existant, sinon spawn par défaut (terrain généré → joueur déposé)
		Vector3 posSpawn = _joueur.GlobalPosition;
		var posSauvegardee = GameState.Instance?.ObtenirPositionJoueurSauvegardee();
		if (posSauvegardee.HasValue)
		{
			posSpawn = posSauvegardee.Value;
			GD.Print($"ZERO-K : Joueur reconnecté à {posSpawn}");
		}
		else
		{
			int hauteurTerrain = Generateur_Voxel.ObtenirHauteurTerrainMonde((int)posSpawn.X, (int)posSpawn.Z, SeedTerrain);
			float ySpawn = hauteurTerrain + 40f;  // Spawn plus haut (terrain généré d'abord, gravité suspendue jusqu'à collision)
			if (hauteurTerrain < 103) ySpawn = Mathf.Max(ySpawn, 142f);
			posSpawn = new Vector3(posSpawn.X, ySpawn, posSpawn.Z);
		}
		_joueur.GlobalPosition = posSpawn;

		if (UseArchitectureReseau)
		{
			DemarrerArchitectureReseau();
		}
		else
		{
			DemarrerLegacy();
		}

		if (PreGenererAuDemarrage)
			_ = PreGenererMonde(RayonPreGeneration);

		CreerMenuPause();
	}

	public override void _Input(InputEvent @event)
	{
		if (@event.IsActionPressed("ui_cancel"))
		{
			ToggleMenuPause();
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _Notification(int what)
	{
		if (what == Node.NotificationWMCloseRequest)
		{
			if (_joueur != null)
				GameState.Instance?.SauvegarderPositionJoueur(_joueur.GlobalPosition);
			if (UseArchitectureReseau)
				_mondeServeur?.SauvegarderMondeEntier();
			else
				foreach (var kv in _chunks)
					(kv.Value as Generateur_Voxel)?.Sauvegarder(kv.Key);
		}
		base._Notification(what);
	}

	public override void _ExitTree()
	{
		// Sauvegarde position joueur (reconnexion au même endroit)
		if (_joueur != null)
			GameState.Instance?.SauvegarderPositionJoueur(_joueur.GlobalPosition);
		// RÈGLE ABSOLUE : sauvegarde des chunks modifiés AVANT destruction (parent _ExitTree avant enfants).
		if (UseArchitectureReseau)
			_mondeServeur?.SauvegarderMondeEntier();
		else
		{
			foreach (var kv in _chunks)
				(kv.Value as Generateur_Voxel)?.Sauvegarder(kv.Key);
		}
		base._ExitTree();
	}

	private Panel _panelPause;
	private bool _pauseVisible;

	private void CreerMenuPause()
	{
		var layer = new CanvasLayer { Layer = 100, ProcessMode = ProcessModeEnum.Always };
		AddChild(layer);
		_panelPause = new Panel();
		_panelPause.SetAnchorsPreset(Control.LayoutPreset.Center);
		_panelPause.OffsetLeft = -100;
		_panelPause.OffsetTop = -80;
		_panelPause.OffsetRight = 100;
		_panelPause.OffsetBottom = 80;
		var vbox = new VBoxContainer();
		vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		vbox.OffsetLeft = 20;
		vbox.OffsetTop = 20;
		vbox.OffsetRight = -20;
		vbox.OffsetBottom = -20;
		vbox.AddThemeConstantOverride("separation", 10);
		_panelPause.AddChild(vbox);
		var lbl = new Label { Text = "Pause", HorizontalAlignment = HorizontalAlignment.Center };
		vbox.AddChild(lbl);
		var btnResume = new Button { Text = "Reprendre" };
		btnResume.Pressed += () => { ToggleMenuPause(); };
		vbox.AddChild(btnResume);
		var btnSave = new Button { Text = "Sauvegarder" };
		btnSave.Pressed += () =>
		{
			if (_joueur != null)
				GameState.Instance?.SauvegarderPositionJoueur(_joueur.GlobalPosition);
			_mondeServeur?.SauvegarderMondeEntier();
			GD.Print("ZERO-K : Sauvegarde manuelle effectuée.");
		};
		vbox.AddChild(btnSave);
		var btnQuit = new Button { Text = "Quitter" };
		btnQuit.Pressed += () => GetTree().ChangeSceneToFile("res://menu_principal.tscn");
		vbox.AddChild(btnQuit);
		layer.AddChild(_panelPause);
		_panelPause.Visible = false;
	}

	private void ToggleMenuPause()
	{
		if (_panelPause == null) CreerMenuPause();
		_pauseVisible = !_pauseVisible;
		_panelPause.Visible = _pauseVisible;
		GetTree().Paused = _pauseVisible;
		Input.MouseMode = _pauseVisible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
	}

	private void DemarrerArchitectureReseau()
	{
		_networkManager = new NetworkManager();
		AddChild(_networkManager);
		_networkManager.DemarrerHostSolo();

		_mondeServeur = new Monde_Serveur();
		_mondeServeur.TailleChunk = TailleChunk;
		_mondeServeur.HauteurMax = HauteurMax;
		_mondeServeur.SeedTerrain = GetNode<GameState>("/root/GameState").SeedTerrainActuel;
		_mondeServeur.RenderDistance = RenderDistance;
		_mondeServeur.FuseauHoraireHeures = FuseauHoraireHeures;
		_mondeServeur.MaterielTerrain = MaterielTerrain ?? GD.Load<Material>("res://Manteau_Planetaire.tres");

		_mondeClient = new Monde_Client();
		_mondeClient.TailleChunk = TailleChunk;
		_mondeClient.HauteurMax = HauteurMax;
		_mondeClient.RenderDistance = RenderDistance;
		_mondeClient.MaxChunksParFrame = MaxChunksParFrame;
		_mondeClient.MaterielTerrain = MaterielTerrain ?? GD.Load<Material>("res://Manteau_Planetaire.tres");
		_mondeClient.Initialiser(
			_joueur,
			GetNode<GameState>("/root/GameState").SeedTerrainActuel,
			coord => _mondeServeur.EnregistrerDemandeChunk(coord),
			(pointImpact, rayon) => _mondeServeur.AppliquerDestructionGlobale(pointImpact, rayon),
			(pointImpact, normale, rayon, idMatiere) => _mondeServeur.AppliquerCreationGlobale(pointImpact, normale, rayon, idMatiere)
		);

		_mondeServeur.Initialiser(
			this,
			(coord, sections) => _mondeClient.RecevoirChunkModifie(coord, sections),
			(coord, donnees) => _mondeClient.RecevoirDonneesChunk(coord, donnees),
			(coord, inventaireFlore) => _mondeClient.RecevoirFloreModifie(coord, inventaireFlore),
			(pos, id) =>
			{
				_mondeServeur.RepliquerPaddingVoisins(pos, id);
				_mondeClient.AppliquerVoxel(pos, id);
				if (Multiplayer.IsServer())
					_mondeClient.Rpc(nameof(Monde_Client.AppliquerVoxelRPC), pos.X, pos.Y, pos.Z, (int)id);
			},
			(coord) =>
			{
				if (Multiplayer.IsServer())
					_mondeClient.Rpc("OrdonnerDestructionChunkRPC", coord.X, coord.Y);
			},
			() => _joueur?.GlobalPosition ?? Vector3.Zero
		);
		AddChild(_mondeServeur);
		AddChild(_mondeClient);

		// Matrice visqueuse : Area3D océan (Y < 103) impose damp + gravité réduite (Archimède)
		CreerAreaOcean();

		// Lier le chunk de spawn en priorité pour éviter chute libre (comme les 2 fois précédentes)
		Vector3 pos = _joueur.GlobalPosition;
		Vector2I chunkSpawn = new Vector2I(Mathf.FloorToInt(pos.X / (float)TailleChunk), Mathf.FloorToInt(pos.Z / (float)TailleChunk));
		_mondeClient.ReserverChunkSpawnPrioritaire(chunkSpawn);

		// Envoyer le fuseau horaire de la dimension au client (spawn / portail)
		EnvoyerFuseauHoraireAuPeer(1); // Peer 1 = hôte local en Solo
		Multiplayer.PeerConnected += EnvoyerFuseauHoraireAuPeer;
	}

	/// <summary>Matrice visqueuse : océan physique couvrant Y &lt; 103. Linear/Angular Damp 4.0, gravité 4 (Archimède).</summary>
	private void CreerAreaOcean()
	{
		const float NIVEAU_EAU = 103f;
		float demiRayon = RayonMondeChunks * TailleChunk;
		float hauteurZone = NIVEAU_EAU + 500f; // Couvre jusqu'en profondeur -500
		var ocean = new Area3D { Name = "Ocean_Physique" };
		ocean.GravitySpaceOverride = Area3D.SpaceOverride.Replace;
		ocean.Gravity = 4.0f; // Poussée d'Archimède (réduit chute)
		ocean.GravityDirection = new Vector3(0, -1, 0);
		ocean.GravityPoint = false;
		ocean.LinearDamp = 4.0f;
		ocean.LinearDampSpaceOverride = Area3D.SpaceOverride.Replace;
		ocean.AngularDamp = 4.0f;
		ocean.AngularDampSpaceOverride = Area3D.SpaceOverride.Replace;
		ocean.Priority = 100; // Priorité haute sur le monde par défaut

		var col = new CollisionShape3D();
		col.Shape = new BoxShape3D { Size = new Vector3(demiRayon * 2f, hauteurZone, demiRayon * 2f) };
		ocean.AddChild(col);
		ocean.Position = new Vector3(0, (NIVEAU_EAU - 500f) / 2f, 0); // Centre du volume
		AddChild(ocean);
	}

	private void EnvoyerFuseauHoraireAuPeer(long peerId)
	{
		if (!Multiplayer.IsServer()) return;
		var soleil = GetParent()?.GetNodeOrNull<Cycle_Solaire>("CycleSolaire");
		if (soleil == null) return;
		double offset = _mondeServeur?.FuseauHoraireHeures ?? 0.0;
		soleil.RpcId(peerId, nameof(Cycle_Solaire.DefinirDecalageHoraire), offset);
	}

	private void DemarrerLegacy()
	{
		_sceneChunk = GD.Load<PackedScene>("res://Generateur_Voxel.tscn");
		ActualiserVisibiliteEtTriChunksLegacy();
	}

	public override void _Process(double delta)
	{
		// Mise à jour des coordonnées affichées en haut à droite
		if (_labelCoords != null && _joueur != null && _joueur.IsInsideTree())
		{
			Vector3 p = _joueur.GlobalPosition;
			_labelCoords.Text = $"X: {p.X:F1}  Y: {p.Y:F1}  Z: {p.Z:F1}";
		}

		if (UseArchitectureReseau)
		{
			// Monde_Client gère son propre _Process
			return;
		}

		// Legacy : goutte-à-goutte visuel (1 mesh/frame max, évite Upload Stall VRAM)
		const int MaxMeshesParFrame = 2;
		int actionsExecutees = 0;
		while (actionsExecutees < MaxMeshesParFrame && _misesAJourUrgentes.TryDequeue(out var a))
		{
			a.Invoke();
			actionsExecutees++;
		}
		while (actionsExecutees < MaxMeshesParFrame && _misesAJourMainThread.TryDequeue(out var a))
		{
			a.Invoke();
			actionsExecutees++;
		}

		Vector2I cj = ObtenirCoordonneesChunkJoueur();
		bool chunkChange = cj != _ancienChunkJoueur;
		if (chunkChange) _ancienChunkJoueur = cj;

		// Radar strict : uniquement quand le joueur change de chunk (zéro alloc quand immobile)
		if (chunkChange)
			ActualiserVisibiliteEtTriChunksLegacy();

		int n = 0;
		while (_chunksACharger.Count > 0 && n < MaxChunksParFrame)
		{
			Vector2I c = _chunksACharger[0];
			_chunksACharger.RemoveAt(0);
			LancerGenerationChunk(c.X, c.Y);
			n++;
		}

		// Eau dynamique (legacy)
		_tempsEcoulement += (float)delta;
		if (_tempsEcoulement >= TICK_EAU)
		{
			_tempsEcoulement = 0;
			int eauCount = Math.Min(_fileEau.Count, MaxEauParTick);
			for (int i = 0; i < eauCount; i++)
			{
				Vector3I pos = _fileEau.Dequeue();
				_eauActive.Remove(pos);
				if (!EstVoxelEauLegacy(pos)) continue;
				Vector3I posBas = pos + new Vector3I(0, -1, 0);
				if (posBas.Y < 0) { DefinirVoxelLegacy(pos, 0); DemanderMiseAJourMeshLegacy(pos); continue; }
				if (EstVoxelAirLegacy(posBas))
				{
					DefinirVoxelLegacy(posBas, 4);
					DefinirVoxelLegacy(pos, 0);
					ActiverEauLegacy(posBas);
					DemanderMiseAJourMeshLegacy(pos);
					DemanderMiseAJourMeshLegacy(posBas);
					ReveillerEauAdjacenteLegacy(new Vector3(pos.X, pos.Y, pos.Z));
					continue;
				}
				bool aPression = EstVoxelEauLegacy(pos + new Vector3I(0, 1, 0));
				foreach (var d in DirEauHorizLegacy)
				{
					Vector3I pc = pos + d, pcb = pc + new Vector3I(0, -1, 0);
					if (!EstVoxelAirLegacy(pc)) continue;
					if (aPression || EstVoxelAirLegacy(pcb))
					{
						DefinirVoxelLegacy(pc, 4);
						DefinirVoxelLegacy(pos, 0);
						ActiverEauLegacy(pc);
						DemanderMiseAJourMeshLegacy(pos);
						DemanderMiseAJourMeshLegacy(pc);
						ReveillerEauAdjacenteLegacy(new Vector3(pos.X, pos.Y, pos.Z));
						break;
					}
				}
			}
		}
	}

	private Vector2I ObtenirCoordonneesChunkJoueur()
	{
		if (_joueur == null) return Vector2I.Zero;
		Vector3 p = _joueur.GlobalPosition;
		return new Vector2I(Mathf.FloorToInt(p.X / (float)TailleChunk), Mathf.FloorToInt(p.Z / (float)TailleChunk));
	}

	public void AppliquerDestructionGlobale(Vector3 pointImpact, float rayon)
	{
		if (UseArchitectureReseau)
			_mondeClient?.AppliquerDestructionGlobale(pointImpact, rayon);
		else
		{
			foreach (var kv in _chunks)
			{
				var g = kv.Value as Generateur_Voxel;
				g?.DetruireVoxel(pointImpact, rayon);
			}
		}
	}

	public void AppliquerCreationGlobale(Vector3 pointImpact, Vector3 normale, float rayon, int idMatiere = 1)
	{
		if (UseArchitectureReseau)
			_mondeClient?.AppliquerCreationGlobale(pointImpact, normale, rayon, idMatiere);
		else
		{
			Vector3 cible = pointImpact + (normale * 1.5f);
			foreach (var kv in _chunks)
			{
				var g = kv.Value as Generateur_Voxel;
				g?.CreerMatiere(cible, rayon, idMatiere);
			}
		}
	}

	/// <summary>Oracle géologique : lecture directe de l'ADN (_materials) depuis le Serveur. Évite la dissonance visuelle (mine terre → reçoit pierre).</summary>
	public int ObtenirMatiereExacte(Vector3 positionGlobale)
	{
		if (UseArchitectureReseau && _mondeServeur != null)
			return _mondeServeur.ObtenirMatiereExacte(positionGlobale);
		return AnalyserMatiereAuPoint(positionGlobale, Vector3.Up); // Fallback legacy
	}

	/// <summary>Oracle géologique (legacy) : déduit l'ID depuis altitude/normale. Utiliser ObtenirMatiereExacte en mode réseau.</summary>
	public int AnalyserMatiereAuPoint(Vector3 positionGlobale, Vector3 normaleSurface)
	{
		float altitude = positionGlobale.Y;

		// Règle 1 : La pente absolue (La Roche) — mur vertical ou falaise
		if (normaleSurface.Y < 0.6f)
			return 2; // ID 2 = Roche

		// Règle 2 : Le niveau de la mer (Le Sable)
		const float NIVEAU_EAU = 103f; // Aligné avec NiveauPlage du terrain (+1 m)
		if (altitude < NIVEAU_EAU + 2.0f && altitude >= NIVEAU_EAU - 5.0f)
			return 3; // ID 3 = Sable

		// Règle 3 : Les hauts sommets (La Neige) — 245-255 (bruit)
		int bruit = (int)((positionGlobale.X * 73856093 + positionGlobale.Z * 19349663) % 37) - 18;
		float seuilNeige = 250f + bruit * 0.3f;
		if (altitude > seuilNeige)
			return 4; // ID 4 = Neige (atlas livre)

		// Par défaut : plat et altitude moyenne
		return 1; // ID 1 = Terre
	}

	// --- Legacy ---
	private bool FichierSauvegardeExiste(Vector2I coord)
		=> FileAccess.FileExists(Generateur_Voxel.ObtenirCheminChunk(coord));

	public async Task PreGenererMonde(int rayonChunks)
	{
		GD.Print($"DÉBUT BAKING : Rayon {rayonChunks} chunks...");
		for (int x = -rayonChunks; x <= rayonChunks; x++)
			for (int z = -rayonChunks; z <= rayonChunks; z++)
			{
				Vector2I coord = new Vector2I(x, z);
				if (FichierSauvegardeExiste(coord)) continue;
				var (d, m) = Generateur_Voxel.GenererDonneesVoxelBrut(coord, SeedTerrain, TailleChunk, HauteurMax);
				Generateur_Voxel.SauvegarderDonneesBrutes(coord, d, m, TailleChunk, HauteurMax);
				await Task.Delay(0);
			}
		GD.Print("FIN BAKING.");
	}

	private void ActualiserVisibiliteEtTriChunksLegacy()
	{
		if (_joueur == null) return;
		if (_radarLegacyEnCours) return;

		_radarLegacyEnCours = true;
		Vector2I chunkJoueur = ObtenirCoordonneesChunkJoueur();
		int cjX = chunkJoueur.X;
		int cjZ = chunkJoueur.Y;
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
					if (Mathf.Abs(coord.X) > RayonMondeChunks || Mathf.Abs(coord.Y) > RayonMondeChunks) continue;
					if (dejaVu.Add(coord))
						copieChunksACharger.Add(coord);
				}

			copieChunksACharger.Sort((a, b) => a.DistanceSquaredTo(chunkJoueur).CompareTo(b.DistanceSquaredTo(chunkJoueur)));

			Callable.From(() => AppliquerNouveauTriRadarLegacy(copieChunksACharger.ToArray())).CallDeferred();
		});
	}

	private void AppliquerNouveauTriRadarLegacy(Vector2I[] nouvelleListeTriee)
	{
		_chunksACharger = new List<Vector2I>(nouvelleListeTriee);
		_radarLegacyEnCours = false;

		Vector2I chunkJoueur = ObtenirCoordonneesChunkJoueur();
		int cjX = chunkJoueur.X;
		int cjZ = chunkJoueur.Y;
		var sup = new List<Vector2I>();
		foreach (var kv in _chunks)
		{
			if (Mathf.Abs(kv.Key.X - cjX) > RenderDistance || Mathf.Abs(kv.Key.Y - cjZ) > RenderDistance)
			{
				(kv.Value as Generateur_Voxel)?.Sauvegarder(kv.Key);
				kv.Value.QueueFree();
				sup.Add(kv.Key);
			}
		}
		foreach (var k in sup) _chunks.Remove(k);
	}

	private void LancerGenerationChunk(int cx, int cz)
	{
		if (!IsInsideTree()) return; // GARROT SPATIAL : pas d'ajout de chunk si l'arbre s'effondre.
		Vector2I coord = new Vector2I(cx, cz);
		if (_chunks.ContainsKey(coord)) return;
		var chunk = _sceneChunk.Instantiate<Node3D>();
		var g = chunk as Generateur_Voxel;
		g.SeedTerrain = SeedTerrain;
		g.ChunkOffsetX = coord.X;
		g.ChunkOffsetZ = coord.Y;
		chunk.Position = new Vector3(coord.X * TailleChunk, 0, coord.Y * TailleChunk);
		AddChild(chunk);
		g.DemarrerGenerationChunk(coord);
		_chunks[coord] = chunk;
	}
}
