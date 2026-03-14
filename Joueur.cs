using Godot;
using System;

/// <summary>Propriétés d'une matière flexible (herbe, liane, boyau, racine traitée...). Comme TableGeologique mais pour les fibres.</summary>
public struct ProfilMatiereFlexible
{
    public string Nom;
    public Color CouleurCorde;      // Teinte que donne cette matière quand tressée
    public float Durabilite;        // Résistance à l'usure (0-20)
    public float TensionMax;       // Charge avant rupture (0-20)
    public float Flexibilite;      // 0-1 : capacité à être tressée/retressée (herbe=1, liane=0.7, boyau=0.5)
    public bool Fragile;           // Se dégrade vite
    public bool Etirable;          // Peut s'allonger sous tension
}

/// <summary>Slot d'inventaire avec ADN morphologique (forme) et chimique (composition).</summary>
public struct SlotInventaire
{
    public int ID;
    public int IndexMorphologique;
    public int IndexChimique;
    /// <summary>True si le slot contient un éclat de fracture (mesh dynamique, pas dans le cache).</summary>
    public bool EstUnEclat;
    /// <summary>Mesh sauvegardé pour les éclats (sinon null).</summary>
    public Mesh MeshEclat;
    /// <summary>Nombre de fractures subies (0 = intact). Conservé au ramassage/lancer pour poudre au-delà de 5.</summary>
    public int NiveauFracture;
    /// <summary>Échelle de l'éclat au ramassage (évite qu'il grossisse au relancer).</summary>
    public Vector3 ScaleEclat;

    public SlotInventaire()
    {
        ID = 0;
        IndexMorphologique = 0;
        IndexChimique = 0;
        EstUnEclat = false;
        MeshEclat = null;
        NiveauFracture = 0;
        ScaleEclat = Vector3.One;
    }

    public bool EstVide => ID == 0;
}

public partial class Joueur : CharacterBody3D
{
    public const float Speed = 5.0f;
    public const float JumpVelocity = 4.5f;

    // Sensibilité chirurgicale de la souris
    public const float MouseSensitivity = 0.003f;

    /// <summary>Rayon du pinceau de sculpture (minage ET pose). Symétrie absolue.</summary>
    private const float RAYON_SCULPTURE = 1.0f;

    /// <summary>Mains avec ADN morphologique : la pierre conserve sa forme exacte.</summary>
    public SlotInventaire MainGauche = new SlotInventaire();
    public SlotInventaire MainDroite = new SlotInventaire();
    /// <summary>True = Slot gauche sélectionné (Main Active), False = Slot droit</summary>
    public bool MainGaucheEstActive = true;

    private Camera3D _camera;
    private RayCast3D _rayon;
    private Gestionnaire_Monde _gestionnaireMonde;
    private Panel _slotGauche;
    private Panel _slotDroite;
    private MeshInstance3D _objetEnMain;
    private SubViewportContainer _viewportSlotGauche;
    private SubViewportContainer _viewportSlotDroite;
    private MeshInstance3D _meshPreviewGauche;
    private MeshInstance3D _meshPreviewDroite;

    private float _forceLancer;
    private const float VitesseChargeBras = 1.8f;

    /// <summary>Clic gauche : charge pour pose (court) ou lancer (long).</summary>
    private bool _gaucheMaintenu = false;
    private float _tempsChargeGauche = 0f;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;

        _camera = GetNode<Camera3D>("Camera3D");
        _rayon = GetNode<RayCast3D>("Camera3D/RayCast3D");
        _rayon.AddException(this); // Ne pas toucher le joueur (sinon le "minage" ne vise pas le sol)
        _gestionnaireMonde = GetParent().GetNode<Gestionnaire_Monde>("Gestionnaire_Monde");
        _slotGauche = GetParent().GetNode<Panel>("Gestionnaire_Monde/HUD_Inventaire/Conteneur_Ancrage/Boite_Slots/Slot_Main_Gauche");
        _slotDroite = GetParent().GetNode<Panel>("Gestionnaire_Monde/HUD_Inventaire/Conteneur_Ancrage/Boite_Slots/Slot_Main_Droite");

        CreerObjetEnMain3D();
        CreerPreviewsInventaire3D();

