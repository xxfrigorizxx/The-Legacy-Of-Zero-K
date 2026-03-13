using Godot;
using System;

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
                // Si c'est du terrain OU que le clic a été très bref, on pose sur le sol (fusion ou dépose)
                if (estTerrainVoxel || _forceLancer < 0.2f)
                {
                    ExecuterPlacement();
                }
                else
                {
                    // C'est un objet physique (pierre, arme) ET le clic a été long -> On lance
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
        Mesh m = main.EstUnEclat ? main.MeshEclat : ObtenirMeshDepuisCache(main.ID, main.IndexMorphologique);
        _objetEnMain.Mesh = m;
        if (m != null && !main.EstUnEclat)
            AppliquerMaterielObjet(_objetEnMain, main.ID, main.IndexChimique);
        // Éclat : le mesh a déjà le matériel intégré (SurfaceTool)
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
        Mesh m = slot.EstUnEclat ? slot.MeshEclat : ObtenirMeshDepuisCache(slot.ID, slot.IndexMorphologique);
        meshNode.Mesh = m;
        if (m != null && !slot.EstUnEclat)
            AppliquerMaterielObjet(meshNode, slot.ID, slot.IndexChimique);
        // Éclat : le mesh a déjà le matériel intégré
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

    private static void AppliquerMaterielObjet(MeshInstance3D visuel, int idObjet, int indexChimique)
    {
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
            var item = objetTouche as ItemPhysique ?? (objetTouche as Node)?.GetNodeOrNull<ItemPhysique>("ItemPhysique");
            nouveauSlot = new SlotInventaire
            {
                ID = id,
                IndexMorphologique = item?.IndexCacheMemoire ?? 0,
                IndexChimique = item?.IndexChimique ?? 0,
                EstUnEclat = item?.EstUnEclat ?? false,
                MeshEclat = (item != null && item.EstUnEclat) ? item.ObtenirMeshVisuel() : null,
                NiveauFracture = item?.NiveauFracture ?? 0
            };
        }
        else if (objetTouche is RigidBody3D rb)
        {
            var item = rb as ItemPhysique ?? rb.GetNodeOrNull<ItemPhysique>("ItemPhysique");
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
                MeshEclat = item.EstUnEclat ? item.ObtenirMeshVisuel() : null
            };
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
                NiveauFracture = item.NiveauFracture
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
        // Objets physiques (roches, silex, buisson) → déposer un bloc au sol
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

    /// <summary>Frappe la roche visée : impulsion + dégâts. Si résistance à 0 → fracture. force = temps de charge (0.1 à 2 s).</summary>
    private void ExecuterFrappe(float force)
    {
        SlotInventaire mainActive = MainGaucheEstActive ? MainGauche : MainDroite;
        if (mainActive.EstVide || !EstObjetProcedural(mainActive.ID)) return;
        _rayon.ForceRaycastUpdate();
        if (!_rayon.IsColliding()) return;

        Object colliderObj = _rayon.GetCollider();
        Node objetTouche = colliderObj as Node;
        if (objetTouche == null) return; // Pas de cible valide (terrain bas-niveau ou autre)

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
            // Reconstruction de l'éclat jeté : mesh dynamique conservé, reste un éclat pour la vie
            var item = new ItemPhysique
            {
                ID_Objet = mainActive.ID,
                IndexChimique = mainActive.IndexChimique,
                EstUnEclat = true,
                NiveauFracture = mainActive.NiveauFracture,
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
