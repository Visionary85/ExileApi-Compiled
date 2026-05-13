using System.Collections.Generic;
using System.Numerics;

namespace AutoExile;

internal class TileSignature
{
	public string Key = "";

	public SignatureTier Tier;

	public int TotalCount;

	public int NearCount;

	public float Concentration;

	public float Score;

	public List<Vector2> NearPositions = new List<Vector2>();
}
