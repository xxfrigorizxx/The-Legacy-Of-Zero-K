using Godot;
using System;
using System.Collections.Generic;

/// <summary>Composition chimique d'une roche. Dicte couleur, rugosité, future résistance et point de fusion.</summary>
public struct ProfilMineral
{
	public string Nom;
	public Color CouleurBase;
	public Color CouleurVeine;
	public Color CouleurTache;
	public float Rugosite;
	public int ResistanceFuture;
}

/// <summary>ADN de l'objet libre : identifie ce que le Raycast du joueur ramasse.
/// 10 = Petite Pierre, 11 = Silex, 12 = Pierre Moyenne, 13 = Grosse Pierre, 14 = Très Grosse Pierre.
/// Hérite de RigidBody3D pour ContactMonitor / BodyEntered (physique de rupture).</summary>
public partial class ItemPhysique : RigidBody3D
{
	/// <summary>Table géologique : compositions minérales réelles (couleur, rugosité, future résistance).</summary>
	public static readonly ProfilMineral[] TableGeologique = new ProfilMineral[]
	{
		new ProfilMineral { Nom = "Granit", CouleurBase = new Color(0.4f, 0.4f, 0.4f), CouleurVeine = new Color(0.8f, 0.8f, 0.8f), CouleurTache = new Color(0.1f, 0.1f, 0.1f), Rugosite = 0.9f, ResistanceFuture = 80 },
		new ProfilMineral { Nom = "Basalte", CouleurBase = new Color(0.15f, 0.15f, 0.15f), CouleurVeine = new Color(0.1f, 0.1f, 0.1f), CouleurTache = new Color(0.05f, 0.05f, 0.05f), Rugosite = 0.95f, ResistanceFuture = 90 },
		new ProfilMineral { Nom = "Calcaire", CouleurBase = new Color(0.85f, 0.85f, 0.80f), CouleurVeine = new Color(0.9f, 0.9f, 0.85f), CouleurTache = new Color(0.7f, 0.7f, 0.6f), Rugosite = 1.0f, ResistanceFuture = 20 },
		new ProfilMineral { Nom = "Grès", CouleurBase = new Color(0.6f, 0.4f, 0.2f), CouleurVeine = new Color(0.7f, 0.5f, 0.3f), CouleurTache = new Color(0.4f, 0.2f, 0.1f), Rugosite = 0.98f, ResistanceFuture = 40 },
		new ProfilMineral { Nom = "Schiste", CouleurBase = new Color(0.3f, 0.35f, 0.35f), CouleurVeine = new Color(0.2f, 0.25f, 0.25f), CouleurTache = new Color(0.4f, 0.45f, 0.45f), Rugosite = 0.8f, ResistanceFuture = 30 },
		new ProfilMineral { Nom = "Silex", CouleurBase = new Color(0.12f, 0.12f, 0.14f), CouleurVeine = new Color(0.18f, 0.18f, 0.20f), CouleurTache = new Color(0.02f, 0.02f, 0.03f), Rugosite = 0.5f, ResistanceFuture = 85 },
		new ProfilMineral { Nom = "Quartz", CouleurBase = new Color(0.9f, 0.88f, 0.85f), CouleurVeine = new Color(0.95f, 0.95f, 0.95f), CouleurTache = new Color(0.6f, 0.55f, 0.5f), Rugosite = 0.3f, ResistanceFuture = 70 },
		new ProfilMineral { Nom = "Marbre", CouleurBase = new Color(0.85f, 0.85f, 0.9f), CouleurVeine = new Color(0.7f, 0.7f, 0.75f), CouleurTache = new Color(0.95f, 0.95f, 0.98f), Rugosite = 0.2f, ResistanceFuture = 50 },
		new ProfilMineral { Nom = "Obsidienne", CouleurBase = new Color(0.08f, 0.08f, 0.1f), CouleurVeine = new Color(0.05f, 0.05f, 0.06f), CouleurTache = new Color(0.15f, 0.15f, 0.18f), Rugosite = 0.15f, ResistanceFuture = 75 },
		new ProfilMineral { Nom = "Gneiss", CouleurBase = new Color(0.45f, 0.42f, 0.4f), CouleurVeine = new Color(0.55f, 0.5f, 0.48f), CouleurTache = new Color(0.25f, 0.22f, 0.2f), Rugosite = 0.85f, ResistanceFuture = 65 }
	};

	[Export] public int ID_Objet = 0;
	/// <summary>Sauvegarde de la forme exacte (index dans la banque d'ADN). -1 = tirage aléatoire au spawn.</summary>
	public int IndexCacheMemoire = -1;
	/// <summary>Index dans TableGeologique. -1 = non défini (tirage au spawn).</summary>
	public int IndexChimique = -1;
	/// <summary>Résistance actuelle (dégâts physiques). Initialisée depuis TableGeologique[IndexChimique].ResistanceFuture. À 0 → fracture.</summary>
	public float ResistanceActuelle { get; set; }
	/// <summary>True si cette roche est un éclat créé par fracture (créé à l'instant, jamais remis au pool ni sauvegardé).</summary>
	public bool EstEclatFracture { get; set; }
	/// <summary>Bouclier d'amnésie : si true, _Ready() n'écrase pas le maillage tranché (pas de chargement depuis le cache).</summary>
	public bool EstUnEclat = false;

	/// <summary>Banque d'ADN : accès public pour rendu en main et UI inventaire.</summary>
	public static IReadOnlyList<Mesh> CacheMeshCaillou => _cacheMeshCaillou;
	public static IReadOnlyList<Shape3D> CacheCollisionCaillou => _cacheCollisionCaillou;
	public static IReadOnlyList<Mesh> CacheMeshSilex => _cacheMeshSilex;
	public static IReadOnlyList<Shape3D> CacheCollisionSilex => _cacheCollisionSilex;

	private static readonly List<Mesh> _cacheMeshCaillou = new List<Mesh>();
	private static readonly List<Shape3D> _cacheCollisionCaillou = new List<Shape3D>();
	private static readonly List<Mesh> _cacheMeshSilex = new List<Mesh>();
	private static readonly List<Shape3D> _cacheCollisionSilex = new List<Shape3D>();
	private const int NbVariationsCache = 50;

	/// <summary>Cache des matériaux procéduraux (évite le freeze à la cassure : pas de génération 256×256 à chaque éclat).</summary>
	private static readonly Dictionary<(bool silex, int idx, bool eclat), StandardMaterial3D> _cacheMateriaux = new Dictionary<(bool, int, bool), StandardMaterial3D>();
	private const int MaxPointsContourFragment = 48;