        RafraichirHUD();
    }

    /// <summary>MeshInstance3D attaché à la caméra pour afficher l'objet tenu en main (forme exacte).</summary>
    private void CreerObjetEnMain3D()
    {
        _objetEnMain = new MeshInstance3D();
        _objetEnMain.Position = new Vector3(0.3f, -0.25f, -0.8f);
        _objetEnMain.RotationDegrees = new Vector3(-15, 10, 5);
        _objetEnMain.Scale = Vector3.One * 0.5f;
        _camera.AddChild(_objetEnMain);
    }

    /// <summary>SubViewport + MeshInstance3D dans chaque slot pour afficher la pierre exacte en 2D.</summary>
    private void CreerPreviewsInventaire3D()
    {
        _viewportSlotGauche = CreerSubViewportPourSlot(_slotGauche, out _meshPreviewGauche);
        _viewportSlotDroite = CreerSubViewportPourSlot(_slotDroite, out _meshPreviewDroite);
    }

    private SubViewportContainer CreerSubViewportPourSlot(Panel slot, out MeshInstance3D meshPreview)
    {
        var container = new SubViewportContainer();
        container.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        container.Stretch = true;
        slot.AddChild(container);

        var viewport = new SubViewport();
        viewport.Size = new Vector2I(64, 64);
        viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible;
        container.AddChild(viewport);

        var cam = new Camera3D();
        cam.SetOrthogonal(0.5f, 0.01f, 10f);
        cam.Position = new Vector3(0, 0, 1.2f);
        viewport.AddChild(cam);

        var meshNode = new MeshInstance3D();
        meshNode.Position = Vector3.Zero;
        meshNode.RotationDegrees = new Vector3(-20, 25, 0);
        viewport.AddChild(meshNode);
        meshPreview = meshNode;

        var light = new DirectionalLight3D();
        light.RotationDegrees = new Vector3(-45, 30, 0);
        light.Set("sky_mode", 1); // LightOnly : pas de disque dans le ciel (évite 2e soleil blanc dans SubViewport)
        viewport.AddChild(light);

        return container;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("clic_gauche"))
        {
            SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;
            if (mainActive.EstVide) ExecuterMinageVoxel(); // MAIN VIDE = CREUSE DIRECTEMENT
            else { _gaucheMaintenu = true; _tempsChargeGauche = 0f; } // MAIN PLEINE = CHARGE LA FRAPPE
        }
        else if (@event.IsActionReleased("clic_gauche") && _gaucheMaintenu)
        {
            _gaucheMaintenu = false;
            SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;
            if (!mainActive.EstVide && EstObjetProcedural(mainActive.ID))
                ExecuterFrappe(Mathf.Clamp(_tempsChargeGauche, 0.1f, 2f)); // RELÂCHE = FRAPPE LA PIERRE
        }
        else if (@event.IsActionPressed("clic_droit"))
        {
            SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;
            if (!mainActive.EstVide) _forceLancer = 0f; // MAIN PLEINE = DÉBUT CHARGE LANCER/POSER
        }
        else if (@event.IsActionReleased("clic_droit"))
        {
            SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;
            if (!mainActive.EstVide)
            {
                // IDENTIFICATION DE LA MATIÈRE : Est-ce du terrain (Voxel) ?
                bool estTerrainVoxel = mainActive.ID >= 1 && mainActive.ID <= 9;
                // Clic bref = poser. Maintien du clic = lancer (seuil ~0,4 s pour éviter de lancer par accident).
                if (estTerrainVoxel || _forceLancer < 0.4f)
                {
                    ExecuterPlacement();
                }
                else
                {
                    ExecuterLancer(Mathf.Clamp(_forceLancer, 0.5f, 2.0f));
                }
                _forceLancer = 0f;
            }
        }
        else if (@event.IsActionPressed("interagir"))
        {
            // E = ramasser roches / objets au sol
            ExecuterRamassageObjet();
        }
        else if (@event.IsActionPressed("changer_main"))
        {
            MainGaucheEstActive = !MainGaucheEstActive;
            RafraichirHUD();
            GD.Print(MainGaucheEstActive ? "ZERO-K : Main Gauche sélectionnée." : "ZERO-K : Main Droite sélectionnée.");
        }
        else if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.T)
                ExecuterTressage();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mouseMotion)
        {
            RotateY(-mouseMotion.Relative.X * MouseSensitivity);
            _camera.RotateX(-mouseMotion.Relative.Y * MouseSensitivity);
            Vector3 cameraRot = _camera.Rotation;
            cameraRot.X = Mathf.Clamp(cameraRot.X, Mathf.DegToRad(-80f), Mathf.DegToRad(80f));
            _camera.Rotation = cameraRot;
        }

        if (Input.IsActionJustPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    private void MettreAJourSlotUI(Panel slot, SlotInventaire slotData, bool selectionne)
    {
        int idMatiere = slotData.ID;
        var style = new StyleBoxFlat();
        if (idMatiere == 0)
            style.BgColor = new Color(0.2f, 0.2f, 0.2f);
        else if (idMatiere == 1)
            style.BgColor = new Color(0.5f, 0.3f, 0.1f); // Marron (Terre)
        else if (idMatiere == 2)
            style.BgColor = new Color(0.4f, 0.4f, 0.4f); // Gris foncé (Roche)
        else if (idMatiere == 3)
            style.BgColor = new Color(0.9f, 0.8f, 0.5f); // Jaune pâle (Sable)
        else if (idMatiere == 4)
            style.BgColor = new Color(0.9f, 0.9f, 0.9f); // Blanc (Neige)
        else if (idMatiere == 5)
            style.BgColor = new Color(0.9f, 0.95f, 1f); // Blanc bleuté (Neige/Glace)
        else if (idMatiere == 6)
            style.BgColor = new Color(0.6f, 0.45f, 0.25f); // Terre aride (Arid earth)
        else if (idMatiere == 7)
            style.BgColor = new Color(0.35f, 0.25f, 0.15f); // Boue (Mud)
        else if (idMatiere == 8)
            style.BgColor = new Color(0.3f, 0.5f, 0.2f); // Terre tropicale
        else if (idMatiere == 9)
            style.BgColor = new Color(0.7f, 0.75f, 0.8f); // Terre gelée
        else if (idMatiere == 10)
            style.BgColor = new Color(0.5f, 0.45f, 0.4f); // Petite Pierre
        else if (idMatiere == 11)
            style.BgColor = new Color(0.6f, 0.55f, 0.5f); // Silex
        else if (idMatiere == 12)
            style.BgColor = new Color(0.45f, 0.4f, 0.35f); // Pierre Moyenne
        else if (idMatiere == 13)
            style.BgColor = new Color(0.4f, 0.35f, 0.3f); // Grosse Pierre
        else if (idMatiere == 14)
            style.BgColor = new Color(0.35f, 0.3f, 0.25f); // Très Grosse Pierre
        else if (idMatiere == 999)
            style.BgColor = new Color(0.1f, 0.8f, 0.2f); // Vert (Objet/Buisson)
        else
            style.BgColor = new Color(0.4f, 0.4f, 0.6f); // Violet (Autre)

        if (selectionne)
        {
            style.BorderColor = new Color(1f, 0.9f, 0.2f);
            style.SetBorderWidthAll(3);
        }

        slot.AddThemeStyleboxOverride("panel", style);
    }

    private void RafraichirHUD()
    {
        MettreAJourSlotUI(_slotGauche, MainGauche, MainGaucheEstActive);
        MettreAJourSlotUI(_slotDroite, MainDroite, !MainGaucheEstActive);
        MettreAJourObjetEnMain();
        MettreAJourPreviewsSlots();
        MettreAJourVisibilitePreviews();
    }

    /// <summary>Assigne le Mesh exact de la main active au MeshInstance3D devant la caméra.</summary>
    private void MettreAJourObjetEnMain()
    {
        var main = MainGaucheEstActive ? MainGauche : MainDroite;
        if (main.EstVide || !EstObjetAvecVisuel(main.ID))
        {
            _objetEnMain.Mesh = null;
            _objetEnMain.MaterialOverride = null;
            return;
        }
        Mesh m = main.EstUnEclat ? main.MeshEclat : ObtenirMeshDepuisCache(main.ID, main.IndexMorphologique);
        _objetEnMain.Mesh = m;
        if (main.EstUnEclat)
            _objetEnMain.MaterialOverride = null; // Éclat : matériau intégré au mesh (SurfaceTool)
        else if (m != null)
            AppliquerMaterielObjet(_objetEnMain, main.ID, main.IndexChimique, main.ID == 20 ? main.IndexMorphologique : 0, main.ID == 20 ? main.NiveauFracture : 0);
    }

    /// <summary>Assigne le Mesh exact au SubViewport de chaque slot (pierre en 3D dans l'UI).</summary>
    private void MettreAJourPreviewsSlots()
    {
        MettreAJourPreviewSlot(_meshPreviewGauche, MainGauche);
        MettreAJourPreviewSlot(_meshPreviewDroite, MainDroite);
    }

    private void MettreAJourPreviewSlot(MeshInstance3D meshNode, SlotInventaire slot)
    {
        if (slot.EstVide || !EstObjetAvecVisuel(slot.ID))
        {
            meshNode.Mesh = null;
            meshNode.MaterialOverride = null;
            return;
        }
        Mesh m = slot.EstUnEclat ? slot.MeshEclat : ObtenirMeshDepuisCache(slot.ID, slot.IndexMorphologique);
        meshNode.Mesh = m;
        if (slot.EstUnEclat)
            meshNode.MaterialOverride = null; // Éclat : matériau intégré au mesh
        else if (m != null)
            AppliquerMaterielObjet(meshNode, slot.ID, slot.IndexChimique, slot.ID == 20 ? slot.IndexMorphologique : 0, slot.ID == 20 ? slot.NiveauFracture : 0);
    }

    /// <summary>Cache le SubViewport quand pas d'objet avec visuel (pierre, fibre, corde), pour laisser voir la couleur du slot.</summary>
    private void MettreAJourVisibilitePreviews()
    {
        if (_viewportSlotGauche != null) _viewportSlotGauche.Visible = !MainGauche.EstVide && EstObjetAvecVisuel(MainGauche.ID);
        if (_viewportSlotDroite != null) _viewportSlotDroite.Visible = !MainDroite.EstVide && EstObjetAvecVisuel(MainDroite.ID);
    }

    private static bool EstObjetProcedural(int id) => id == 10 || id == 11 || id == 12;

    /// <summary>True si l'objet a un mesh à afficher en main / preview (pierre, silex, fibre, corde).</summary>
    private static bool EstObjetAvecVisuel(int id) => id == 10 || id == 11 || id == 12 || id == 15 || id == 20;

    private static bool EstMatiereFlexible(int id)
    {
        int[] flexibles = { 15, 16, 17, 20 }; // 20 = corde : flexible, peut être retressée
        return Array.IndexOf(flexibles, id) != -1;
    }

    private static bool EstObjetRigide(int id)
    {
        return id >= 10 && id <= 14;
    }

    /// <summary>Table des matières flexibles (comme TableGeologique pour les roches). ID 15=herbe, 16=liane, 17=boyau. Ajouter racine traitée etc. plus tard.</summary>
    private static readonly ProfilMatiereFlexible[] TableMatiereFlexible = new ProfilMatiereFlexible[]
    {
        new ProfilMatiereFlexible { Nom = "Herbe", CouleurCorde = new Color(0.35f, 0.52f, 0.18f), Durabilite = 4f, TensionMax = 3f, Flexibilite = 1f, Fragile = true, Etirable = false },
        new ProfilMatiereFlexible { Nom = "Liane", CouleurCorde = new Color(0.4f, 0.38f, 0.22f), Durabilite = 10f, TensionMax = 8f, Flexibilite = 0.7f, Fragile = false, Etirable = false },
        new ProfilMatiereFlexible { Nom = "Boyau", CouleurCorde = new Color(0.6f, 0.45f, 0.35f), Durabilite = 14f, TensionMax = 14f, Flexibilite = 0.5f, Fragile = false, Etirable = true }
    };

    private const float SEUIL_MIN_FLEXIBILITE = 0.18f;   // En-dessous = trop rigide pour tresser
    private const float PERTE_FLEX_PAR_MIX = 0.38f;      // Chaque retressage réduit la flexibilité (~38 %)

    private static int IdFlexibleToIndex(int id)
    {
        if (id == 15) return 0; if (id == 16) return 1; if (id == 17) return 2;
        return -1;
    }

    private static bool ObtenirProfilFlexible(int id, out ProfilMatiereFlexible p)
    {
        int i = IdFlexibleToIndex(id);
        if (i < 0 || i >= TableMatiereFlexible.Length) { p = default; return false; }
        p = TableMatiereFlexible[i]; return true;
    }

    /// <summary>Flexibilité effective d'un slot : fibre = Flexibilite de la table, corde = baseFlex * (1 - perte par niveau). Tier 2 + tier 1 = on peut tresser si les deux ont assez de flex.</summary>
    private static float ObtenirFlexibiliteEffective(SlotInventaire slot)
    {
        if (slot.ID == 20)
        {
            float fa = ObtenirProfilFlexible(slot.IndexChimique, out var pa) ? pa.Flexibilite : 0.5f;
            float fb = ObtenirProfilFlexible(slot.IndexMorphologique, out var pb) ? pb.Flexibilite : 0.5f;
            float baseFlex = (fa + fb) * 0.5f;
            return baseFlex * Mathf.Max(0f, 1f - slot.NiveauFracture * PERTE_FLEX_PAR_MIX);
        }
        return ObtenirProfilFlexible(slot.ID, out var p) ? p.Flexibilite : 0f;
    }

    /// <summary>Teinte de la corde selon les deux matières tressées. Chaque retressage assombrit un peu.</summary>
    private static Color ObtenirTeinteCordeTressage(int idMatiereA, int idMatiereB, int niveauTressage = 0)
    {
        bool okA = ObtenirProfilFlexible(idMatiereA, out var pa);
        bool okB = ObtenirProfilFlexible(idMatiereB, out var pb);
        Color c;
        if (!okA && !okB) c = new Color(0.52f, 0.42f, 0.28f);
        else if (!okA) c = pb.CouleurCorde;
        else if (!okB) c = pa.CouleurCorde;
        else c = new Color(
            (pa.CouleurCorde.R + pb.CouleurCorde.R) * 0.5f,
            (pa.CouleurCorde.G + pb.CouleurCorde.G) * 0.5f,
            (pa.CouleurCorde.B + pb.CouleurCorde.B) * 0.5f
        );
        if (niveauTressage > 0) c = c * Mathf.Pow(0.84f, niveauTressage);
        return c;
    }

    /// <summary>Matériau corde : si 2 matières différentes = dégradé (on voit ce qui est mixé). Chaque retressage assombrit.</summary>
    private static Material ObtenirMaterielCorde(int idA, int idB, int niveauTressage)
    {
        float assombri = niveauTressage > 0 ? Mathf.Pow(0.84f, niveauTressage) : 1f;
        Color ca = (ObtenirProfilFlexible(idA, out var pa) ? pa.CouleurCorde : new Color(0.52f, 0.42f, 0.28f)) * assombri;
        Color cb = (ObtenirProfilFlexible(idB, out var pb) ? pb.CouleurCorde : new Color(0.52f, 0.42f, 0.28f)) * assombri;
        var mat = new StandardMaterial3D { Roughness = 0.85f };
        if (idA == idB)
        {
            mat.AlbedoColor = ca;
        }
        else
        {
            var grad = new Gradient();
            grad.AddPoint(0f, ca);
            grad.AddPoint(1f, cb);
            var tex = new GradientTexture2D { Width = 32, Height = 64, Gradient = grad };
            tex.FillFrom = new Vector2(0.5f, 0f);
            tex.FillTo = new Vector2(0.5f, 1f);
            mat.AlbedoTexture = tex;
        }
        return mat;
    }

    /// <summary>Durabilité et tension de la corde : tressage = flexible mais un peu moins que les brins bruts, mais plus résistant et supporte plus de tension/force.</summary>
    private static void ObtenirStatsCorde(int idA, int idB, out float durabilite, out float tensionMax)
    {
        bool okA = ObtenirProfilFlexible(idA, out var pa);
        bool okB = ObtenirProfilFlexible(idB, out var pb);
        if (!okA && !okB) { durabilite = 6f; tensionMax = 5f; return; }
        if (!okA) { pa = pb; } if (!okB) { pb = pa; }
        float baseDurabilite = (pa.Durabilite + pb.Durabilite) * 0.5f;
        float baseTension = (pa.TensionMax + pb.TensionMax) * 0.5f;
        durabilite = baseDurabilite * 1.35f;  // Corde plus résistante que les fibres brutes
        tensionMax = baseTension * 1.5f;      // Supporte plus de tension et de force
        if (pa.Fragile || pb.Fragile) durabilite *= 0.75f;
    }

    private static Mesh _cacheMeshCorde;

    private static Mesh CreerMeshCordeTressee()
    {
        if (_cacheMeshCorde != null) return _cacheMeshCorde;
        const float rayonHelice = 0.026f;
        const float rayonTube = 0.012f;
        const float hauteur = 0.28f;
        const int nbTours = 3;
        const int ringsParStrand = 24;
        const int segsParRing = 6;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int strand = 0; strand < 3; strand++)
        {
            float phase = strand * Mathf.Tau / 3f;
            for (int r = 0; r < ringsParStrand; r++)
            {
                float t = r / (float)(ringsParStrand - 1);
                float angle = phase + t * nbTours * Mathf.Tau;
                Vector3 centre = new Vector3(rayonHelice * Mathf.Cos(angle), t * hauteur - hauteur * 0.5f, rayonHelice * Mathf.Sin(angle));
                Vector3 tangent = new Vector3(-Mathf.Sin(angle), hauteur / (rayonHelice * nbTours * Mathf.Tau), Mathf.Cos(angle)).Normalized();
                Vector3 radial = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                Vector3 binormal = tangent.Cross(radial).Normalized();
                for (int s = 0; s < segsParRing; s++)
                {
                    float a = s * Mathf.Tau / segsParRing;
                    Vector3 offset = (radial * Mathf.Cos(a) + binormal * Mathf.Sin(a)) * rayonTube;
                    st.AddVertex(centre + offset);
                }
            }
            for (int r = 0; r < ringsParStrand - 1; r++)
                for (int s = 0; s < segsParRing; s++)
                {
                    int v = strand * ringsParStrand * segsParRing + r * segsParRing + s;
                    int vn = v + segsParRing;
                    int s1 = (s + 1) % segsParRing;
                    st.AddIndex(v); st.AddIndex(v + s1); st.AddIndex(vn);
                    st.AddIndex(vn); st.AddIndex(v + s1); st.AddIndex(vn + s1);
                }
        }
        st.GenerateNormals();
        _cacheMeshCorde = st.Commit();
        return _cacheMeshCorde;
    }

    private static Mesh ObtenirMeshDepuisCache(int id, int index)
    {
        if (id == 11)
        {
            var cache = ItemPhysique.CacheMeshSilex;
            if (index >= 0 && index < cache.Count) return cache[index];
        }
        else if (id == 10 || id == 12)
        {
            var cache = ItemPhysique.CacheMeshCaillou;
            if (index >= 0 && index < cache.Count) return cache[index];
        }
        else if (id == 15) return new CapsuleMesh { Radius = 0.009f, Height = 0.34f };
        else if (id == 20) return CreerMeshCordeTressee();
        return null;
    }

    private static void AppliquerMaterielObjet(MeshInstance3D visuel, int idObjet, int indexChimique, int indexMorphologique = 0, int niveauTressage = 0)
    {
        if (idObjet == 15) { visuel.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.55f, 0.15f), Roughness = 0.9f }; return; }
        if (idObjet == 20) { visuel.MaterialOverride = ObtenirMaterielCorde(indexChimique, indexMorphologique, niveauTressage); return; }
        int chimique = Mathf.Clamp(indexChimique, 0, ItemPhysique.TableGeologique.Length - 1);
        visuel.MaterialOverride = ItemPhysique.CreerMaterielProcedural(idObjet == 11, chimique);
    }


    /// <summary>Phase 1 pure : minage du terrain Marching Cubes uniquement. Clic gauche.</summary>
    private void ExecuterMinageVoxel()
    {
        _rayon.ForceRaycastUpdate();
        if (!_rayon.IsColliding()) return;
        Object colliderObj = _rayon.GetCollider();
        Node objetTouche = colliderObj as Node;
        // Si on touche un objet physique valide, on annule le minage
        if (objetTouche != null && (objetTouche is ItemPhysique || objetTouche is RigidBody3D || objetTouche.IsInGroup("BlocsPoses"))) return;

        // Si objetTouche est null, cela signifie qu'on a touché le terrain bas-niveau ! ON CONTINUE LE MINAGE.
        Vector3 pointImpact = _rayon.GetCollisionPoint();
        Vector3 normaleImpact = _rayon.GetCollisionNormal();
        Vector3 pointDeSondage = pointImpact - (normaleImpact * 0.1f);

        int idExtrait = _gestionnaireMonde?.ObtenirMatiereExacte(pointDeSondage) ?? 1;
        // Toujours terrain (1-9) pour que la pose refusionne avec le sol ; jamais 10/11/12/999 (bloc vert).
        if (idExtrait < 1 || idExtrait > 9) idExtrait = 1;

        if (MainGaucheEstActive && !MainGauche.EstVide && !MainDroite.EstVide) return;
        if (!MainGaucheEstActive && !MainDroite.EstVide && !MainGauche.EstVide) return;

        _gestionnaireMonde?.AppliquerDestructionGlobale(pointImpact, RAYON_SCULPTURE);

        var nouveauSlot = new SlotInventaire { ID = idExtrait, IndexMorphologique = 0, IndexChimique = 0 };
        if (MainGaucheEstActive)
        {
            if (MainGauche.EstVide) MainGauche = nouveauSlot;
            else MainDroite = nouveauSlot;
        }
        else
        {
            if (MainDroite.EstVide) MainDroite = nouveauSlot;
            else MainGauche = nouveauSlot;
        }
        RafraichirHUD();
    }

    /// <summary>Phase 2 pure : ramassage des objets physiques (Caillou, Silex, BlocsPoses). Touche E (interagir).
    /// Copie IndexCacheMemoire dans le SlotInventaire pour conserver la forme exacte.</summary>
    private void ExecuterRamassageObjet()
    {
        if (!MainGauche.EstVide && !MainDroite.EstVide) return;
        if (!_rayon.IsColliding()) return;

        Node objetTouche = (Node)_rayon.GetCollider();
        if (objetTouche == null) return;

        SlotInventaire nouveauSlot = default;

        if (objetTouche.IsInGroup("BlocsPoses"))
        {
            int id = objetTouche.HasMeta("ID_Matiere") ? (int)objetTouche.GetMeta("ID_Matiere").AsInt32() : 1;
            var item = objetTouche as ItemPhysique ?? (objetTouche as Node)?.GetParent() as ItemPhysique ?? (objetTouche as Node)?.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            nouveauSlot = new SlotInventaire
            {
                ID = id,
                IndexMorphologique = item?.IndexCacheMemoire ?? 0,
                IndexChimique = item?.IndexChimique ?? 0,
                EstUnEclat = item?.EstUnEclat ?? false,
                MeshEclat = (item != null && item.EstUnEclat) ? item.ObtenirMeshVisuel() : null,
                NiveauFracture = item?.NiveauFracture ?? 0,
                ScaleEclat = (item != null && item.EstUnEclat) ? item.Scale : Vector3.One
            };
        }
        else if (objetTouche is RigidBody3D rb)
        {
            // BlocChutant (fibre, buisson tombé) : pas d'ItemPhysique, on lit le meta.
            if (objetTouche is BlocChutant)
            {
                int id = objetTouche.HasMeta("ID_Matiere") ? (int)objetTouche.GetMeta("ID_Matiere").AsInt32() : 1;
                nouveauSlot = new SlotInventaire { ID = id, IndexMorphologique = 0, IndexChimique = 0 };
            }
            else
            {
            var item = rb as ItemPhysique ?? (rb as Node)?.GetParent() as ItemPhysique ?? rb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            if (item == null) return;
            if (item.ID_Objet == 13 || item.ID_Objet == 14)
            {
                GD.Print("ZERO-K : Masse excessive. La colonne vertébrale céderait. Action bloquée.");
                return;
            }
            nouveauSlot = new SlotInventaire
            {
                ID = item.ID_Objet,
                IndexMorphologique = item.IndexCacheMemoire,
                IndexChimique = item.IndexChimique,
                EstUnEclat = item.EstUnEclat,
                MeshEclat = item.EstUnEclat ? item.ObtenirMeshVisuel() : null,
                NiveauFracture = item.NiveauFracture,
                ScaleEclat = item.EstUnEclat ? item.Scale : Vector3.One
            };
            }
        }
        else if (objetTouche is StaticBody3D sb)
        {
            var item = sb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            if (item == null) return;
            if (item.ID_Objet == 13 || item.ID_Objet == 14)
            {
                GD.Print("ZERO-K : Masse excessive. La colonne vertébrale céderait. Action bloquée.");
                return;
            }
            nouveauSlot = new SlotInventaire
            {
                ID = item.ID_Objet,
                IndexMorphologique = item.IndexCacheMemoire,
                IndexChimique = item.IndexChimique,
                EstUnEclat = item.EstUnEclat,
                MeshEclat = item.EstUnEclat ? item.ObtenirMeshVisuel() : null,
                NiveauFracture = item.NiveauFracture,
                ScaleEclat = item.EstUnEclat ? item.Scale : Vector3.One
            };
        }
        else
            return;

        if (MainGaucheEstActive)
        {
            if (MainGauche.EstVide) MainGauche = nouveauSlot;
            else if (MainDroite.EstVide) MainDroite = nouveauSlot;
            else return;
        }
        else
        {
            if (MainDroite.EstVide) MainDroite = nouveauSlot;
            else if (MainGauche.EstVide) MainGauche = nouveauSlot;
            else return;
        }
        objetTouche.QueueFree();
        RafraichirHUD();
    }

    /// <summary>Craft émergent : tressage de deux matières flexibles en corde (ID 20). La corde est le résultat dynamique : teinte, durabilité et tension viennent des deux matières (TableMatiereFlexible). Touche T.</summary>
    private void ExecuterTressage()
    {
        if (MainGauche.EstVide || MainDroite.EstVide)
        {
            GD.Print("ZERO-K : Il faut deux matériaux pour initier une torsion.");
            return;
        }
        if (!EstMatiereFlexible(MainGauche.ID) || !EstMatiereFlexible(MainDroite.ID))
        {
            GD.Print("ZERO-K : Torsion impossible. Au moins l'un des matériaux est trop rigide et se briserait.");
            return;
        }
        float flexG = ObtenirFlexibiliteEffective(MainGauche);
        float flexD = ObtenirFlexibiliteEffective(MainDroite);
        if (flexG < SEUIL_MIN_FLEXIBILITE || flexD < SEUIL_MIN_FLEXIBILITE)
        {
            GD.Print("ZERO-K : Au moins l'un des matériaux n'est plus assez flexible pour être tressé (épaisseur, rigidité).");
            return;
        }
        // Corde (20) = flexible si niveau < max. On "déplie" les matières (IndexChimique, IndexMorphologique pour une corde).
        int m1a = MainGauche.ID == 20 ? MainGauche.IndexChimique : MainGauche.ID;
        int m1b = MainGauche.ID == 20 ? MainGauche.IndexMorphologique : MainGauche.ID;
        int m2a = MainDroite.ID == 20 ? MainDroite.IndexChimique : MainDroite.ID;
        int m2b = MainDroite.ID == 20 ? MainDroite.IndexMorphologique : MainDroite.ID;
        int idA = Mathf.Min(Mathf.Min(m1a, m1b), Mathf.Min(m2a, m2b));
        int idB = Mathf.Max(Mathf.Max(m1a, m1b), Mathf.Max(m2a, m2b));
        ObtenirStatsCorde(idA, idB, out float durabilite, out float tensionMax);
        ObtenirProfilFlexible(idA, out var pa);
        ObtenirProfilFlexible(idB, out var pb);
        bool estRetressage = MainGauche.ID == 20 || MainDroite.ID == 20;
        int niveauTressage = estRetressage ? Mathf.Max(MainGauche.ID == 20 ? MainGauche.NiveauFracture : 0, MainDroite.ID == 20 ? MainDroite.NiveauFracture : 0) + 1 : 0;
        GD.Print("ZERO-K : Tressage systémique en cours...");
        SlotInventaire cordeSystemique = new SlotInventaire
        {
            ID = 20,
            IndexChimique = idA,
            IndexMorphologique = idB,
            EstUnEclat = false,
            NiveauFracture = niveauTressage  // 0 = simple, 1+ = retressée (plus foncé)
        };
        if (MainGaucheEstActive)
        {
            MainGauche = cordeSystemique;
            MainDroite = default;
        }
        else
        {
            MainDroite = cordeSystemique;
            MainGauche = default;
        }
        RafraichirHUD();
        GD.Print($"ZERO-K : Liaison réussie. Corde {pa.Nom}-{pb.Nom} : durabilité {durabilite:F0}, tension max {tensionMax:F0}.");
    }

    /// <summary>Placement (construction ou rejet d'objet). Clic droit.</summary>
    private void ExecuterPlacement()
    {
        SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;

        if (mainActive.EstVide)
        {
            GD.Print("ZERO-K : La main sélectionnée est vide. Impossible de poser.");
            return;
        }

        _rayon.ForceRaycastUpdate();
        if (!_rayon.IsColliding()) return;

        Vector3 pointImpact = _rayon.GetCollisionPoint();
        Vector3 normaleImpact = _rayon.GetCollisionNormal();
        Vector3 pointDeChute = pointImpact + (normaleImpact * 0.1f);
        float distance = GlobalPosition.DistanceTo(pointDeChute);
        if (distance < 1.4f) return;

        int id = mainActive.ID;
        if (id == 0) return; // Slot vide ou invalide
        // Terrain (terre, roche, sable, etc.) → modifier les voxels du monde (fusion avec le sol)
        if (id >= 1 && id <= 9 && id != 4)
        {
            _gestionnaireMonde?.AppliquerCreationGlobale(pointImpact, normaleImpact, RAYON_SCULPTURE, id);
        }
        // Objets physiques (roches, silex, buisson, fibre, corde) → déposer un bloc au sol
        else if (id == 999 || id == 10 || id == 11 || id == 12 || id == 15 || id == 20)
        {
            CreerBlocPose(pointDeChute, mainActive);
        }
        else
        {
            GD.Print($"ZERO-K : Matière {id} non géologique. Pose ignorée.");
        }

        if (MainGaucheEstActive) MainGauche = default;
        else MainDroite = default;

        RafraichirHUD();
    }

    /// <summary>Frappe la roche visée : impulsion + dégâts. Si résistance à 0 → fracture. Lame (Silex/Éclat) sur sol → fauchage.</summary>
    private void ExecuterFrappe(float force)
    {
        SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;
        if (mainActive.EstVide || !EstObjetProcedural(mainActive.ID)) return;
        _rayon.ForceRaycastUpdate();
        if (!_rayon.IsColliding()) return;

        Object colliderObj = _rayon.GetCollider();
        Node objetTouche = colliderObj as Node;

        // Frappe sur le sol ou le vide : si la main tient une lame (Silex ou Éclat), fauchage.
        if (objetTouche == null || objetTouche.Name.ToString().Contains("TerrainSection") || objetTouche.Name.ToString().Contains("CollisionSection"))
        {
            if (mainActive.EstUnEclat || mainActive.ID == 11)
            {
                Vector3 pointImpact = _rayon.GetCollisionPoint();
                _gestionnaireMonde?.AppliquerFauchageGlobal(pointImpact, 1.5f);
                GD.Print("ZERO-K : Lame appliquée sur le sol. Fauchage en cours.");
            }
            return;
        }

        RigidBody3D rbCible = objetTouche as RigidBody3D;
        if (rbCible == null && objetTouche.HasNode("ItemPhysique"))
        {
            var parentRb = objetTouche.GetParent() as RigidBody3D;
            if (parentRb != null) rbCible = parentRb;
        }
        if (rbCible == null) return;

        var item = rbCible as ItemPhysique ?? rbCible.GetNodeOrNull<ItemPhysique>("ItemPhysique");
        if (item == null) return;

        Vector3 dirFrappe = -_rayon.GetCollisionNormal();
        float impulsionFrappe = 4f * force * (1f + rbCible.Mass * 0.5f);
        rbCible.ApplyCentralImpulse(dirFrappe * impulsionFrappe);
        float degats = 15f * force * (1f + rbCible.Mass * 0.2f);
        item.ResistanceActuelle -= degats;
        if (item.ResistanceActuelle <= 0)
        {
            Vector3 pointImpact = _rayon.GetCollisionPoint();
            Vector3 dirVue = (pointImpact - _camera.GlobalPosition).Normalized();
            item.FracturerPublic(dirVue, pointImpact);
        }
    }

    /// <summary>Lance la roche tenue : spawn devant la caméra + impulsion (évite le bug sous la map du Raycast).</summary>
    private void ExecuterLancer(float force)
    {
        SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;
        if (mainActive.EstVide) return;

        // 1. On spawn la roche légèrement devant la caméra pour éviter de la jeter dans notre propre corps
        Vector3 direction = -_camera.GlobalTransform.Basis.Z.Normalized();
        Vector3 pointDeSpawn = _camera.GlobalPosition + (direction * 1.5f);

        // 2. On invoque le bloc
        Node3D corpsCree = CreerBlocPose(pointDeSpawn, mainActive);

        // 3. Si c'est un objet soumis à la gravité, on applique l'énergie cinétique
        if (corpsCree is RigidBody3D rb)
        {
            rb.ApplyCentralImpulse(direction * (15f * force));
        }

        // 4. On vide la main
        if (MainGaucheEstActive) MainGauche = default;
        else MainDroite = default;
        RafraichirHUD();
    }

    /// <summary>Crée un bloc physique posé avec IndexCacheMemoire assigné (forme exacte conservée au rejet). Retourne le nœud créé (pour lancer avec impulsion). ItemPhysique est le RigidBody3D racine.</summary>
    private Node3D CreerBlocPose(Vector3 pointDeChute, SlotInventaire mainActive)
    {
        int id = mainActive.ID;
        Node3D corps;
        if (mainActive.EstUnEclat && mainActive.MeshEclat != null)
        {
            // Reconstruction de l'éclat jeté : mesh + échelle conservés pour garder la même taille
            var item = new ItemPhysique
            {
                ID_Objet = mainActive.ID,
                IndexChimique = mainActive.IndexChimique,
                EstUnEclat = true,
                NiveauFracture = mainActive.NiveauFracture,
                Scale = mainActive.ScaleEclat,
                Name = "ItemPhysique"
            };
            item.AddChild(new MeshInstance3D { Name = "MeshInstance3D", Mesh = mainActive.MeshEclat });
            item.AddChild(new CollisionShape3D { Name = "CollisionShape3D", Shape = mainActive.MeshEclat.CreateConvexShape(true, false) });
            corps = item;
        }
        else if (id == 10 || id == 12) // Petite Pierre ou Pierre Moyenne (ItemPhysique = RigidBody3D)
        {
            float rayon = id == 10 ? 0.15f : 0.25f;
            float hauteur = rayon * 2f;
            var item = new ItemPhysique { ID_Objet = id, IndexCacheMemoire = mainActive.IndexMorphologique, IndexChimique = mainActive.IndexChimique, NiveauFracture = mainActive.NiveauFracture, Name = "ItemPhysique" };
            item.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = rayon, Height = hauteur } });
            item.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = rayon } });
            corps = item;
        }
        else if (id == 11) // Silex (ItemPhysique = RigidBody3D, l'eau gère le ralentissement)
        {
            var item = new ItemPhysique { ID_Objet = id, IndexCacheMemoire = mainActive.IndexMorphologique, IndexChimique = mainActive.IndexChimique, NiveauFracture = mainActive.NiveauFracture, Name = "ItemPhysique" };
            item.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.2f, 0.15f, 0.25f) } });
            item.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.2f, 0.15f, 0.25f) } });
            corps = item;
        }
        else if (id == 15) // Fibre d'herbe : fagot de brins (capsules = tiges organiques)
        {
            var item = new ItemPhysique { ID_Objet = id, Name = "ItemPhysique" };
            var matHerbe = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.55f, 0.15f), Roughness = 0.9f, Metallic = 0f };
            float l = 0.38f;
            for (int i = 0; i < 6; i++)
            {
                float a = (i / 6f) * Mathf.Pi * 0.6f - 0.15f;
                float x = Mathf.Sin(a) * 0.025f; float z = Mathf.Cos(a) * 0.025f;
                var mi = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.01f, Height = l - 0.02f }, MaterialOverride = matHerbe, Position = new Vector3(x, l * 0.5f, z), Rotation = new Vector3(0.08f * (i - 3), 0.1f * (i % 2 - 0.5f), 0.06f * (i - 2)) };
                item.AddChild(mi);
            }
            item.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.12f, l, 0.12f) }, Position = new Vector3(0, l * 0.5f, 0) });
            corps = item;
        }
        else if (id == 20) // Tressage / corde : dégradé des 2 matières (on voit ce qui est mixé). Chaque retressage assombrit.
        {
            int idA = mainActive.IndexChimique, idB = mainActive.IndexMorphologique;
            var item = new ItemPhysique { ID_Objet = id, IndexChimique = idA, IndexCacheMemoire = idB, NiveauFracture = mainActive.NiveauFracture, Name = "ItemPhysique" };
            var matCorde = ObtenirMaterielCorde(idA, idB, mainActive.NiveauFracture);
            item.AddChild(new MeshInstance3D { Mesh = CreerMeshCordeTressee(), MaterialOverride = matCorde });
            item.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.045f, Height = 0.28f } });
            corps = item;
        }
        else // 999 Buisson
        {
            var sb = new StaticBody3D();
            sb.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            sb.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.8f, 0.2f) } });
            corps = sb;
        }
        corps.SetMeta("ID_Matiere", id);
        corps.AddToGroup("BlocsPoses");
        GetParent().AddChild(corps);
        corps.GlobalPosition = pointDeChute;
        return corps;
    }

    private float _tempsAttenteSpawn;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;

        if (_gaucheMaintenu)
            _tempsChargeGauche += dt;
        // Clic droit maintenu avec main pleine = charge du lancer
        if (!mainActive.EstVide && Input.IsActionPressed("clic_droit"))
            _forceLancer = Mathf.Min(1f, _forceLancer + VitesseChargeBras * dt);

        Vector3 velocity = Velocity;
        bool spawnPret = _gestionnaireMonde == null || _gestionnaireMonde.EstSpawnPret();

        if (!spawnPret)
        {
            _tempsAttenteSpawn += (float)delta;
            if (_tempsAttenteSpawn < 4f)
                velocity.Y = Mathf.MoveToward(velocity.Y, 0, 2f * (float)delta);
            else
                velocity += GetGravity() * (float)delta;
        }
        else
        {
            _tempsAttenteSpawn = 0f;
            if (!IsOnFloor())
                velocity += GetGravity() * (float)delta;
        }

        if (Input.IsActionJustPressed("ui_accept") && IsOnFloor())
            velocity.Y = JumpVelocity;

        Vector2 inputDir = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
        Vector3 direction = (Transform.Basis * new Vector3(inputDir.X, 0, inputDir.Y)).Normalized();
        if (direction != Vector3.Zero)
        {
            velocity.X = direction.X * Speed;
            velocity.Z = direction.Z * Speed;
        }
        else
        {
            velocity.X = Mathf.MoveToward(velocity.X, 0, Speed);
            velocity.Z = Mathf.MoveToward(velocity.Z, 0, Speed);
        }

        Velocity = velocity;
        MoveAndSlide();
    }
}
