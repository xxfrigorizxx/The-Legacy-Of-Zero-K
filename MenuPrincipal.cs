using Godot;
using System.Collections.Generic;

/// <summary>Menu principal : New World, Load Game, Quit.</summary>
public partial class MenuPrincipal : Control
{
	private VBoxContainer _vbox;
	private OptionButton _listeMondes;
	private Button _btnNewWorld;
	private Button _btnLoadGame;
	private Button _btnQuit;

	public override void _Ready()
	{
		_vbox = GetNode<VBoxContainer>("VBoxContainer");
		_btnNewWorld = _vbox.GetNode<Button>("BtnNewWorld");
		_btnLoadGame = _vbox.GetNode<Button>("BtnLoadGame");
		_listeMondes = _vbox.GetNode<OptionButton>("ListeMondes");
		_btnQuit = _vbox.GetNode<Button>("BtnQuit");

		_btnNewWorld.Pressed += OnNewWorld;
		_btnLoadGame.Pressed += OnLoadGame;
		_btnQuit.Pressed += () => GetTree().Quit();

		RafraichirListeMondes();
	}

	private GameState Etat => GetNode<GameState>("/root/GameState");

	private void RafraichirListeMondes()
	{
		_listeMondes.Clear();
		_listeMondes.AddItem("-- Choisir un monde --", 0);
		var mondes = Etat.ObtenirListeMondes();
		for (int i = 0; i < mondes.Count; i++)
			_listeMondes.AddItem(mondes[i], i + 1);
	}

	private void OnNewWorld()
	{
		Etat.CreerNouveauMonde();
		GetTree().ChangeSceneToFile("res://monde_zero.tscn");
	}

	private void OnLoadGame()
	{
		int idx = _listeMondes.Selected;
		if (idx <= 0) return;
		string nom = _listeMondes.GetItemText(idx);
		if (!Etat.ChargerMonde(nom)) return;
		GetTree().ChangeSceneToFile("res://monde_zero.tscn");
	}
}