	public override void _Ready()
	{
		// CORRECTION CRITIQUE : Chercher dans THIS, pas dans GetParent()
		MeshInstance3D visuel = null;
		CollisionShape3D hitbox = null;
		foreach (Node child in this.GetChildren())
		{
			if (child is MeshInstance3D mi) visuel = mi;
			else if (child is CollisionShape3D cs) hitbox = cs;
		}
		if (visuel == null || hitbox == null) return;

		if (IndexChimique == -1)
			IndexChimique = GD.RandRange(0, TableGeologique.Length - 1);

		// LE BOUCLIER : Si c'est un éclat coupé procéduralement, on ne génère RIEN depuis le cache.
		if (EstUnEclat) return;

		AppliquerMateriel(visuel);

		// MODIFICATION CRITIQUE : Si IndexCacheMemoire déjà défini (objet relâché par le joueur), on NE TIRE PAS au hasard.
		if (IndexCacheMemoire == -1)
			IndexCacheMemoire = ID_Objet == 11
				? PreparerCacheEtTirerIndex(true)
				: PreparerCacheEtTirerIndex(false);

		// Biomécanique : résistance aux chocs (pour physique de rupture)
		int idxChim = Mathf.Clamp(IndexChimique, 0, TableGeologique.Length - 1);
		ResistanceActuelle = TableGeologique[idxChim].ResistanceFuture;

		// Activation des senseurs de choc (Silex id 11 = immobile, pas de BodyEntered)
		if (ID_Objet != 11)
		{
			ContactMonitor = true;
			MaxContactsReported = 1;
			BodyEntered += SurImpactPhysique;
		}

		// Appliquer la forme EXACTE depuis le cache
		int idx = Mathf.Clamp(IndexCacheMemoire, 0, int.MaxValue);
		if (ID_Objet == 11)
		{
			if (idx < _cacheMeshSilex.Count)
			{
				visuel.Mesh = _cacheMeshSilex[idx];
				hitbox.Shape = _cacheCollisionSilex[idx];
			}
		}
		else
		{
			if (idx < _cacheMeshCaillou.Count)
			{
				visuel.Mesh = _cacheMeshCaillou[idx];
				hitbox.Shape = _cacheCollisionCaillou[idx];
			}
			// Mise à l'échelle selon la taille (cache = unité 0.15)
			float scale = ID_Objet == 10 ? 1f : ID_Objet == 12 ? 0.25f / 0.15f : ID_Objet == 13 ? 0.4f / 0.15f : 0.6f / 0.15f;
			Scale = new Vector3(scale, scale, scale);
		}

		RotationDegrees = new Vector3(GD.RandRange(0, 360), GD.RandRange(0, 360), GD.RandRange(0, 360));
	}

	// ----- MOTEUR DE FRACTURE (SurImpactPhysique → Fracturer → SpawnEclatVrai) -----
	/// <summary>Appelé à chaque contact physique. body peut être null (terrain PhysicsServer3D bas-niveau) → traité comme sol.</summary>
	private void SurImpactPhysique(Node body)
	{
		// 1. Détection du corps fantôme (terrain bas-niveau)
		bool frappeLeSol = (body == null);

		// 2. Calcul de l'énergie cinétique
		float velociteRelative = LinearVelocity.Length();
		if (!frappeLeSol && body is RigidBody3D rigidBody)
			velociteRelative += rigidBody.LinearVelocity.Length();

		float energieCinetique = Mass * velociteRelative;
		if (energieCinetique < 10.0f) return;

		// 3. Dureté adverse
		float dureteAdverse = 50f;
		if (frappeLeSol)
			dureteAdverse = 80f;
		else if (body is ItemPhysique autreRoche)
		{
			int idxAutre = Mathf.Clamp(autreRoche.IndexChimique, 0, TableGeologique.Length - 1);
			dureteAdverse = TableGeologique[idxAutre].ResistanceFuture;
		}

		// 4. Calcul des dégâts internes
		int idxMoi = Mathf.Clamp(IndexChimique, 0, TableGeologique.Length - 1);
		float maDurete = TableGeologique[idxMoi].ResistanceFuture;
		float degatsSubis = (energieCinetique * dureteAdverse) / Mathf.Max(0.01f, maDurete);
		ResistanceActuelle -= degatsSubis;

		if (!frappeLeSol && TableGeologique[idxMoi].Nom == "Silex" && dureteAdverse > 70f && energieCinetique > 30f)
			GenererParticulesEtincelle();

		// 5. La fracture
		if (ResistanceActuelle <= 0)
			Fracturer(null, null);
	}

	/// <summary>Appelé depuis l'extérieur (ex: frappe du joueur) pour déclencher la fracture.</summary>
	public void FracturerPublic()
	{
		Fracturer(null, null);
	}

	/// <summary>Fracture la roche avec un plan de coupe aligné sur le regard du joueur (cassure nette, face plate vers le joueur).</summary>
	/// <param name="directionVueMonde">Direction du regard (du joueur vers le point d'impact), en espace monde. Si null, plan aléatoire (choc physique).</param>
	/// <param name="pointImpactMonde">Point d'impact du raycast en espace monde. Si null, le plan passe par le centre local.</param>
	public void FracturerPublic(Vector3? directionVueMonde, Vector3? pointImpactMonde)
	{
		Fracturer(directionVueMonde, pointImpactMonde);
	}

	private void GenererParticulesEtincelle()
	{
		// Placeholder : étincelles (GPUParticles3D ou effet visuel). À brancher sur un asset si besoin.
	}

