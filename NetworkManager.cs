using Godot;
using System;

/// <summary>Gestionnaire ENet pour Solo (Host local) et MMORPG. Héberger/Solo = serveur 25565 + client local.</summary>
public partial class NetworkManager : Node
{
	public const ushort PortServeur = 25565;

	private ENetMultiplayerPeer _peer;
	private bool _estServeur;

	public bool EstServeur => _estServeur;
	public bool EstConnecte => Multiplayer.HasMultiplayerPeer() && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

	/// <summary>Démarre en mode Héberger/Solo : crée un serveur et connecte le client local.</summary>
	public void DemarrerHostSolo()
	{
		_peer = new ENetMultiplayerPeer();
		Error err = _peer.CreateServer(PortServeur);
		if (err != Error.Ok)
		{
			GD.PrintErr($"NetworkManager: création serveur échouée ({err})");
			return;
		}

		Multiplayer.MultiplayerPeer = _peer;
		_estServeur = true;

		_peer.PeerConnected += (id) => GD.Print($"Client connecté: {id}");
		_peer.PeerDisconnected += (id) => GD.Print($"Client déconnecté: {id}");

		// En Solo: l'hôte EST le serveur (GetUniqueId() == 1). Pas besoin de connexion client.
		GD.Print("NetworkManager: Serveur démarré (Solo/Host) sur port ", PortServeur);
	}

	/// <summary>Connecte le client à une adresse. Pour rejoindre un serveur distant.</summary>
	public void ConnecterClient(string adresse = "127.0.0.1")
	{
		_peer = new ENetMultiplayerPeer();
		Error err = _peer.CreateClient(adresse, PortServeur);
		if (err != Error.Ok)
		{
			GD.PrintErr($"NetworkManager: connexion client échouée ({err})");
			return;
		}
		Multiplayer.MultiplayerPeer = _peer;
		_estServeur = false;
	}

	/// <summary>RPC appelé par le client pour demander la destruction d'un bloc.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
	public void DemanderDestructionBloc(Vector3I pos, float rayon)
	{
		int idAppelant = Multiplayer.GetRemoteSenderId();
		// Le serveur traite et répond par ActualiserBlocClient
		EmitSignal(SignalName.DestructionDemandee, pos, rayon, idAppelant);
	}

	/// <summary>RPC envoyé par le serveur pour notifier les clients d'un changement de bloc.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
	public void ActualiserBlocClient(Vector3I pos, int nouvelId)
	{
		EmitSignal(SignalName.BlocActualise, pos, nouvelId);
	}

	[Signal]
	public delegate void DestructionDemandeeEventHandler(Vector3I pos, float rayon, long peerId);

	[Signal]
	public delegate void BlocActualiseEventHandler(Vector3I pos, int nouvelId);
}
