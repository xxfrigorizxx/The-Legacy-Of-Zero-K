using Godot;
using System;

/// <summary>Cycle solaire/lunaire basé sur UtcNow + décalage du fuseau horaire. Contrôle soleil, lune et atmosphère.</summary>
public partial class Cycle_Solaire : Node
{
	[Export] private DirectionalLight3D _soleil;
	[Export] private DirectionalLight3D _lune; // Deuxième lampe (bleutée, ombre activée)
	[Export] private WorldEnvironment _environnement; // Pour brouillard et ambiance

	/// <summary>Décalage en heures de la dimension actuelle. Monde 1 = 0, Monde 2 = +6, etc.</summary>
	private double _decalageMondeHeures = 0.0;

	/// <summary>RPC appelé par le Serveur une seule fois quand le joueur spawn ou traverse un portail.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
	public void DefinirDecalageHoraire(double heuresDeDecalage)
	{
		_decalageMondeHeures = heuresDeDecalage;
	}

	public override void _Ready()
	{
		// Fallback : résolution des nœuds par chemin si les Exports sont vides
		if (_soleil == null)
		{
			_soleil = GetParent()?.GetNodeOrNull<DirectionalLight3D>("Soleil");
			if (_soleil == null) GD.PrintErr("ZERO-K ALERTE : Nœud 'Soleil' introuvable !");
		}
		if (_lune == null)
		{
			_lune = GetParent()?.GetNodeOrNull<DirectionalLight3D>("Lune");
			if (_lune == null) GD.PrintErr("ZERO-K ALERTE : Nœud 'Lune' introuvable !");
		}
		if (_environnement == null)
		{
			_environnement = GetParent()?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
			if (_environnement == null) GD.PrintErr("ZERO-K ALERTE : Nœud 'WorldEnvironment' introuvable !");
		}

		GD.Print("Moteur Thermodynamique : EN LIGNE.");
	}

