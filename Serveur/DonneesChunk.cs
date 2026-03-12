using Godot;
using System;
using System.Collections.Generic;

/// <summary>Structure pour transfert RPC. Données quantifiées (byte[]) pour éviter la saturation du Main Thread.</summary>
public partial class DonneesChunk : RefCounted
{
	public Vector2I CoordChunk;
	public int TailleChunk;
	public int HauteurMax;

	/// <summary>Registre de flore : position globale → type (0=Gazon seul, 1=Buisson Plein, 2=Buisson Vide). Null si vide.</summary>
	public Dictionary<Vector3I, byte> InventaireFlore;

	/// <summary>Densités quantifiées (byte 0-255). Null si format local (float[]).</summary>
	public byte[] DensitiesQuantifiees;
	/// <summary>Densités eau quantifiées. Null si pas d'eau ou format local.</summary>
	public byte[] DensitiesEauQuantifiees;
	public byte[] MaterialsFlat;

	// Format local (Solo, même processus) - évite quantification si appel direct
	public float[] DensitiesFlat;
	public float[] DensitiesEauFlat;

	/// <summary>Chemin binaire pour sauvegarde/chargement. TOUJOURS user://saves/{NomMondeActuel}/chunks/.</summary>
	public static string ObtenirCheminChunk(Vector2I coord)
	{
		string nom = GameState.Instance?.NomMondeActuel ?? "MonMonde";
		return $"user://saves/{nom}/chunks/chunk_{coord.X}_{coord.Y}.bin";
	}

	/// <summary>Échelle pour normaliser les densités (typiquement -64 à 64 → -1 à 1).</summary>
	private const float EchelleQuantification = 64f;

	/// <summary>Quantifie float[,,] (-64..64) vers byte[] (0-255). Divise le poids par 4.</summary>
	public static byte[] CompresserDensitesPourReseau(float[,,] densites3D, int tailleX, int tailleY, int tailleZ)
	{
		int size = tailleX * tailleY * tailleZ;
		var result = new byte[size];
		int idx = 0;
		for (int x = 0; x < tailleX; x++)
			for (int y = 0; y < tailleY; y++)
				for (int z = 0; z < tailleZ; z++)
				{
					float val = Mathf.Clamp(densites3D[x, y, z] / EchelleQuantification, -1f, 1f);
					result[idx++] = (byte)Mathf.Clamp((int)((val + 1f) * 127.5f), 0, 255);
				}
		return result;
	}

	/// <summary>Déquantifie byte[] vers float[] plat (x + y*tx + z*tx*ty). Plus rapide que [,,] en C#.</summary>
	public static float[] DecompresserDensitesFlat(byte[] densitesPlates, int tailleX, int tailleY, int tailleZ)
	{
		int size = tailleX * tailleY * tailleZ;
		var result = new float[size];
		for (int i = 0; i < size; i++)
			result[i] = (densitesPlates[i] / 127.5f - 1f) * EchelleQuantification;
		return result;
	}

	/// <summary>Déquantifie byte[] vers float[,,]. À appeler dans Task.Run (background). Conservé pour compatibilité serveur.</summary>
	public static float[,,] DecompresserDensites(byte[] densitesPlates, int tailleX, int tailleY, int tailleZ)
	{
		var result = new float[tailleX, tailleY, tailleZ];
		int idx = 0;
		for (int x = 0; x < tailleX; x++)
			for (int y = 0; y < tailleY; y++)
				for (int z = 0; z < tailleZ; z++)
					result[x, y, z] = (densitesPlates[idx++] / 127.5f - 1f) * EchelleQuantification;
		return result;
	}
}
