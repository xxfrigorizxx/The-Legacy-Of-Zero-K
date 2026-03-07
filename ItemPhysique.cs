using Godot;

/// <summary>ADN de l'objet libre : identifie ce que le Raycast du joueur ramasse.
/// 10 = Petite Pierre (ramassable), 11 = Silex (ramassable), 12 = Pierre Moyenne (ramassable),
/// 13 = Grosse Pierre (NON ramassable), 14 = Très Grosse Pierre (NON ramassable).</summary>
public partial class ItemPhysique : Node3D
{
	[Export] public int ID_Objet = 0;
}
