using Godot;
using System;
using System.Buffers;
using System.Threading.Tasks;

public partial class Generateur_Voxel : Node3D
{
	[ThreadStatic] private static float[] _valsRecyclables;
	[ThreadStatic] private static Vector3[] _vertsRecyclables;
	[ThreadStatic] private static Vector3[] _vertListRecyclables;
	[ThreadStatic] private static float[] _valsEauRecyclables;
	[ThreadStatic] private static Vector3[] _vertsEauRecyclables;
	[ThreadStatic] private static Vector3[] _vertListEauRecyclables;

	private const int TAILLE_MAX_SECTION = 17 * 17 * 17;
	[Export] public int TailleChunk = 16;
	[Export] public int HauteurMax = 720;  // Montagnes jusqu'à 700
	[Export] public float EchelleBruit = 0.03f;
	[Export] public float AmplitudeBruit = 35.0f;
	[Export] public int SeedTerrain = 19847;
	public int ChunkOffsetX { get; set; }
	public int ChunkOffsetZ { get; set; }

	private const float Isolevel = 0.0f;
	private const int HAUTEUR_SECTION = 16;
	private const int NB_SECTIONS = 16;

	// Constantes de la nouvelle ère (hauteurs relatives)
	private int NiveauEau = 103;           // Océan à Y=103 (+1 m)
	private int ProfondeurBase = 104;     // Socle continental (légèrement au-dessus de l'eau)
	private int AmplitudeMontagne = 396;  // Max ~500 (très rare en haut)

	// [LEGACY] Remplacé par injection d'ID dans canal rouge pour shader triplanaire
	// private static Color ObtenirCouleurMateriau(byte id) { ... }

	private FastNoiseLite _noiseSurface;
	private FastNoiseLite _noiseErosion;
	private FastNoiseLite _noiseTemperature;
	private FastNoiseLite _noiseHumidite;
	private FastNoiseLite _noiseCavernes;
	private FastNoiseLite _noiseRivieres;
	private FastNoiseLite _noiseNeige;
	private MeshInstance3D[] _sectionsTerrain;
	private MeshInstance3D[] _sectionsEau;
	private CollisionShape3D[] _sectionsPhysiques;
	[Export] public Material MaterielTerre;
	private float[,,] _densities;
	private float[,,] _densitiesEau;
	private byte[,,] _materials;
	private readonly object _verrouVoxel = new object();

	// Tables Marching Cubes (Paul Bourke)
	/// <summary>Exposé pour ConstantesMarchingCubes (Client).</summary>
	public static int[,] GetTriTableShared() => TriTable;

	private static readonly int[] EdgeTable = {
		0x0  , 0x109, 0x203, 0x30a, 0x406, 0x50f, 0x605, 0x70c,
		0x80c, 0x905, 0xa0f, 0xb06, 0xc0a, 0xd03, 0xe09, 0xf00,
		0x190, 0x99 , 0x393, 0x29a, 0x596, 0x49f, 0x795, 0x69c,
		0x99c, 0x895, 0xb9f, 0xa96, 0xd9a, 0xc93, 0xf99, 0xe90,
		0x230, 0x339, 0x33 , 0x13a, 0x636, 0x73f, 0x435, 0x53c,
		0xa3c, 0xb35, 0x83f, 0x936, 0xe3a, 0xf33, 0xc39, 0xd30,
		0x3a0, 0x2a9, 0x1a3, 0xaa , 0x7a6, 0x6af, 0x5a5, 0x4ac,
		0xbac, 0xaa5, 0x9af, 0x8a6, 0xfaa, 0xea3, 0xda9, 0xca0,
		0x460, 0x569, 0x663, 0x76a, 0x66 , 0x16f, 0x265, 0x36c,
		0xc6c, 0xd65, 0xe6f, 0xf66, 0x86a, 0x963, 0xa69, 0xb60,
		0x5f0, 0x4f9, 0x7f3, 0x6fa, 0x1f6, 0xff , 0x3f5, 0x2fc,
		0xdfc, 0xcf5, 0xfff, 0xef6, 0x9fa, 0x8f3, 0xbf9, 0xaf0,
		0x650, 0x759, 0x453, 0x55a, 0x256, 0x35f, 0x55 , 0x15c,
		0xe5c, 0xf55, 0xc5f, 0xd56, 0xa5a, 0xb53, 0x859, 0x950,
		0x7c0, 0x6c9, 0x5c3, 0x4ca, 0x3c6, 0x2cf, 0x1c5, 0xcc ,
		0xfcc, 0xec5, 0xdcf, 0xcc6, 0xbca, 0xac3, 0x9c9, 0x8c0,
		0x8c0, 0x9c9, 0xac3, 0xbca, 0xcc6, 0xdcf, 0xec5, 0xfcc,
		0xcc , 0x1c5, 0x2cf, 0x3c6, 0x4ca, 0x5c3, 0x6c9, 0x7c0,
		0x950, 0x859, 0xb53, 0xa5a, 0xd56, 0xc5f, 0xf55, 0xe5c,
		0x15c, 0x55 , 0x35f, 0x256, 0x55a, 0x453, 0x759, 0x650,
		0xaf0, 0xbf9, 0x8f3, 0x9fa, 0xef6, 0xfff, 0xcf5, 0xdfc,
		0x2fc, 0x3f5, 0xff , 0x1f6, 0x6fa, 0x7f3, 0x4f9, 0x5f0,
		0xb60, 0xa69, 0x963, 0x86a, 0xf66, 0xe6f, 0xd65, 0xc6c,
		0x36c, 0x265, 0x16f, 0x66 , 0x76a, 0x663, 0x569, 0x460,
		0xca0, 0xda9, 0xea3, 0xfaa, 0x8a6, 0x9af, 0xaa5, 0xbac,
		0x4ac, 0x5a5, 0x6af, 0x7a6, 0xaa , 0x1a3, 0x2a9, 0x3a0,
		0xd30, 0xc39, 0xf33, 0xe3a, 0x936, 0x83f, 0xb35, 0xa3c,
		0x53c, 0x435, 0x73f, 0x636, 0x13a, 0x33 , 0x339, 0x230,
		0xe90, 0xf99, 0xc93, 0xd9a, 0xa96, 0xb9f, 0x895, 0x99c,
		0x69c, 0x795, 0x49f, 0x596, 0x29a, 0x393, 0x99 , 0x190,
		0xf00, 0xe09, 0xd03, 0xc0a, 0xb06, 0xa0f, 0x905, 0x80c,
		0x70c, 0x605, 0x50f, 0x406, 0x30a, 0x203, 0x109, 0x0
	};

