using Godot;
using System;

public partial class Joueur : CharacterBody3D
{
    public const float Speed = 5.0f;
    public const float JumpVelocity = 4.5f;

    // Sensibilité chirurgicale de la souris
    public const float MouseSensitivity = 0.003f;

    /// <summary>Rayon du pinceau de sculpture (minage ET pose). Symétrie absolue.</summary>
    private const float RAYON_SCULPTURE = 1.0f;

    // 0 signifie "Main Vide". Toute autre valeur est un ID d'objet (ex: ID_BUISSON_TOMBÉ)
    public int MainGauche = 0;
    public int MainDroite = 0;
    /// <summary>True = Slot gauche sélectionné (Main Active), False = Slot droit</summary>
    public bool MainGaucheEstActive = true;

    private Camera3D _camera;
    private RayCast3D _rayon;
    private Gestionnaire_Monde _gestionnaireMonde;
    private Panel _slotGauche;
    private Panel _slotDroite;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;

        _camera = GetNode<Camera3D>("Camera3D");
        _rayon = GetNode<RayCast3D>("Camera3D/RayCast3D");
        _gestionnaireMonde = GetParent().GetNode<Gestionnaire_Monde>("Gestionnaire_Monde");
        _slotGauche = GetParent().GetNode<Panel>("Gestionnaire_Monde/HUD_Inventaire/Conteneur_Ancrage/Boite_Slots/Slot_Main_Gauche");
        _slotDroite = GetParent().GetNode<Panel>("Gestionnaire_Monde/HUD_Inventaire/Conteneur_Ancrage/Boite_Slots/Slot_Main_Droite");

        RafraichirHUD();
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

    private void MettreAJourSlotUI(Panel slot, int idMatiere, bool selectionne)
    {
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

        if (MainGaucheEstActive && MainGauche != 0 && MainDroite != 0) return;
        if (!MainGaucheEstActive && MainDroite != 0 && MainGauche != 0) return;

        _gestionnaireMonde?.AppliquerDestructionGlobale(pointImpact, RAYON_SCULPTURE);

        if (MainGaucheEstActive)
        {
            if (MainGauche == 0) MainGauche = idExtrait;
            else MainDroite = idExtrait;
        }
        else
        {
            if (MainDroite == 0) MainDroite = idExtrait;
            else MainGauche = idExtrait;
        }
        RafraichirHUD();
    }

    /// <summary>Phase 2 pure : ramassage des objets physiques (Caillou, Silex, BlocsPoses). Touche E (interagir).</summary>
    private void ExecuterRamassageObjet()
    {
        if (MainGauche != 0 && MainDroite != 0) return;
        if (!_rayon.IsColliding()) return;

        Node objetTouche = (Node)_rayon.GetCollider();
        int idObjet = 0;

        if (objetTouche.IsInGroup("BlocsPoses"))
        {
            idObjet = objetTouche.HasMeta("ID_Matiere") ? (int)objetTouche.GetMeta("ID_Matiere").AsInt32() : 1;
        }
        else if (objetTouche is RigidBody3D rb)
        {
            var item = rb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            if (item == null) return;
            idObjet = item.ID_Objet;
            if (idObjet == 13 || idObjet == 14)
            {
                GD.Print("ZERO-K : Masse excessive. La colonne vertébrale céderait. Action bloquée.");
                return;
            }
        }
        else if (objetTouche is StaticBody3D sb)
        {
            var item = sb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            if (item == null) return;
            idObjet = item.ID_Objet;
            if (idObjet == 13 || idObjet == 14)
            {
                GD.Print("ZERO-K : Masse excessive. La colonne vertébrale céderait. Action bloquée.");
                return;
            }
        }
        else
            return;

        if (MainGaucheEstActive)
        {
            if (MainGauche == 0) MainGauche = idObjet;
            else if (MainDroite == 0) MainDroite = idObjet;
            else return;
        }
        else
        {
            if (MainDroite == 0) MainDroite = idObjet;
            else if (MainGauche == 0) MainGauche = idObjet;
            else return;
        }
        objetTouche.QueueFree();
        RafraichirHUD();
    }

    /// <summary>Placement (construction ou rejet d'objet). Clic droit.</summary>
    private void ExecuterPlacement()
    {
        int matiereActive = MainGaucheEstActive ? MainGauche : MainDroite;

        if (matiereActive == 0)
        {
            GD.Print("ZERO-K : La main sélectionnée est vide. Impossible de poser.");
            return;
        }

        if (!_rayon.IsColliding()) return;

        Vector3 pointImpact = _rayon.GetCollisionPoint();
        Vector3 normaleImpact = _rayon.GetCollisionNormal();
        // On réduit le multiplicateur pour que le bloc s'enfonce légèrement dans le sol au lieu de léviter
        Vector3 pointDeChute = pointImpact + (normaleImpact * 0.1f);
        float distance = GlobalPosition.DistanceTo(pointDeChute);
        if (distance < 1.4f) return;

        // Route unique : tout ID géologique (1, 2, 3, 5, 6, 7, 8, 9) va au Marching Cubes. L'eau (4) n'est pas posable.
        if (matiereActive >= 1 && matiereActive <= 9 && matiereActive != 4)
        {
            _gestionnaireMonde?.AppliquerCreationGlobale(pointImpact, normaleImpact, RAYON_SCULPTURE, matiereActive);
        }
        else if (matiereActive == 999 || matiereActive == 10 || matiereActive == 11 || matiereActive == 12)
        {
            // Buisson, Petite Pierre, Silex, Pierre Moyenne : objets physiques posables (hors terrain voxel).
            CreerBlocPose(pointDeChute, matiereActive);
        }
        else
        {
            GD.Print($"ZERO-K : Matière {matiereActive} non géologique. Pose ignorée.");
        }

        if (MainGaucheEstActive) MainGauche = 0;
        else MainDroite = 0;

        RafraichirHUD();
    }

    /// <summary>Crée un bloc physique posé (Buisson, pierres 10/12, Silex). Les IDs géologiques passent par le terrain voxel.</summary>
    private void CreerBlocPose(Vector3 pointDeChute, int matiereActive)
    {
        Node3D corps;
        if (matiereActive == 10 || matiereActive == 12) // Petite Pierre ou Pierre Moyenne
        {
            float rayon = matiereActive == 10 ? 0.15f : 0.25f;
            float hauteur = rayon * 2f;
            var rb = new RigidBody3D();
            rb.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = rayon, Height = hauteur }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.45f, 0.4f) } });
            rb.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = rayon } });
            corps = rb;
        }
        else if (matiereActive == 11) // Silex
        {
            var sb = new StaticBody3D();
            sb.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.2f, 0.15f, 0.25f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.55f, 0.5f) } });
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
        corps.SetMeta("ID_Matiere", matiereActive);
        corps.AddToGroup("BlocsPoses");
        if (matiereActive == 10 || matiereActive == 11 || matiereActive == 12)
            corps.AddChild(new ItemPhysique { ID_Objet = matiereActive, Name = "ItemPhysique" });
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
