using Godot;
using System.Collections.Generic;

/// <summary>ADN de l'objet libre : identifie ce que le Raycast du joueur ramasse.
/// 10 = Petite Pierre, 11 = Silex, 12 = Pierre Moyenne, 13 = Grosse Pierre, 14 = Très Grosse Pierre.
/// Banque d'ADN : 5 variations procédurales en cache, les objets piochent au lieu de recalculer.</summary>
public partial class ItemPhysique : Node3D
{
	[Export] public int ID_Objet = 0;

	private static readonly List<Mesh> CacheMeshCaillou = new List<Mesh>();
	private static readonly List<Shape3D> CacheCollisionCaillou = new List<Shape3D>();
	private static readonly List<Mesh> CacheMeshSilex = new List<Mesh>();
	private static readonly List<Shape3D> CacheCollisionSilex = new List<Shape3D>();
	private const int NbVariationsCache = 10;

	public override void _Ready()
	{
		Node parent = GetParent();
		MeshInstance3D visuel = null;
		CollisionShape3D hitbox = null;
		foreach (Node child in parent.GetChildren())
		{
			if (child is MeshInstance3D mi) visuel = mi;
			else if (child is CollisionShape3D cs) hitbox = cs;
		}
		if (visuel == null || hitbox == null) return;

		AppliquerMateriel(visuel);

		if (ID_Objet == 11) // SILEX
		{
			lock (CacheMeshSilex)
			{
				if (CacheMeshSilex.Count < NbVariationsCache)
					GenererEtMettreEnCache(true);
			}
			int idx = GD.RandRange(0, Mathf.Max(0, CacheMeshSilex.Count - 1));
			visuel.Mesh = CacheMeshSilex[idx];
			hitbox.Shape = CacheCollisionSilex[idx];
		}
		else // CAILLOU / PIERRES (10, 12, 13, 14)
		{
			lock (CacheMeshCaillou)
			{
				if (CacheMeshCaillou.Count < NbVariationsCache)
					GenererEtMettreEnCache(false);
			}
			int idx = GD.RandRange(0, Mathf.Max(0, CacheMeshCaillou.Count - 1));
			visuel.Mesh = CacheMeshCaillou[idx];
			hitbox.Shape = CacheCollisionCaillou[idx];

			// Mise à l'échelle selon la taille (cache = unité 0.15)
			float scale = ID_Objet == 10 ? 1f : ID_Objet == 12 ? 0.25f / 0.15f : ID_Objet == 13 ? 0.4f / 0.15f : 0.6f / 0.15f;
			if (parent is Node3D n3d)
				n3d.Scale = new Vector3(scale, scale, scale);
		}

		RotationDegrees = new Vector3(GD.RandRange(0, 360), GD.RandRange(0, 360), GD.RandRange(0, 360));
	}

	private void AppliquerMateriel(MeshInstance3D visuel)
	{
		var materiel = new StandardMaterial3D();
		if (ID_Objet == 11)
		{
			materiel.AlbedoColor = new Color(0.1f, 0.1f, 0.15f);
			materiel.Roughness = 0.4f;
			materiel.Metallic = 0.5f;
		}
		else
		{
			materiel.AlbedoColor = new Color(0.4f, 0.4f, 0.4f);
			materiel.Roughness = 0.9f;
			materiel.Metallic = 0.0f;
		}
		visuel.MaterialOverride = materiel;
	}

	private void GenererEtMettreEnCache(bool estSilex)
	{
		ArrayMesh arrayMesh;
		float forceDeformation;

		if (estSilex)
		{
			var primitive = new SphereMesh { Radius = 0.12f, Height = 0.24f }; // Forme de base sphère (déformation inchangée)
			arrayMesh = new ArrayMesh();
			arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, primitive.GetMeshArrays());
			forceDeformation = 0.3f;
		}
		else
		{
			var primitive = new SphereMesh { Radius = 0.15f, Height = 0.3f };
			arrayMesh = new ArrayMesh();
			arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, primitive.GetMeshArrays());
			forceDeformation = 0.15f;
		}

		var bruit = new FastNoiseLite();
		bruit.Seed = (int)GD.Randi();
		if (estSilex)
		{
			bruit.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
			bruit.CellularDistanceFunction = FastNoiseLite.CellularDistanceFunctionEnum.Euclidean;
			bruit.CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue;
		}
		else
			bruit.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;

		var mdt = new MeshDataTool();
		if (mdt.CreateFromSurface(arrayMesh, 0) != Error.Ok) return;

		// Génétique des proportions : vecteur d'écrasement/étirement procédural unique par modèle
		Vector3 adnMorphologique;
		if (!estSilex)
		{
			// CAILLOU : X et Z varient un peu, Y varie énormément (galette 0.3 → patate ronde 1.0)
			adnMorphologique = new Vector3(
				0.7f + (float)GD.Randf() * 0.5f,
				0.3f + (float)GD.Randf() * 0.7f,
				0.7f + (float)GD.Randf() * 0.5f
			);
		}
		else
		{
			// SILEX : étirement sur un axe pour forme de lame ou d'éclat
			adnMorphologique = new Vector3(
				0.6f + (float)GD.Randf() * 0.4f,
				0.6f + (float)GD.Randf() * 0.4f,
				1.0f + (float)GD.Randf() * 0.8f
			);
		}

		for (int i = 0; i < mdt.GetVertexCount(); i++)
		{
			Vector3 pos = mdt.GetVertex(i);
			Vector3 n = mdt.GetVertexNormal(i);
			float b = bruit.GetNoise3D(pos.X * 10f, pos.Y * 10f, pos.Z * 10f);
			Vector3 positionNouvelle = pos + (n * b * forceDeformation);
			// Écrase/étire le sommet selon l'ADN morphologique de ce modèle
			positionNouvelle.X *= adnMorphologique.X;
			positionNouvelle.Y *= adnMorphologique.Y;
			positionNouvelle.Z *= adnMorphologique.Z;
			mdt.SetVertex(i, positionNouvelle);
		}

		// Recalcul des normales (MeshDataTool n'a pas GenerateNormals) : moyenne des normales des faces adjacentes
		for (int i = 0; i < mdt.GetVertexCount(); i++)
		{
			int[] faces = mdt.GetVertexFaces(i);
			Vector3 sum = Vector3.Zero;
			foreach (int faceIdx in faces)
				sum += mdt.GetFaceNormal(faceIdx);
			if (sum.LengthSquared() > 0.0001f)
				mdt.SetVertexNormal(i, sum.Normalized());
		}

		var nouveauMesh = new ArrayMesh();
		mdt.CommitToSurface(nouveauMesh);

		// Caillou ET Silex : hitbox convexe précise qui épouse les bosses (dormance physique désirée)
		Shape3D nouvelleCollision = nouveauMesh.CreateConvexShape(true, false);

		if (estSilex)
		{
			CacheMeshSilex.Add(nouveauMesh);
			CacheCollisionSilex.Add(nouvelleCollision);
		}
		else
		{
			CacheMeshCaillou.Add(nouveauMesh);
			CacheCollisionCaillou.Add(nouvelleCollision);
		}
	}
}
