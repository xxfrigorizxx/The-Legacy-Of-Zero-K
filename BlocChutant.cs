using Godot;

public partial class BlocChutant : RigidBody3D
{
	private const byte ID_BUISSON_PLEIN = 10;
	private const byte ID_BUISSON_VIDE = 11;
	private const byte ID_FIBRE_HERBE = 15;

	/// <summary>Crée un BlocChutant. Le parent doit l'ajouter à la scène, puis définir GlobalPosition immédiatement après.</summary>
	public static BlocChutant Creer(Vector3 positionMonde, byte idMateriau, Material matTerrain)
	{
		var bloc = new BlocChutant();
		bloc.SetMeta("ID_Matiere", (int)idMateriau);
		bloc._ConstruireVisuelEtCollision(idMateriau, matTerrain);
		// GlobalPosition nécessite is_inside_tree() == true : à définir par l'appelant après AddChild().
		return bloc;
	}

	private void _ConstruireVisuelEtCollision(byte idMateriau, Material matTerrain)
	{
		var meshInstance = new MeshInstance3D { Name = "MeshInstance3D" };

		switch (idMateriau)
		{
			case ID_BUISSON_PLEIN:
				meshInstance.Mesh = _ExtraireMeshBuisson("res://Modeles/Botanique/Buisson_Plein.glb");
				meshInstance.Scale = new Vector3(0.008f, 0.008f, 0.008f);
				break;
			case ID_BUISSON_VIDE:
				meshInstance.Mesh = _ExtraireMeshBuisson("res://Modeles/Botanique/Buisson_Vide.glb");
				meshInstance.Scale = new Vector3(0.008f, 0.008f, 0.008f);
				break;
			case ID_FIBRE_HERBE:
				{
					var matHerbe = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.55f, 0.15f), Roughness = 0.9f };
					float l = 0.38f;
					for (int i = 0; i < 4; i++)
					{
						float x = (i - 1.5f) * 0.02f; float z = (i % 2) * 0.018f - 0.009f;
						var mi = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.01f, Height = l - 0.02f }, MaterialOverride = matHerbe, Position = new Vector3(x, l * 0.5f, z), Rotation = new Vector3(0.05f * (i - 2), 0, 0.04f * (i - 1)) };
						AddChild(mi);
					}
				}
				break;
			default:
				meshInstance.Mesh = _ConstruireMeshCube(idMateriau);
				break;
		}

		// Buissons : garder le matériau du GLB. Fibre : déjà ajouté (brins). Terrain : override avec matTerrain.
		if (idMateriau != ID_BUISSON_PLEIN && idMateriau != ID_BUISSON_VIDE && idMateriau != ID_FIBRE_HERBE && matTerrain != null)
			meshInstance.MaterialOverride = (Material)matTerrain.Duplicate();
		if (idMateriau != ID_FIBRE_HERBE)
			AddChild(meshInstance);

		// Collision simple BoxShape3D — jamais ConvexPolygonShape3D (perf potato PC)
		var collision = new CollisionShape3D();
		bool estBuisson = idMateriau == ID_BUISSON_PLEIN || idMateriau == ID_BUISSON_VIDE;
		bool estFibre = idMateriau == ID_FIBRE_HERBE;
		if (estFibre)
		{
			collision.Shape = new BoxShape3D { Size = new Vector3(0.1f, 0.4f, 0.1f) };
			collision.Position = new Vector3(0.05f, 0.2f, 0.05f);
		}
		else if (estBuisson)
		{
			// Collision fixe 0.25m — indépendante du mesh géant (perf)
			float tailleCollision = 0.25f;
			collision.Shape = new BoxShape3D { Size = new Vector3(tailleCollision, tailleCollision, tailleCollision) };
			collision.Position = new Vector3(tailleCollision * 0.5f, tailleCollision * 0.5f, tailleCollision * 0.5f);
		}
		else
		{
			collision.Shape = new BoxShape3D { Size = Vector3.One };
			collision.Position = new Vector3(0.5f, 0.5f, 0.5f);
		}
		AddChild(collision);
	}

	private static Mesh _ExtraireMeshBuisson(string path)
	{
		var scene = GD.Load<PackedScene>(path);
		if (scene == null) return null;
		Node racine = scene.Instantiate();
		Mesh mesh = _ExtraireMeshRecursif(racine);
		racine.QueueFree();
		return mesh;
	}

	private static Mesh _ExtraireMeshRecursif(Node noeud)
	{
		if (noeud is MeshInstance3D mi && mi.Mesh != null) return mi.Mesh;
		foreach (Node enfant in noeud.GetChildren())
		{
			Mesh m = _ExtraireMeshRecursif(enfant);
			if (m != null) return m;
		}
		return null;
	}

	private static Mesh _ConstruireMeshCube(byte idMateriau)
	{
		Color couleurId = new Color(idMateriau / 255.0f, 0.0f, 0.0f, 1.0f);
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		Vector3[] verts = {
			new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
			new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1)
		};
		int[] indices = { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 2, 6, 7, 2, 7, 3, 0, 3, 7, 0, 7, 4, 1, 5, 6, 1, 6, 2 };
		for (int i = 0; i < indices.Length; i += 3)
		{
			Vector3 n = (verts[indices[i + 1]] - verts[indices[i]]).Cross(verts[indices[i + 2]] - verts[indices[i]]).Normalized();
			st.SetNormal(n); st.SetColor(couleurId); st.AddVertex(verts[indices[i]]);
			st.SetNormal(n); st.SetColor(couleurId); st.AddVertex(verts[indices[i + 1]]);
			st.SetNormal(n); st.SetColor(couleurId); st.AddVertex(verts[indices[i + 2]]);
		}
		st.GenerateNormals();
		return st.Commit();
	}
}
