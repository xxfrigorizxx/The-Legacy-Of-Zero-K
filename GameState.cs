using Godot;
using System;
using System.Collections.Generic;
using System.IO;

/// <summary>État global du jeu. Autoload pour passer monde/seed entre menu et jeu.</summary>
public partial class GameState : Node
{
	/// <summary>Instance statique pour accès fiable (Engine.HasSingleton peu fiable avec autoloads C#).</summary>
	public static GameState Instance { get; private set; }

	/// <summary>Nom du monde actuel (dossier dans user://saves/). TOUJOURS utilisé pour chunks.</summary>
	public string NomMondeActuel { get; private set; } = "MonMonde";

	public override void _Ready()
	{
		Instance = this;
	}

	/// <summary>Seed du terrain pour le monde actuel.</summary>
	public int SeedTerrainActuel { get; private set; } = 19847;

	/// <summary>Prépare un nouveau monde (nouvelle seed, nouveau dossier). Retourne le nom du monde créé.</summary>
	public string CreerNouveauMonde()
	{
		int seed = (int)(DateTime.UtcNow.Ticks % 2147483647);
		if (seed < 0) seed = -seed;
		string nom = $"Monde_{seed}";
		NomMondeActuel = nom;
		SeedTerrainActuel = seed;
		string dossier = ProjectSettings.GlobalizePath($"user://saves/{nom}/chunks");
		Directory.CreateDirectory(dossier);
		SauvegarderMetadataMonde(nom, seed);
		GD.Print($"ZERO-K : Nouveau monde créé : {nom} (seed {seed})");
		return nom;
	}

	/// <summary>Charge un monde existant par son nom. Retourne true si trouvé. Rétrocompatibilité : MonMonde sans world_meta → seed 19847.</summary>
	public bool ChargerMonde(string nomMonde)
	{
		string dossier = ProjectSettings.GlobalizePath($"user://saves/{nomMonde}");
		string cheminMeta = Path.Combine(dossier, "world_meta.dat");
		int seed = 19847;
		if (File.Exists(cheminMeta))
		{
			try
			{
				using var reader = new BinaryReader(File.Open(cheminMeta, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read));
				seed = reader.ReadInt32();
			}
			catch (Exception ex)
			{
				GD.PrintErr($"ZERO-K : Erreur lecture metadata : {ex.Message}");
			}
		}
		else if (!Directory.Exists(dossier))
		{
			GD.PrintErr($"ZERO-K : Monde '{nomMonde}' introuvable (dossier absent).");
			return false;
		}
		NomMondeActuel = nomMonde;
		SeedTerrainActuel = seed;
		GD.Print($"ZERO-K : Monde chargé : {nomMonde} (seed {seed})");
		return true;
	}

	/// <summary>Liste les noms des mondes sauvegardés. Inclut les dossiers avec chunks/ (rétrocompatibilité MonMonde).</summary>
	public List<string> ObtenirListeMondes()
	{
		var liste = new List<string>();
		string basePath = ProjectSettings.GlobalizePath("user://saves");
		if (!Directory.Exists(basePath)) return liste;
		foreach (string dir in Directory.GetDirectories(basePath))
		{
			string nom = Path.GetFileName(dir);
			if (File.Exists(Path.Combine(dir, "world_meta.dat")) || Directory.Exists(Path.Combine(dir, "chunks")))
				liste.Add(nom);
		}
		return liste;
	}

	private void SauvegarderMetadataMonde(string nom, int seed)
	{
		string dossier = ProjectSettings.GlobalizePath($"user://saves/{nom}");
		Directory.CreateDirectory(dossier);
		string chemin = Path.Combine(dossier, "world_meta.dat");
		using var writer = new BinaryWriter(File.Open(chemin, FileMode.Create));
		writer.Write(seed);
	}
}
