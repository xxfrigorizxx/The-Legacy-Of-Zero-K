using Godot;
using System;

/// <summary>Slot d'inventaire avec ADN morphologique pour conserver la forme exacte (Caillou/Silex).</summary>
public struct SlotInventaire
{
    public int ID;
    public int IndexMorphologique;
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

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;

        _camera = GetNode<Camera3D>("Camera3D");
        _rayon = GetNode<RayCast3D>("Camera3D/RayCast3D");
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
        viewport.AddChild(light);

        return container;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("clic_gauche"))
        {
            // STRICTEMENT RÉSERVÉ AU MINAGE DU TERRAIN (Phase 1)
            ExecuterMinageVoxel();
        }
        else if (@event.IsActionPressed("clic_droit"))
        {
            // STRICTEMENT RÉSERVÉ AU PLACEMENT (Construction ou Rejet d'objet)
            ExecuterPlacement();
        }
        else if (@event.IsActionPressed("interagir"))
        {
            // STRICTEMENT RÉSERVÉ AU RAMASSAGE DES OBJETS PHYSIQUES (Phase 2)
            ExecuterRamassageObjet();
        }
        else if (@event.IsActionPressed("changer_main"))
        {
            MainGaucheEstActive = !MainGaucheEstActive;
            RafraichirHUD();
            GD.Print(MainGaucheEstActive ? "ZERO-K : Main Gauche sélectionnée." : "ZERO-K : Main Droite sélectionnée.");
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
        if (main.EstVide || !EstObjetProcedural(main.ID))
        {
            _objetEnMain.Mesh = null;
            return;
        }
        Mesh m = ObtenirMeshDepuisCache(main.ID, main.IndexMorphologique);
        _objetEnMain.Mesh = m;
        if (m != null)
            AppliquerMaterielObjet(_objetEnMain, main.ID);
    }

    /// <summary>Assigne le Mesh exact au SubViewport de chaque slot (pierre en 3D dans l'UI).</summary>
    private void MettreAJourPreviewsSlots()
    {
        MettreAJourPreviewSlot(_meshPreviewGauche, MainGauche);
        MettreAJourPreviewSlot(_meshPreviewDroite, MainDroite);
    }

    private void MettreAJourPreviewSlot(MeshInstance3D meshNode, SlotInventaire slot)
    {
        if (slot.EstVide || !EstObjetProcedural(slot.ID))
        {
            meshNode.Mesh = null;
            return;
        }
        Mesh m = ObtenirMeshDepuisCache(slot.ID, slot.IndexMorphologique);
        meshNode.Mesh = m;
        if (m != null)
            AppliquerMaterielObjet(meshNode, slot.ID);
    }

    /// <summary>Cache le SubViewport quand pas de pierre procédurale, pour laisser voir la couleur du slot.</summary>
    private void MettreAJourVisibilitePreviews()
    {
        if (_viewportSlotGauche != null) _viewportSlotGauche.Visible = !MainGauche.EstVide && EstObjetProcedural(MainGauche.ID);
        if (_viewportSlotDroite != null) _viewportSlotDroite.Visible = !MainDroite.EstVide && EstObjetProcedural(MainDroite.ID);
    }

    private static bool EstObjetProcedural(int id) => id == 10 || id == 11 || id == 12;

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
        return null;
    }

    private static void AppliquerMaterielObjet(MeshInstance3D visuel, int idObjet)
    {
        var mat = new StandardMaterial3D();
        if (idObjet == 11)
        {
            mat.AlbedoColor = new Color(0.1f, 0.1f, 0.15f);
            mat.Roughness = 0.4f;
            mat.Metallic = 0.5f;
        }
        else
        {
            mat.AlbedoColor = new Color(0.4f, 0.4f, 0.4f);
            mat.Roughness = 0.9f;
            mat.Metallic = 0f;
        }
        visuel.MaterialOverride = mat;
    }

    /// <summary>Phase 1 pure : minage du terrain Marching Cubes uniquement. Clic gauche.</summary>
    private void ExecuterMinageVoxel()
    {
        if (!_rayon.IsColliding()) return;
        Node objetTouche = (Node)_rayon.GetCollider();

        // Si le rayon frappe un objet physique (Caillou/Silex/BlocPose), on ne fait rien avec le clic gauche
        if (objetTouche is RigidBody3D) return;
        if (objetTouche.IsInGroup("BlocsPoses")) return;
        if (objetTouche is StaticBody3D sb && sb.GetNodeOrNull<ItemPhysique>("ItemPhysique") != null) return;

        Vector3 pointImpact = _rayon.GetCollisionPoint();
        Vector3 normaleImpact = _rayon.GetCollisionNormal();
        Vector3 pointDeSondage = pointImpact - (normaleImpact * 0.1f);

        int idExtrait = _gestionnaireMonde?.ObtenirMatiereExacte(pointDeSondage) ?? 1;

        if (MainGaucheEstActive && !MainGauche.EstVide && !MainDroite.EstVide) return;
        if (!MainGaucheEstActive && !MainDroite.EstVide && !MainGauche.EstVide) return;

        _gestionnaireMonde?.AppliquerDestructionGlobale(pointImpact, RAYON_SCULPTURE);

        var nouveauSlot = new SlotInventaire { ID = idExtrait, IndexMorphologique = 0 };
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
        SlotInventaire nouveauSlot = default;

        if (objetTouche.IsInGroup("BlocsPoses"))
        {
            int id = objetTouche.HasMeta("ID_Matiere") ? (int)objetTouche.GetMeta("ID_Matiere").AsInt32() : 1;
            var item = objetTouche.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            nouveauSlot = new SlotInventaire { ID = id, IndexMorphologique = item?.IndexCacheMemoire ?? 0 };
        }
        else if (objetTouche is RigidBody3D rb)
        {
            var item = rb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            if (item == null) return;
            if (item.ID_Objet == 13 || item.ID_Objet == 14)
            {
                GD.Print("ZERO-K : Masse excessive. La colonne vertébrale céderait. Action bloquée.");
                return;
            }
            nouveauSlot = new SlotInventaire { ID = item.ID_Objet, IndexMorphologique = item.IndexCacheMemoire };
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
            nouveauSlot = new SlotInventaire { ID = item.ID_Objet, IndexMorphologique = item.IndexCacheMemoire };
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

    /// <summary>Placement (construction ou rejet d'objet). Clic droit.</summary>
    private void ExecuterPlacement()
    {
        SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;

        if (mainActive.EstVide)
        {
            GD.Print("ZERO-K : La main sélectionnée est vide. Impossible de poser.");
            return;
        }

        if (!_rayon.IsColliding()) return;

        Vector3 pointImpact = _rayon.GetCollisionPoint();
        Vector3 normaleImpact = _rayon.GetCollisionNormal();
        Vector3 pointDeChute = pointImpact + (normaleImpact * 0.1f);
        float distance = GlobalPosition.DistanceTo(pointDeChute);
        if (distance < 1.4f) return;

        int id = mainActive.ID;
        if (id >= 1 && id <= 9 && id != 4)
        {
            _gestionnaireMonde?.AppliquerCreationGlobale(pointImpact, normaleImpact, RAYON_SCULPTURE, id);
        }
        else if (id == 999 || id == 10 || id == 11 || id == 12)
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

    /// <summary>Crée un bloc physique posé avec IndexCacheMemoire assigné (forme exacte conservée au rejet).</summary>
    private void CreerBlocPose(Vector3 pointDeChute, SlotInventaire mainActive)
    {
        int id = mainActive.ID;
        Node3D corps;
        if (id == 10 || id == 12) // Petite Pierre ou Pierre Moyenne
        {
            float rayon = id == 10 ? 0.15f : 0.25f;
            float hauteur = rayon * 2f;
            var rb = new RigidBody3D();
            rb.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = rayon, Height = hauteur } });
            rb.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = rayon } });
            corps = rb;
        }
        else if (id == 11) // Silex
        {
            var sb = new StaticBody3D();
            sb.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.2f, 0.15f, 0.25f) } });
            sb.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.2f, 0.15f, 0.25f) } });
            corps = sb;
        }
        else // 999 Buisson
        {
            var sb = new StaticBody3D();
            sb.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            sb.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.8f, 0.2f) } });
            corps = sb;
        }
        corps.GlobalPosition = pointDeChute;
        corps.SetMeta("ID_Matiere", id);
        corps.AddToGroup("BlocsPoses");
        if (id == 10 || id == 11 || id == 12)
        {
            var item = new ItemPhysique { ID_Objet = id, IndexCacheMemoire = mainActive.IndexMorphologique, Name = "ItemPhysique" };
            corps.AddChild(item);
        }
        GetParent().AddChild(corps);
    }

    private float _tempsAttenteSpawn;

    public override void _PhysicsProcess(double delta)
    {
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