	private void Fracturer(Vector3? directionVueMonde, Vector3? pointImpactMonde)
	{
		MeshInstance3D monVisuel = null;
		foreach (Node child in this.GetChildren())
		{
			if (child is MeshInstance3D mi) { monVisuel = mi; break; }
		}
		if (monVisuel == null || monVisuel.Mesh == null)
		{
			GD.PrintErr("FRACTURE ÉCHOUÉE : Aucun MeshInstance3D trouvé !");
			QueueFree();
			return;
		}

		Vector3 normaleCoupe;
		if (directionVueMonde.HasValue && directionVueMonde.Value.LengthSquared() > 0.01f)
			normaleCoupe = (GlobalTransform.Basis.Inverse() * directionVueMonde.Value).Normalized();
		else
			normaleCoupe = new Vector3((float)GD.Randf() - 0.5f, (float)GD.Randf() - 0.5f, (float)GD.Randf() - 0.5f).Normalized();

		Vector3 pointSurLePlanLocal = pointImpactMonde.HasValue ? GlobalTransform.AffineInverse() * pointImpactMonde.Value : Vector3.Zero;
		Plane planCoupe = new Plane(normaleCoupe, -normaleCoupe.Dot(pointSurLePlanLocal));

		Vector3 impactPos = pointImpactMonde.HasValue ? pointImpactMonde.Value : (GlobalPosition + Vector3.Up * 0.1f);
		Vector3 normalMonde = GlobalTransform.Basis * normaleCoupe;
		float masseFragment = Mass * 0.5f;

		// Découpe de la roche exacte au plan : 2 moitiés avec la texture d'origine (modèles temporaires jusqu'à destruction)
		Material matRoche = monVisuel.MaterialOverride ?? (monVisuel.Mesh.GetSurfaceCount() > 0 ? monVisuel.Mesh.SurfaceGetMaterial(0) : null);
		if (matRoche != null && monVisuel.Mesh is ArrayMesh arrMesh && DecouperMeshEtSpawnerMoities(arrMesh, planCoupe, impactPos, normalMonde, masseFragment, matRoche))
		{
			if (ID_Objet != 11)
				BodyEntered -= SurImpactPhysique;
			QueueFree();
			return;
		}

		// Fallback : méthode par contour (si découpe mesh échoue)
		Vector3[] sommetsActuels = monVisuel.Mesh.GetFaces();
		if (sommetsActuels == null || sommetsActuels.Length == 0) { QueueFree(); return; }
		var ptsA = new List<Vector3>();
		var ptsB = new List<Vector3>();
		foreach (Vector3 pt in sommetsActuels)
		{
			float dist = planCoupe.DistanceTo(pt);
			if (dist > 0) ptsA.Add(pt); else ptsB.Add(pt);
			if (Mathf.Abs(dist) < 0.1f) { Vector3 proj = planCoupe.Project(pt); ptsA.Add(proj); ptsB.Add(proj); }
		}
		if (ptsA.Count > MaxPointsContourFragment) ReduirePointsContour(ptsA, MaxPointsContourFragment);
		if (ptsB.Count > MaxPointsContourFragment) ReduirePointsContour(ptsB, MaxPointsContourFragment);
		Vector3[] facesA = ptsA.Count >= 4 ? OrdonnerPointsDansPlan(ptsA, planCoupe) : PointsFallbackFragment(planCoupe, 1);
		Vector3[] facesB = ptsB.Count >= 4 ? OrdonnerPointsDansPlan(ptsB, planCoupe) : PointsFallbackFragment(planCoupe, -1);
		SpawnEclatVrai(facesA, masseFragment, impactPos + (normalMonde * 0.03f), normaleCoupe);
		SpawnEclatVrai(facesB, masseFragment, impactPos - (normalMonde * 0.03f), -normaleCoupe);
		if (ID_Objet != 11) BodyEntered -= SurImpactPhysique;
		QueueFree();
	}

	/// <summary>Découpe le mesh de la roche au plan et crée 2 moitiés (même texture, modèles temporaires). Retourne true si succès.</summary>
	private bool DecouperMeshEtSpawnerMoities(ArrayMesh mesh, Plane plan, Vector3 impactPos, Vector3 normalMonde, float masseFragment, Material matRoche)
	{
		if (mesh == null || mesh.GetSurfaceCount() == 0) return false;
		var mdt = new MeshDataTool();
		if (mdt.CreateFromSurface(mesh, 0) != Error.Ok) return false;

		var trisA = new List<(Vector3 a, Vector3 b, Vector3 c, Vector2 uva, Vector2 uvb, Vector2 uvc, Vector3 na, Vector3 nb, Vector3 nc)>();
		var trisB = new List<(Vector3 a, Vector3 b, Vector3 c, Vector2 uva, Vector2 uvb, Vector2 uvc, Vector3 na, Vector3 nb, Vector3 nc)>();
		var capA = new List<Vector3>();
		var capB = new List<Vector3>();

		for (int f = 0; f < mdt.GetFaceCount(); f++)
		{
			int i0 = mdt.GetFaceVertex(f, 0), i1 = mdt.GetFaceVertex(f, 1), i2 = mdt.GetFaceVertex(f, 2);
			Vector3 v0 = mdt.GetVertex(i0), v1 = mdt.GetVertex(i1), v2 = mdt.GetVertex(i2);
			Vector2 uv0 = mdt.GetVertexUV(i0), uv1 = mdt.GetVertexUV(i1), uv2 = mdt.GetVertexUV(i2);
			Vector3 n0 = mdt.GetVertexNormal(i0), n1 = mdt.GetVertexNormal(i1), n2 = mdt.GetVertexNormal(i2);
			DecouperTriangle(plan, v0, v1, v2, uv0, uv1, uv2, n0, n1, n2, trisA, trisB, capA, capB);
		}

		if (trisA.Count == 0 || trisB.Count == 0) return false;

		ArrayMesh meshA = ConstruireMeshMoitie(trisA, capA, plan, 1f);
		ArrayMesh meshB = ConstruireMeshMoitie(trisB, capB, plan, -1f);
		if (meshA.GetFaces().Length == 0 || meshB.GetFaces().Length == 0) return false;

		// Face de cassure : un côté transparent (intérieur), l'autre noir (cassure)
		meshA.SurfaceSetMaterial(0, matRoche);
		if (meshA.GetSurfaceCount() > 1) meshA.SurfaceSetMaterial(1, MatCapTransparent());
		meshB.SurfaceSetMaterial(0, matRoche);
		if (meshB.GetSurfaceCount() > 1) meshB.SurfaceSetMaterial(1, MatCapNoir());

		SpawnMoitieRoche(meshA, impactPos + normalMonde * 0.02f, normalMonde, masseFragment);
		SpawnMoitieRoche(meshB, impactPos - normalMonde * 0.02f, -normalMonde, masseFragment);
		return true;
	}

