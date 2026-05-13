using System;
using AutoExile.Mechanics;
using AutoExile.Systems;

namespace AutoExile;

internal class AreaStateCache
{
	public ExplorationSnapshot Exploration;

	public MechanicsSnapshot Mechanics;

	public long AreaHash;

	public DateTime CachedAt;
}
