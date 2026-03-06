using Godot;
using System;

public partial class Joueur : CharacterBody3D
{
    public const float Speed = 5.0f;
    public const float JumpVelocity = 4.5f;

    // Sensibilité chirurgicale de la souris
    public const float MouseSensitivity = 0.003f; 

    private Camera3D _camera;
    private RayCast3D _rayCast;
    private Gestionnaire_Monde _gestionnaireMonde;

    public override void _Ready()
    {
        // Capture la souris (elle disparaît et contrôle la vue)
        Input.MouseMode = Input.MouseModeEnum.Captured;
        
        _camera = GetNode<Camera3D>("Camera3D");
        _rayCast = GetNode<RayCast3D>("Camera3D/RayCast3D");
        _gestionnaireMonde = GetParent().GetNode<Gestionnaire_Monde>("Gestionnaire_Monde");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Si le moteur détecte un mouvement de souris
        if (@event is InputEventMouseMotion mouseMotion)
        {
            // Rotation du corps entier sur l'axe Y (Gauche/Droite)
            RotateY(-mouseMotion.Relative.X * MouseSensitivity);

            // Rotation de la tête seule sur l'axe X (Haut/Bas)
            _camera.RotateX(-mouseMotion.Relative.Y * MouseSensitivity);

            // Verrouillage des cervicales pour éviter l'inversion de la colonne vertébrale
            Vector3 cameraRot = _camera.Rotation;
            cameraRot.X = Mathf.Clamp(cameraRot.X, Mathf.DegToRad(-80f), Mathf.DegToRad(80f));
            _camera.Rotation = cameraRot;
        }
        
        // Touche Échap (ui_cancel) pour libérer la souris si tu veux fermer la fenêtre
        if (Input.IsActionJustPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }

        // Clic gauche : destruction globale (tous les chunks concernés)
        if (Input.IsMouseButtonPressed(MouseButton.Left) && _rayCast.IsColliding())
        {
            Vector3 pointImpact = _rayCast.GetCollisionPoint();
            _gestionnaireMonde?.AppliquerDestructionGlobale(pointImpact, 2.0f);
        }

        // Clic droit : création de matière (une seule fois par pression, évite le spam)
        if (@event is InputEventMouseButton mouseBtn && mouseBtn.ButtonIndex == MouseButton.Right && mouseBtn.Pressed)
        {
            if (_rayCast.IsColliding())
            {
                const float rayonCreation = 1.5f;
                Vector3 pointImpact = _rayCast.GetCollisionPoint();
                Vector3 normale = _rayCast.GetCollisionNormal();
                Vector3 pointCible = pointImpact + (normale * rayonCreation);

                float distance = GlobalPosition.DistanceTo(pointCible);
                if (distance >= 1.4f)
                {
                    _gestionnaireMonde?.AppliquerCreationGlobale(pointImpact, normale, rayonCreation);
                }
            }
        }
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