	private static void DecouperTriangle(Plane plan, Vector3 v0, Vector3 v1, Vector3 v2, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 n0, Vector3 n1, Vector3 n2,
		List<(Vector3, Vector3, Vector3, Vector2, Vector2, Vector2, Vector3, Vector3, Vector3)> trisA,
		List<(Vector3, Vector3, Vector3, Vector2, Vector2, Vector2, Vector3, Vector3, Vector3)> trisB,
		List<Vector3> capA, List<Vector3> capB)
	{
		float d0 = plan.DistanceTo(v0), d1 = plan.DistanceTo(v1), d2 = plan.DistanceTo(v2);
		const float eps = 0.0001f;
		if (d0 >= -eps && d1 >= -eps && d2 >= -eps) { trisA.Add((v0, v1, v2, uv0, uv1, uv2, n0, n1, n2)); return; }
		if (d0 <= eps && d1 <= eps && d2 <= eps) { trisB.Add((v0, v1, v2, uv0, uv1, uv2, n0, n1, n2)); return; }
		float t02 = Mathf.Abs(d0 - d2) < eps ? 0.5f : d0 / (d0 - d2);
		float t12 = Mathf.Abs(d1 - d2) < eps ? 0.5f : d1 / (d1 - d2);
		float t01 = Mathf.Abs(d0 - d1) < eps ? 0.5f : d0 / (d0 - d1);
		Vector3 p01 = v0 + t01 * (v1 - v0); Vector2 uv01 = uv0 + t01 * (uv1 - uv0); Vector3 n01 = (n0 + t01 * (n1 - n0)).Normalized();
		Vector3 p02 = v0 + t02 * (v2 - v0); Vector2 uv02 = uv0 + t02 * (uv2 - uv0); Vector3 n02 = (n0 + t02 * (n2 - n0)).Normalized();
		Vector3 p12 = v1 + t12 * (v2 - v1); Vector2 uv12 = uv1 + t12 * (uv2 - uv1); Vector3 n12 = (n1 + t12 * (n2 - n1)).Normalized();
		// v0,v1 côté A, v2 côté B → intersections 0-2 et 1-2
		if (d0 >= -eps && d1 >= -eps)
		{
			trisA.Add((v0, v1, p12, uv0, uv1, uv12, n0, n1, n12)); trisA.Add((v0, p12, p02, uv0, uv12, uv02, n0, n12, n02));
			trisB.Add((v2, p02, p12, uv2, uv02, uv12, n2, n02, n12));
			capA.Add(p02); capA.Add(p12); capB.Add(p02); capB.Add(p12);
		}
		// v0,v2 côté A, v1 côté B → intersections 0-1 et 1-2
		else if (d0 >= -eps && d2 >= -eps)
		{
			trisA.Add((v0, p02, p01, uv0, uv02, uv01, n0, n02, n01)); trisA.Add((v0, p01, v2, uv0, uv01, uv2, n0, n01, n2));
			trisB.Add((v1, p12, p01, uv1, uv12, uv01, n1, n12, n01));
			capA.Add(p01); capA.Add(p02); capB.Add(p01); capB.Add(p12);
		}
		// v1,v2 côté A, v0 côté B → intersections 0-1 et 0-2
		else if (d1 >= -eps && d2 >= -eps)
		{
			trisA.Add((v1, p12, p01, uv1, uv12, uv01, n1, n12, n01)); trisA.Add((v1, p01, v2, uv1, uv01, uv2, n1, n01, n2));
			trisB.Add((v0, p02, p01, uv0, uv02, uv01, n0, n02, n01));
			capA.Add(p01); capA.Add(p12); capB.Add(p01); capB.Add(p02);
		}
		// v0,v1 côté B, v2 côté A
		else if (d0 <= eps && d1 <= eps)
		{
			trisB.Add((v0, v1, p12, uv0, uv1, uv12, n0, n1, n12)); trisB.Add((v0, p12, p02, uv0, uv12, uv02, n0, n12, n02));
			trisA.Add((v2, p02, p12, uv2, uv02, uv12, n2, n02, n12));
			capB.Add(p02); capB.Add(p12); capA.Add(p02); capA.Add(p12);
		}
		// v0,v2 côté B, v1 côté A
		else if (d0 <= eps && d2 <= eps)
		{
			trisB.Add((v0, p02, p01, uv0, uv02, uv01, n0, n02, n01)); trisB.Add((v0, p01, v2, uv0, uv01, uv2, n0, n01, n2));
			trisA.Add((v1, p12, p01, uv1, uv12, uv01, n1, n12, n01));
			capB.Add(p01); capB.Add(p02); capA.Add(p01); capA.Add(p12);
		}
		// v1,v2 côté B, v0 côté A
		else
		{
			trisB.Add((v1, p12, p01, uv1, uv12, uv01, n1, n12, n01)); trisB.Add((v1, p01, v2, uv1, uv01, uv2, n1, n01, n2));
			trisA.Add((v0, p02, p01, uv0, uv02, uv01, n0, n02, n01));
			capB.Add(p01); capB.Add(p12); capA.Add(p01); capA.Add(p02);
		}
	}

	/// <summary>Surface 0 = roche, surface 1 = face de cassure (cap). Les matériaux cap sont appliqués après (transparent / noir).</summary>
	private static ArrayMesh ConstruireMeshMoitie(
		List<(Vector3 a, Vector3 b, Vector3 c, Vector2 uva, Vector2 uvb, Vector2 uvc, Vector3 na, Vector3 nb, Vector3 nc)> tris,
		List<Vector3> cap, Plane plan, float signeNormaleCap)
	{
		var mesh = new ArrayMesh();
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		foreach (var t in tris)
		{
			st.SetNormal(t.na); st.SetUV(t.uva); st.AddVertex(t.a);
			st.SetNormal(t.nb); st.SetUV(t.uvb); st.AddVertex(t.b);
			st.SetNormal(t.nc); st.SetUV(t.uvc); st.AddVertex(t.c);
		}
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, st.CommitToArrays());

