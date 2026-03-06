using Godot;
using Godot.Collections;

[Tool]
public partial class ForgeLivreTerrain : Node
{
	[Export] public Texture2D[] TexturesInput = new Texture2D[10];

	private bool _boutonGenerer;

	[Export]
	public bool BoutonGenerer
	{
		get => _boutonGenerer;
		set
		{
			if (value == true)
			{
				CreerLivre();
			}
			_boutonGenerer = false; // Décoche la case automatiquement après l'action
		}
	}

	private void CreerLivre()
	{
		var images = new Array<Image>();
		Vector2I? tailleRef = null;
		Image.Format formatRef = Image.Format.Rgba8;

		for (int i = 0; i < TexturesInput.Length; i++)
		{
			Image img;
			if (TexturesInput[i] != null)
			{
				img = TexturesInput[i].GetImage();
				if (img == null || img.GetWidth() == 0 || img.GetHeight() == 0)
				{
					GD.PrintErr($"ForgeLivreTerrain : texture slot {i} invalide, placeholder utilisé.");
					img = Image.CreateEmpty(256, 256, false, Image.Format.Rgba8);
					img.Fill(new Color(0.5f, 0.5f, 0.5f, 1f));
				}
			}
			else
			{
				img = Image.CreateEmpty(256, 256, false, Image.Format.Rgba8);
				img.Fill(new Color(0.3f, 0.3f, 0.3f, 1f));
			}

			if (tailleRef == null)
			{
				tailleRef = new Vector2I(img.GetWidth(), img.GetHeight());
				formatRef = img.GetFormat();
			}
			else if (img.GetWidth() != tailleRef.Value.X || img.GetHeight() != tailleRef.Value.Y)
			{
				img.Resize(tailleRef.Value.X, tailleRef.Value.Y);
			}

			if (img.GetFormat() != formatRef)
				img.Convert(formatRef);

			images.Add(img);
		}

		if (images.Count == 0)
		{
			GD.PrintErr("ForgeLivreTerrain : aucune texture valide.");
			return;
		}

		var livre = new Texture2DArray();
		Error err = livre.CreateFromImages(images);
		if (err != Error.Ok)
		{
			GD.PrintErr($"ForgeLivreTerrain : CreateFromImages échoué ({err}).");
			return;
		}

		err = ResourceSaver.Save(livre, "res://Livre_Terrain.tres", ResourceSaver.SaverFlags.Compress);
		if (err != Error.Ok)
		{
			GD.PrintErr($"ForgeLivreTerrain : sauvegarde échouée ({err}).");
			return;
		}

		GD.Print("ForgeLivreTerrain : Livre_Terrain.tres créé avec ", images.Count, " couches.");
	}
}