	private static readonly int[,] TriTable = {
		{-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,8,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,1,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,8,3,9,8,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,2,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,8,3,1,2,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{9,2,10,0,2,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{2,8,3,2,10,8,10,9,8,-1,-1,-1,-1,-1,-1,-1},
		{3,11,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,11,2,8,11,0,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,9,0,2,3,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,11,2,1,9,11,9,8,11,-1,-1,-1,-1,-1,-1,-1},
		{3,10,1,11,10,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,10,1,0,8,10,8,11,10,-1,-1,-1,-1,-1,-1,-1},
		{3,9,0,3,11,9,11,10,9,-1,-1,-1,-1,-1,-1,-1},
		{9,8,10,10,8,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,7,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,3,0,7,3,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,1,9,8,4,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,1,9,4,7,1,7,3,1,-1,-1,-1,-1,-1,-1,-1},
		{1,2,10,8,4,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{3,4,7,3,0,4,1,2,10,-1,-1,-1,-1,-1,-1,-1},
		{9,2,10,9,0,2,8,4,7,-1,-1,-1,-1,-1,-1,-1},
		{2,10,9,2,9,7,2,7,3,7,9,4,-1,-1,-1,-1},
		{8,4,7,3,11,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{11,4,7,11,2,4,2,0,4,-1,-1,-1,-1,-1,-1,-1},
		{9,0,1,8,4,7,2,3,11,-1,-1,-1,-1,-1,-1,-1},
		{4,7,11,9,4,11,9,11,2,9,2,1,-1,-1,-1,-1},
		{3,10,1,3,11,10,7,8,4,-1,-1,-1,-1,-1,-1,-1},
		{1,11,10,1,4,11,1,0,4,7,11,4,-1,-1,-1,-1},
		{4,7,8,9,0,11,9,11,10,11,0,3,-1,-1,-1,-1},
		{4,7,11,4,11,9,9,11,10,-1,-1,-1,-1,-1,-1,-1},
		{9,5,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{9,5,4,0,8,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,5,4,1,5,0,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{8,5,4,8,3,5,3,1,5,-1,-1,-1,-1,-1,-1,-1},
		{1,2,10,9,5,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{3,0,8,1,2,10,4,9,5,-1,-1,-1,-1,-1,-1,-1},
		{5,2,10,5,4,2,4,0,2,-1,-1,-1,-1,-1,-1,-1},
		{2,10,5,3,2,5,3,5,4,3,4,8,-1,-1,-1,-1},
		{9,5,4,2,3,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,11,2,0,8,11,4,9,5,-1,-1,-1,-1,-1,-1,-1},
		{0,5,4,0,1,5,2,3,11,-1,-1,-1,-1,-1,-1,-1},
		{2,1,5,2,5,8,2,8,11,4,8,5,-1,-1,-1,-1},
		{10,3,11,10,1,3,9,5,4,-1,-1,-1,-1,-1,-1,-1},
		{4,9,5,0,8,1,8,10,1,8,11,10,-1,-1,-1,-1},
		{5,4,0,5,0,11,5,11,10,11,0,3,-1,-1,-1,-1},
		{5,4,8,5,8,10,10,8,11,-1,-1,-1,-1,-1,-1,-1},
		{9,7,8,5,7,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{9,3,0,9,5,3,5,7,3,-1,-1,-1,-1,-1,-1,-1},
		{0,7,8,0,1,7,1,5,7,-1,-1,-1,-1,-1,-1,-1},
		{1,5,3,3,5,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{9,7,8,9,5,7,10,1,2,-1,-1,-1,-1,-1,-1,-1},
		{10,1,2,9,5,0,5,3,0,5,7,3,-1,-1,-1,-1},
		{8,0,2,8,2,5,8,5,7,10,5,2,-1,-1,-1,-1},
		{2,10,5,2,5,3,3,5,7,-1,-1,-1,-1,-1,-1,-1},
		{7,9,5,7,8,9,3,11,2,-1,-1,-1,-1,-1,-1,-1},
		{9,5,7,9,7,2,9,2,0,2,7,11,-1,-1,-1,-1},
		{2,3,11,0,1,8,1,7,8,1,5,7,-1,-1,-1,-1},
		{11,2,1,11,1,7,7,1,5,-1,-1,-1,-1,-1,-1,-1},
		{9,5,8,8,5,7,10,1,3,10,3,11,-1,-1,-1,-1},
		{5,7,0,5,0,9,7,11,0,1,0,10,11,10,0,-1},
		{11,10,0,11,0,3,10,5,0,8,0,7,5,7,0,-1},
		{11,10,5,7,11,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{10,6,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,8,3,5,10,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{9,0,1,5,10,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,8,3,1,9,8,5,10,6,-1,-1,-1,-1,-1,-1,-1},
		{1,6,5,2,6,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,6,5,1,2,6,3,0,8,-1,-1,-1,-1,-1,-1,-1},
		{9,6,5,9,0,6,0,2,6,-1,-1,-1,-1,-1,-1,-1},
		{5,9,8,5,8,2,5,2,6,3,2,8,-1,-1,-1,-1},
		{2,3,11,10,6,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{11,0,8,11,2,0,10,6,5,-1,-1,-1,-1,-1,-1,-1},
		{0,1,9,2,3,11,5,10,6,-1,-1,-1,-1,-1,-1,-1},
		{5,10,6,1,9,2,9,11,2,9,8,11,-1,-1,-1,-1},
		{6,3,11,6,5,3,5,1,3,-1,-1,-1,-1,-1,-1,-1},
		{0,8,11,0,11,5,0,5,1,5,11,6,-1,-1,-1,-1},
		{3,11,6,0,3,6,0,6,5,0,5,9,-1,-1,-1,-1},
		{6,5,9,6,9,11,11,9,8,-1,-1,-1,-1,-1,-1,-1},
		{5,10,6,4,7,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,3,0,4,7,3,6,5,10,-1,-1,-1,-1,-1,-1,-1},
		{1,9,0,5,10,6,8,4,7,-1,-1,-1,-1,-1,-1,-1},
		{10,6,5,1,9,7,1,7,3,7,9,4,-1,-1,-1,-1},
		{6,1,2,6,5,1,4,7,8,-1,-1,-1,-1,-1,-1,-1},
		{1,2,5,5,2,6,3,0,4,3,4,7,-1,-1,-1,-1},
		{8,4,7,9,0,5,0,6,5,0,2,6,-1,-1,-1,-1},
		{7,3,9,7,9,4,3,2,9,5,9,6,2,6,9,-1},
		{3,11,2,7,8,4,10,6,5,-1,-1,-1,-1,-1,-1,-1},
		{5,10,6,4,7,2,4,2,0,2,7,11,-1,-1,-1,-1},
		{0,1,9,4,7,8,2,3,11,5,10,6,-1,-1,-1,-1},
		{9,2,1,9,11,2,9,4,11,7,11,4,5,10,6,-1},
		{8,4,7,3,11,5,3,5,1,5,11,6,-1,-1,-1,-1},
		{5,1,11,5,11,6,1,0,11,7,11,4,0,4,11,-1},
		{0,5,9,0,6,5,0,3,6,11,6,3,8,4,7,-1},
		{6,5,9,6,9,11,4,7,9,7,11,9,-1,-1,-1,-1},
		{10,4,9,6,4,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,10,6,4,9,10,0,8,3,-1,-1,-1,-1,-1,-1,-1},
		{10,0,1,10,6,0,6,4,0,-1,-1,-1,-1,-1,-1,-1},
		{8,3,1,8,1,6,8,6,4,6,1,10,-1,-1,-1,-1},
		{1,4,9,1,2,4,2,6,4,-1,-1,-1,-1,-1,-1,-1},
		{3,0,8,1,2,9,2,4,9,2,6,4,-1,-1,-1,-1},
		{0,2,4,4,2,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{8,3,2,8,2,4,4,2,6,-1,-1,-1,-1,-1,-1,-1},
		{10,4,9,10,6,4,11,2,3,-1,-1,-1,-1,-1,-1,-1},
		{0,8,2,2,8,11,4,9,10,4,10,6,-1,-1,-1,-1},
		{3,11,2,0,1,6,0,6,4,6,1,10,-1,-1,-1,-1},
		{6,4,1,6,1,10,4,8,1,2,1,11,8,11,1,-1},
		{9,6,4,9,3,6,9,1,3,11,6,3,-1,-1,-1,-1},
		{8,11,1,8,1,0,11,6,1,9,1,4,6,4,1,-1},
		{3,11,6,3,6,0,0,6,4,-1,-1,-1,-1,-1,-1,-1},
		{6,4,8,11,6,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{7,10,6,7,8,10,8,9,10,-1,-1,-1,-1,-1,-1,-1},
		{0,7,3,0,10,7,0,9,10,6,7,10,-1,-1,-1,-1},
		{10,6,7,1,10,7,1,7,8,1,8,0,-1,-1,-1,-1},
		{10,6,7,10,7,1,1,7,3,-1,-1,-1,-1,-1,-1,-1},
		{1,2,6,1,6,8,1,8,9,8,6,7,-1,-1,-1,-1},
		{2,6,9,2,9,1,6,7,9,0,9,3,7,3,9,-1},
		{7,8,0,7,0,6,6,0,2,-1,-1,-1,-1,-1,-1,-1},
		{7,3,2,6,7,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{2,3,11,10,6,8,10,8,9,8,6,7,-1,-1,-1,-1},
		{2,0,7,2,7,11,0,9,7,6,7,10,9,10,7,-1},
		{1,8,0,1,7,8,1,10,7,6,7,10,2,3,11,-1},
		{11,2,1,11,1,7,10,6,1,6,7,1,-1,-1,-1,-1},
		{8,9,6,8,6,7,9,1,6,11,6,3,1,3,6,-1},
		{0,9,1,11,6,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{7,8,0,7,0,6,3,11,0,11,6,0,-1,-1,-1,-1},
		{7,11,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{7,6,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{3,0,8,11,7,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,1,9,11,7,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{8,1,9,8,3,1,11,7,6,-1,-1,-1,-1,-1,-1,-1},
		{10,1,2,6,11,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,2,10,3,0,8,6,11,7,-1,-1,-1,-1,-1,-1,-1},
		{2,9,0,2,10,9,6,11,7,-1,-1,-1,-1,-1,-1,-1},
		{6,11,7,2,10,3,10,8,3,10,9,8,-1,-1,-1,-1},
		{7,2,3,6,2,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{7,0,8,7,6,0,6,2,0,-1,-1,-1,-1,-1,-1,-1},
		{2,7,6,2,3,7,0,1,9,-1,-1,-1,-1,-1,-1,-1},
		{1,6,2,1,8,6,1,9,8,8,7,6,-1,-1,-1,-1},
		{10,7,6,10,1,7,1,3,7,-1,-1,-1,-1,-1,-1,-1},
		{10,7,6,1,7,10,1,8,7,1,0,8,-1,-1,-1,-1},
		{0,3,7,0,7,10,0,10,9,6,10,7,-1,-1,-1,-1},
		{7,6,10,7,10,8,8,10,9,-1,-1,-1,-1,-1,-1,-1},
		{6,8,4,11,8,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{3,6,11,3,0,6,0,4,6,-1,-1,-1,-1,-1,-1,-1},
		{8,6,11,8,4,6,9,0,1,-1,-1,-1,-1,-1,-1,-1},
		{9,4,6,9,6,3,9,3,1,11,3,6,-1,-1,-1,-1},
		{6,8,4,6,11,8,2,10,1,-1,-1,-1,-1,-1,-1,-1},
		{1,2,10,3,0,11,0,6,11,0,4,6,-1,-1,-1,-1},
		{4,11,8,4,6,11,0,2,9,2,10,9,-1,-1,-1,-1},
		{10,9,3,10,3,2,9,4,3,11,3,6,4,6,3,-1},
		{8,2,3,8,4,2,4,6,2,-1,-1,-1,-1,-1,-1,-1},
		{0,4,2,4,6,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,9,0,2,3,4,2,4,6,4,3,8,-1,-1,-1,-1},
		{1,9,4,1,4,2,2,4,6,-1,-1,-1,-1,-1,-1,-1},
		{8,1,3,8,6,1,8,4,6,6,10,1,-1,-1,-1,-1},
		{10,1,0,10,0,6,6,0,4,-1,-1,-1,-1,-1,-1,-1},
		{4,6,3,4,3,8,6,10,3,0,3,9,10,9,3,-1},
		{10,9,4,6,10,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,9,5,7,6,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,8,3,4,9,5,11,7,6,-1,-1,-1,-1,-1,-1,-1},
		{5,0,1,5,4,0,7,6,11,-1,-1,-1,-1,-1,-1,-1},
		{11,7,6,8,3,4,3,5,4,3,1,5,-1,-1,-1,-1},
		{9,5,4,10,1,2,7,6,11,-1,-1,-1,-1,-1,-1,-1},
		{6,11,7,1,2,10,0,8,3,4,9,5,-1,-1,-1,-1},
		{7,6,11,5,4,10,4,2,10,4,0,2,-1,-1,-1,-1},
		{3,4,8,3,5,4,3,2,5,10,5,2,11,7,6,-1},
		{7,2,3,7,6,2,5,4,9,-1,-1,-1,-1,-1,-1,-1},
		{9,5,4,0,8,6,0,6,2,6,8,7,-1,-1,-1,-1},
		{3,6,2,3,7,6,1,5,0,5,4,0,-1,-1,-1,-1},
		{6,2,8,6,8,7,2,1,8,4,8,5,1,5,8,-1},
		{9,5,4,10,1,6,1,7,6,1,3,7,-1,-1,-1,-1},
		{1,6,10,1,7,6,1,0,7,8,7,0,9,5,4,-1},
		{4,0,10,4,10,5,0,3,10,6,10,7,3,7,10,-1},
		{7,6,10,7,10,8,5,4,10,4,8,10,-1,-1,-1,-1},
		{6,9,5,6,11,9,11,8,9,-1,-1,-1,-1,-1,-1,-1},
		{3,6,11,0,6,3,0,5,6,0,9,5,-1,-1,-1,-1},
		{0,11,8,0,5,11,0,1,5,5,6,11,-1,-1,-1,-1},
		{6,11,3,6,3,5,5,3,1,-1,-1,-1,-1,-1,-1,-1},
		{1,2,10,9,5,11,9,11,8,11,5,6,-1,-1,-1,-1},
		{0,11,3,0,6,11,0,9,6,5,6,9,1,2,10,-1},
		{11,8,5,11,5,6,8,0,5,10,5,2,0,2,5,-1},
		{6,11,3,6,3,5,2,10,3,10,5,3,-1,-1,-1,-1},
		{5,8,9,5,2,8,5,6,2,3,8,2,-1,-1,-1,-1},
		{9,5,6,9,6,0,0,6,2,-1,-1,-1,-1,-1,-1,-1},
		{1,5,8,1,8,0,5,6,8,3,8,2,6,2,8,-1},
		{1,5,6,2,1,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,3,6,1,6,10,3,8,6,5,6,9,8,9,6,-1},
		{10,1,0,10,0,6,9,5,0,5,6,0,-1,-1,-1,-1},
		{0,3,8,5,6,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{10,5,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{11,5,10,7,5,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{11,5,10,11,7,5,8,3,0,-1,-1,-1,-1,-1,-1,-1},
		{5,11,7,5,10,11,1,9,0,-1,-1,-1,-1,-1,-1,-1},
		{10,7,5,10,11,7,9,8,1,8,3,1,-1,-1,-1,-1},
		{11,1,2,11,7,1,7,5,1,-1,-1,-1,-1,-1,-1,-1},
		{0,8,3,1,2,7,1,7,5,7,2,11,-1,-1,-1,-1},
		{9,7,5,9,2,7,9,0,2,2,11,7,-1,-1,-1,-1},
		{7,5,2,7,2,11,5,9,2,3,2,8,9,8,2,-1},
		{2,5,10,2,3,5,3,7,5,-1,-1,-1,-1,-1,-1,-1},
		{8,2,0,8,5,2,8,7,5,10,2,5,-1,-1,-1,-1},
		{9,0,1,5,10,3,5,3,7,3,10,2,-1,-1,-1,-1},
		{9,8,2,9,2,1,8,7,2,10,2,5,7,5,2,-1},
		{1,3,5,3,7,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,8,7,0,7,1,1,7,5,-1,-1,-1,-1,-1,-1,-1},
		{9,0,3,9,3,5,5,3,7,-1,-1,-1,-1,-1,-1,-1},
		{9,8,7,5,9,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{5,8,4,5,10,8,10,11,8,-1,-1,-1,-1,-1,-1,-1},
		{5,0,4,5,11,0,5,10,11,11,3,0,-1,-1,-1,-1},
		{0,1,9,8,4,10,8,10,11,10,4,5,-1,-1,-1,-1},
		{10,11,4,10,4,5,11,3,4,9,4,1,3,1,4,-1},
		{2,5,1,2,8,5,2,11,8,4,5,8,-1,-1,-1,-1},
		{0,4,11,0,11,3,4,5,11,2,11,1,5,1,11,-1},
		{0,2,5,0,5,9,2,11,5,4,5,8,11,8,5,-1},
		{9,4,5,2,11,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{2,5,10,3,5,2,3,4,5,3,8,4,-1,-1,-1,-1},
		{5,10,2,5,2,4,4,2,0,-1,-1,-1,-1,-1,-1,-1},
		{3,10,2,3,5,10,3,8,5,4,5,8,0,1,9,-1},
		{5,10,2,5,2,4,1,9,2,9,4,2,-1,-1,-1,-1},
		{8,4,5,8,5,3,3,5,1,-1,-1,-1,-1,-1,-1,-1},
		{0,4,5,1,0,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{8,4,5,8,5,3,9,0,5,0,3,5,-1,-1,-1,-1},
		{9,4,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,11,7,4,9,11,9,10,11,-1,-1,-1,-1,-1,-1,-1},
		{0,8,3,4,9,7,9,11,7,9,10,11,-1,-1,-1,-1},
		{1,10,11,1,11,4,1,4,0,7,4,11,-1,-1,-1,-1},
		{3,1,4,3,4,8,1,10,4,7,4,11,10,11,4,-1},
		{4,11,7,9,11,4,9,2,11,9,1,2,-1,-1,-1,-1},
		{9,7,4,9,11,7,9,1,11,2,11,1,0,8,3,-1},
		{11,7,4,11,4,2,2,4,0,-1,-1,-1,-1,-1,-1,-1},
		{11,7,4,11,4,2,8,3,4,3,2,4,-1,-1,-1,-1},
		{2,9,10,2,7,9,2,3,7,7,4,9,-1,-1,-1,-1},
		{9,10,7,9,7,4,10,2,7,8,7,0,2,0,7,-1},
		{3,7,10,3,10,2,7,4,10,1,10,0,4,0,10,-1},
		{1,10,2,8,7,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,9,1,4,1,7,7,1,3,-1,-1,-1,-1,-1,-1,-1},
		{4,9,1,4,1,7,0,8,1,8,7,1,-1,-1,-1,-1},
		{4,0,3,7,4,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{4,8,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{9,10,8,10,11,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{3,0,9,3,9,11,11,9,10,-1,-1,-1,-1,-1,-1,-1},
		{0,1,10,0,10,8,8,10,11,-1,-1,-1,-1,-1,-1,-1},
		{3,1,10,11,3,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,2,11,1,11,9,9,11,8,-1,-1,-1,-1,-1,-1,-1},
		{3,0,9,3,9,11,1,2,9,2,11,9,-1,-1,-1,-1},
		{0,2,11,8,0,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{3,2,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{2,3,8,2,8,10,10,8,9,-1,-1,-1,-1,-1,-1,-1},
		{9,10,2,0,9,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{2,3,8,2,8,10,0,1,8,1,10,8,-1,-1,-1,-1},
		{1,10,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{1,3,8,9,1,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,9,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{0,3,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
		{-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1}
	};

	public override void _Ready()
	{
		// CERVEAU DE SURFACE (Dessine les montagnes et plaines)
		_noiseSurface = new FastNoiseLite();
		_noiseSurface.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		_noiseSurface.Seed = SeedTerrain;
		_noiseSurface.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		_noiseSurface.FractalOctaves = 5;
		_noiseSurface.Frequency = 0.002f;

		_noiseErosion = new FastNoiseLite();
		_noiseErosion.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		_noiseErosion.Seed = SeedTerrain + 1;
		_noiseErosion.FractalOctaves = 5;
		_noiseErosion.Frequency = 0.002f;

		// Température : Fbm + octaves = transitions lentes, zones climatiques étendues
		_noiseTemperature = new FastNoiseLite();
		_noiseTemperature.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noiseTemperature.Seed = SeedTerrain + 2;
		_noiseTemperature.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		_noiseTemperature.FractalOctaves = 4;
		_noiseTemperature.Frequency = 0.0005f;  // Zones larges = transitions douces

		// Humidité : idem, plusieurs stades avec variation progressive
		_noiseHumidite = new FastNoiseLite();
		_noiseHumidite.Seed = SeedTerrain + 3;
		_noiseHumidite.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noiseHumidite.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		_noiseHumidite.FractalOctaves = 4;
		_noiseHumidite.Frequency = 0.0006f;  // Légèrement différent de temp = biomes variés

		// CERVEAU SOUTERRAIN (Creuse le réseau de grottes) — ne doit JAMAIS influencer la surface
		_noiseCavernes = new FastNoiseLite();
		_noiseCavernes.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
		_noiseCavernes.Seed = SeedTerrain + 4;
		_noiseCavernes.FractalType = FastNoiseLite.FractalTypeEnum.Ridged;
		_noiseCavernes.FractalOctaves = 3;
		_noiseCavernes.Frequency = 0.015f;

		// ÉROSION (Serpentins et lacs sous NiveauEau)
		_noiseRivieres = new FastNoiseLite();
		_noiseRivieres.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		_noiseRivieres.FractalType = FastNoiseLite.FractalTypeEnum.Ridged; // Lignes fines = ravins
		_noiseRivieres.Frequency = 0.003f; // Fleuves longs et larges
		_noiseRivieres.Seed = SeedTerrain + 5;

		_noiseNeige = new FastNoiseLite();
		_noiseNeige.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		_noiseNeige.Seed = SeedTerrain + 10;
		_noiseNeige.Frequency = 0.008f;  // Variation locale naturelle de la limite des neiges

		_densitiesEau = new float[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];

		var shaderEau = GD.Load<Shader>("res://EauTriplanar.gdshader");
		var matEau = new ShaderMaterial();
		matEau.Shader = shaderEau;
		matEau.SetShaderParameter("albedo_color", new Color(0.1f, 0.3f, 0.6f, 0.6f));

		_sectionsTerrain = new MeshInstance3D[NB_SECTIONS];
		_sectionsEau = new MeshInstance3D[NB_SECTIONS];
		_sectionsPhysiques = new CollisionShape3D[NB_SECTIONS];

		for (int i = 0; i < NB_SECTIONS; i++)
		{
			var miTerrain = new MeshInstance3D();
			miTerrain.Name = $"TerrainSection_{i}";
			AddChild(miTerrain);
			_sectionsTerrain[i] = miTerrain;

			var corps = new StaticBody3D();
			corps.Name = $"CollisionSection_{i}";
			var collisionShape = new CollisionShape3D();
			corps.AddChild(collisionShape);
			miTerrain.AddChild(corps);
			_sectionsPhysiques[i] = collisionShape;

			var miEau = new MeshInstance3D();

			miEau.Name = $"EauSection_{i}";
			miEau.MaterialOverride = matEau;
			AddChild(miEau);
			_sectionsEau[i] = miEau;
		}
	}

	/// <summary>Section prête si son CollisionShape3D est construit (legacy).</summary>
	public bool SectionAPret(int section)
	{
		if (_sectionsPhysiques == null || section < 0 || section >= NB_SECTIONS) return false;
		return _sectionsPhysiques[section]?.Shape != null;
	}

	public void DetruireVoxel(Vector3 pointImpactGlobal, float rayonExplosion)
	{
		Vector3 pointLocal = pointImpactGlobal - GlobalPosition;
		var positionsDetruites = new System.Collections.Generic.List<Vector3I>();

		lock (_verrouVoxel)
		{
		float rayon2 = rayonExplosion * rayonExplosion;
		bool modifie = false;

		for (int x = 0; x <= TailleChunk; x++)
		{
			for (int y = 0; y <= HauteurMax; y++)
			{
				for (int z = 0; z <= TailleChunk; z++)
				{
					if (y <= 2) continue;

					float dx = pointLocal.X - x;
					float dy = pointLocal.Y - y;
					float dz = pointLocal.Z - z;
					float dist2 = dx * dx + dy * dy + dz * dz;

					if (dist2 <= rayon2)
					{
						bool etaitSolide = _densities[x, y, z] > Isolevel;
						_densities[x, y, z] = Mathf.Max(_densities[x, y, z] - 5.0f, -1.0f); // Plancher absolu
						modifie = true;
						if (etaitSolide)
							positionsDetruites.Add(new Vector3I(x, y, z));
					}
				}
			}
		}

		if (!modifie) return;

		foreach (var pos in positionsDetruites)
			VerifierStabilite(pos);
		}

		var gestionnaire = GetParent() as Gestionnaire_Monde;
		if (gestionnaire != null)
		{
			foreach (var pos in positionsDetruites)
			{
				Vector3 posGlobal = GlobalPosition + new Vector3(pos.X, pos.Y, pos.Z);
				gestionnaire.ReveillerEauAdjacente(posGlobal);
			}
		}

		ActualiserSectionsAffectees(positionsDetruites, urgent: true);
	}

	private System.Collections.Generic.HashSet<int> ObtenirSectionsAffectees(System.Collections.Generic.List<Vector3I> positions)
	{
		var sections = new System.Collections.Generic.HashSet<int>();
		foreach (var pos in positions)
		{
			int idx = Mathf.FloorToInt(pos.Y / (float)HAUTEUR_SECTION);
			if (idx >= 0 && idx < NB_SECTIONS) sections.Add(idx);
			if (pos.Y % HAUTEUR_SECTION == 0 && pos.Y > 0 && idx - 1 >= 0)
				sections.Add(idx - 1);
		}
		return sections;
	}

	/// <summary>Vérifie si (x,y,z) est dans les limites du tableau local. Pas d'accès au tableau.</summary>
	private bool EstDansLimitesChunk(int x, int y, int z)
	{
		return x >= 0 && x <= TailleChunk && y >= 0 && y <= HauteurMax && z >= 0 && z <= TailleChunk;
	}

	/// <summary>Coordonnées du chunk voisin si (x,z) sort du chunk actuel. Retourne null si hors limites verticales.</summary>
	private Vector2I? ObtenirChunkVoisinSiHorsLimites(int x, int y, int z)
	{
		if (y < 0 || y > HauteurMax) return null;
		if (x >= 0 && x <= TailleChunk && z >= 0 && z <= TailleChunk) return null;
		int dx = x < 0 ? -1 : (x > TailleChunk ? 1 : 0);
		int dz = z < 0 ? -1 : (z > TailleChunk ? 1 : 0);
		return new Vector2I(ChunkOffsetX + dx, ChunkOffsetZ + dz);
	}

	private bool EstSolide(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return false;
		return _densities[x, y, z] > Isolevel;
	}

	/// <summary>Distance horizontale max qu'un bloc peut supporter sans pilier en dessous. 0 = sable (support direct obligatoire).</summary>
	private static int ObtenirResistanceMateriau(byte idMateriau)
	{
		if (idMateriau == 3) return 0; // Sable : doit avoir un bloc directement en dessous
		if (idMateriau == 2) return 2; // Roche : plus solide
		return 1; // Tout le reste : Herbe, Terre aride, Argile, Neige, Boue, Glace, etc.
	}

	private bool AUnSupport(int blocX, int blocY, int blocZ, byte idMateriau)
	{
		int resistance = ObtenirResistanceMateriau(idMateriau);

		if (resistance == 0)
			return EstSolide(blocX, blocY - 1, blocZ);

		bool estSoutenu = false;
		for (int x = -resistance; x <= resistance; x++)
		{
			for (int z = -resistance; z <= resistance; z++)
			{
				if (Mathf.Abs(x) + Mathf.Abs(z) <= resistance)
				{
					if (EstSolide(blocX + x, blocY, blocZ + z) &&
						EstSolide(blocX + x, blocY - 1, blocZ + z))
					{
						estSoutenu = true;
						break;
					}
				}
			}
			if (estSoutenu) break;
		}
		return estSoutenu;
	}

	public void VerifierStabilite(Vector3I positionVoxel)
	{
		int xu = positionVoxel.X;
		int yu = positionVoxel.Y + 1;
		int zu = positionVoxel.Z;

		// Hors limites verticales : plafond du monde ou sous-sol
		if (yu < 0 || yu > HauteurMax) return;

		// Le voxel au-dessus est dans un chunk voisin ? Pas d'accès au tableau.
		if (!EstDansLimitesChunk(xu, yu, zu))
		{
			var coordVoisin = ObtenirChunkVoisinSiHorsLimites(xu, yu, zu);
			if (coordVoisin == null) return;
			var gestionnaire = GetParent() as Gestionnaire_Monde;
			if (gestionnaire == null || !gestionnaire.ChunkEstCharge(coordVoisin.Value))
				return;
			return;
		}

		if (!EstSolide(xu, yu, zu)) return;

		byte mat = _materials[xu, yu, zu];
		if (mat == 0) mat = 2;

		if (AUnSupport(xu, yu, zu, mat)) return;

		// a) Effacer le voxel
		_densities[xu, yu, zu] = -10.0f;
		if (_densitiesEau != null)
			_densitiesEau[xu, yu, zu] = -1.0f;

		var gest = GetParent() as Gestionnaire_Monde;
		if (gest != null)
		{
			Vector3 posGlobal = GlobalPosition + new Vector3(xu, yu, zu);
			gest.ReveillerEauAdjacente(posGlobal);
		}

		// b) Spawn BlocChutant
		Vector3 posMonde = GlobalPosition + new Vector3(xu + 0.5f, yu + 0.5f, zu + 0.5f);
		var bloc = BlocChutant.Creer(posMonde, mat, MaterielTerre);
		GetParent().AddChild(bloc);
		bloc.GlobalPosition = posMonde;

		// d) Propagation : au-dessus et sur les côtés
		VerifierStabilite(new Vector3I(xu, yu, zu));
		VerifierStabilite(new Vector3I(xu - 1, yu - 1, zu));
		VerifierStabilite(new Vector3I(xu + 1, yu - 1, zu));
		VerifierStabilite(new Vector3I(xu, yu - 1, zu - 1));
		VerifierStabilite(new Vector3I(xu, yu - 1, zu + 1));
	}

	public void CreerMatiere(Vector3 pointCibleGlobal, float rayon, int idMatiere = 1)
	{
		Vector3 pointLocal = pointCibleGlobal - GlobalPosition;
		var positionsModifiees = new System.Collections.Generic.List<Vector3I>();
		byte mat = (byte)Mathf.Clamp(idMatiere, 0, 255);

		lock (_verrouVoxel)
		{
		float rayon2 = rayon * rayon;
		bool modifie = false;

		for (int x = 0; x <= TailleChunk; x++)
		{
			for (int y = 0; y <= HauteurMax; y++)
			{
				for (int z = 0; z <= TailleChunk; z++)
				{
					if (y <= 2) continue;

					float dx = pointLocal.X - x;
					float dy = pointLocal.Y - y;
					float dz = pointLocal.Z - z;
					float dist2 = dx * dx + dy * dy + dz * dz;

					if (dist2 <= rayon2)
					{
						_densities[x, y, z] = Mathf.Min(_densities[x, y, z] + 5.0f, 1.0f); // Plafond absolu
						_materials[x, y, z] = mat; // Injection couleur : le Shader lit ce tableau
						modifie = true;
						positionsModifiees.Add(new Vector3I(x, y, z));
					}
				}
			}
		}

		if (!modifie) return;
		}

		var gestionnaire = GetParent() as Gestionnaire_Monde;
		if (gestionnaire != null)
		{
			foreach (var pos in positionsModifiees)
			{
				Vector3 posGlobal = GlobalPosition + new Vector3(pos.X, pos.Y, pos.Z);
				gestionnaire.ReveillerEauAdjacente(posGlobal);
			}
		}

		ActualiserSectionsAffectees(positionsModifiees, urgent: true);
	}

	private float ObtenirAmplitudeBiome(float globalX, float globalZ)
	{
		float erosion = _noiseErosion.GetNoise2D(globalX, globalZ);

		if (erosion < -0.1f)
			return 2.0f;
		if (erosion > 0.3f)
			return 45.0f;

		float t = (erosion - (-0.1f)) / (0.3f - (-0.1f));
		return Mathf.Lerp(2.0f, 45.0f, t);
	}

	/// <summary>Calcule la hauteur du terrain à des coordonnées monde (pour spawn dynamique).</summary>
	public static int ObtenirHauteurTerrainMonde(int worldX, int worldZ, int seed)
	{
		var noiseSurface = new FastNoiseLite();
		noiseSurface.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		noiseSurface.Seed = seed;
		noiseSurface.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		noiseSurface.FractalOctaves = 5;
		noiseSurface.Frequency = 0.002f;

		var noiseRivieres = new FastNoiseLite();
		noiseRivieres.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		noiseRivieres.FractalType = FastNoiseLite.FractalTypeEnum.Ridged;
		noiseRivieres.Frequency = 0.003f;
		noiseRivieres.Seed = seed + 5;

		float bruitBrut = noiseSurface.GetNoise2D(worldX, worldZ);
		float bruitNormalise = (bruitBrut + 1.0f) / 2.0f;
		float relief = Mathf.Pow(bruitNormalise, 2.0f);  // Exposant 2 : montagnes visibles (³ écrasait tout)

		// Plaine : plaines basses 103-105 (biais fort vers 103) + plaine principale 105-118
		float bruitPlaine = noiseSurface.GetNoise2D(worldX * 0.0003f, worldZ * 0.0003f);
		float bruitVague = noiseSurface.GetNoise2D(worldX * 0.0012f + 3000f, worldZ * 0.0012f + 3000f);
		float bruitMicro = noiseSurface.GetNoise2D(worldX * 0.005f + 5000f, worldZ * 0.005f + 5000f);
		float mix = (bruitPlaine + 1f) * 0.5f * 0.4f + (bruitVague + 1f) * 0.5f * 0.35f + (bruitMicro + 1f) * 0.5f * 0.25f;
		mix = Mathf.Clamp(mix, 0f, 1f);
		float rampBase;
		if (mix < 0.6f) {
			float t = mix / 0.6f;
			rampBase = 103f + t * t * t * 2f;  // t³ : plus de temps à 103, peu à 105
		} else {
			float t = (mix - 0.6f) / 0.4f;
			rampBase = 105f + t * 13f;  // Plaine principale 105 → 118
		}

		// Tier 2 + Montagnes : seuils abaissés (relief²) → montagnes ~25% du terrain
		float tTier2 = Mathf.Clamp((relief - 0.15f) / 0.35f, 0f, 1f);
		float tMont = Mathf.Clamp((relief - 0.30f) / 0.45f, 0f, 1f);
		float hTier2 = tTier2 * tTier2 * 82f;
		float hMontagnes = tMont * tMont * 500f;  // Montagnes jusqu'à 700

		// Transition progressive base → tier2+montagnes (blend 0.05 → 0.22)
		float poidsBase = 1f - Mathf.Clamp((relief - 0.05f) / 0.17f, 0f, 1f);
		poidsBase = poidsBase * poidsBase * (3f - 2f * poidsBase);
		float hauteurHaut = 118f + hTier2 + hMontagnes;
		int hauteurBase = (int)(rampBase * poidsBase + hauteurHaut * (1f - poidsBase));
		float crevasseBrute = noiseRivieres.GetNoise2D(worldX, worldZ);
		int profondeurEau = 0;
		if (crevasseBrute > 0.12f)
		{
			float intensiteRiviera = (crevasseBrute - 0.12f) / 0.88f;
			float tSmooth = intensiteRiviera * intensiteRiviera * (3f - 2f * intensiteRiviera);  // Descente très douce vers l'eau
			profondeurEau = (int)(tSmooth * 22.0f);
		}

		return hauteurBase - profondeurEau;
	}

	/// <summary>x et z doivent être en espace GLOBAL (worldX, worldZ) pour éviter le tiling.</summary>
	private int CalculerHauteurTerrain(int xGlobal, int zGlobal)
	{
		// --- 1. LE SOCLE TECTONIQUE (Plaines → Collines → Petites montagnes → Grandes montagnes) ---
		float bruitBrut = _noiseSurface.GetNoise2D(xGlobal, zGlobal);
		float bruitNormalise = (bruitBrut + 1.0f) / 2.0f;

		float relief = Mathf.Pow(bruitNormalise, 2.0f);  // Exposant 2 : montagnes visibles (³ écrasait tout)

		// Plaine : plaines basses 103-105 (biais fort vers 103) + plaine principale 105-118
		float bruitPlaine = _noiseErosion.GetNoise2D(xGlobal * 0.0003f, zGlobal * 0.0003f);
		float bruitVague = _noiseErosion.GetNoise2D(xGlobal * 0.0012f + 3000f, zGlobal * 0.0012f + 3000f);
		float bruitMicro = _noiseErosion.GetNoise2D(xGlobal * 0.005f + 5000f, zGlobal * 0.005f + 5000f);
		float mix = (bruitPlaine + 1f) * 0.5f * 0.4f + (bruitVague + 1f) * 0.5f * 0.35f + (bruitMicro + 1f) * 0.5f * 0.25f;
		mix = Mathf.Clamp(mix, 0f, 1f);
		float rampBase;
		if (mix < 0.6f) {
			float t = mix / 0.6f;
			rampBase = 103f + t * t * t * 2f;  // t³ : plus de temps à 103, peu à 105
		} else {
			float t = (mix - 0.6f) / 0.4f;
			rampBase = 105f + t * 13f;  // Plaine principale 105 → 118
		}

		// Tier 2 + Montagnes : seuils abaissés (relief²) → montagnes ~25%
		float tTier2 = Mathf.Clamp((relief - 0.15f) / 0.35f, 0f, 1f);
		float tMont = Mathf.Clamp((relief - 0.30f) / 0.45f, 0f, 1f);
		float hTier2 = tTier2 * tTier2 * 82f;
		float hMontagnes = tMont * tMont * 500f;

		// Transition progressive base → tier2+montagnes (blend 0.05 → 0.22)
		float poidsBase = 1f - Mathf.Clamp((relief - 0.05f) / 0.17f, 0f, 1f);
		poidsBase = poidsBase * poidsBase * (3f - 2f * poidsBase);
		float hauteurHaut = 118f + hTier2 + hMontagnes;
		int hauteurBase = (int)(rampBase * poidsBase + hauteurHaut * (1f - poidsBase));

		// --- 2. L'ENDIGUEMENT HYDROLOGIQUE (Berges en pente douce) ---
		float crevasseBrute = _noiseRivieres.GetNoise2D(xGlobal, zGlobal);
		int profondeurEau = 0;
		if (crevasseBrute > 0.12f)
		{
			float intensiteRiviera = (crevasseBrute - 0.12f) / 0.88f;
			float tSmooth = intensiteRiviera * intensiteRiviera * (3f - 2f * intensiteRiviera);  // Descente très douce vers l'eau
			profondeurEau = (int)(tSmooth * 22.0f);
		}

		// --- 3. RENDU FINAL ---
		return hauteurBase - profondeurEau;
	}

	private const int NiveauPlage = 102;  // Sable jusqu'à 102, herbe à 103-104 (niveau eau inchangé)
	private const int SeuilNeigeBase = 350;  // Neige à partir de 350 (montagnes jusqu'à 700), bruit ±18
	private const int SeuilMontagneRoche = 250;  // À partir de 250 : roche nue (n'affecte pas biomes au sol)

	private byte DeterminerMateriauCroûte(float globalX, float globalZ, int globalY, int hauteurSurface, float temperature, float humidite)
	{
		float bruitNeige = _noiseNeige.GetNoise2D(globalX, globalZ);
		int seuilLocal = SeuilNeigeBase + (int)(bruitNeige * 18f);
		if (globalY >= seuilLocal) return 5;  // NEIGE (sommets 350-700)
		if (globalY >= SeuilMontagneRoche) return 2;  // Roche nue (250-350, sous la limite des neiges)
		if (globalY <= NiveauPlage) return (humidite > 0.2f) ? (byte)7 : (byte)3;  // Plage : seuil doux
		// Sable UNIQUEMENT quand très sec ET très chaud (temp + humidité liés logiquement)
		if (temperature > 0.5f && humidite < -0.5f) return 3;  // Désert : sable
		// Plusieurs stades temp/hum avec seuils progressifs (transitions lentes)
		if (temperature > 0.4f)  // Très chaud
		{
			if (humidite > 0.4f) return 8;   // Argile humide
			if (humidite > 0.1f) return 6;   // Terre aride
			return 1;   // Sec mais pas assez pour sable → herbe jaunâtre (shader)
		}
		if (temperature > 0.15f)  // Chaud
		{
			if (humidite > 0.35f) return 8;
			if (humidite > 0.0f) return 6;
			return 1;   // Sec → herbe (shader jaunâtre)
		}
		if (temperature < -0.4f) return 5;  // Très froid = toujours neige
		if (temperature < -0.15f)  // Froid
			return (humidite > 0.2f) ? (byte)9 : (byte)5;  // Glace si humide, neige sinon
		// Tempéré / Frais : humide → boue, sec → roche, entre-deux → herbe
		if (humidite < -0.35f) return 6;   // Très sec
		if (humidite < -0.15f) return 1;   // Sec : herbe
		if (humidite > 0.4f) return 7;     // Très humide : boue
		if (humidite > 0.2f) return 7;    // Humide : boue
		if (humidite > 0.05f) return 1;   // Légèrement humide : herbe
		return 1;  // Herbe par défaut
	}

	public void GenererBruitVierge()
	{
		GenererDonneesVoxel(GlobalPosition.X, GlobalPosition.Z);
	}

	private void GenererDonneesVoxel(float baseX, float baseZ)
	{
		lock (_verrouVoxel)
		{
		_densities = new float[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];
		_materials = new byte[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];
		_densitiesEau = new float[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];
		for (int x = 0; x <= TailleChunk; x++)
		{
			for (int y = 0; y <= HauteurMax; y++)
			{
				for (int z = 0; z <= TailleChunk; z++)
				{
					float globalX = baseX + x;
					float globalY = y;
					float globalZ = baseZ + z;

					int hauteurSurface = CalculerHauteurTerrain((int)globalX, (int)globalZ);
					float temperature = _noiseTemperature.GetNoise2D(globalX, globalZ);
					float humidite = _noiseHumidite.GetNoise2D(globalX, globalZ);

					_densitiesEau[x, y, z] = -1.0f;

					if (y <= 2)
					{
						_densities[x, y, z] = 1000.0f;
						_materials[x, y, z] = 2;
					}
					else if (globalY == hauteurSurface)
					{
						_materials[x, y, z] = DeterminerMateriauCroûte(globalX, globalZ, (int)globalY, hauteurSurface, temperature, humidite);
						_densities[x, y, z] = 10.0f;
					}
					else if (globalY < hauteurSurface && globalY >= hauteurSurface - 4)
					{
						float valeurGrotte = _noiseCavernes.GetNoise3D(globalX, globalY, globalZ);
						if (valeurGrotte > 0.75f)
						{
							_densities[x, y, z] = -10.0f;
							_materials[x, y, z] = 0;
						}
						else
						{
							_densities[x, y, z] = 10.0f;
							int seuilNeigeLocal = SeuilNeigeBase + (int)(_noiseNeige.GetNoise2D(globalX, globalZ) * 18f);
							_materials[x, y, z] = (hauteurSurface >= SeuilMontagneRoche || hauteurSurface >= seuilNeigeLocal) ? (byte)2 : (humidite > 0.3f ? (byte)7 : (byte)6);
						}
					}
					else if (globalY < hauteurSurface - 4)
					{
						float valeurGrotte = _noiseCavernes.GetNoise3D(globalX, globalY, globalZ);
						if (valeurGrotte > 0.55f)
						{
							_densities[x, y, z] = -10.0f;
							_materials[x, y, z] = 0;
						}
						else
						{
							_densities[x, y, z] = 10.0f;
							_materials[x, y, z] = 2;  // Profondeur = toujours roche
						}
					}
					else if (globalY > hauteurSurface && globalY <= NiveauEau)
					{
						_densities[x, y, z] = -10.0f;
						_materials[x, y, z] = 0;
						_densitiesEau[x, y, z] = (NiveauEau + 1.0f) - y;
					}
					else
					{
						_densities[x, y, z] = -10.0f;
						_materials[x, y, z] = 0;
					}
				}
			}
		}
		}
	}

	public void Sauvegarder(Vector2I coordChunk)
	{
		string chemin = ObtenirCheminChunk(coordChunk);
		using var file = FileAccess.Open(chemin, FileAccess.ModeFlags.Write);
		if (file == null) return;

		lock (_verrouVoxel)
		{
		for (int x = 0; x <= TailleChunk; x++)
		{
			for (int y = 0; y <= HauteurMax; y++)
			{
				for (int z = 0; z <= TailleChunk; z++)
				{
					file.StoreFloat(_densities[x, y, z]);
					file.Store8(_materials[x, y, z]);
				}
			}
		}
		}
	}

	/// <summary>Chemin du fichier de sauvegarde pour un chunk.</summary>
	public static string ObtenirCheminChunk(Vector2I coordChunk)
		=> $"user://chunks/chunk_{coordChunk.X}_{coordChunk.Y}.dat";

	/// <summary>Pré-génère les données voxel sans instancier de mesh. Pour baking initial.</summary>
	public static (float[,,] densities, byte[,,] materials) GenererDonneesVoxelBrut(Vector2I coordChunk, int seed, int tailleChunk, int hauteurMax)
	{
		float baseX = coordChunk.X * tailleChunk;
		float baseZ = coordChunk.Y * tailleChunk;

		var noiseSurface = new FastNoiseLite();
		noiseSurface.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
		noiseSurface.Seed = seed;
		noiseSurface.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		noiseSurface.FractalOctaves = 5;
		noiseSurface.Frequency = 0.002f;

		var noiseTemperature = new FastNoiseLite();
		noiseTemperature.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		noiseTemperature.Seed = seed + 2;
		noiseTemperature.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		noiseTemperature.FractalOctaves = 4;
		noiseTemperature.Frequency = 0.0005f;  // Zones larges = transitions douces

		var noiseHumidite = new FastNoiseLite();
		noiseHumidite.Seed = seed + 3;
		noiseHumidite.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		noiseHumidite.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		noiseHumidite.FractalOctaves = 4;
		noiseHumidite.Frequency = 0.0006f;  // Légèrement différent de temp = biomes variés

		var noiseCavernes = new FastNoiseLite();
		noiseCavernes.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
		noiseCavernes.Seed = seed + 4;
		noiseCavernes.FractalType = FastNoiseLite.FractalTypeEnum.Ridged;
		noiseCavernes.FractalOctaves = 3;
		noiseCavernes.Frequency = 0.015f;

		var noiseNeige = new FastNoiseLite();
		noiseNeige.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
		noiseNeige.Seed = seed + 10;
		noiseNeige.Frequency = 0.008f;

		const int NiveauEau = 103;  // +1 m
		const int SeuilNeigeBase = 350;  // Neige à 350 (montagnes jusqu'à 700)

		var densities = new float[tailleChunk + 1, hauteurMax + 1, tailleChunk + 1];
		var materials = new byte[tailleChunk + 1, hauteurMax + 1, tailleChunk + 1];

		for (int x = 0; x <= tailleChunk; x++)
		{
			for (int y = 0; y <= hauteurMax; y++)
			{
				for (int z = 0; z <= tailleChunk; z++)
				{
					float globalX = baseX + x;
					float globalY = y;
					float globalZ = baseZ + z;

					int hauteurSurface = ObtenirHauteurTerrainMonde((int)globalX, (int)globalZ, seed);
					float temperature = noiseTemperature.GetNoise2D(globalX, globalZ);
					float humidite = noiseHumidite.GetNoise2D(globalX, globalZ);

					if (y <= 2)
					{
						densities[x, y, z] = 1000.0f;
						materials[x, y, z] = 2;
					}
					else if (globalY == hauteurSurface)
					{
						materials[x, y, z] = DeterminerMateriauCroûteStatique(globalX, globalZ, (int)globalY, hauteurSurface, temperature, humidite, noiseNeige, SeuilNeigeBase);
						densities[x, y, z] = 10.0f;
					}
					else if (globalY < hauteurSurface && globalY >= hauteurSurface - 4)
					{
						float valeurGrotte = noiseCavernes.GetNoise3D(globalX, globalY, globalZ);
						if (valeurGrotte > 0.75f)
						{
							densities[x, y, z] = -10.0f;
							materials[x, y, z] = 0;
						}
						else
						{
							densities[x, y, z] = 10.0f;
							int seuilNeigeLocal = SeuilNeigeBase + (int)(noiseNeige.GetNoise2D(globalX, globalZ) * 18f);
							const int SeuilMontagneRoche = 250;
							materials[x, y, z] = (hauteurSurface >= SeuilMontagneRoche || hauteurSurface >= seuilNeigeLocal) ? (byte)2 : (humidite > 0.3f ? (byte)7 : (byte)6);
						}
					}
					else if (globalY < hauteurSurface - 4)
					{
						float valeurGrotte = noiseCavernes.GetNoise3D(globalX, globalY, globalZ);
						if (valeurGrotte > 0.55f)
						{
							densities[x, y, z] = -10.0f;
							materials[x, y, z] = 0;
						}
						else
						{
							densities[x, y, z] = 10.0f;
							materials[x, y, z] = 2;
						}
					}
					else if (globalY > hauteurSurface && globalY <= NiveauEau)
					{
						densities[x, y, z] = -10.0f;
						materials[x, y, z] = 0;
					}
					else if (globalY > hauteurSurface)
					{
						densities[x, y, z] = -10.0f;
						materials[x, y, z] = 0;
					}
					else
					{
						densities[x, y, z] = -10.0f;
						materials[x, y, z] = 0;
					}
				}
			}
		}
		return (densities, materials);
	}

	private static byte DeterminerMateriauCroûteStatique(float globalX, float globalZ, int globalY, int hauteurSurface, float temperature, float humidite, FastNoiseLite noiseNeige, int seuilNeigeBase)
	{
		const int NiveauPlage = 102;  // Sable jusqu'à 102, herbe à 103-104
		const int SeuilMontagneRoche = 250;  // À partir de 250 : que de la pierre
		float bruitNeige = noiseNeige.GetNoise2D(globalX, globalZ);
		int seuilLocal = seuilNeigeBase + (int)(bruitNeige * 18f);
		if (globalY >= seuilLocal) return 5;  // NEIGE (sommets 350+)
		if (globalY >= SeuilMontagneRoche) return 2;  // Roche nue (250-350)
		if (globalY <= NiveauPlage) return (humidite > 0.2f) ? (byte)7 : (byte)3;
		// Sable UNIQUEMENT quand très sec ET très chaud (temp + humidité liés)
		if (temperature > 0.5f && humidite < -0.5f) return 3;  // Désert : sable
		// Plusieurs stades temp/hum avec seuils progressifs (transitions lentes)
		if (temperature > 0.4f)  // Très chaud
		{
			if (humidite > 0.4f) return 8;
			if (humidite > 0.1f) return 6;
			return 1;   // Sec → herbe jaunâtre (shader)
		}
		if (temperature > 0.15f)  // Chaud
		{
			if (humidite < -0.4f) return 1;  // Sec → herbe (shader jaunâtre)
			if (humidite > 0.35f) return 8;
			if (humidite > 0.0f) return 6;
			return 6;
		}
		if (temperature < -0.4f) return 5;  // Très froid = toujours neige
		if (temperature < -0.15f) return (humidite > 0.2f) ? (byte)9 : (byte)5;  // Glace si humide, neige sinon
		if (humidite < -0.35f) return 6;
		if (humidite < -0.15f) return 1;
		if (humidite > 0.4f) return 7;
		if (humidite > 0.2f) return 7;
		if (humidite > 0.05f) return 1;
		return 1;
	}

	/// <summary>Sauvegarde des données brutes sur disque (sans mesh).</summary>
	public static void SauvegarderDonneesBrutes(Vector2I coordChunk, float[,,] densities, byte[,,] materials, int tailleChunk, int hauteurMax)
	{
		string chemin = ObtenirCheminChunk(coordChunk);
		using var file = FileAccess.Open(chemin, FileAccess.ModeFlags.Write);
		if (file == null) return;

		for (int x = 0; x <= tailleChunk; x++)
		{
			for (int y = 0; y <= hauteurMax; y++)
			{
				for (int z = 0; z <= tailleChunk; z++)
				{
					file.StoreFloat(densities[x, y, z]);
					file.Store8(materials[x, y, z]);
				}
			}
		}
	}

	public bool Charger(Vector2I coordChunk)
	{
		string chemin = ObtenirCheminChunk(coordChunk);
		if (!FileAccess.FileExists(chemin))
			return false;

		using var file = FileAccess.Open(chemin, FileAccess.ModeFlags.Read);
		if (file == null) return false;

		lock (_verrouVoxel)
		{
		int voxelCount = (TailleChunk + 1) * (HauteurMax + 1) * (TailleChunk + 1);
		long tailleFichier = (long)file.GetLength();
		bool formatAvecMaterials = tailleFichier >= voxelCount * 5L;

		_densities = new float[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];
		_materials = new byte[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];

		for (int x = 0; x <= TailleChunk; x++)
		{
			for (int y = 0; y <= HauteurMax; y++)
			{
				for (int z = 0; z <= TailleChunk; z++)
				{
					_densities[x, y, z] = file.GetFloat();
					if (formatAvecMaterials)
						_materials[x, y, z] = file.Get8();
					else
						_materials[x, y, z] = 2;
				}
			}
		}

		int[,] hauteurSurface = new int[TailleChunk + 1, TailleChunk + 1];
		for (int x = 0; x <= TailleChunk; x++)
			for (int z = 0; z <= TailleChunk; z++)
			{
				hauteurSurface[x, z] = 0;
				for (int y = HauteurMax; y >= 0; y--)
					if (_densities[x, y, z] > 0.0f) { hauteurSurface[x, z] = y; break; }
			}
		_densitiesEau = new float[TailleChunk + 1, HauteurMax + 1, TailleChunk + 1];
		for (int x = 0; x <= TailleChunk; x++)
		{
			for (int y = 0; y <= HauteurMax; y++)
			{
				for (int z = 0; z <= TailleChunk; z++)
				{
					_densitiesEau[x, y, z] = -1.0f;
					int hSurf = hauteurSurface[x, z];
					if (y > hSurf && y <= NiveauEau)
						_densitiesEau[x, y, z] = (NiveauEau + 1.0f) - y;
				}
			}
		}
		}
		return true;
	}

	public void DemarrerGenerationChunk(Vector2I coordChunk)
	{
		float baseX = coordChunk.X * TailleChunk;
		float baseZ = coordChunk.Y * TailleChunk;

		var gestionnaire = GetParent() as Gestionnaire_Monde;
		Task.Run(() =>
		{
			bool charge = Charger(coordChunk);
			if (!charge)
				GenererDonneesVoxel(baseX, baseZ);

			for (int i = 0; i < NB_SECTIONS; i++)
			{
				var (meshTerrain, meshEau) = ConstruireMeshSection(i, baseX, baseZ);
				int idx = i;
				if (gestionnaire != null)
					gestionnaire.EnqueueMiseAJourMainThread(() => AppliquerMeshSection(idx, meshTerrain, meshEau));
			}
		});
	}

	private void AppliquerMeshSection(int indexSection, ArrayMesh meshTerrain, ArrayMesh meshEau)
	{
		if (!IsInsideTree()) return; // Chunk libéré ou arbre en destruction — pas de modification spatiale.
		var miTerrain = _sectionsTerrain[indexSection];
		miTerrain.Mesh = meshTerrain;
		if (MaterielTerre != null)
			miTerrain.MaterialOverride = MaterielTerre;
		else
			GD.PrintErr("CRITIQUE : MaterielTerre manquant sur le Chunk !");

		var collisionShape = _sectionsPhysiques[indexSection];
		if (collisionShape.Shape != null)
			collisionShape.Shape.Dispose();
		collisionShape.Shape = (meshTerrain != null && meshTerrain.GetFaces().Length > 0) ? meshTerrain.CreateTrimeshShape() : null;

		if (_densitiesEau != null && meshEau != null && indexSection < _sectionsEau.Length)
			_sectionsEau[indexSection].Mesh = meshEau;
	}

	private (ArrayMesh terrain, ArrayMesh eau) ConstruireMeshSection(int indexSection, float baseX, float baseZ)
	{
		int yDebut = indexSection * HAUTEUR_SECTION;
		int yFin = Math.Min(yDebut + HAUTEUR_SECTION, HauteurMax);
		int tailleY = yFin - yDebut + 1; // +1 pour les coins (marching cubes)
		int tx = TailleChunk + 1, tz = TailleChunk + 1;
		int stride = tailleY * tz;

		if (_valsRecyclables == null) _valsRecyclables = new float[8];
		if (_vertsRecyclables == null) _vertsRecyclables = new Vector3[8];
		if (_vertListRecyclables == null) _vertListRecyclables = new Vector3[12];

		var bufferDensities = ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION);
		var bufferMaterials = ArrayPool<byte>.Shared.Rent(TAILLE_MAX_SECTION);
		float[] bufferEau = _densitiesEau != null ? ArrayPool<float>.Shared.Rent(TAILLE_MAX_SECTION) : null;
		ArrayMesh meshTerrain = null;
		ArrayMesh meshEau = null;
		try
		{
		lock (_verrouVoxel)
		{
			for (int x = 0; x < tx; x++)
				for (int y = 0; y < tailleY; y++)
					for (int z = 0; z < tz; z++)
					{
						int idx = x * stride + y * tz + z;
						bufferDensities[idx] = _densities[x, yDebut + y, z];
						bufferMaterials[idx] = _materials[x, yDebut + y, z];
						if (bufferEau != null) bufferEau[idx] = _densitiesEau[x, yDebut + y, z];
					}
		}

		float ValD(int x, int y, int z) => bufferDensities[x * stride + y * tz + z];
		byte MatD(int x, int y, int z) => bufferMaterials[x * stride + y * tz + z];
		float EauD(int x, int y, int z) => bufferEau[x * stride + y * tz + z];

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		float[] vals = _valsRecyclables;
		Vector3[] verts = _vertsRecyclables;

		for (int x = 0; x < TailleChunk; x++)
		{
			for (int y = 0; y < yFin - yDebut; y++)
			{
				int yG = yDebut + y;
				for (int z = 0; z < TailleChunk; z++)
				{
					verts[0] = new Vector3(x, yG, z);
					verts[1] = new Vector3(x + 1, yG, z);
					verts[2] = new Vector3(x + 1, yG + 1, z);
					verts[3] = new Vector3(x, yG + 1, z);
					verts[4] = new Vector3(x, yG, z + 1);
					verts[5] = new Vector3(x + 1, yG, z + 1);
					verts[6] = new Vector3(x + 1, yG + 1, z + 1);
					verts[7] = new Vector3(x, yG + 1, z + 1);

					vals[0] = ValD(x, y, z);
					vals[1] = ValD(x + 1, y, z);
					vals[2] = ValD(x + 1, y + 1, z);
					vals[3] = ValD(x, y + 1, z);
					vals[4] = ValD(x, y, z + 1);
					vals[5] = ValD(x + 1, y, z + 1);
					vals[6] = ValD(x + 1, y + 1, z + 1);
					vals[7] = ValD(x, y + 1, z + 1);

					int cubeIndex = 0;
					for (int i = 0; i < 8; i++)
						if (vals[i] > Isolevel) cubeIndex |= 1 << i;

					if (EdgeTable[cubeIndex] == 0) continue;

					Vector3[] vertList = _vertListRecyclables;
					vertList[0] = Interp(verts[0], verts[1], vals[0], vals[1]);
					vertList[1] = Interp(verts[1], verts[2], vals[1], vals[2]);
					vertList[2] = Interp(verts[2], verts[3], vals[2], vals[3]);
					vertList[3] = Interp(verts[3], verts[0], vals[3], vals[0]);
					vertList[4] = Interp(verts[4], verts[5], vals[4], vals[5]);
					vertList[5] = Interp(verts[5], verts[6], vals[5], vals[6]);
					vertList[6] = Interp(verts[6], verts[7], vals[6], vals[7]);
					vertList[7] = Interp(verts[7], verts[4], vals[7], vals[4]);
					vertList[8] = Interp(verts[0], verts[4], vals[0], vals[4]);
					vertList[9] = Interp(verts[1], verts[5], vals[1], vals[5]);
					vertList[10] = Interp(verts[2], verts[6], vals[2], vals[6]);
					vertList[11] = Interp(verts[3], verts[7], vals[3], vals[7]);

					byte idMateriau = MatD(x, y, z);
					if (idMateriau == 0)
					{
						int searchY = y;
						while (searchY > 0 && MatD(x, searchY, z) == 0)
							searchY--;
						idMateriau = MatD(x, searchY, z);
						if (idMateriau == 0)
							idMateriau = 2;
					}
					// Récupération des données climatiques (-1.0 à 1.0)
					float globalX = baseX + x;
					float globalZ = baseZ + z;
					float tempReelle = _noiseTemperature.GetNoise2D(globalX, globalZ);
					float humReelle = _noiseHumidite.GetNoise2D(globalX, globalZ);
					// Compression mathématique pour les couleurs (0.0 à 1.0)
					float canalVert = (tempReelle + 1.0f) * 0.5f;
					float canalBleu = (humReelle + 1.0f) * 0.5f;
					// Canal Rouge = ID Matériau. Vert = Température. Bleu = Humidité.
					Color couleurId = new Color(idMateriau / 255.0f, canalVert, canalBleu, 1.0f);

					for (int i = 0; TriTable[cubeIndex, i] != -1; i += 3)
					{
						Vector3 v0 = vertList[TriTable[cubeIndex, i]];
						Vector3 v1 = vertList[TriTable[cubeIndex, i + 1]];
						Vector3 v2 = vertList[TriTable[cubeIndex, i + 2]];

						Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
						st.SetNormal(n);
						st.SetColor(couleurId);
						st.AddVertex(v0);
						st.SetNormal(n);
						st.SetColor(couleurId);
						st.AddVertex(v1);
						st.SetNormal(n);
						st.SetColor(couleurId);
						st.AddVertex(v2);
					}
				}
			}
		}

		st.GenerateNormals();
		meshTerrain = st.Commit();

		if (bufferEau != null)
		{
			if (_valsEauRecyclables == null) _valsEauRecyclables = new float[8];
			if (_vertsEauRecyclables == null) _vertsEauRecyclables = new Vector3[8];
			if (_vertListEauRecyclables == null) _vertListEauRecyclables = new Vector3[12];

			var stEau = new SurfaceTool();
			stEau.Begin(Mesh.PrimitiveType.Triangles);
			float[] valsEau = _valsEauRecyclables;
			Vector3[] vertsEau = _vertsEauRecyclables;

			for (int x = 0; x < TailleChunk; x++)
			{
				for (int y = 0; y < yFin - yDebut; y++)
				{
					int yG = yDebut + y;
					for (int z = 0; z < TailleChunk; z++)
					{
						vertsEau[0] = new Vector3(x, yG, z);
						vertsEau[1] = new Vector3(x + 1, yG, z);
						vertsEau[2] = new Vector3(x + 1, yG + 1, z);
						vertsEau[3] = new Vector3(x, yG + 1, z);
						vertsEau[4] = new Vector3(x, yG, z + 1);
						vertsEau[5] = new Vector3(x + 1, yG, z + 1);
						vertsEau[6] = new Vector3(x + 1, yG + 1, z + 1);
						vertsEau[7] = new Vector3(x, yG + 1, z + 1);

						valsEau[0] = EauD(x, y, z);
						valsEau[1] = EauD(x + 1, y, z);
						valsEau[2] = EauD(x + 1, y + 1, z);
						valsEau[3] = EauD(x, y + 1, z);
						valsEau[4] = EauD(x, y, z + 1);
						valsEau[5] = EauD(x + 1, y, z + 1);
						valsEau[6] = EauD(x + 1, y + 1, z + 1);
						valsEau[7] = EauD(x, y + 1, z + 1);

						int cubeIndex = 0;
						for (int i = 0; i < 8; i++)
							if (valsEau[i] > Isolevel) cubeIndex |= 1 << i;

						if (EdgeTable[cubeIndex] == 0) continue;

						Vector3[] vertList = _vertListEauRecyclables;
						vertList[0] = Interp(vertsEau[0], vertsEau[1], valsEau[0], valsEau[1]);
						vertList[1] = Interp(vertsEau[1], vertsEau[2], valsEau[1], valsEau[2]);
						vertList[2] = Interp(vertsEau[2], vertsEau[3], valsEau[2], valsEau[3]);
						vertList[3] = Interp(vertsEau[3], vertsEau[0], valsEau[3], valsEau[0]);
						vertList[4] = Interp(vertsEau[4], vertsEau[5], valsEau[4], valsEau[5]);
						vertList[5] = Interp(vertsEau[5], vertsEau[6], valsEau[5], valsEau[6]);
						vertList[6] = Interp(vertsEau[6], vertsEau[7], valsEau[6], valsEau[7]);
						vertList[7] = Interp(vertsEau[7], vertsEau[4], valsEau[7], valsEau[4]);
						vertList[8] = Interp(vertsEau[0], vertsEau[4], valsEau[0], valsEau[4]);
						vertList[9] = Interp(vertsEau[1], vertsEau[5], valsEau[1], valsEau[5]);
						vertList[10] = Interp(vertsEau[2], vertsEau[6], valsEau[2], valsEau[6]);
						vertList[11] = Interp(vertsEau[3], vertsEau[7], valsEau[3], valsEau[7]);

						for (int i = 0; TriTable[cubeIndex, i] != -1; i += 3)
						{
							Vector3 v0 = vertList[TriTable[cubeIndex, i]];
							Vector3 v1 = vertList[TriTable[cubeIndex, i + 1]];
							Vector3 v2 = vertList[TriTable[cubeIndex, i + 2]];
							Vector3 n = (v1 - v0).Cross(v2 - v0).Normalized();
							stEau.SetNormal(n);
							stEau.AddVertex(v0);
							stEau.SetNormal(n);
							stEau.AddVertex(v1);
							stEau.SetNormal(n);
							stEau.AddVertex(v2);
						}
					}
				}
			}

			stEau.GenerateNormals();
			meshEau = stEau.Commit();
		}
		}
		finally
		{
			ArrayPool<float>.Shared.Return(bufferDensities);
			ArrayPool<byte>.Shared.Return(bufferMaterials);
			if (bufferEau != null) ArrayPool<float>.Shared.Return(bufferEau);
		}
		return (meshTerrain, meshEau);
	}

	public void ActualiserMesh(bool urgent = false)
	{
		var toutesSections = new System.Collections.Generic.List<int>();
		for (int i = 0; i < NB_SECTIONS; i++)
			toutesSections.Add(i);
		ActualiserSectionsAffectees(toutesSections, urgent);
	}

	private void ActualiserSectionsAffectees(System.Collections.Generic.IEnumerable<int> sectionIndices, bool urgent)
	{
		float baseX = ChunkOffsetX * TailleChunk;
		float baseZ = ChunkOffsetZ * TailleChunk;
		var gestionnaire = GetParent() as Gestionnaire_Monde;

		foreach (int idx in sectionIndices)
		{
			int i = idx;
			Task.Run(() =>
			{
				var (meshTerrain, meshEau) = ConstruireMeshSection(i, baseX, baseZ);

				if (gestionnaire != null)
				{
					var apply = () => AppliquerMeshSection(i, meshTerrain, meshEau);
					if (urgent)
						gestionnaire.EnqueueMiseAJourUrgente(apply);
					else
						gestionnaire.EnqueueMiseAJourMainThread(apply);
				}
			});
		}
	}

	private void ActualiserSectionsAffectees(System.Collections.Generic.List<Vector3I> positions, bool urgent)
	{
		var sections = ObtenirSectionsAffectees(positions);
		ActualiserSectionsAffectees(sections, urgent);
	}

	private Vector3 Interp(Vector3 p1, Vector3 p2, float v1, float v2)
	{
		if (Mathf.Abs(Isolevel - v1) < 0.00001f) return p1;
		if (Mathf.Abs(Isolevel - v2) < 0.00001f) return p2;
		if (Mathf.Abs(v1 - v2) < 0.00001f) return p1;

		float t = (Isolevel - v1) / (v2 - v1);
		return p1 + t * (p2 - p1);
	}

	// API eau dynamique (coordonnées locales au chunk)
	public bool EstVoxelEau(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return false;
		lock (_verrouVoxel)
			return _densitiesEau != null && _densitiesEau[x, y, z] > Isolevel;
	}

	public bool EstVoxelAir(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return false;
		lock (_verrouVoxel)
		{
			bool sol = _densities[x, y, z] > Isolevel;
			bool eau = _densitiesEau != null && _densitiesEau[x, y, z] > Isolevel;
			return !sol && !eau;
		}
	}

	public bool EstVoxelSolide(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return false;
		lock (_verrouVoxel)
			return _densities[x, y, z] > Isolevel;
	}

	public void DefinirVoxelEau(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return;
		if (y <= 2) return;
		lock (_verrouVoxel)
		{
			_densities[x, y, z] = -10.0f;
			_materials[x, y, z] = 4;
			if (_densitiesEau != null)
				_densitiesEau[x, y, z] = 1.0f;
		}
	}

	public void DefinirVoxelAir(int x, int y, int z)
	{
		if (!EstDansLimitesChunk(x, y, z)) return;
		lock (_verrouVoxel)
		{
			_densities[x, y, z] = -10.0f;
			_materials[x, y, z] = 0;
			if (_densitiesEau != null)
				_densitiesEau[x, y, z] = -1.0f;
		}
	}
}