	public override void _Process(double delta)
	{
		if (!IsInsideTree()) return; // GARROT SPATIAL : le Soleil ne tourne pas si l'univers s'effondre.
		if (_soleil == null) return;

		DateTime heureDansCeMonde = DateTime.UtcNow.AddHours(_decalageMondeHeures);
		TimeSpan heureActuelle = heureDansCeMonde.TimeOfDay;
		double pourcentageJournee = heureActuelle.TotalHours / 24.0;

		// Calcul de l'angle X (Midi = -90°)
		float angleX = 90f - (float)(pourcentageJournee * 360.0);
		// GD.Print("Heure Universelle Relative : " + heureDansCeMonde.ToString("HH:mm:ss") + " | Angle : " + angleX);
		_soleil.RotationDegrees = new Vector3(angleX, -30f, 0f);

		// L'angle de la lune (toujours à l'opposé du soleil)
		if (_lune != null)
		{
			_lune.RotationDegrees = new Vector3(angleX + 180f, -30f, 0f);
		}

		// --- GESTION DE LA NUIT ET DE L'ATMOSPHÈRE ---
		// Hauteur : 1 = Zénith (Midi), 0 = Horizon, -1 = Nadir (Minuit)
		float hauteurSoleil = Mathf.Sin(Mathf.DegToRad(-angleX));

		// Le soleil s'éteint sous l'horizon, la lune s'allume
		// ProceduralSkyMaterial affiche 1 disque par DirectionalLight → sky_mode=1 (LightOnly) exclut du ciel
		if (hauteurSoleil < 0)
		{
			_soleil.LightEnergy = 0f;
			_soleil.Set("sky_mode", 1); // Pas de disque soleil (sous l'horizon)
			if (_lune != null)
			{
				_lune.Visible = true;
				_lune.LightEnergy = Mathf.Clamp(-hauteurSoleil * 0.15f, 0f, 0.15f);
				_lune.Set("sky_mode", 0); // Disque lune visible la nuit (LightAndSky)
			}
		}
		else
		{
			_soleil.LightEnergy = Mathf.Clamp(hauteurSoleil * 1.5f, 0f, 1.5f);
			_soleil.Set("sky_mode", 0); // Disque soleil le jour (LightAndSky)
			if (_lune != null)
			{
				_lune.Visible = false;
				_lune.LightEnergy = 0f;
				_lune.Set("sky_mode", 1); // CRITIQUE : LightOnly = pas de disque dans le ciel
			}
		}

		// Assombrissement du monde (brouillard, ambiance, CIEL)
		if (_environnement != null && _environnement.Environment != null)
		{
			float intensiteJour = Mathf.Clamp(hauteurSoleil + 0.2f, 0f, 1f); // 0 = nuit, 1 = jour
			bool crepuscule = hauteurSoleil > -0.15f && hauteurSoleil < 0.35f; // Lever/coucher
			float intensiteCrepuscule = crepuscule ? 1f - Mathf.Abs(hauteurSoleil - 0.1f) / 0.45f : 0f;

			Color couleurBrouillardJour = new Color(0.6f, 0.7f, 0.8f);
			Color couleurBrouillardNuit = new Color(0.01f, 0.01f, 0.03f);

			_environnement.Environment.AmbientLightEnergy = intensiteJour;
			_environnement.Environment.AmbientLightSkyContribution = intensiteJour;

			if (_environnement.Environment.FogEnabled)
			{
				_environnement.Environment.FogLightColor = couleurBrouillardNuit.Lerp(couleurBrouillardJour, intensiteJour);
			}

			// Ciel dynamique : jour (bleu) ↔ crépuscule (orange/rose) ↔ nuit (sombre)
			var sky = _environnement.Environment.Sky;
			if (sky?.SkyMaterial is ProceduralSkyMaterial skyMat)
			{
				// Couleurs jour
				Color cielHautJour = new Color(0.38f, 0.5f, 0.65f);   // Bleu ciel
				Color cielHorizonJour = new Color(0.55f, 0.62f, 0.75f);
				Color solHorizonJour = new Color(0.5f, 0.55f, 0.6f);

				// Couleurs crépuscule (lever/coucher)
				Color cielHautCrepuscule = new Color(0.4f, 0.25f, 0.5f);   // Violet/rose
				Color cielHorizonCrepuscule = new Color(0.95f, 0.45f, 0.25f); // Orange
				Color solHorizonCrepuscule = new Color(0.6f, 0.3f, 0.2f);

				// Couleurs nuit
				Color cielHautNuit = new Color(0.02f, 0.02f, 0.08f);  // Bleu nuit profond
				Color cielHorizonNuit = new Color(0.03f, 0.03f, 0.12f);
				Color solHorizonNuit = new Color(0.05f, 0.05f, 0.15f);

				// Interpolation : nuit → crépuscule → jour
				Color cielHaut, cielHorizon, solHorizon;
				if (intensiteJour > 0.5f)
				{
					// Jour ou fin de crépuscule
					float t = Mathf.Clamp((intensiteJour - 0.5f) * 2f, 0f, 1f);
					cielHaut = cielHautCrepuscule.Lerp(cielHautJour, t);
					cielHorizon = cielHorizonCrepuscule.Lerp(cielHorizonJour, t);
					solHorizon = solHorizonCrepuscule.Lerp(solHorizonJour, t);
				}
				else if (intensiteCrepuscule > 0f)
				{
					// Crépuscule actif
					cielHaut = cielHautNuit.Lerp(cielHautCrepuscule, intensiteCrepuscule);
					cielHorizon = cielHorizonNuit.Lerp(cielHorizonCrepuscule, intensiteCrepuscule);
					solHorizon = solHorizonNuit.Lerp(solHorizonCrepuscule, intensiteCrepuscule);
				}
				else
				{
					// Nuit pure
					cielHaut = cielHautNuit;
					cielHorizon = cielHorizonNuit;
					solHorizon = solHorizonNuit;
				}

				skyMat.SkyTopColor = cielHaut;
				skyMat.SkyHorizonColor = cielHorizon;
				skyMat.GroundHorizonColor = solHorizon;
				skyMat.GroundBottomColor = solHorizonNuit.Lerp(new Color(0.2f, 0.17f, 0.13f), intensiteJour);
				skyMat.SkyEnergyMultiplier = Mathf.Lerp(0.15f, 1f, intensiteJour);
				skyMat.GroundEnergyMultiplier = Mathf.Lerp(0.1f, 1f, intensiteJour);
				// Étoiles visibles la nuit, invisibles le jour
				skyMat.SkyCoverModulate = new Color(1f, 1f, 1f, Mathf.Lerp(1f, 0.05f, intensiteJour));
			}
		}
	}
}