		if (cap.Count >= 3)
		{
			Vector3 centrePlan = -plan.D * plan.Normal;
			Vector3 u = plan.Normal.Cross(Vector3.Up).Normalized();
			if (u.LengthSquared() < 0.01f) u = plan.Normal.Cross(Vector3.Right).Normalized();
			Vector3 v = plan.Normal.Cross(u).Normalized();
			Vector2 centre2D = Vector2.Zero;
			foreach (Vector3 p in cap) { Vector3 d = p - centrePlan; centre2D += new Vector2(d.Dot(u), d.Dot(v)); }
			centre2D /= cap.Count;
			var ordre = new List<int>();
			for (int i = 0; i < cap.Count; i++) ordre.Add(i);
			ordre.Sort((i, j) => {
				Vector3 di = cap[i] - centrePlan, dj = cap[j] - centrePlan;
				float ai = Mathf.Atan2(di.Dot(v) - centre2D.Y, di.Dot(u) - centre2D.X);
				float aj = Mathf.Atan2(dj.Dot(v) - centre2D.Y, dj.Dot(u) - centre2D.X);
				return ai.CompareTo(aj);
			});
			var capOrdre = new Vector3[cap.Count];
			for (int i = 0; i < cap.Count; i++) capOrdre[i] = cap[ordre[i]];
			var pts2D = new Vector2[capOrdre.Length];
			for (int i = 0; i < capOrdre.Length; i++) { Vector3 d = capOrdre[i] - centrePlan; pts2D[i] = new Vector2(d.Dot(u), d.Dot(v)); }
			int[] ind = Geometry2D.TriangulatePolygon(pts2D);
			Vector3 nCap = plan.Normal * signeNormaleCap;
			st.Clear();
			st.Begin(Mesh.PrimitiveType.Triangles);
			if (ind != null && ind.Length >= 3)
				for (int t = 0; t + 2 < ind.Length; t += 3)
				{
					Vector3 pa = capOrdre[ind[t]], pb = capOrdre[ind[t + 1]], pc = capOrdre[ind[t + 2]];
					st.SetNormal(nCap); st.SetUV(new Vector2(0.5f, 0.5f)); st.AddVertex(pa);
					st.SetNormal(nCap); st.SetUV(new Vector2(0.5f, 0.5f)); st.AddVertex(pb);
					st.SetNormal(nCap); st.SetUV(new Vector2(0.5f, 0.5f)); st.AddVertex(pc);
				}
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, st.CommitToArrays());
		}
		return mesh;
	}

	private static StandardMaterial3D _matCapTransparent;
	private static StandardMaterial3D _matCapNoir;
	private static StandardMaterial3D MatCapTransparent()
	{
		if (_matCapTransparent != null) return _matCapTransparent;
		_matCapTransparent = new StandardMaterial3D();
		_matCapTransparent.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
		_matCapTransparent.AlbedoColor = new Color(1, 1, 1, 0f);
		_matCapTransparent.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
		return _matCapTransparent;
	}
	private static StandardMaterial3D MatCapNoir()
	{
		if (_matCapNoir != null) return _matCapNoir;
		_matCapNoir = new StandardMaterial3D();
		_matCapNoir.AlbedoColor = new Color(0.05f, 0.05f, 0.05f);
		_matCapNoir.Roughness = 1f;
		return _matCapNoir;
	}

	/// <summary>Spawn une moitié de roche (modèle temporaire). Matériaux déjà assignés par surface sur le mesh (roche + cap transparent/noir).</summary>
	private void SpawnMoitieRoche(ArrayMesh meshMoitie, Vector3 positionInitiale, Vector3 directionImpulsion, float nouvelleMasse)
	{
		Shape3D shape = meshMoitie.CreateConvexShape(true, false);
		if (shape == null) shape = new BoxShape3D { Size = new Vector3(0.08f, 0.08f, 0.08f) };
		ItemPhysique moitie = new ItemPhysique();
		moitie.EstUnEclat = true;
		moitie.EstEclatFracture = true;
		moitie.ID_Objet = ID_Objet;
		moitie.IndexChimique = IndexChimique;
		moitie.Mass = nouvelleMasse;
		int idxCh = Mathf.Clamp(IndexChimique, 0, TableGeologique.Length - 1);
		moitie.ResistanceActuelle = TableGeologique[idxCh].ResistanceFuture * (nouvelleMasse / 50f);
		moitie.ContinuousCd = true;
		moitie.Scale = Scale;

		MeshInstance3D visuel = new MeshInstance3D();
		visuel.Name = "MeshInstance3D";
		visuel.Mesh = meshMoitie;
		visuel.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
		moitie.AddChild(visuel);

		CollisionShape3D hitbox = new CollisionShape3D();
		hitbox.Name = "CollisionShape3D";
		hitbox.Shape = shape;
		moitie.AddChild(hitbox);

		if (moitie.ID_Objet != 11) { moitie.ContactMonitor = true; moitie.MaxContactsReported = 1; moitie.BodyEntered += moitie.SurImpactPhysique; }
		moitie.Name = "ItemPhysique";
		moitie.AddToGroup("BlocsPoses");
		moitie.SetMeta("ID_Matiere", moitie.ID_Objet);
		moitie.SetMeta("spawn_pos", positionInitiale);
		moitie.SetMeta("spawn_impulse", directionImpulsion * 0.8f + new Vector3((float)GD.Randf() - 0.5f, 0.3f, (float)GD.Randf() - 0.5f));
		moitie.TreeEntered += () => AppliquerSpawnEclat(moitie);
		GetParent().AddChild(moitie);
	}

	/// <summary>Ordonne les points dans le plan de coupe (angle autour du centre) pour former un vrai contour asymétrique, pas un éventail.</summary>
	private static Vector3[] OrdonnerPointsDansPlan(List<Vector3> points, Plane plan)
	{
		if (points == null || points.Count < 3) return points?.ToArray() ?? System.Array.Empty<Vector3>();
		Vector3 centrePlan = -plan.D * plan.Normal;
		Vector3 u = plan.Normal.Cross(Vector3.Up).Normalized();
		if (u.LengthSquared() < 0.01f) u = plan.Normal.Cross(Vector3.Right).Normalized();
		Vector3 v = plan.Normal.Cross(u).Normalized();
		var withAngle = new List<(Vector3 p3, float angle)>();
		Vector2 sum2D = Vector2.Zero;
		foreach (Vector3 pt in points)
		{
			Vector3 onPlan = plan.Project(pt);
			Vector3 d = onPlan - centrePlan;
			float xu = d.Dot(u);
			float xv = d.Dot(v);
			sum2D += new Vector2(xu, xv);
			withAngle.Add((onPlan, 0f));
		}
		Vector2 centre2D = sum2D / points.Count;
		for (int i = 0; i < points.Count; i++)
		{
			Vector3 onPlan = withAngle[i].p3;
			Vector3 d = onPlan - centrePlan;
			float angle = Mathf.Atan2(d.Dot(v) - centre2D.Y, d.Dot(u) - centre2D.X);
			withAngle[i] = (onPlan, angle);
		}
		withAngle.Sort((a, b) => a.angle.CompareTo(b.angle));
		var result = new Vector3[withAngle.Count];
		for (int i = 0; i < withAngle.Count; i++) result[i] = withAngle[i].p3;
		return result;
	}

	/// <summary>Quatre points d'un petit quad d'un côté du plan (fallback, plan = cassure passant par l'impact).</summary>
	private static Vector3[] PointsFallbackFragment(Plane plan, int cote)
	{
		Vector3 centrePlan = -plan.D * plan.Normal; // point sur le plan (cassure)
		float d = cote > 0 ? 0.12f : -0.12f;
		Vector3 n = plan.Normal;
		Vector3 u = n.Cross(Vector3.Up).Normalized();
		if (u.LengthSquared() < 0.01f) u = n.Cross(Vector3.Right).Normalized();
		Vector3 v = n.Cross(u).Normalized();
		float s = 0.05f;
		Vector3 basePt = centrePlan + d * n;
		return new Vector3[] { basePt, basePt + s * u, basePt + s * (u + v), basePt + s * v };
	}

	/// <summary>Reconstruit un éclat à partir des points d'une moitié. normaleDeCoupe = normale de la face de cassure (espace local) pour éclater l'effet éventail.</summary>
	private void SpawnEclatVrai(Vector3[] pointsFragment, float nouvelleMasse, Vector3 positionInitiale, Vector3 normaleDeCoupe)
	{
		if (pointsFragment == null || pointsFragment.Length < 4) return;

		// Centre et base UV = plan de cassure (texture alignée sur les angles du fragment)
		Vector3 centre = Vector3.Zero;
		foreach (Vector3 p in pointsFragment) centre += p;
		centre /= pointsFragment.Length;

		// Normale moyenne du fragment (surface de cassure) + repère tangent pour les UV
		Vector3 normalPlan = Vector3.Zero;
		int n = pointsFragment.Length;
		for (int i = 0; i < n; i++)
		{
			Vector3 v1 = pointsFragment[i];
			Vector3 v2 = pointsFragment[(i + 1) % n];
			normalPlan += (v1 - centre).Cross(v2 - centre);
		}
		if (normalPlan.LengthSquared() < 0.0001f) normalPlan = Vector3.Up;
		normalPlan = normalPlan.Normalized();
		Vector3 tangentU = normalPlan.Cross(Vector3.Up).Normalized();
		if (tangentU.LengthSquared() < 0.01f) tangentU = normalPlan.Cross(Vector3.Right).Normalized();
		Vector3 tangentV = normalPlan.Cross(tangentU).Normalized();
		float maxExtent = 0.01f;
		foreach (Vector3 p in pointsFragment) maxExtent = Mathf.Max(maxExtent, (p - centre).Length());
		float scaleUV = 0.35f / maxExtent;
		// Épaisseur type "caillou" : volume 3D au lieu d'une feuille plate
		float epaisseur = Mathf.Min(0.06f, maxExtent * 0.5f);

		// Triangulation du polygone
		var points2D = new Vector2[pointsFragment.Length];
		for (int i = 0; i < pointsFragment.Length; i++)
		{
			Vector3 d = pointsFragment[i] - centre;
			points2D[i] = new Vector2(d.Dot(tangentU), d.Dot(tangentV));
		}
		int[] indices = Geometry2D.TriangulatePolygon(points2D);

		// Sommets face arrière (extrusion pour donner du volume = caillou, pas papier)
		Vector3[] pointsArriere = new Vector3[n];
		for (int i = 0; i < n; i++)
			pointsArriere[i] = pointsFragment[i] - normalPlan * epaisseur;

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		void AddTri(Vector3 a, Vector3 b, Vector3 c, Vector3 norm, Vector3 cen)
		{
			Vector3 cr = (b - a).Cross(c - a);
			if (cr.LengthSquared() < 0.0001f) return;
			Vector3 n = cr.Normalized();
			if (Mathf.Abs(n.Dot(normaleDeCoupe)) > 0.9f) n = normalPlan * Mathf.Sign(n.Dot(normalPlan));
			st.SetUV(UVPlanCassure(cen, a, normalPlan, tangentU, tangentV, scaleUV)); st.SetNormal(n); st.AddVertex(a);
			st.SetUV(UVPlanCassure(cen, b, normalPlan, tangentU, tangentV, scaleUV)); st.SetNormal(n); st.AddVertex(b);
			st.SetUV(UVPlanCassure(cen, c, normalPlan, tangentU, tangentV, scaleUV)); st.SetNormal(n); st.AddVertex(c);
		}

		if (indices != null && indices.Length >= 3)
		{
			// Face avant (cassure)
			for (int t = 0; t + 2 < indices.Length; t += 3)
			{
				int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
				AddTri(pointsFragment[i0], pointsFragment[i1], pointsFragment[i2], normalPlan, centre);
			}
			// Face arrière (sens inverse pour que la normale pointe vers l'extérieur)
			for (int t = 0; t + 2 < indices.Length; t += 3)
			{
				int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
				AddTri(pointsArriere[i0], pointsArriere[i2], pointsArriere[i1], -normalPlan, centre);
			}
			// Bords (quads entre face avant et arrière) = tranche du caillou
			for (int i = 0; i < n; i++)
			{
				int j = (i + 1) % n;
				Vector3 nBord = (pointsFragment[j] - pointsFragment[i]).Cross(pointsArriere[i] - pointsFragment[i]).Normalized();
				if (nBord.LengthSquared() < 0.01f) continue;
				AddTri(pointsFragment[i], pointsFragment[j], pointsArriere[j], nBord, centre);
				AddTri(pointsFragment[i], pointsArriere[j], pointsArriere[i], nBord, centre);
			}
		}
		else
		{
			// Fallback triangle fan + extrusion
			for (int i = 0; i < n; i++)
			{
				Vector3 v0 = centre, v1 = pointsFragment[i], v2 = pointsFragment[(i + 1) % n];
				Vector3 cAr = centre - normalPlan * epaisseur;
				Vector3 ar1 = pointsArriere[i], ar2 = pointsArriere[(i + 1) % n];
				AddTri(v0, v1, v2, normalPlan, centre);
				AddTri(cAr, ar2, ar1, -normalPlan, cAr);
				AddTri(v1, v2, ar2, (v2 - v1).Cross(ar1 - v1).Normalized(), centre);
				AddTri(v1, ar2, ar1, (v2 - v1).Cross(ar1 - v1).Normalized(), centre);
			}
		}
		ArrayMesh meshFragment = st.Commit();
		// Fallback : si la triangulation n'a rien donné, mesh minimal pour que le fragment apparaisse à l'écran
		if (meshFragment.GetFaces().Length == 0)
			meshFragment = CreerMeshFallbackFragment(centre, normalPlan, tangentU, tangentV);

		ItemPhysique eclat = new ItemPhysique();
		eclat.EstUnEclat = true;
		eclat.EstEclatFracture = true;
		eclat.ID_Objet = ID_Objet;
		eclat.IndexChimique = IndexChimique;
		eclat.Mass = nouvelleMasse;
		int idxCh = Mathf.Clamp(IndexChimique, 0, TableGeologique.Length - 1);
		eclat.ResistanceActuelle = TableGeologique[idxCh].ResistanceFuture * (nouvelleMasse / 50f);
		eclat.ContinuousCd = true;

		bool estSilex = (ID_Objet == 11);
		Shape3D shapeCollision = meshFragment.CreateConvexShape(true, false);
		if (shapeCollision == null)
			shapeCollision = new BoxShape3D { Size = new Vector3(0.08f, 0.08f, 0.08f) };
		eclat.Scale = Scale;

		MeshInstance3D visuel = new MeshInstance3D();
		visuel.Name = "MeshInstance3D";
		visuel.Mesh = meshFragment;
		visuel.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
		visuel.MaterialOverride = CreerMaterielProcedural(estSilex, IndexChimique, pourEclat: true);
		eclat.AddChild(visuel);

		CollisionShape3D hitbox = new CollisionShape3D();
		hitbox.Name = "CollisionShape3D";
		hitbox.Shape = shapeCollision;
		eclat.AddChild(hitbox);

		if (eclat.ID_Objet != 11)
		{
			eclat.ContactMonitor = true;
			eclat.MaxContactsReported = 1;
			eclat.BodyEntered += eclat.SurImpactPhysique;
		}
		eclat.Name = "ItemPhysique";
		eclat.AddToGroup("BlocsPoses");
		eclat.SetMeta("ID_Matiere", eclat.ID_Objet);

		// Position et impulsion stockées pour appliquer une fois dans l'arbre (évite problèmes d'affichage)
		eclat.SetMeta("spawn_pos", positionInitiale);
		Vector3 explosion = new Vector3((float)GD.Randf() - 0.5f, 0.8f, (float)GD.Randf() - 0.5f).Normalized();
		eclat.SetMeta("spawn_impulse", explosion * 1.0f);
		eclat.TreeEntered += () => AppliquerSpawnEclat(eclat);

		GetParent().AddChild(eclat);
	}

	/// <summary>Applique position et impulsion une fois l'éclat dans l'arbre (fragment bien visible à l'écran).</summary>
	private static void AppliquerSpawnEclat(ItemPhysique eclat)
	{
		if (!eclat.HasMeta("spawn_pos")) return;
		eclat.GlobalPosition = (Vector3)eclat.GetMeta("spawn_pos");
		eclat.RemoveMeta("spawn_pos");
		if (eclat.HasMeta("spawn_impulse"))
		{
			eclat.ApplyCentralImpulse((Vector3)eclat.GetMeta("spawn_impulse"));
			eclat.RemoveMeta("spawn_impulse");
		}
	}

	/// <summary>Quad épais minimal si la triangulation échoue (le fragment apparaît quand même).</summary>
	private static ArrayMesh CreerMeshFallbackFragment(Vector3 centre, Vector3 normalPlan, Vector3 tangentU, Vector3 tangentV)
	{
		float s = 0.06f;
		float e = 0.03f;
		Vector3 ar = centre - normalPlan * e;
		Vector3 v0 = centre + s * tangentU + s * tangentV;
		Vector3 v1 = centre + s * tangentU - s * tangentV;
		Vector3 v2 = centre - s * tangentU - s * tangentV;
		Vector3 v3 = centre - s * tangentU + s * tangentV;
		Vector3 a0 = ar + s * tangentU + s * tangentV;
		Vector3 a1 = ar + s * tangentU - s * tangentV;
		Vector3 a2 = ar - s * tangentU - s * tangentV;
		Vector3 a3 = ar - s * tangentU + s * tangentV;
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		Action<Vector3, Vector3, Vector3, Vector3> tri = (a, b, c, n) => {
			st.SetNormal(n); st.AddVertex(a); st.SetNormal(n); st.AddVertex(b); st.SetNormal(n); st.AddVertex(c);
		};
		tri(v0, v1, v2, normalPlan); tri(v0, v2, v3, normalPlan);
		tri(a0, a2, a1, -normalPlan); tri(a0, a3, a2, -normalPlan);
		tri(v0, v3, a3, tangentU); tri(v0, a3, a0, tangentU);
		tri(v1, v0, a0, tangentV); tri(v1, a0, a1, tangentV);
		tri(v2, v1, a1, -tangentU); tri(v2, a1, a2, -tangentU);
		tri(v3, v2, a2, -tangentV); tri(v3, a2, a3, -tangentV);
		return st.Commit();
	}

	/// <summary>Réapplique mesh/collision/matériau après réutilisation depuis un pool (ID_Objet ou IndexCache/Chimique changés).</summary>
	public void ReappliquerApparence()
	{
		MeshInstance3D visuel = null;
		CollisionShape3D hitbox = null;
		foreach (Node child in GetChildren())
		{
			if (child is MeshInstance3D mi) visuel = mi;
			else if (child is CollisionShape3D cs) hitbox = cs;
		}
		if (visuel == null || hitbox == null) return;
		if (IndexChimique < 0) IndexChimique = GD.RandRange(0, TableGeologique.Length - 1);
		AppliquerMateriel(visuel);
		if (IndexCacheMemoire < 0)
		{
			bool formesCassées = (IndexCacheMemoire == -2);
			IndexCacheMemoire = ID_Objet == 11 ? PreparerCacheEtTirerIndex(true, formesCassées) : PreparerCacheEtTirerIndex(false, formesCassées);
		}
		int idx = Mathf.Clamp(IndexCacheMemoire, 0, int.MaxValue);
		if (ID_Objet == 11)
		{
			if (idx < _cacheMeshSilex.Count) { visuel.Mesh = _cacheMeshSilex[idx]; hitbox.Shape = _cacheCollisionSilex[idx]; }
		}
		else
		{
			if (idx < _cacheMeshCaillou.Count) { visuel.Mesh = _cacheMeshCaillou[idx]; hitbox.Shape = _cacheCollisionCaillou[idx]; }
			float scale = ID_Objet == 10 ? 1f : ID_Objet == 12 ? 0.25f / 0.15f : ID_Objet == 13 ? 0.4f / 0.15f : 0.6f / 0.15f;
			Scale = new Vector3(scale, scale, scale);
		}
	}

	private int PreparerCacheEtTirerIndex(bool estSilex, bool formesCassées = false)
	{
		if (estSilex)
		{
			lock (_cacheMeshSilex)
			{
				if (_cacheMeshSilex.Count < NbVariationsCache)
					GenererEtMettreEnCache(true);
				int count = _cacheMeshSilex.Count;
				if (count == 0) return 0;
				if (formesCassées && count > 1) return GD.RandRange(count / 2, count - 1);
				return GD.RandRange(0, Mathf.Max(0, (count / 2) - 1));
			}
		}
		lock (_cacheMeshCaillou)
		{
			if (_cacheMeshCaillou.Count < NbVariationsCache)
				GenererEtMettreEnCache(false);
			int count = _cacheMeshCaillou.Count;
			if (count == 0) return 0;
			if (formesCassées && count > 1) return GD.RandRange(count / 2, count - 1);
			return GD.RandRange(0, Mathf.Max(0, (count / 2) - 1));
		}
	}

	private void AppliquerMateriel(MeshInstance3D visuel)
	{
		visuel.MaterialOverride = CreerMaterielProcedural(ID_Objet == 11, IndexChimique);
	}

	/// <summary>Retourne le mesh du premier MeshInstance3D enfant (pour éclats et ramassage).</summary>
	public Mesh ObtenirMeshVisuel()
	{
		foreach (Node c in GetChildren())
			if (c is MeshInstance3D mi) return mi.Mesh;
		return null;
	}

	/// <summary>Matériau procédural basé sur la chimie réelle (TableGeologique). Taches, veines, rugosité. Mis en cache pour éviter le freeze à la cassure.</summary>
	/// <param name="pourEclat">Si true, désactive le triplanar et utilise les UV du mesh (évite l'effet "pizza" sur les fragments).</param>
	public static StandardMaterial3D CreerMaterielProcedural(bool estSilex, int indexChimique, bool pourEclat = false)
	{
		int idx = Mathf.Clamp(indexChimique, 0, TableGeologique.Length - 1);
		var key = (estSilex, idx, pourEclat);
		lock (_cacheMateriaux)
		{
			if (_cacheMateriaux.TryGetValue(key, out StandardMaterial3D cached))
				return cached;
		}
		var materiel = new StandardMaterial3D();
		// Seed déterministe par minéral : roche et ses éclats ont la même apparence (même type de pierre)
		int seedCouleur = 50000 + idx * 7919;
		int seedRelief = 60000 + idx * 7919;
		var bruitRelief = new FastNoiseLite { Seed = seedRelief };

		ProfilMineral chimie = TableGeologique[idx];
		materiel.Roughness = chimie.Rugosite;

		// 1. Pigmentation : même texture pour roches et éclats (taches, veines)
		var bruitCouleur = new FastNoiseLite
		{
			Seed = seedCouleur,
			NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
			Frequency = 0.03f,
			FractalType = FastNoiseLite.FractalTypeEnum.Fbm
		};
		var textureCouleur = new NoiseTexture2D { Width = 256, Height = 256, Noise = bruitCouleur };
		var degradeMineral = new Gradient();
		degradeMineral.AddPoint(0f, chimie.CouleurTache);
		degradeMineral.AddPoint(0.5f, chimie.CouleurBase);
		degradeMineral.AddPoint(1f, chimie.CouleurVeine);
		textureCouleur.ColorRamp = degradeMineral;
		materiel.AlbedoTexture = textureCouleur;

		// 2. Micro-relief : même que les roches (éclats = même apparence, forme cassée uniquement)
		var textureRelief = new NoiseTexture2D { Width = 256, Height = 256, GenerateMipmaps = true, AsNormalMap = true };
		if (estSilex)
		{
			materiel.Metallic = 0.2f;
			bruitRelief.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
			bruitRelief.Frequency = 0.08f;
			textureRelief.BumpStrength = 3.0f;
		}
		else
		{
			materiel.Metallic = 0.0f;
			bruitRelief.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
			bruitRelief.Frequency = 0.15f;
			textureRelief.BumpStrength = 1.5f;
		}
		textureRelief.Noise = bruitRelief;
		materiel.NormalEnabled = true;
		materiel.NormalTexture = textureRelief;
		if (!pourEclat)
		{
			// Triplanar en espace objet (évite étirement quand la roche roule) + scale pour casser la grille
			materiel.Uv1Triplanar = true;
			materiel.Uv1WorldTriplanar = false; // INTERDIT SUR LES OBJETS PHYSIQUES
			materiel.Uv1Scale = new Vector3(0.4f, 0.4f, 0.4f); // Évite l'effet de grille dense
			materiel.Uv1TriplanarSharpness = 2.0f;
		}
		// Pour les éclats : pas de triplanar, UV planaire sur la cassure (réduit quadrillage)
		lock (_cacheMateriaux) { _cacheMateriaux[key] = materiel; }
		return materiel;
	}

	/// <summary>Réduit la liste à au plus maxPoints en gardant des points répartis (évite freeze). Garde au moins 4 points.</summary>
	private static void ReduirePointsContour(List<Vector3> points, int maxPoints)
	{
		if (points == null || points.Count <= maxPoints) return;
		int step = Mathf.Max(1, points.Count / Mathf.Max(4, maxPoints));
		var reduced = new List<Vector3>();
		for (int i = 0; i < points.Count && reduced.Count < maxPoints; i += step)
			reduced.Add(points[i]);
		while (reduced.Count < 4 && reduced.Count < points.Count)
			reduced.Add(points[reduced.Count]);
		points.Clear();
		points.AddRange(reduced);
	}

	/// <summary>UV sphériques (fallback).</summary>
	private static Vector2 UVSpherique(Vector3 centre, Vector3 point)
	{
		Vector3 d = (point - centre).Normalized();
		float u = 0.5f + Mathf.Atan2(d.Z, d.X) / (2f * Mathf.Pi);
		float v = 0.5f - Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f)) / Mathf.Pi;
		return new Vector2(u, v);
	}

	/// <summary>UV en projection planaire sur la surface de cassure : la texture suit les angles du fragment.</summary>
	private static Vector2 UVPlanCassure(Vector3 centre, Vector3 point, Vector3 normalPlan, Vector3 tangentU, Vector3 tangentV, float scaleUV)
	{
		Vector3 d = point - centre;
		float u = d.Dot(tangentU) * scaleUV + 0.5f;
		float v = d.Dot(tangentV) * scaleUV + 0.5f;
		return new Vector2(u, v);
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
			_cacheMeshSilex.Add(nouveauMesh);
			_cacheCollisionSilex.Add(nouvelleCollision);
		}
		else
		{
			_cacheMeshCaillou.Add(nouveauMesh);
			_cacheCollisionCaillou.Add(nouvelleCollision);
		}
	}
}
