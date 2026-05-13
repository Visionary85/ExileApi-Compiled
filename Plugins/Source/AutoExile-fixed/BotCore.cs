using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutoExile.Mechanics;
using AutoExile.Modes;
using AutoExile.Modes.BossEncounters;
using AutoExile.Modes.WaveFarm;
using AutoExile.Modes.WaveFarm.FarmPlans;
using AutoExile.Systems;
using AutoExile.WebServer;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using GameOffsets;
using ImGuiNET;
using SharpDX;

namespace AutoExile;

public class BotCore : BaseSettingsPlugin<BotSettings>
{
	public class MinimapIconEntry
	{
		public long EntityId;

		public string IconName = "";

		public string Path = "";

		public Vector2 GridPos;

		public string EntityType = "";
	}

	private BotContext _ctx;

	private IBotMode _mode = new IdleMode();

	private readonly Dictionary<string, IBotMode> _modes = new Dictionary<string, IBotMode>();

	private NavigationSystem _navigation = new NavigationSystem();

	private InteractionSystem _interaction = new InteractionSystem();

	private TileMap _tileMap = new TileMap();

	private CombatSystem _combat = new CombatSystem();

	private LootSystem _loot = new LootSystem();

	private MapDeviceSystem _mapDevice = new MapDeviceSystem();

	private StashSystem _stash = new StashSystem();

	private StashIndexer _stashIndex = new StashIndexer();

	private FaustusSystem _faustus = new FaustusSystem();

	private DateTime _lastGemLevelAt = DateTime.MinValue;

	private const int GemLevelCooldownMs = 10000;

	private ExplorationMap _exploration = new ExplorationMap();

	private LootTracker _lootTracker = new LootTracker();

	private MapMechanicManager _mechanics = new MapMechanicManager();

	private ThreatSystem _threat = new ThreatSystem();

	private EldritchAltarHandler _altarHandler = new EldritchAltarHandler();

	private NinjaPriceService _ninjaPrice = new NinjaPriceService();

	private RuntimeTracker _runtime = new RuntimeTracker();

	private EntityCache _entityCache = new EntityCache();

	private ThreatMap _threatMap = new ThreatMap();

	private DateTime _lastEntityPrune = DateTime.MinValue;

	private GemValuationService _gemValuation = new GemValuationService();

	private BotRecorder _recorder = new BotRecorder();

	private BossFightRecorder _mavenRecorder = new BossFightRecorder();

	private HumanGameplayRecorder _humanRecorder = new HumanGameplayRecorder();

	private BotWebServer? _webServer;

	private DataStore? _dataStore;

	private ProfileManager? _profileManager;

	private MapDatabase _mapDatabase;

	private readonly PerformanceTracker _perf = new PerformanceTracker();

	private FollowerMode? _followerMode;

	private BlightMode? _blightMode;

	private SimulacrumMode? _simulacrumMode;

	private HeistMode? _heistMode;

	private LabyrinthMode? _labyrinthMode;

	private BossMode? _bossMode;

	private string _lastAreaName = "";

	private long _lastAreaHash;

	private DateTime _areaChangedAt = DateTime.MinValue;

	private readonly Dictionary<string, AreaStateCache> _areaStateCache = new Dictionary<string, AreaStateCache>();

	private const int MaxCachedAreas = 3;

	private string _debugCircleLabel = "";

	private int _debugCircleRadius;

	private DateTime _debugCircleExpiry = DateTime.MinValue;

	private readonly Dictionary<string, int> _lastRangeValues = new Dictionary<string, int>();

	private List<TileSignature> _tileSignatures = new List<TileSignature>();

	private string _tileSignatureArea = "";

	private Vector2 _tileSignaturePlayerPos;

	private DateTime _tileSignatureScanTime;

	private bool _mapListPopulated;

	private DateTime _lastConfigSave = DateTime.Now;

	private const double ConfigSaveIntervalSec = 30.0;

	private bool _buffScanActive;

	private int _buffScanSlotIndex = -1;

	private HashSet<string> _buffScanBaseline = new HashSet<string>();

	private List<string> _buffScanResults = new List<string>();

	private string _buffScanStatus = "";

	private DateTime _buffScanStartTime;

	private bool _buffScanWaitingForCast;

	private const float BuffScanTimeoutSeconds = 8f;

	private long _lastTerrainHash;

	private DateTime _lastTerrainRefresh = DateTime.MinValue;

	private const double TerrainRefreshIntervalSec = 3.0;

	private readonly Dictionary<long, MinimapIconEntry> _knownMinimapIcons = new Dictionary<long, MinimapIconEntry>();

	private DateTime _lastMinimapIconScan = DateTime.MinValue;

	private const int MinimapIconScanIntervalMs = 2000;

	private string _dumpStatus = "";

	private bool _wasDead;

	private DateTime _deathTime;

	private int _reviveDelayMs;

	private DateTime _lastReviveClickAt = DateTime.MinValue;

	private DateTime _lastDismissAt = DateTime.MinValue;

	private readonly Random _rng = new Random();

	private IList<string>? _lastStashTabNames;

	public static BotCore? Instance { get; private set; }

	public NavigationSystem Navigation => _navigation;

	public CombatSystem Combat => _combat;

	public LootSystem Loot => _loot;

	public InteractionSystem Interaction => _interaction;

	public ExplorationMap Exploration => _exploration;

	public LootTracker LootTrackerInstance => _lootTracker;

	public MapMechanicManager Mechanics => _mechanics;

	public ThreatSystem Threat => _threat;

	public NinjaPriceService NinjaPrice => _ninjaPrice;

	public BotRecorder Recorder => _recorder;

	public IBotMode ActiveMode => _mode;

	public BotContext Context => _ctx;

	public HeistState? HeistState => _heistMode?.State;

	private float AreaSettleSeconds => base.Settings.AreaSettleSeconds.Value;

	public override bool Initialise()
	{
		base.Name = "AutoExile";
		Instance = this;
		_recorder.SetOutputDir(Path.Combine(base.DirectoryFullName, "Recordings"));
		_mavenRecorder.Initialize(base.DirectoryFullName);
		_humanRecorder.Initialize(base.DirectoryFullName, delegate(string msg)
		{
			base.LogMessage("[AutoExile] " + msg);
		});
		_ninjaPrice.Initialize(base.DirectoryFullName, delegate(string msg)
		{
			base.LogMessage("[AutoExile] NinjaPrice: " + msg);
		});
		_mapDatabase = new MapDatabase(delegate(string msg)
		{
			base.LogMessage("[AutoExile] " + msg);
		});
		_mapDatabase.Initialize(base.DirectoryFullName);
		_ctx = new BotContext
		{
			Game = base.GameController,
			Navigation = _navigation,
			Interaction = _interaction,
			TileMap = _tileMap,
			Combat = _combat,
			Loot = _loot,
			MapDevice = _mapDevice,
			Stash = _stash,
			StashIndex = _stashIndex,
			Faustus = _faustus,
			Exploration = _exploration,
			LootTracker = _lootTracker,
			Mechanics = _mechanics,
			Threat = _threat,
			AltarHandler = _altarHandler,
			NinjaPrice = _ninjaPrice,
			Runtime = _runtime,
			Entities = _entityCache,
			ThreatMap = _threatMap,
			MapDatabase = _mapDatabase,
			Settings = base.Settings,
			Perf = _perf,
			Log = delegate(string msg)
			{
				base.LogMessage("[AutoExile] " + msg);
			}
		};
		RegisterMode(new IdleMode());
		_followerMode = new FollowerMode();
		RegisterMode(_followerMode);
		_blightMode = new BlightMode();
		RegisterMode(_blightMode);
		_simulacrumMode = new SimulacrumMode();
		RegisterMode(_simulacrumMode);
		RegisterMode(new LevelingMode());
		_heistMode = new HeistMode();
		RegisterMode(_heistMode);
		_labyrinthMode = new LabyrinthMode();
		RegisterMode(_labyrinthMode);
		_bossMode = new BossMode();
		_bossMode.Register(new KingEncounter());
		_bossMode.Register(new OshabiEncounter());
		_bossMode.Register(new FearEncounter());
		_bossMode.Register(new MavenEncounter());
		_bossMode.Register(new SareshEncounter());
		RegisterMode(_bossMode);
		WaveFarmMode waveFarm = new WaveFarmMode();
		waveFarm.Register(new AlchAndGoPlan());
		waveFarm.Register(new StackedDeckPlan());
		RegisterMode(waveFarm);
		ListNode farmStrategy = base.Settings.Farming.FarmStrategy;
		farmStrategy.OnValueSelected = (Action<string>)Delegate.Combine(farmStrategy.OnValueSelected, (Action<string>)delegate(string name)
		{
			waveFarm.ApplyPlanDefaults(name, base.Settings, delegate(string msg)
			{
				base.LogMessage("[AutoExile] " + msg);
			});
			_profileManager?.SaveActive(base.Settings);
		});
		_mechanics.Register(new UltimatumMechanic());
		_mechanics.Register(new HarvestMechanic());
		_mechanics.Register(new WishesMechanic());
		_mechanics.Register(new EssenceMechanic());
		_mechanics.Register(new RitualMechanic());
		_combat.RefreshKeybindings(base.GameController);
		string value = base.Settings.Boss.BossType.Value;
		base.Settings.Boss.BossType.SetListValues(_bossMode.EncounterNames.ToList());
		if (!string.IsNullOrEmpty(value) && _bossMode.EncounterNames.Contains(value))
		{
			base.Settings.Boss.BossType.Value = value;
		}
		ListNode activeMode = base.Settings.ActiveMode;
		string text = ((activeMode != null) ? activeMode.Value : null);
		base.Settings.ActiveMode.SetListValues(_modes.Keys.ToList());
		if (!string.IsNullOrEmpty(text) && _modes.ContainsKey(text))
		{
			SetMode(text);
		}
		else
		{
			SetMode("Idle");
		}
		ListNode activeMode2 = base.Settings.ActiveMode;
		activeMode2.OnValueSelected = (Action<string>)Delegate.Combine(activeMode2.OnValueSelected, (Action<string>)delegate(string name)
		{
			if (_modes.ContainsKey(name) && _mode.Name != name)
			{
				SetMode(name);
			}
		});
		_profileManager = new ProfileManager(delegate(string msg)
		{
			base.LogMessage("[AutoExile] " + msg);
		});
		_profileManager.Initialize(base.DirectoryFullName);
		_profileManager.OnProfileSwitched += delegate
		{
			_runtime.Reset();
		};
		_profileManager.LoadActive(base.Settings);
		_dataStore = new DataStore(delegate(string msg)
		{
			base.LogMessage("[AutoExile] " + msg);
		});
		_dataStore.Initialize(base.DirectoryFullName);
		_lootTracker.OnItemRecorded = delegate(string name, double num, int slots)
		{
			GameController gameController = base.GameController;
			object obj;
			if (gameController == null)
			{
				obj = null;
			}
			else
			{
				AreaController area = gameController.Area;
				if (area == null)
				{
					obj = null;
				}
				else
				{
					AreaInstance currentArea = area.CurrentArea;
					obj = ((currentArea != null) ? currentArea.Name : null);
				}
			}
			if (obj == null)
			{
				obj = "";
			}
			string area2 = (string)obj;
			_dataStore.RecordLoot(name, num, slots, area2, _mode.Name);
			BotSettings.NotificationSettings notifications = base.Settings.Notifications;
			if (notifications.EnableDiscordNotifications.Value && !string.IsNullOrWhiteSpace(notifications.DiscordWebhookUrl.Value) && num >= (double)notifications.MinChaosValueNotification.Value)
			{
				string text2 = notifications.NotificationKeywords.Value ?? "";
				if (string.IsNullOrWhiteSpace(text2) || Array.Exists(text2.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), (string kw) => name.Contains(kw, StringComparison.OrdinalIgnoreCase)))
				{
					DiscordNotifier.Notify(notifications.DiscordWebhookUrl.Value, name, area2, num);
				}
			}
		};
		_loot.OnItemSkipped = delegate(string itemName, string reason, double chaosValue)
		{
			GameController gameController = base.GameController;
			object obj;
			if (gameController == null)
			{
				obj = null;
			}
			else
			{
				AreaController area = gameController.Area;
				if (area == null)
				{
					obj = null;
				}
				else
				{
					AreaInstance currentArea = area.CurrentArea;
					obj = ((currentArea != null) ? currentArea.Name : null);
				}
			}
			if (obj == null)
			{
				obj = "";
			}
			string details = (string)obj;
			string text2 = ((chaosValue > 0.0) ? $" ({chaosValue:F0}c)" : "");
			_dataStore.RecordEvent("loot_skip", itemName + text2 + ": " + reason, details);
			_perf.RecordFailure("lootSkip", reason ?? "(unknown)");
		};
		_mapListPopulated = false;
		if (base.Settings.WebUiEnabled.Value)
		{
			_webServer = new BotWebServer(base.Settings.WebUiPort.Value, base.Settings.WebUiNetworkAccess.Value, delegate(string msg)
			{
				base.LogMessage("[AutoExile] " + msg);
			});
			_webServer.Settings = base.Settings;
			_webServer.DataStore = _dataStore;
			_webServer.ProfileManager = _profileManager;
			_webServer.Runtime = _runtime;
			_webServer.MapDatabase = _mapDatabase;
			_webServer.NinjaPrice = _ninjaPrice;
			_webServer.LootTracker = _lootTracker;
			_webServer.GemValuation = _gemValuation;
			_webServer.ScanNearbyMonsters = ScanNearbyMonstersForWebUI;
			_webServer.GetPlayerBuffs = GetPlayerBuffsForWebUI;
			_webServer.Start();
		}
		return base.Initialise();
	}

	private List<(string Name, string Rarity, float Distance)> ScanNearbyMonstersForWebUI()
	{
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Invalid comparison between Unknown and I4
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Expected I4, but got Unknown
		List<(string, string, float)> list = new List<(string, string, float)>();
		try
		{
			GameController gameController = base.GameController;
			if (((gameController != null) ? gameController.Player : null) == null || !gameController.InGame)
			{
				return list;
			}
			Vector2 gridPosNum = gameController.Player.GridPosNum;
			foreach (Entity onlyValidEntity in gameController.EntityListWrapper.OnlyValidEntities)
			{
				if ((int)onlyValidEntity.Type == 100 && onlyValidEntity.IsHostile && onlyValidEntity.IsAlive)
				{
					string renderName = onlyValidEntity.RenderName;
					if (!string.IsNullOrEmpty(renderName))
					{
						float item = Vector2.Distance(onlyValidEntity.GridPosNum, gridPosNum);
						MonsterRarity rarity = onlyValidEntity.Rarity;
						list.Add((renderName, (rarity - 1) switch
						{
							0 => "Magic", 
							1 => "Rare", 
							2 => "Unique", 
							_ => "Normal", 
						}, item));
					}
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private List<string> GetPlayerBuffsForWebUI()
	{
		List<string> list = new List<string>();
		try
		{
			GameController gameController = base.GameController;
			if (((gameController != null) ? gameController.Player : null) == null || !gameController.InGame)
			{
				return list;
			}
			List<Buff> buffs = gameController.Player.Buffs;
			if (buffs == null)
			{
				return list;
			}
			foreach (Buff item in buffs)
			{
				if (!string.IsNullOrEmpty(item.Name) && !list.Contains(item.Name))
				{
					list.Add(item.Name);
				}
			}
		}
		catch
		{
		}
		return list;
	}

	public override void AreaChange(AreaInstance area)
	{
		string text = ((area != null) ? area.Name : null) ?? "";
		IngameState ingameState = base.GameController.IngameState;
		uint? obj;
		if (ingameState == null)
		{
			obj = null;
		}
		else
		{
			IngameData data = ingameState.Data;
			obj = ((data != null) ? new uint?(data.CurrentAreaHash) : ((uint?)null));
		}
		uint? num = obj;
		uint valueOrDefault = num.GetValueOrDefault();
		if (valueOrDefault == 0 || valueOrDefault == _lastAreaHash)
		{
			return;
		}
		string lastAreaName = _lastAreaName;
		base.LogMessage($"[BotCore] Area changed: '{lastAreaName}' -> '{text}' (hash {_lastAreaHash} -> {valueOrDefault})");
		if (!string.IsNullOrEmpty(lastAreaName) && _exploration.IsInitialized && !_areaStateCache.ContainsKey(lastAreaName))
		{
			_mechanics.ForceCompleteActive();
			_areaStateCache[lastAreaName] = new AreaStateCache
			{
				Exploration = _exploration.CreateSnapshot(),
				Mechanics = _mechanics.CreateSnapshot(),
				AreaHash = _lastAreaHash,
				CachedAt = DateTime.Now
			};
			while (_areaStateCache.Count > 3)
			{
				string text2 = null;
				DateTime dateTime = DateTime.MaxValue;
				foreach (KeyValuePair<string, AreaStateCache> item in _areaStateCache)
				{
					if (item.Value.CachedAt < dateTime)
					{
						dateTime = item.Value.CachedAt;
						text2 = item.Key;
					}
				}
				if (text2 == null)
				{
					break;
				}
				_areaStateCache.Remove(text2);
			}
			_ctx.Log($"[Cache] Saved area state for '{lastAreaName}' hash={_lastAreaHash} ({_areaStateCache.Count} cached)");
		}
		_lastAreaName = text;
		_lastAreaHash = valueOrDefault;
		_areaChangedAt = DateTime.Now;
		_tileMap.Clear();
		_tileMap.Load(base.GameController);
		_ctx.TileScan = (_tileMap.IsLoaded ? TileScanner.ScanMapWide(_tileMap) : null);
		if (_ctx.TileScan != null)
		{
			base.LogMessage($"[TileScan] {text}: {_ctx.TileScan.DetectedMechanics.Count} mechanics detected (map-wide)");
		}
		_loot.ClearFailed();
		_entityCache.Rebuild(base.GameController.EntityListWrapper.OnlyValidEntities);
		IngameState ingameState2 = base.GameController.IngameState;
		object obj2;
		if (ingameState2 == null)
		{
			obj2 = null;
		}
		else
		{
			IngameData data2 = ingameState2.Data;
			obj2 = ((data2 != null) ? data2.RawPathfindingData : null);
		}
		int[][] array = (int[][])obj2;
		if (array != null)
		{
			_threatMap.Initialize(array);
			_threatMap.RebuildFromEntities(_entityCache.Monsters);
		}
		_combat.ClearUnreachable();
		_combat.RefreshKeybindings(base.GameController);
		_altarHandler.Reset();
		_lootTracker.OnAreaChanged();
		ClearMinimapIcons();
		ScanMinimapIcons();
		if (_areaStateCache.TryGetValue(text, out AreaStateCache value) && value.AreaHash == valueOrDefault)
		{
			_exploration.RestoreSnapshot(value.Exploration);
			_mechanics.RestoreSnapshot(value.Mechanics);
			_areaStateCache.Remove(text);
			_ctx.Log($"[Cache] Restored area state for '{text}' hash={valueOrDefault}");
			return;
		}
		_mechanics.Reset();
		IngameState ingameState3 = base.GameController.IngameState;
		object obj3;
		if (ingameState3 == null)
		{
			obj3 = null;
		}
		else
		{
			IngameData data3 = ingameState3.Data;
			obj3 = ((data3 != null) ? data3.RawPathfindingData : null);
		}
		int[][] array2 = (int[][])obj3;
		IngameState ingameState4 = base.GameController.IngameState;
		object obj4;
		if (ingameState4 == null)
		{
			obj4 = null;
		}
		else
		{
			IngameData data4 = ingameState4.Data;
			obj4 = ((data4 != null) ? data4.RawTerrainTargetingData : null);
		}
		int[][] tgtGrid = (int[][])obj4;
		if (array2 != null && base.GameController.Player != null)
		{
			Vector2 playerGridPos = new Vector2(base.GameController.Player.GridPosNum.X, base.GameController.Player.GridPosNum.Y);
			_exploration.Initialize(array2, tgtGrid, playerGridPos, base.Settings.Build.BlinkRange.Value);
		}
	}

	public override Job Tick()
	{
		//IL_031c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0321: Unknown result type (might be due to invalid IL or missing references)
		//IL_0354: Unknown result type (might be due to invalid IL or missing references)
		if (!ToggleNode.op_Implicit(base.Settings.Enable) || !base.GameController.InGame)
		{
			return base.Tick();
		}
		TickWebServer();
		_runtime.Tick(base.Settings.Running.Value);
		if (base.Settings.Running.Value && _runtime.IsExpired(base.Settings.Run.MaxRuntimeMinutes.Value))
		{
			base.Settings.Running.Value = false;
			base.LogMessage($"[AutoExile] Max runtime ({base.Settings.Run.MaxRuntimeMinutes.Value} min) reached — bot stopped");
		}
		if ((DateTime.Now - _lastConfigSave).TotalSeconds >= 30.0)
		{
			_lastConfigSave = DateTime.Now;
			_profileManager?.SaveActive(base.Settings);
		}
		if (!base.GameController.IsForeGroundCache)
		{
			return base.Tick();
		}
		_ctx.DeltaTime = (float)base.GameController.DeltaTime;
		_ctx.MinimapIcons = _knownMinimapIcons;
		if (!_mapListPopulated)
		{
			PopulateMapList();
		}
		SyncStashTabNames();
		bool canAct = BotInput.CanAct;
		if (!_exploration.IsInitialized && base.GameController.Player != null)
		{
			IngameState ingameState = base.GameController.IngameState;
			object obj;
			if (ingameState == null)
			{
				obj = null;
			}
			else
			{
				IngameData data = ingameState.Data;
				obj = ((data != null) ? data.RawPathfindingData : null);
			}
			int[][] array = (int[][])obj;
			IngameState ingameState2 = base.GameController.IngameState;
			object obj2;
			if (ingameState2 == null)
			{
				obj2 = null;
			}
			else
			{
				IngameData data2 = ingameState2.Data;
				obj2 = ((data2 != null) ? data2.RawTerrainTargetingData : null);
			}
			int[][] tgtGrid = (int[][])obj2;
			if (array != null)
			{
				Vector2 playerGridPos = new Vector2(base.GameController.Player.GridPosNum.X, base.GameController.Player.GridPosNum.Y);
				_exploration.Initialize(array, tgtGrid, playerGridPos, base.Settings.Build.BlinkRange.Value);
			}
		}
		if (_exploration.IsInitialized && base.GameController.Player != null)
		{
			Vector2 playerGridPos2 = new Vector2(base.GameController.Player.GridPosNum.X, base.GameController.Player.GridPosNum.Y);
			_exploration.Update(playerGridPos2);
			ScanAreaTransitions();
			ScanMinimapIcons();
		}
		_navigation.BlinkRange = base.Settings.Build.BlinkRange.Value;
		_navigation.DashMinDistance = base.Settings.Build.DashMinDistance.Value;
		_navigation.PathMergeThreshold = base.Settings.Build.PathMergeThreshold.Value;
		BotInput.ActionCooldownMs = base.Settings.ActionCooldownMs.Value;
		BotInput.WindowRect = base.GameController.Window.GetWindowRectangleTimeCache;
		BotInput.TickHeldKeys();
		BotInput.TickMovementLayer();
		BotSettings.SkillSlotConfig primaryMovement = base.Settings.Build.GetPrimaryMovement();
		_navigation.MoveKey = (Keys)((primaryMovement == null) ? 84 : ((int)primaryMovement.Key.Value));
		_combat.RefreshSkillBar(base.GameController, base.Settings.Build);
		_navigation.MovementSkills = _combat.MovementSkills;
		BotSettings.ThreatSettings threat = base.Settings.Threat;
		_threat.Enabled = threat.Enabled.Value;
		_threat.ThreatRadius = threat.ThreatRadius.Value;
		_threat.DodgeTriggerDistance = threat.DodgeTriggerDistance.Value;
		_threat.DodgeMinProgress = threat.DodgeMinProgress.Value;
		_threat.DodgeMaxProgress = threat.DodgeMaxProgress.Value;
		_threat.MonitorRares = threat.MonitorRares.Value;
		if (base.Settings.DumpGameState.PressedOnce())
		{
			base.LogMessage("[AutoExile] Dumping all debug data...");
			TriggerGameStateDump();
			_recorder.ForceDump("hotkey");
			base.LogMessage("[AutoExile] Recording: " + _recorder.LastDumpStatus);
			if (_tileSignatures.Count == 0)
			{
				ScanTileSignatures();
			}
			base.LogMessage($"[AutoExile] Tile signatures: {_tileSignatures.Count}");
		}
		if (base.Settings.RecordGameplay.PressedOnce())
		{
			_humanRecorder.Toggle(base.GameController, base.Settings.Running.Value);
			string text = (base.Settings.Running.Value ? "BOT" : "HUMAN");
			base.LogMessage("[AutoExile] Recorder (" + text + "): " + (_humanRecorder.IsRecording ? "RECORDING" : "stopped"));
		}
		if (_humanRecorder.IsRecording)
		{
			_humanRecorder.RecordTick(base.GameController, _ctx);
		}
		if (_tileSignatures.Count > 0 && _lastAreaName != _tileSignatureArea)
		{
			_tileSignatures.Clear();
		}
		_loot.SkipLowValueUniques = base.Settings.Loot.SkipLowValueUniques.Value;
		_loot.MinUniqueChaosValue = base.Settings.Loot.MinUniqueChaosValue.Value;
		_loot.MinChaosPerSlot = base.Settings.Loot.MinChaosPerSlot.Value;
		_loot.IgnoreQuestItems = base.Settings.Loot.IgnoreQuestItems.Value;
		_loot.FilterClusterJewels = base.Settings.Loot.FilterClusterJewels.Value;
		_loot.MinClusterJewelChaosValue = base.Settings.Loot.MinClusterJewelChaosValue.Value;
		_loot.FilterSkillGems = base.Settings.Loot.FilterSkillGems.Value;
		_loot.MinGemChaosValue = base.Settings.Loot.MinGemChaosValue.Value;
		_loot.AlwaysLoot20QualityGems = base.Settings.Loot.AlwaysLoot20QualityGems.Value;
		_loot.FilterSynthesisedItems = base.Settings.Loot.FilterSynthesisedItems.Value;
		string text2 = base.Settings.Loot.SynthesisedWhitelist.Value ?? "";
		_loot.SynthesisedWhitelist = (from s in text2.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			where s.Length > 0
			select s).ToList();
		string text3 = base.Settings.Loot.MustLootUniques.Value ?? "";
		_loot.MustLootUniques = new HashSet<string>(from s in text3.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			where s.Length > 0
			select s, StringComparer.OrdinalIgnoreCase);
		_loot.LabelToggleUnstick = base.Settings.Loot.LabelToggleUnstick.Value;
		_loot.LabelToggleCooldownSeconds = base.Settings.Loot.LabelToggleCooldownSeconds.Value;
		_loot.PriceService = _ninjaPrice;
		_lootTracker.PriceService = _ninjaPrice;
		_interaction.Cache = _entityCache;
		if ((DateTime.Now - _lastEntityPrune).TotalMilliseconds > 1000.0)
		{
			_entityCache.Prune();
			_lastEntityPrune = DateTime.Now;
		}
		if (base.GameController.Player != null)
		{
			_threatMap.Reconcile(base.GameController.Player.GridPosNum, _entityCache);
		}
		_interaction.InteractRadius = base.Settings.InteractRadius.Value;
		_mapDevice.InteractRadius = base.Settings.InteractRadius.Value;
		_mapDevice.Interaction = _interaction;
		_stash.InteractRadius = base.Settings.InteractRadius.Value;
		int num = base.Settings.ExtraLatencyMs.Value;
		if (num == 0)
		{
			IngameState ingameState3 = base.GameController.IngameState;
			int? obj3;
			if (ingameState3 == null)
			{
				obj3 = null;
			}
			else
			{
				ServerData serverData = ingameState3.ServerData;
				obj3 = ((serverData != null) ? new int?(serverData.Latency) : ((int?)null));
			}
			int? num2 = obj3;
			int valueOrDefault = num2.GetValueOrDefault();
			num = ((valueOrDefault > 0) ? valueOrDefault : 0);
		}
		float extraLatencySec = (float)num / 1000f;
		_interaction.ExtraLatencySec = extraLatencySec;
		_interaction.MaxClickAttempts = base.Settings.MaxClickAttempts.Value;
		_mapDevice.ExtraLatencySec = extraLatencySec;
		_mapDevice.MaxClickAttempts = base.Settings.MaxClickAttempts.Value;
		_stash.ExtraLatencySec = extraLatencySec;
		_combat.ExtraLatencySec = extraLatencySec;
		string text4 = base.Settings.Build.BlacklistedEnemies.Value ?? "";
		_combat.BlacklistedEnemies = new HashSet<string>(from s in text4.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			where s.Length > 0
			select s, StringComparer.OrdinalIgnoreCase);
		_navigation.ExtraLatencyMs = num;
		_ninjaPrice.Tick(base.GameController);
		_gemValuation.BuildColourMap(base.GameController);
		_stash.ActionCooldownMs = base.Settings.Loot.StashItemCooldownMs.Value;
		_stash.ApplyIncubators = base.Settings.AutoApplyIncubators.Value;
		_recorder.RecordTick(base.GameController, _mode.Name, (_mode as WaveFarmMode)?.Status ?? (_mode as BlightMode)?.Phase.ToString() ?? (_mode as SimulacrumMode)?.Phase.ToString() ?? (_mode as HeistMode)?.Phase.ToString() ?? (_mode as LabyrinthMode)?.Phase.ToString() ?? (_mode as FollowerMode)?.State.ToString() ?? (_mode as BossMode)?.Phase.ToString() ?? "", (_mode as WaveFarmMode)?.Decision ?? (_mode as SimulacrumMode)?.Decision ?? (_mode as HeistMode)?.Decision ?? (_mode as FollowerMode)?.Decision ?? (_mode as BossMode)?.Decision ?? "", (_mode as WaveFarmMode)?.Status ?? (_mode as BossMode)?.Status ?? "", _navigation, _interaction, _loot, _threat);
		if (!ToggleNode.op_Implicit(base.Settings.Running))
		{
			BotInput.StopMovement();
			return base.Tick();
		}
		if (!HandleInterrupts())
		{
			return base.Tick();
		}
		if ((DateTime.Now - _areaChangedAt).TotalSeconds < (double)AreaSettleSeconds)
		{
			return base.Tick();
		}
		if (!BotInput.CanTick)
		{
			return base.Tick();
		}
		_threat.Tick(base.GameController);
		_mavenRecorder.Tick(base.GameController);
		if (_followerMode != null)
		{
			_followerMode.LeaderName = base.Settings.Follower.LeaderName.Value;
			_followerMode.FollowDistance = base.Settings.Follower.FollowDistance.Value;
			_followerMode.StopDistance = base.Settings.Follower.StopDistance.Value;
			_followerMode.FollowThroughTransitions = base.Settings.Follower.FollowThroughTransitions.Value;
			_followerMode.EnableCombat = base.Settings.Follower.EnableCombat.Value;
			_followerMode.EnableLoot = base.Settings.Follower.EnableLoot.Value;
			_followerMode.LootNearLeaderOnly = base.Settings.Follower.LootNearLeaderOnly.Value;
		}
		_mode.Tick(_ctx);
		if (_mode is BossMode bossMode && !string.IsNullOrEmpty(bossMode.LastDodgeAction))
		{
			_recorder.SetDodgeAction(bossMode.LastDodgeAction);
		}
		if (canAct)
		{
			_navigation.Tick(base.GameController);
		}
		TickGemLevelUp();
		return base.Tick();
	}

	public override void Render()
	{
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_011f: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02af: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_034c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0351: Unknown result type (might be due to invalid IL or missing references)
		//IL_0336: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0507: Unknown result type (might be due to invalid IL or missing references)
		if (!ToggleNode.op_Implicit(base.Settings.Enable) || !base.GameController.InGame)
		{
			return;
		}
		if (base.Settings.ToggleRunning.PressedOnce())
		{
			base.Settings.Running.Value = !base.Settings.Running.Value;
			if (base.Settings.Running.Value)
			{
				if (!_lootTracker.IsActive)
				{
					_lootTracker.StartSession();
				}
				_simulacrumMode?.State.ResetWaveTimer();
			}
			else if (_lootTracker.IsActive)
			{
				_lootTracker.StopSession();
			}
		}
		UpdateDebugRangeCircle();
		bool value = base.Settings.Running.Value;
		Color val = (value ? Color.LimeGreen : Color.Yellow);
		string text = (value ? ("BOT: " + _mode.Name) : ("BOT: PAUSED (" + _mode.Name + ")"));
		base.Graphics.DrawText(text, new Vector2(100f, 80f), val);
		int value2 = base.Settings.Run.MaxRuntimeMinutes.Value;
		TimeSpan activeDuration = _runtime.ActiveDuration;
		string text2 = $"{(int)activeDuration.TotalHours}:{activeDuration.Minutes:D2}";
		string text3;
		Color val2;
		if (value2 <= 0)
		{
			text3 = "Runtime: " + text2 + " (no limit)";
			val2 = Color.LightGray;
		}
		else
		{
			TimeSpan timeSpan = _runtime.Remaining(value2);
			string value3 = $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:D2}";
			text3 = $"Runtime: {text2} / {value2 / 60}:{value2 % 60:D2}  (stopping in {value3})";
			double num = timeSpan.TotalMinutes / (double)value2;
			val2 = ((timeSpan.TotalMinutes < 5.0) ? Color.Red : ((num < 0.1) ? Color.Orange : Color.LightGray));
		}
		base.Graphics.DrawText(text3, new Vector2(100f, 96f), val2);
		if (_humanRecorder.IsRecording)
		{
			string text4 = $"REC  {_humanRecorder.TicksRecorded} ticks";
			base.Graphics.DrawText(text4, new Vector2(100f, 116f), Color.Red);
		}
		RectangleF windowRectangle = base.GameController.Window.GetWindowRectangle();
		float width = ((RectangleF)(ref windowRectangle)).Width;
		_lootTracker.Render(base.Graphics, new Vector2(width - 250f, 80f));
		_ctx.Graphics = base.Graphics;
		_mode.Render(_ctx);
		RitualMechanic.RenderShopOverlay(_ctx, base.Graphics, base.GameController);
		_ctx.Graphics = null;
		if (base.Settings.DebugIncubatorOverlay.Value)
		{
			StashElement stashElement = base.GameController.IngameState.IngameUi.StashElement;
			if (stashElement == null || !((Element)stashElement).IsVisible)
			{
				InventoryElement inventoryPanel = base.GameController.IngameState.IngameUi.InventoryPanel;
				if (inventoryPanel == null || !((Element)inventoryPanel).IsVisible)
				{
					goto IL_0433;
				}
			}
			_stash.RenderDebugIncubators(base.Graphics, base.GameController);
		}
		goto IL_0433;
		IL_0433:
		RenderTileSignatures();
		RenderBossMarker();
		if (DateTime.Now < _debugCircleExpiry && _debugCircleRadius > 0 && base.GameController.Player != null)
		{
			Vector3 posNum = base.GameController.Player.PosNum;
			float num2 = (float)_debugCircleRadius * 10.88f;
			base.Graphics.DrawCircleInWorld(new Vector3(posNum.X, posNum.Y, posNum.Z), num2, Color.Yellow, 2f);
			Vector2 vector = base.GameController.IngameState.Camera.WorldToScreen(posNum);
			base.Graphics.DrawText(_debugCircleLabel, new Vector2(vector.X - 40f, vector.Y - 60f), Color.Yellow);
		}
	}

	private void UpdateDebugRangeCircle()
	{
		BotSettings.BuildSettings build = base.Settings.Build;
		_ = base.Settings.Loot;
		CheckRange("Blink Range", build.BlinkRange.Value);
		CheckRange("Dash Min Distance", build.DashMinDistance.Value);
		CheckRange("Fight Range", build.FightRange.Value);
		CheckRange("Combat Range", build.CombatRange.Value);
		CheckRange("Interact Radius", base.Settings.InteractRadius.Value);
		BotSettings.FollowerSettings follower = base.Settings.Follower;
		CheckRange("Follow Distance", follower.FollowDistance.Value);
		CheckRange("Stop Distance", follower.StopDistance.Value);
		int num = 1;
		foreach (BotSettings.SkillSlotConfig allSkillSlot in build.AllSkillSlots)
		{
			CheckRange($"Skill {num} Range", allSkillSlot.MaxTargetRange.Value);
			num++;
		}
	}

	private void CheckRange(string label, int currentValue)
	{
		if (_lastRangeValues.TryGetValue(label, out var value) && value != currentValue)
		{
			_debugCircleLabel = $"{label}: {currentValue}";
			_debugCircleRadius = currentValue;
			_debugCircleExpiry = DateTime.Now.AddSeconds(5.0);
		}
		_lastRangeValues[label] = currentValue;
	}

	public override void DrawSettings()
	{
		bool value = base.Settings.Enable.Value;
		if (ImGui.Checkbox("Enable", ref value))
		{
			base.Settings.Enable.Value = value;
		}
		ImGui.Separator();
		ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "All settings are managed via the web dashboard.");
		bool value2 = base.Settings.WebUiEnabled.Value;
		if (ImGui.Checkbox("Web UI Enabled", ref value2))
		{
			base.Settings.WebUiEnabled.Value = value2;
		}
		if (base.Settings.WebUiEnabled.Value)
		{
			bool value3 = base.Settings.WebUiNetworkAccess.Value;
			if (ImGui.Checkbox("Network Access", ref value3))
			{
				base.Settings.WebUiNetworkAccess.Value = value3;
			}
		}
		if (_webServer != null && _webServer.IsRunning)
		{
			ImGui.Separator();
			ImGui.TextColored(new Vector4(0.42f, 0.55f, 1f, 1f), "Web Dashboard: " + _webServer.Url);
			if (ImGui.SmallButton("Copy URL"))
			{
				ImGui.SetClipboardText(_webServer.Url);
			}
		}
		else if (base.Settings.WebUiEnabled.Value)
		{
			string text = _webServer?.LastError;
			if (!string.IsNullOrEmpty(text))
			{
				ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Web server failed: " + text);
			}
			else if (_webServer == null)
			{
				ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Web server not created — check WebUiEnabled setting, restart plugin");
			}
			else
			{
				ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Web server not running — restart plugin");
			}
		}
		ImGui.Separator();
		ImGui.Text($"Mode: {_mode.Name} | Running: {base.Settings.Running.Value}");
	}

	private unsafe void TickWebServer()
	{
		//IL_044a: Unknown result type (might be due to invalid IL or missing references)
		//IL_044f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0451: Unknown result type (might be due to invalid IL or missing references)
		//IL_0458: Unknown result type (might be due to invalid IL or missing references)
		//IL_045b: Invalid comparison between Unknown and I4
		//IL_0460: Unknown result type (might be due to invalid IL or missing references)
		//IL_0463: Invalid comparison between Unknown and I4
		if (_webServer == null || !_webServer.IsRunning)
		{
			return;
		}
		IngameState ingameState = base.GameController.IngameState;
		uint? obj;
		if (ingameState == null)
		{
			obj = null;
		}
		else
		{
			IngameData data = ingameState.Data;
			obj = ((data != null) ? new uint?(data.CurrentAreaHash) : ((uint?)null));
		}
		uint? num = obj;
		uint valueOrDefault = num.GetValueOrDefault();
		if ((valueOrDefault != _lastTerrainHash || (DateTime.Now - _lastTerrainRefresh).TotalSeconds >= 3.0) && valueOrDefault != 0)
		{
			IngameState ingameState2 = base.GameController.IngameState;
			object pfGrid;
			if (ingameState2 == null)
			{
				pfGrid = null;
			}
			else
			{
				IngameData data2 = ingameState2.Data;
				pfGrid = ((data2 != null) ? data2.RawPathfindingData : null);
			}
			IngameState ingameState3 = base.GameController.IngameState;
			object obj2;
			if (ingameState3 == null)
			{
				obj2 = null;
			}
			else
			{
				IngameData data3 = ingameState3.Data;
				obj2 = ((data3 != null) ? data3.RawTerrainTargetingData : null);
			}
			int[][] tgtGrid = (int[][])obj2;
			MapTerrainData mapTerrainData = MapRenderer.BuildTerrainData((int[][]?)pfGrid, tgtGrid, _exploration);
			if (mapTerrainData != null)
			{
				_webServer.UpdateTerrain(mapTerrainData, valueOrDefault);
				_lastTerrainHash = valueOrDefault;
				_lastTerrainRefresh = DateTime.Now;
			}
		}
		WebCommand command;
		while (_webServer.TryDequeueCommand(out command))
		{
			switch (command.Action)
			{
			case "start":
				base.Settings.Running.Value = true;
				if (!_lootTracker.IsActive)
				{
					_lootTracker.StartSession();
				}
				break;
			case "stop":
				base.Settings.Running.Value = false;
				if (_lootTracker.IsActive)
				{
					_lootTracker.StopSession();
				}
				break;
			case "setMode":
				if (!string.IsNullOrEmpty(command.Value) && _modes.ContainsKey(command.Value))
				{
					SetMode(command.Value);
				}
				break;
			}
		}
		try
		{
			string phase = "";
			string decision = "";
			string status = "";
			if (_mode is WaveFarmMode waveFarmMode)
			{
				status = waveFarmMode.Status;
				decision = waveFarmMode.Decision;
			}
			else if (_mode is SimulacrumMode { Phase: var phase2 } simulacrumMode)
			{
				phase = phase2.ToString();
				decision = simulacrumMode.Decision;
				status = simulacrumMode.StatusText;
			}
			else if (_mode is BlightMode { Phase: var phase3 } blightMode)
			{
				phase = phase3.ToString();
				status = blightMode.StatusText;
			}
			else if (_mode is HeistMode { Phase: var phase4 } heistMode)
			{
				phase = phase4.ToString();
				decision = heistMode.Decision;
			}
			else if (_mode is FollowerMode { State: var state } followerMode)
			{
				phase = state.ToString();
				decision = followerMode.Decision;
				status = followerMode.StatusText;
			}
			else if (_mode is LabyrinthMode { Phase: var phase5 } labyrinthMode)
			{
				phase = phase5.ToString();
				status = labyrinthMode.StatusText;
			}
			Entity player = base.GameController.Player;
			Vector2 vector = ((player != null) ? player.GridPosNum : Vector2.Zero);
			Vector2 playerGrid = new Vector2(vector.X, vector.Y);
			List<DetectedSkillSlot> list = null;
			try
			{
				IngameState ingameState4 = base.GameController.IngameState;
				object obj3;
				if (ingameState4 == null)
				{
					obj3 = null;
				}
				else
				{
					ServerData serverData = ingameState4.ServerData;
					obj3 = ((serverData != null) ? serverData.SkillBarIds : null);
				}
				IList<ushort> list2 = (IList<ushort>)obj3;
				Entity player2 = base.GameController.Player;
				Actor val = ((player2 != null) ? player2.GetComponent<Actor>() : null);
				if (list2 != null && ((val != null) ? val.ActorSkills : null) != null)
				{
					Dictionary<int, ActorSkill> dictionary = new Dictionary<int, ActorSkill>();
					foreach (ActorSkill actorSkill in val.ActorSkills)
					{
						int id = actorSkill.Id;
						if (!dictionary.ContainsKey(id))
						{
							dictionary[id] = actorSkill;
						}
					}
					list = new List<DetectedSkillSlot>();
					int num2 = Math.Min(list2.Count, 8);
					for (int i = 0; i < num2; i++)
					{
						Keys val2 = _combat.KeyForSlot(i);
						if ((int)val2 == 0 || (int)val2 == 2 || (int)val2 == 4)
						{
							continue;
						}
						ushort num3 = list2[i];
						if (num3 != 0 && dictionary.TryGetValue(num3, out var value))
						{
							string text = value.Name ?? "";
							if (!string.IsNullOrEmpty(text))
							{
								list.Add(new DetectedSkillSlot
								{
									SlotIndex = i,
									Key = ((object)(*(Keys*)(&val2))/*cast due to constrained. prefix*/).ToString(),
									SkillName = text,
									InternalName = (value.InternalName ?? ""),
									IsSpell = (value != null && value.IsSpell),
									IsAttack = (value != null && value.IsAttack),
									IsVaalSkill = (value != null && value.IsVaalSkill),
									IsInstant = (value != null && value.IsInstant),
									IsCry = (value != null && value.IsCry),
									IsChanneling = (value != null && value.IsChanneling),
									IsTotem = (value != null && value.IsTotem),
									IsTrap = (value != null && value.IsTrap),
									IsMine = (value != null && value.IsMine),
									SoulsPerUse = ((value != null) ? value.SoulsPerUse : 0),
									DeployedCount = ((value == null) ? ((int?)null) : value.DeployedObjects?.Count).GetValueOrDefault()
								});
							}
						}
					}
				}
			}
			catch
			{
			}
			Entity player3 = base.GameController.Player;
			Life obj5 = ((player3 != null) ? player3.GetComponent<Life>() : null);
			float hpPercent = Sanitize((obj5 != null) ? obj5.HPPercentage : 0f);
			float esPercent = Sanitize((obj5 != null) ? obj5.ESPercentage : 0f);
			float manaPercent = Sanitize((obj5 != null) ? obj5.MPPercentage : 0f);
			List<MapEntity> entities = null;
			try
			{
				entities = MapRenderer.CollectEntities(base.GameController, playerGrid);
			}
			catch
			{
			}
			List<float[]> navPath = null;
			try
			{
				navPath = MapRenderer.CollectNavPath(_navigation);
			}
			catch
			{
			}
			BotWebServer? webServer = _webServer;
			BotStatusSnapshot obj8 = new BotStatusSnapshot
			{
				Running = base.Settings.Running.Value,
				InGame = base.GameController.InGame,
				Mode = (_mode?.Name ?? "Unknown"),
				Phase = phase,
				Decision = decision,
				Status = status
			};
			AreaController area = base.GameController.Area;
			object obj9;
			if (area == null)
			{
				obj9 = null;
			}
			else
			{
				AreaInstance currentArea = area.CurrentArea;
				obj9 = ((currentArea != null) ? currentArea.Name : null);
			}
			if (obj9 == null)
			{
				obj9 = "";
			}
			obj8.Area = (string)obj9;
			obj8.HpPercent = hpPercent;
			obj8.EsPercent = esPercent;
			obj8.ManaPercent = manaPercent;
			obj8.InCombat = _combat.InCombat;
			obj8.NearbyMonsters = _combat.NearbyMonsterCount;
			Entity? bestTarget = _combat.BestTarget;
			obj8.CombatTarget = ((bestTarget != null) ? bestTarget.RenderName : null);
			obj8.IsNavigating = _navigation.IsNavigating;
			obj8.WaypointIndex = _navigation.CurrentWaypointIndex;
			obj8.WaypointTotal = _navigation.CurrentNavPath?.Count ?? 0;
			obj8.ExplorationCoverage = Sanitize((_exploration.IsInitialized && _exploration.ActiveBlob != null) ? _exploration.ActiveBlob.Coverage : 0f);
			obj8.ExplorationRegions = ((_exploration.IsInitialized && _exploration.ActiveBlob != null) ? _exploration.ActiveBlob.Regions.Count : 0);
			obj8.LootCandidates = _loot.Candidates.Count;
			obj8.SessionChaos = Sanitize((float)_lootTracker.TotalChaosValue);
			obj8.ChaosPerHour = Sanitize((float)_lootTracker.ChaosPerHour);
			obj8.ChaosPerDivine = Sanitize((float)_ninjaPrice.ChaosPerDivine);
			obj8.ItemsLooted = _lootTracker.TotalItemsLooted;
			obj8.MapsCompleted = _lootTracker.MapsCompleted;
			obj8.SessionDuration = ((_lootTracker.SessionDuration.TotalSeconds > 0.0) ? _lootTracker.SessionDuration.ToString("hh\\:mm\\:ss") : "");
			obj8.RuntimeActiveSeconds = (int)_runtime.ActiveDuration.TotalSeconds;
			obj8.RuntimeRemainingSeconds = ((base.Settings.Run.MaxRuntimeMinutes.Value > 0) ? ((int)_runtime.Remaining(base.Settings.Run.MaxRuntimeMinutes.Value).TotalSeconds) : 0);
			obj8.RuntimeMaxMinutes = base.Settings.Run.MaxRuntimeMinutes.Value;
			obj8.SimWave = _simulacrumMode?.State.CurrentWave ?? 0;
			obj8.SimWaveActive = _simulacrumMode?.State.IsWaveActive ?? false;
			obj8.SimDeaths = _simulacrumMode?.State.DeathCount ?? 0;
			obj8.SimRuns = _simulacrumMode?.State.RunsCompleted ?? 0;
			obj8.SimAvgWaves = Sanitize((float)(_simulacrumMode?.State.AverageWavesPerRun ?? 0.0));
			SimulacrumMode? simulacrumMode2 = _simulacrumMode;
			obj8.SimAvgRunTime = ((simulacrumMode2 != null && simulacrumMode2.State.RunsCompleted > 0) ? _simulacrumMode.State.AverageRunDuration.ToString("m\\:ss") : "");
			obj8.SimRunTime = ((_simulacrumMode != null && _mode == _simulacrumMode && _simulacrumMode.Phase >= SimPhase.FindMonolith && _simulacrumMode.Phase <= SimPhase.ExitMap) ? (DateTime.Now - _simulacrumMode.State.RunStartedAt).ToString("m\\:ss") : "");
			obj8.BossRuns = _bossMode?.RunsCompleted ?? 0;
			obj8.BossDeaths = _bossMode?.Deaths ?? 0;
			obj8.BossDrops = _bossMode?.TargetItemsLooted ?? 0;
			obj8.BossAvgRunTime = Sanitize((float)(_bossMode?.AvgRunTimeSeconds ?? 0.0));
			obj8.BossRunsPerDrop = Sanitize((float)(_bossMode?.RunsPerDrop ?? 0.0));
			obj8.BossChaosPerHour = Sanitize((float)(_bossMode?.ChaosPerHour(base.Settings.Boss.KeyDropChaosValue.Value) ?? 0.0));
			obj8.BossRunTime = ((_bossMode != null && _mode == _bossMode && _bossMode.Phase >= BossMode.BossPhase.InBossZone && _bossMode.Phase <= BossMode.BossPhase.ExitMap) ? (DateTime.Now - _bossMode.RunStartTime).ToString("m\\:ss") : "");
			obj8.FarmStrategy = ((_mode is WaveFarmMode) ? "Wave Farm" : "");
			obj8.FarmRuns = (_mode as WaveFarmMode)?.RunsCompleted ?? 0;
			obj8.FarmPhase = (_mode as WaveFarmMode)?.Status ?? "";
			obj8.LabIzaroEncounters = _labyrinthMode?.State.IzaroEncounterCount ?? 0;
			obj8.LabDeaths = _labyrinthMode?.State.DeathCount ?? 0;
			obj8.LabRuns = _labyrinthMode?.State.RunsCompleted ?? 0;
			obj8.LabGemsTransformed = _labyrinthMode?.State.GemsTransformed ?? 0;
			obj8.LabTotalProfit = Sanitize((float)(_labyrinthMode?.State.TotalProfit ?? 0.0));
			obj8.LabSelectedGem = _labyrinthMode?.State.SelectedGemName ?? "";
			obj8.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			obj8.PlayerGridX = Sanitize(playerGrid.X);
			obj8.PlayerGridY = Sanitize(playerGrid.Y);
			obj8.AreaHash = valueOrDefault;
			obj8.Entities = entities;
			obj8.NavPath = navPath;
			obj8.SkillBar = list;
			webServer.UpdateStatus(obj8);
		}
		catch (Exception ex)
		{
			base.LogMessage("[AutoExile] Web snapshot error: " + ex.Message);
		}
	}

	private static float Sanitize(float v)
	{
		if (!float.IsFinite(v))
		{
			return 0f;
		}
		return v;
	}

	public override void OnClose()
	{
		_webServer?.Stop();
		_webServer = null;
		Instance = null;
		base.OnClose();
	}

	private void ScanAreaTransitions()
	{
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Invalid comparison between Unknown and I4
		if (!_exploration.IsInitialized)
		{
			return;
		}
		foreach (Entity onlyValidEntity in base.GameController.EntityListWrapper.OnlyValidEntities)
		{
			if ((int)onlyValidEntity.Type == 104)
			{
				Vector2 gridPos = new Vector2(onlyValidEntity.GridPosNum.X, onlyValidEntity.GridPosNum.Y);
				_exploration.RecordTransition(gridPos, onlyValidEntity.RenderName ?? onlyValidEntity.Path ?? "");
			}
		}
	}

	private void ScanMinimapIcons()
	{
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		GameController gameController = base.GameController;
		if (((gameController != null) ? gameController.Player : null) == null || (DateTime.Now - _lastMinimapIconScan).TotalMilliseconds < 2000.0)
		{
			return;
		}
		_lastMinimapIconScan = DateTime.Now;
		IngameState ingameState = gameController.IngameState;
		object obj;
		if (ingameState == null)
		{
			obj = null;
		}
		else
		{
			IngameData data = ingameState.Data;
			obj = ((data != null) ? data.TileEntities : null);
		}
		List<Entity> list = (List<Entity>)obj;
		if (list == null)
		{
			return;
		}
		foreach (Entity item in list)
		{
			if (item == null || item.Path == null || _knownMinimapIcons.ContainsKey(item.Id))
			{
				continue;
			}
			try
			{
				MinimapIcon component = item.GetComponent<MinimapIcon>();
				if (component != null && component.Name != null)
				{
					_knownMinimapIcons[item.Id] = new MinimapIconEntry
					{
						EntityId = item.Id,
						IconName = component.Name,
						Path = item.Path,
						GridPos = item.GridPosNum,
						EntityType = ((object)item.Type/*cast due to constrained. prefix*/).ToString()
					};
				}
			}
			catch
			{
			}
		}
	}

	private void ClearMinimapIcons()
	{
		_knownMinimapIcons.Clear();
		_lastMinimapIconScan = DateTime.MinValue;
	}

	private void TriggerGameStateDump()
	{
		GameController gameController = base.GameController;
		IngameState ingameState = gameController.IngameState;
		object obj;
		if (ingameState == null)
		{
			obj = null;
		}
		else
		{
			IngameData data = ingameState.Data;
			obj = ((data != null) ? data.RawPathfindingData : null);
		}
		int[][] pfGrid = (int[][])obj;
		IngameState ingameState2 = gameController.IngameState;
		object obj2;
		if (ingameState2 == null)
		{
			obj2 = null;
		}
		else
		{
			IngameData data2 = ingameState2.Data;
			obj2 = ((data2 != null) ? data2.RawTerrainTargetingData : null);
		}
		int[][] tgtGrid = (int[][])obj2;
		IngameState ingameState3 = gameController.IngameState;
		object obj3;
		if (ingameState3 == null)
		{
			obj3 = null;
		}
		else
		{
			IngameData data3 = ingameState3.Data;
			obj3 = ((data3 != null) ? data3.RawTerrainHeightData : null);
		}
		float[][] heightGrid = (float[][])obj3;
		if (pfGrid == null || gameController.Player == null)
		{
			_dumpStatus = "Dump failed: no terrain data or player";
			base.LogMessage("[AutoExile] " + _dumpStatus);
			return;
		}
		Vector2 playerGrid = new Vector2(gameController.Player.GridPosNum.X, gameController.Player.GridPosNum.Y);
		AreaController area = gameController.Area;
		object obj4;
		if (area == null)
		{
			obj4 = null;
		}
		else
		{
			AreaInstance currentArea = area.CurrentArea;
			obj4 = ((currentArea != null) ? currentArea.Name : null);
		}
		if (obj4 == null)
		{
			obj4 = "Unknown";
		}
		string areaName = (string)obj4;
		string outputDir = Path.Combine(base.DirectoryFullName, "Dumps");
		GameStateSnapshot snapshot = BuildGameStateSnapshot(gameController, playerGrid);
		_dumpStatus = "Dumping...";
		base.LogMessage("[AutoExile] Starting game state dump...");
		Task.Run(delegate
		{
			string text = GameStateDump.Dump(pfGrid, tgtGrid, heightGrid, playerGrid, base.Settings.Build.BlinkRange.Value, _exploration, _navigation, snapshot, areaName, outputDir);
			_dumpStatus = text;
			base.LogMessage("[AutoExile] " + text);
		});
	}

	private GameStateSnapshot BuildGameStateSnapshot(GameController gc, Vector2 playerGrid)
	{
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Invalid comparison between Unknown and I4
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0332: Unknown result type (might be due to invalid IL or missing references)
		//IL_0337: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Invalid comparison between Unknown and I4
		GameStateSnapshot gameStateSnapshot = new GameStateSnapshot();
		StateMachine val = default(StateMachine);
		foreach (Entity onlyValidEntity in gc.EntityListWrapper.OnlyValidEntities)
		{
			Vector2 gridPosNum = onlyValidEntity.GridPosNum;
			float num = Vector2.Distance(gridPosNum, playerGrid);
			if (num > 360f)
			{
				continue;
			}
			EntityCategory category = CategorizeEntity(onlyValidEntity);
			EntitySnapshot obj = new EntitySnapshot
			{
				Id = onlyValidEntity.Id,
				Metadata = (onlyValidEntity.Metadata ?? ""),
				Path = (onlyValidEntity.Path ?? ""),
				EntityType = ((object)onlyValidEntity.Type/*cast due to constrained. prefix*/).ToString(),
				GridPos = gridPosNum,
				DistanceToPlayer = num,
				Category = category,
				IsAlive = onlyValidEntity.IsAlive,
				IsTargetable = onlyValidEntity.IsTargetable,
				IsHostile = onlyValidEntity.IsHostile,
				Rarity = (((int)onlyValidEntity.Type == 100) ? ((object)onlyValidEntity.Rarity/*cast due to constrained. prefix*/).ToString() : null),
				ShortName = ExtractShortName(onlyValidEntity.Metadata ?? onlyValidEntity.Path ?? "")
			};
			object renderName;
			if ((int)onlyValidEntity.Type != 109)
			{
				renderName = onlyValidEntity.RenderName ?? "";
			}
			else
			{
				Player component = onlyValidEntity.GetComponent<Player>();
				renderName = ((component != null) ? component.PlayerName : null) ?? onlyValidEntity.RenderName ?? "";
			}
			obj.RenderName = (string)renderName;
			EntitySnapshot entitySnapshot = obj;
			try
			{
				MinimapIcon component2 = onlyValidEntity.GetComponent<MinimapIcon>();
				if (component2 != null && component2.Name != null)
				{
					entitySnapshot.MinimapIconName = component2.Name;
				}
			}
			catch
			{
			}
			if (onlyValidEntity.TryGetComponent<StateMachine>(ref val) && val.States != null)
			{
				Dictionary<string, long> dictionary = new Dictionary<string, long>();
				try
				{
					foreach (StateMachineState state2 in val.States)
					{
						if (!string.IsNullOrEmpty(state2.Name))
						{
							dictionary[state2.Name] = state2.Value;
						}
					}
				}
				catch
				{
				}
				if (dictionary.Count > 0)
				{
					entitySnapshot.States = dictionary;
				}
			}
			gameStateSnapshot.Entities.Add(entitySnapshot);
		}
		HashSet<long> hashSet = new HashSet<long>(gameStateSnapshot.Entities.Select((EntitySnapshot e) => e.Id));
		IngameState ingameState = gc.IngameState;
		object obj4;
		if (ingameState == null)
		{
			obj4 = null;
		}
		else
		{
			IngameData data = ingameState.Data;
			obj4 = ((data != null) ? data.TileEntities : null);
		}
		List<Entity> list = (List<Entity>)obj4;
		if (list != null)
		{
			foreach (Entity item in list)
			{
				if (item == null || item.Path == null || hashSet.Contains(item.Id))
				{
					continue;
				}
				try
				{
					MinimapIcon component3 = item.GetComponent<MinimapIcon>();
					if (component3 != null && component3.Name != null)
					{
						gameStateSnapshot.Entities.Add(new EntitySnapshot
						{
							Id = item.Id,
							Path = item.Path,
							EntityType = ((object)item.Type/*cast due to constrained. prefix*/).ToString(),
							GridPos = item.GridPosNum,
							DistanceToPlayer = Vector2.Distance(item.GridPosNum, playerGrid),
							Category = CategorizeEntity(item),
							ShortName = ExtractShortName(item.Path),
							MinimapIconName = component3.Name
						});
					}
				}
				catch
				{
				}
			}
		}
		CombatSnapshot obj6 = new CombatSnapshot
		{
			InCombat = _combat.InCombat,
			NearbyMonsterCount = _combat.NearbyMonsterCount,
			CachedMonsterCount = _combat.CachedMonsterCount,
			PackCenter = _combat.PackCenter,
			DenseClusterCenter = _combat.DenseClusterCenter,
			NearestMonsterPos = _combat.NearestMonsterPos,
			LastAction = _combat.LastAction,
			LastSkillAction = _combat.LastSkillAction
		};
		Entity? bestTarget = _combat.BestTarget;
		obj6.BestTargetId = ((bestTarget != null) ? new uint?(bestTarget.Id) : ((uint?)null));
		obj6.WantsToMove = _combat.WantsToMove;
		obj6.NearestDormantPos = _combat.NearestDormantPos;
		obj6.NearestDormantDistance = _combat.NearestDormantDistance;
		obj6.NearestDormantPath = _combat.NearestDormantPath;
		gameStateSnapshot.Combat = obj6;
		gameStateSnapshot.Mode = new ModeSnapshot
		{
			Name = _mode.Name
		};
		if (_mode is SimulacrumMode simulacrumMode)
		{
			gameStateSnapshot.Mode.Phase = simulacrumMode.Phase.ToString();
			gameStateSnapshot.Mode.Status = simulacrumMode.StatusText;
			gameStateSnapshot.Mode.Decision = simulacrumMode.Decision;
			gameStateSnapshot.Mode.Extra["wave"] = simulacrumMode.State.CurrentWave;
			gameStateSnapshot.Mode.Extra["isWaveActive"] = simulacrumMode.State.IsWaveActive;
			gameStateSnapshot.Mode.Extra["deaths"] = simulacrumMode.State.DeathCount;
			gameStateSnapshot.Mode.Extra["monolithPos"] = ((!simulacrumMode.State.MonolithPosition.HasValue) ? ((object)Array.Empty<float>()) : ((object)new float[2]
			{
				simulacrumMode.State.MonolithPosition.Value.X,
				simulacrumMode.State.MonolithPosition.Value.Y
			}));
			gameStateSnapshot.Mode.Extra["highestWave"] = simulacrumMode.State.HighestWaveThisRun;
		}
		else if (_mode is WaveFarmMode waveFarmMode)
		{
			gameStateSnapshot.Mode.Status = waveFarmMode.Status;
			gameStateSnapshot.Mode.Decision = waveFarmMode.Decision;
			gameStateSnapshot.Mode.Extra["runsCompleted"] = waveFarmMode.RunsCompleted;
		}
		else if (_mode is BlightMode blightMode)
		{
			gameStateSnapshot.Mode.Phase = blightMode.Phase.ToString();
			gameStateSnapshot.Mode.Status = blightMode.StatusText;
			BlightState state = blightMode.State;
			gameStateSnapshot.Mode.Extra["pumpEntityId"] = state.PumpEntityId;
			gameStateSnapshot.Mode.Extra["pumpPos"] = ((!state.PumpPosition.HasValue) ? ((object)Array.Empty<float>()) : ((object)new float[2]
			{
				state.PumpPosition.Value.X,
				state.PumpPosition.Value.Y
			}));
			gameStateSnapshot.Mode.Extra["isEncounterActive"] = state.IsEncounterActive;
			gameStateSnapshot.Mode.Extra["isEncounterDone"] = state.IsEncounterDone;
			gameStateSnapshot.Mode.Extra["isTimerDone"] = state.IsTimerDone;
			gameStateSnapshot.Mode.Extra["encounterSucceeded"] = state.EncounterSucceeded;
			gameStateSnapshot.Mode.Extra["pumpUnderAttack"] = state.PumpUnderAttack;
			gameStateSnapshot.Mode.Extra["aliveMonsterCount"] = state.AliveMonsterCount;
			gameStateSnapshot.Mode.Extra["chestCount"] = state.ChestPositions.Count;
			gameStateSnapshot.Mode.Extra["portalPos"] = ((!state.PortalPosition.HasValue) ? ((object)Array.Empty<float>()) : ((object)new float[2]
			{
				state.PortalPosition.Value.X,
				state.PortalPosition.Value.Y
			}));
			gameStateSnapshot.Mode.Extra["deathCount"] = state.DeathCount;
		}
		else if (_mode is FollowerMode followerMode)
		{
			gameStateSnapshot.Mode.Phase = followerMode.State.ToString();
			gameStateSnapshot.Mode.Status = followerMode.StatusText;
			gameStateSnapshot.Mode.Decision = followerMode.Decision;
			gameStateSnapshot.Mode.Extra["leaderName"] = followerMode.LeaderName;
			gameStateSnapshot.Mode.Extra["followDistance"] = followerMode.FollowDistance;
			gameStateSnapshot.Mode.Extra["stopDistance"] = followerMode.StopDistance;
			gameStateSnapshot.Mode.Extra["lastLeaderPos"] = ((!followerMode.LastLeaderGridPos.HasValue) ? ((object)Array.Empty<float>()) : ((object)new float[2]
			{
				followerMode.LastLeaderGridPos.Value.X,
				followerMode.LastLeaderGridPos.Value.Y
			}));
			gameStateSnapshot.Mode.Extra["transitionTarget"] = ((!followerMode.TransitionTargetGridPos.HasValue) ? ((object)Array.Empty<float>()) : ((object)new float[2]
			{
				followerMode.TransitionTargetGridPos.Value.X,
				followerMode.TransitionTargetGridPos.Value.Y
			}));
			gameStateSnapshot.Mode.Extra["combatEnabled"] = followerMode.EnableCombat;
			gameStateSnapshot.Mode.Extra["lootEnabled"] = followerMode.EnableLoot;
		}
		gameStateSnapshot.Interaction = new InteractionSnapshot
		{
			IsBusy = _interaction.IsBusy,
			Status = _interaction.Status
		};
		_loot.Scan(gc);
		int visibleGroundLabelCount = 0;
		try
		{
			IngameState ingameState2 = gc.IngameState;
			object obj7;
			if (ingameState2 == null)
			{
				obj7 = null;
			}
			else
			{
				IngameUIElements ingameUi = ingameState2.IngameUi;
				if (ingameUi == null)
				{
					obj7 = null;
				}
				else
				{
					ItemsOnGroundLabelElement itemsOnGroundLabelElement = ingameUi.ItemsOnGroundLabelElement;
					obj7 = ((itemsOnGroundLabelElement != null) ? itemsOnGroundLabelElement.VisibleGroundItemLabels : null);
				}
			}
			List<VisibleGroundItemDescription> list2 = (List<VisibleGroundItemDescription>)obj7;
			if (list2 != null)
			{
				visibleGroundLabelCount = list2.Count();
			}
		}
		catch
		{
		}
		gameStateSnapshot.Loot = new LootSnapshot
		{
			HasLootNearby = _loot.HasLootNearby,
			CandidateCount = _loot.Candidates.Count,
			FailedCount = _loot.FailedCount,
			LastSkipReason = _loot.LastSkipReason,
			NinjaBridgeStatus = _loot.NinjaBridgeStatus,
			LootRadius = _interaction.InteractRadius,
			VisibleGroundLabelCount = visibleGroundLabelCount,
			Candidates = _loot.Candidates.Select((LootCandidate c) => new LootCandidateSnapshot
			{
				EntityId = c.Entity.Id,
				ItemName = c.ItemName,
				Distance = c.Distance,
				ChaosValue = c.ChaosValue,
				InventorySlots = c.InventorySlots,
				ChaosPerSlot = c.ChaosPerSlot,
				GridPos = c.Entity.GridPosNum
			}).ToList()
		};
		return gameStateSnapshot;
	}

	private static EntityCategory CategorizeEntity(Entity entity)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Invalid comparison between Unknown and I4
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Invalid comparison between Unknown and I4
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Invalid comparison between Unknown and I4
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Invalid comparison between Unknown and I4
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Invalid comparison between Unknown and I4
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Invalid comparison between Unknown and I4
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Invalid comparison between Unknown and I4
		EntityType type = entity.Type;
		string text = entity.Path ?? "";
		if ((int)type == 109)
		{
			return EntityCategory.Player;
		}
		if ((int)type == 100)
		{
			return EntityCategory.Monster;
		}
		if ((int)type == 101)
		{
			return EntityCategory.Chest;
		}
		if ((int)type == 104)
		{
			return EntityCategory.AreaTransition;
		}
		if ((int)type == 118 || (int)type == 105)
		{
			return EntityCategory.Portal;
		}
		if ((int)type == 107)
		{
			return EntityCategory.Stash;
		}
		if (text.Contains("Afflictionator"))
		{
			return EntityCategory.Monolith;
		}
		if (text.Contains("MiscellaneousObjects/Stash"))
		{
			return EntityCategory.Stash;
		}
		return EntityCategory.Other;
	}

	private static string ExtractShortName(string metadata)
	{
		if (string.IsNullOrEmpty(metadata))
		{
			return "";
		}
		int num = metadata.LastIndexOf('/');
		if (num < 0 || num >= metadata.Length - 1)
		{
			return metadata;
		}
		int num2 = num + 1;
		return metadata.Substring(num2, metadata.Length - num2);
	}

	private void TickGemLevelUp()
	{
		//IL_03f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0409: Unknown result type (might be due to invalid IL or missing references)
		//IL_041d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0133: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		if (!base.Settings.AutoLevelGems.Value || !BotInput.CanAct || (DateTime.Now - _lastGemLevelAt).TotalMilliseconds < 10000.0)
		{
			return;
		}
		try
		{
			GemLvlUpPanel gemLvlUpPanel = base.GameController.IngameState.IngameUi.GemLvlUpPanel;
			if (gemLvlUpPanel == null || !((Element)gemLvlUpPanel).IsVisible)
			{
				return;
			}
			List<GemLevelUpElement> gemsToLvlUp = gemLvlUpPanel.GemsToLvlUp;
			if (gemsToLvlUp == null || gemsToLvlUp.Count == 0)
			{
				return;
			}
			RectangleF windowRectangle = base.GameController.Window.GetWindowRectangle();
			try
			{
				dynamic val = gemLvlUpPanel;
				Element val2 = (Element)val.LevelUpAllGemsButton;
				if (val2 != null && val2.IsVisible)
				{
					RectangleF clientRect = val2.GetClientRect();
					BotInput.Click(new Vector2(((RectangleF)(ref windowRectangle)).X + ((RectangleF)(ref clientRect)).Center.X, ((RectangleF)(ref windowRectangle)).Y + ((RectangleF)(ref clientRect)).Center.Y));
					_lastGemLevelAt = DateTime.Now;
					return;
				}
			}
			catch
			{
			}
			foreach (GemLevelUpElement item in gemsToLvlUp)
			{
				if (item == null || !((Element)item).IsVisible)
				{
					continue;
				}
				dynamic val3 = null;
				int num = 0;
				for (int i = 0; i < ((Element)item).ChildCount; i++)
				{
					Element childAtIndex = ((Element)item).GetChildAtIndex(i);
					if (childAtIndex == null || !childAtIndex.IsVisible)
					{
						continue;
					}
					RectangleF clientRect2 = childAtIndex.GetClientRect();
					if (((RectangleF)(ref clientRect2)).Width > 5f && ((RectangleF)(ref clientRect2)).Width < 60f && ((RectangleF)(ref clientRect2)).Height > 5f && ((RectangleF)(ref clientRect2)).Height < 60f)
					{
						num++;
						if (num == 2)
						{
							val3 = childAtIndex;
							break;
						}
					}
				}
				if (val3 == null)
				{
					continue;
				}
				try
				{
					if (!(bool)val3.IsEnabled)
					{
						continue;
					}
				}
				catch
				{
				}
				RectangleF val4 = (RectangleF)val3.GetClientRect();
				BotInput.Click(new Vector2(((RectangleF)(ref windowRectangle)).X + ((RectangleF)(ref val4)).Center.X, ((RectangleF)(ref windowRectangle)).Y + ((RectangleF)(ref val4)).Center.Y));
				_lastGemLevelAt = DateTime.Now;
				break;
			}
		}
		catch
		{
		}
	}

	private bool HandleInterrupts()
	{
		//IL_0394: Unknown result type (might be due to invalid IL or missing references)
		//IL_0399: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_02de: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01be: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ea: Unknown result type (might be due to invalid IL or missing references)
		GameController gameController = base.GameController;
		if (gameController.IsLoading)
		{
			return false;
		}
		if (!gameController.Player.IsAlive)
		{
			if (!_wasDead && _blightMode != null)
			{
				_blightMode.State.DeathCount++;
			}
			if (!_wasDead && _simulacrumMode != null)
			{
				_simulacrumMode.State.DeathCount++;
			}
			if (!_wasDead && _heistMode != null)
			{
				_heistMode.State.DeathCount++;
			}
			if (!_wasDead && _labyrinthMode != null)
			{
				_labyrinthMode.State.DeathCount++;
			}
			if (!_wasDead && _bossMode != null)
			{
				_bossMode.IncrementDeathCount();
			}
			if (!_wasDead)
			{
				_deathTime = DateTime.Now;
			}
			_wasDead = true;
			int num = ((_reviveDelayMs == 0) ? (_reviveDelayMs = 500 + _rng.Next(500)) : _reviveDelayMs);
			if ((DateTime.Now - _deathTime).TotalMilliseconds < (double)num)
			{
				return false;
			}
			if (BotInput.CanAct && (DateTime.Now - _lastReviveClickAt).TotalMilliseconds > 1000.0)
			{
				try
				{
					ResurrectPanel resurrectPanel = gameController.IngameState.IngameUi.ResurrectPanel;
					if (resurrectPanel != null && ((Element)resurrectPanel).IsVisible)
					{
						Element resurrectAtCheckpoint = resurrectPanel.ResurrectAtCheckpoint;
						if (resurrectAtCheckpoint != null && resurrectAtCheckpoint.IsVisible)
						{
							RectangleF clientRect = resurrectAtCheckpoint.GetClientRect();
							Vector2 vector = new Vector2(((RectangleF)(ref clientRect)).Center.X, ((RectangleF)(ref clientRect)).Center.Y);
							RectangleF windowRectangle = gameController.Window.GetWindowRectangle();
							BotInput.Click(new Vector2(((RectangleF)(ref windowRectangle)).X + vector.X, ((RectangleF)(ref windowRectangle)).Y + vector.Y));
							_lastReviveClickAt = DateTime.Now;
						}
					}
				}
				catch
				{
				}
			}
			return false;
		}
		_wasDead = false;
		_reviveDelayMs = 0;
		try
		{
			IngameUIElements ingameUi = gameController.IngameState.IngameUi;
			RitualWindow ritualWindow = ingameUi.RitualWindow;
			if (ritualWindow != null && ((Element)ritualWindow).IsVisible && !(_mode is WaveFarmMode) && BotInput.CanAct && (DateTime.Now - _lastDismissAt).TotalMilliseconds > 500.0)
			{
				Element childAtIndex = ((Element)ingameUi.RitualWindow).GetChildAtIndex(9);
				if (childAtIndex != null && childAtIndex.IsVisible)
				{
					RectangleF clientRect2 = childAtIndex.GetClientRect();
					if (((RectangleF)(ref clientRect2)).Width > 5f)
					{
						RectangleF getWindowRectangleTimeCache = gameController.Window.GetWindowRectangleTimeCache;
						BotInput.Click(new Vector2(((RectangleF)(ref clientRect2)).X + ((RectangleF)(ref clientRect2)).Width / 2f + ((RectangleF)(ref getWindowRectangleTimeCache)).X, ((RectangleF)(ref clientRect2)).Y + ((RectangleF)(ref clientRect2)).Height / 2f + ((RectangleF)(ref getWindowRectangleTimeCache)).Y));
						_lastDismissAt = DateTime.Now;
						base.LogMessage("[AutoExile] Dismissing unexpected RitualShop (clicking X button)");
						return false;
					}
				}
			}
			SellWindow sellWindow = ingameUi.SellWindow;
			if (sellWindow != null && ((Element)sellWindow).IsVisible && BotInput.CanAct && (DateTime.Now - _lastDismissAt).TotalMilliseconds > 500.0)
			{
				RectangleF windowRectangle2 = gameController.Window.GetWindowRectangle();
				BotInput.Click(new Vector2(((RectangleF)(ref windowRectangle2)).X + ((RectangleF)(ref windowRectangle2)).Width * 0.5f, ((RectangleF)(ref windowRectangle2)).Y + ((RectangleF)(ref windowRectangle2)).Height * 0.4f));
				_lastDismissAt = DateTime.Now;
				base.LogMessage("[AutoExile] Dismissing unexpected VendorWindow (clicking world)");
				return false;
			}
		}
		catch
		{
		}
		return true;
	}

	public override void EntityAdded(Entity entity)
	{
		_entityCache.OnEntityAdded(entity);
		_threatMap.OnEntityAdded(entity);
		if (_blightMode != null && _mode == _blightMode)
		{
			_blightMode.OnEntityAdded(entity);
		}
	}

	public override void EntityRemoved(Entity entity)
	{
		_entityCache.OnEntityRemoved(entity);
		_threatMap.OnEntityRemoved(entity);
		if (_blightMode != null && _mode == _blightMode)
		{
			GameController gameController = base.GameController;
			if (((gameController != null) ? gameController.Player : null) != null)
			{
				Vector2 gridPosNum = base.GameController.Player.GridPosNum;
				_blightMode.OnEntityRemoved(entity, gridPosNum);
			}
		}
	}

	public void RegisterMode(IBotMode mode)
	{
		_modes[mode.Name] = mode;
	}

	public void SetMode(string name)
	{
		if (!_modes.TryGetValue(name, out IBotMode value))
		{
			_ctx.Log("Unknown mode: " + name);
		}
		else if (value != _mode)
		{
			_ctx.Log("Switching mode: " + _mode.Name + " -> " + value.Name);
			_mode.OnExit();
			_mode = value;
			_mode.OnEnter(_ctx);
			if (base.Settings.ActiveMode != null)
			{
				base.Settings.ActiveMode.Value = name;
			}
			_profileManager?.SaveActive(base.Settings);
		}
	}

	private void StartBuffScan(int slotIndex)
	{
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Invalid comparison between Unknown and I4
		GameController gameController = base.GameController;
		if (((gameController != null) ? gameController.Player : null) == null || !gameController.InGame)
		{
			_buffScanStatus = "Not in game";
			return;
		}
		_buffScanSlotIndex = slotIndex;
		_buffScanResults.Clear();
		_buffScanStatus = "Snapshotting nearby monster buffs...";
		_buffScanBaseline.Clear();
		foreach (Entity onlyValidEntity in gameController.EntityListWrapper.OnlyValidEntities)
		{
			if ((int)onlyValidEntity.Type != 100 || !onlyValidEntity.IsHostile || !onlyValidEntity.IsAlive || Vector2.Distance(onlyValidEntity.GridPosNum, gameController.Player.GridPosNum) > 80f)
			{
				continue;
			}
			try
			{
				List<Buff> buffs = onlyValidEntity.Buffs;
				if (buffs == null)
				{
					continue;
				}
				foreach (Buff item in buffs)
				{
					if (!string.IsNullOrEmpty(item.Name))
					{
						_buffScanBaseline.Add(item.Name);
					}
				}
			}
			catch
			{
			}
		}
		_buffScanActive = true;
		_buffScanWaitingForCast = true;
		_buffScanStartTime = DateTime.Now;
		_buffScanStatus = $"Baseline: {_buffScanBaseline.Count} buff names. Cast your skill on nearby monsters now!";
		base.LogMessage($"[AutoExile] Buff scan started for slot {slotIndex + 1} — {_buffScanBaseline.Count} baseline buffs");
	}

	private void TickBuffScan()
	{
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Invalid comparison between Unknown and I4
		if (!_buffScanActive)
		{
			return;
		}
		GameController gameController = base.GameController;
		if (((gameController != null) ? gameController.Player : null) == null)
		{
			_buffScanActive = false;
			_buffScanStatus = "Lost game state";
			return;
		}
		if ((DateTime.Now - _buffScanStartTime).TotalSeconds > 8.0)
		{
			_buffScanActive = false;
			_buffScanWaitingForCast = false;
			if (_buffScanResults.Count == 0)
			{
				_buffScanStatus = "Timed out — no new buffs detected. Cast the skill on enemies and try again.";
			}
			return;
		}
		HashSet<string> hashSet = new HashSet<string>();
		foreach (Entity onlyValidEntity in gameController.EntityListWrapper.OnlyValidEntities)
		{
			if ((int)onlyValidEntity.Type != 100 || !onlyValidEntity.IsHostile || !onlyValidEntity.IsAlive || Vector2.Distance(onlyValidEntity.GridPosNum, gameController.Player.GridPosNum) > 80f)
			{
				continue;
			}
			try
			{
				List<Buff> buffs = onlyValidEntity.Buffs;
				if (buffs == null)
				{
					continue;
				}
				foreach (Buff item in buffs)
				{
					if (!string.IsNullOrEmpty(item.Name) && !_buffScanBaseline.Contains(item.Name))
					{
						hashSet.Add(item.Name);
					}
				}
			}
			catch
			{
			}
		}
		if (hashSet.Count > 0)
		{
			_buffScanResults = hashSet.OrderBy((string n) => n).ToList();
			_buffScanWaitingForCast = false;
			_buffScanActive = false;
			_buffScanStatus = $"Found {_buffScanResults.Count} new buff(s). Pick one:";
			base.LogMessage("[AutoExile] Buff scan found: " + string.Join(", ", _buffScanResults));
		}
	}

	private void DrawBuffScannerUI()
	{
		TickBuffScan();
		if (_buffScanSlotIndex < 0)
		{
			return;
		}
		if (!string.IsNullOrEmpty(_buffScanStatus))
		{
			ImGui.TextColored(_buffScanWaitingForCast ? new Vector4(1f, 1f, 0f, 1f) : ((_buffScanResults.Count > 0) ? new Vector4(0f, 1f, 0f, 1f) : new Vector4(1f, 0.5f, 0f, 1f)), _buffScanStatus);
		}
		if (_buffScanResults.Count > 0)
		{
			BotSettings.SkillSlotConfig[] array = base.Settings.Build.AllSkillSlots.ToArray();
			if (_buffScanSlotIndex < array.Length)
			{
				BotSettings.SkillSlotConfig skillSlotConfig = array[_buffScanSlotIndex];
				foreach (string buffScanResult in _buffScanResults)
				{
					if (ImGui.Button(buffScanResult))
					{
						skillSlotConfig.BuffDebuffName.Value = buffScanResult;
						_buffScanStatus = $"Set Slot{_buffScanSlotIndex + 1} buff name to \"{buffScanResult}\"";
						_buffScanResults.Clear();
						base.LogMessage($"[AutoExile] Set slot {_buffScanSlotIndex + 1} BuffDebuffName = \"{buffScanResult}\"");
					}
					ImGui.SameLine();
				}
				ImGui.NewLine();
			}
			if (ImGui.SmallButton("Cancel##buffscan"))
			{
				_buffScanResults.Clear();
				_buffScanSlotIndex = -1;
				_buffScanStatus = "";
			}
		}
		else if (_buffScanActive && ImGui.SmallButton("Cancel Scan"))
		{
			_buffScanActive = false;
			_buffScanStatus = "Cancelled";
		}
	}

	private void PopulateMapList()
	{
		try
		{
			FilesContainer files = base.GameController.Files;
			List<AtlasNode> list = ((files == null) ? null : files.AtlasNodes?.EntriesList);
			if (list == null || list.Count == 0)
			{
				return;
			}
			List<string> list2 = new List<string>();
			int num = Math.Min(list.Count, 110);
			for (int i = 0; i < num; i++)
			{
				WorldArea area = list[i].Area;
				string text = ((area != null) ? area.Name : null);
				if (!string.IsNullOrEmpty(text))
				{
					string text2 = (_mapDatabase.IsSupported(text) ? "★ " : "");
					list2.Add(text2 + text);
				}
			}
			list2.Sort((string a, string b) => a.TrimStart(new char[2] { '★', ' ' }).CompareTo(b.TrimStart(new char[2] { '★', ' ' })));
			string savedFarmMap = base.Settings.Farming.MapName.Value;
			base.Settings.Farming.MapName.SetListValues(list2);
			if (!string.IsNullOrEmpty(savedFarmMap))
			{
				string text3 = list2.FirstOrDefault((string m) => m.TrimStart(new char[2] { '★', ' ' }) == savedFarmMap.TrimStart(new char[2] { '★', ' ' }));
				if (text3 != null)
				{
					base.Settings.Farming.MapName.Value = text3;
				}
			}
			_mapListPopulated = true;
			base.LogMessage($"[AutoExile] Map list populated: {list2.Count} maps ({_mapDatabase.SupportedMaps.Count()} supported)");
		}
		catch (Exception ex)
		{
			base.LogMessage("[AutoExile] Map list population failed: " + ex.Message);
		}
	}

	private static List<string> WithSavedOption(List<string> options, string? saved)
	{
		if (string.IsNullOrWhiteSpace(saved) || options.Contains(saved))
		{
			return options;
		}
		return new List<string>(options) { saved };
	}

	private void SyncStashTabNames()
	{
		try
		{
			IngameState ingameState = base.GameController.IngameState;
			object obj;
			if (ingameState == null)
			{
				obj = null;
			}
			else
			{
				IngameUIElements ingameUi = ingameState.IngameUi;
				obj = ((ingameUi != null) ? ingameUi.StashElement : null);
			}
			StashElement val = (StashElement)obj;
			if (val == null || !((Element)val).IsVisible)
			{
				return;
			}
			IList<string> allStashNames = val.AllStashNames;
			if (allStashNames == null || allStashNames.Count == 0)
			{
				return;
			}
			if (_lastStashTabNames != null && _lastStashTabNames.Count == allStashNames.Count)
			{
				bool flag = true;
				for (int i = 0; i < allStashNames.Count; i++)
				{
					if (allStashNames[i] != _lastStashTabNames[i])
					{
						flag = false;
						break;
					}
				}
				if (flag)
				{
					return;
				}
			}
			_lastStashTabNames = allStashNames.ToList();
			List<string> list = new List<string>();
			list.Add("");
			list.AddRange(allStashNames);
			string value = base.Settings.Stash.DumpTabName.Value;
			string value2 = base.Settings.Stash.FragmentTabName.Value;
			string value3 = base.Settings.Stash.MappingSuppliesTabName.Value;
			List<string> listValues = WithSavedOption(list, value);
			List<string> listValues2 = WithSavedOption(list, value2);
			List<string> listValues3 = WithSavedOption(list, value3);
			base.Settings.Stash.DumpTabName.SetListValues(listValues);
			base.Settings.Stash.FragmentTabName.SetListValues(listValues2);
			base.Settings.Stash.MappingSuppliesTabName.SetListValues(listValues3);
			base.Settings.Stash.DumpTabName.Value = value;
			base.Settings.Stash.FragmentTabName.Value = value2;
			base.Settings.Stash.MappingSuppliesTabName.Value = value3;
		}
		catch
		{
		}
	}

	private void ScanTileSignatures()
	{
		GameController gameController = base.GameController;
		if (((gameController != null) ? gameController.Player : null) == null || !_tileMap.IsLoaded)
		{
			base.LogMessage("[AutoExile] Tile scan failed: no player or tile map not loaded");
			return;
		}
		Vector2 vector = new Vector2(gameController.Player.GridPosNum.X, gameController.Player.GridPosNum.Y);
		AreaController area = gameController.Area;
		object obj;
		if (area == null)
		{
			obj = null;
		}
		else
		{
			AreaInstance currentArea = area.CurrentArea;
			obj = ((currentArea != null) ? currentArea.Name : null);
		}
		if (obj == null)
		{
			obj = "Unknown";
		}
		string text = (string)obj;
		List<TileSignature> list = new List<TileSignature>();
		foreach (string allKey in _tileMap.GetAllKeys())
		{
			List<Vector2> positions = _tileMap.GetPositions(allKey);
			if (positions == null)
			{
				continue;
			}
			int count = positions.Count;
			if (count >= 20)
			{
				continue;
			}
			List<Vector2> list2 = new List<Vector2>();
			foreach (Vector2 item in positions)
			{
				if (Vector2.Distance(vector, item) <= 100f)
				{
					list2.Add(item);
				}
			}
			if (list2.Count != 0)
			{
				float num = (float)list2.Count / (float)count;
				SignatureTier? signatureTier = null;
				if (count == 1)
				{
					signatureTier = SignatureTier.Unique;
				}
				else if (count <= 3)
				{
					signatureTier = SignatureTier.VeryRare;
				}
				else if (count <= 9 && num >= 0.5f)
				{
					signatureTier = SignatureTier.Rare;
				}
				else if (count <= 19 && num >= 0.8f)
				{
					signatureTier = SignatureTier.Clustered;
				}
				if (signatureTier.HasValue)
				{
					float score = 1f / (float)count * num * (float)list2.Count;
					list.Add(new TileSignature
					{
						Key = allKey,
						Tier = signatureTier.Value,
						TotalCount = count,
						NearCount = list2.Count,
						Concentration = num,
						Score = score,
						NearPositions = list2
					});
				}
			}
		}
		list.Sort(delegate(TileSignature a, TileSignature b)
		{
			int num2 = a.Tier.CompareTo(b.Tier);
			return (num2 == 0) ? b.Score.CompareTo(a.Score) : num2;
		});
		_tileSignatures = list;
		_tileSignatureArea = text;
		_tileSignaturePlayerPos = vector;
		_tileSignatureScanTime = DateTime.Now;
		base.LogMessage($"[AutoExile] Tile scan: {list.Count} signatures found in {text}");
		WriteTileSignatureLog();
		WriteTileDump(text);
		List<string> list3 = (from s in list
			where s.Tier == SignatureTier.Unique || s.Tier == SignatureTier.VeryRare
			select s.Key).ToList();
		if (list3.Count > 0)
		{
			_mapDatabase.SaveBossTiles(text, list3);
			_mapListPopulated = false;
		}
	}

	private void WriteTileSignatureLog()
	{
		if (_tileSignatures.Count == 0)
		{
			return;
		}
		string text = Path.Combine(base.DirectoryFullName, "Dumps");
		Directory.CreateDirectory(text);
		string text2 = Path.Combine(text, "TileSignatures.log");
		List<string> list = new List<string>();
		list.Add($"=== {_tileSignatureScanTime:yyyy-MM-dd HH:mm:ss} | {_tileSignatureArea} | Player: ({_tileSignaturePlayerPos.X:F0},{_tileSignaturePlayerPos.Y:F0}) | {_tileSignatures.Count} signatures ===");
		foreach (TileSignature tileSignature in _tileSignatures)
		{
			string value = string.Join("; ", tileSignature.NearPositions.Select((Vector2 p) => $"({p.X:F0},{p.Y:F0})"));
			list.Add($"  [{tileSignature.Tier}] {tileSignature.Key} | total={tileSignature.TotalCount} near={tileSignature.NearCount} conc={tileSignature.Concentration:P0} score={tileSignature.Score:F3} | {value}");
		}
		list.Add("");
		File.AppendAllLines(text2, list);
		base.LogMessage("[AutoExile] Tile signatures written to " + text2);
	}

	private void WriteTileDump(string areaName)
	{
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_0178: Unknown result type (might be due to invalid IL or missing references)
		//IL_017f: Invalid comparison between Unknown and I4
		GameController gameController = base.GameController;
		if (((gameController != null) ? gameController.Player : null) == null || !_tileMap.IsLoaded)
		{
			return;
		}
		TerrainData terrain = gameController.IngameState.Data.Terrain;
		IMemory memory = gameController.Memory;
		int numCols = terrain.NumCols;
		int numRows = terrain.NumRows;
		TileStructure[] array;
		try
		{
			array = memory.ReadStdVector<TileStructure>(terrain.TgtArray);
		}
		catch
		{
			base.LogMessage("[AutoExile] TileDump: failed to read tile data");
			return;
		}
		if (array == null || array.Length == 0)
		{
			return;
		}
		Vector2 playerGrid = new Vector2(gameController.Player.GridPosNum.X, gameController.Player.GridPosNum.Y);
		(string Detail, string Path)[] tileEntries = new(string, string)[array.Length];
		for (int i = 0; i < array.Length; i++)
		{
			try
			{
				TgtTileStruct val = memory.Read<TgtTileStruct>(array[i].TgtFilePtr);
				tileEntries[i] = (Detail: MiscHelpers.ToString(memory.Read<TgtDetailStruct>(val.TgtDetailPtr).name, memory), Path: MiscHelpers.ToString(val.TgtPath, memory));
			}
			catch
			{
				tileEntries[i] = (Detail: "", Path: "");
			}
		}
		List<(long Id, string Path, string Name, int X, int Y, bool Targetable)> transitions = new List<(long, string, string, int, int, bool)>();
		foreach (Entity onlyValidEntity in gameController.EntityListWrapper.OnlyValidEntities)
		{
			if ((int)onlyValidEntity.Type == 104)
			{
				transitions.Add((onlyValidEntity.Id, onlyValidEntity.Path ?? "", onlyValidEntity.RenderName ?? "", (int)onlyValidEntity.GridPosNum.X, (int)onlyValidEntity.GridPosNum.Y, onlyValidEntity.IsTargetable));
			}
		}
		int pfMinX = int.MaxValue;
		int pfMaxX = 0;
		int pfMinY = int.MaxValue;
		int pfMaxY = 0;
		int[][] rawPathfindingData = gameController.IngameState.Data.RawPathfindingData;
		if (rawPathfindingData != null)
		{
			for (int j = 0; j < rawPathfindingData.Length; j++)
			{
				int[] array2 = rawPathfindingData[j];
				for (int k = 0; k < array2.Length; k++)
				{
					if (array2[k] > 0)
					{
						if (k < pfMinX)
						{
							pfMinX = k;
						}
						if (k > pfMaxX)
						{
							pfMaxX = k;
						}
						if (j < pfMinY)
						{
							pfMinY = j;
						}
						if (j > pfMaxY)
						{
							pfMaxY = j;
						}
					}
				}
			}
		}
		string outputDir = Path.Combine(base.DirectoryFullName, "Dumps");
		DateTime scanTime = DateTime.Now;
		Task.Run(delegate
		{
			try
			{
				Directory.CreateDirectory(outputDir);
				string text = Path.Combine(outputDir, $"TileDump_{areaName.Replace(" ", "_")}_{scanTime:yyyyMMdd_HHmmss}.json");
				using FileStream utf8Json = File.Create(text);
				using Utf8JsonWriter utf8JsonWriter = new Utf8JsonWriter(utf8Json, new JsonWriterOptions
				{
					Indented = false
				});
				utf8JsonWriter.WriteStartObject();
				utf8JsonWriter.WriteString("area", areaName);
				utf8JsonWriter.WriteString("scanTime", scanTime.ToString("o"));
				utf8JsonWriter.WriteNumber("playerX", (int)playerGrid.X);
				utf8JsonWriter.WriteNumber("playerY", (int)playerGrid.Y);
				utf8JsonWriter.WriteNumber("tileCols", numCols);
				utf8JsonWriter.WriteNumber("tileRows", numRows);
				utf8JsonWriter.WriteNumber("gridWidth", numCols * 23);
				utf8JsonWriter.WriteNumber("gridHeight", numRows * 23);
				utf8JsonWriter.WritePropertyName("walkableBounds");
				utf8JsonWriter.WriteStartObject();
				utf8JsonWriter.WriteNumber("minX", (pfMinX != int.MaxValue) ? pfMinX : 0);
				utf8JsonWriter.WriteNumber("maxX", pfMaxX);
				utf8JsonWriter.WriteNumber("minY", (pfMinY != int.MaxValue) ? pfMinY : 0);
				utf8JsonWriter.WriteNumber("maxY", pfMaxY);
				utf8JsonWriter.WriteEndObject();
				utf8JsonWriter.WritePropertyName("areaTransitions");
				utf8JsonWriter.WriteStartArray();
				foreach (var item in transitions)
				{
					utf8JsonWriter.WriteStartObject();
					utf8JsonWriter.WriteNumber("id", item.Id);
					utf8JsonWriter.WriteString("path", item.Path);
					utf8JsonWriter.WriteString("name", item.Name);
					utf8JsonWriter.WriteNumber("gridX", item.X);
					utf8JsonWriter.WriteNumber("gridY", item.Y);
					utf8JsonWriter.WriteBoolean("targetable", item.Targetable);
					utf8JsonWriter.WriteEndObject();
				}
				utf8JsonWriter.WriteEndArray();
				Dictionary<string, int> dictionary = new Dictionary<string, int>();
				Dictionary<string, int> dictionary2 = new Dictionary<string, int>();
				(string, string)[] array3 = tileEntries;
				for (int l = 0; l < array3.Length; l++)
				{
					var (text2, text3) = array3[l];
					if (!string.IsNullOrEmpty(text2))
					{
						dictionary[text2] = dictionary.GetValueOrDefault(text2) + 1;
					}
					if (!string.IsNullOrEmpty(text3))
					{
						dictionary2[text3] = dictionary2.GetValueOrDefault(text3) + 1;
					}
				}
				utf8JsonWriter.WritePropertyName("detailCounts");
				utf8JsonWriter.WriteStartObject();
				foreach (KeyValuePair<string, int> item2 in dictionary.OrderBy((KeyValuePair<string, int> kv) => kv.Value))
				{
					utf8JsonWriter.WriteNumber(item2.Key, item2.Value);
				}
				utf8JsonWriter.WriteEndObject();
				utf8JsonWriter.WritePropertyName("pathCounts");
				utf8JsonWriter.WriteStartObject();
				foreach (KeyValuePair<string, int> item3 in dictionary2.OrderBy((KeyValuePair<string, int> kv) => kv.Value))
				{
					utf8JsonWriter.WriteNumber(item3.Key, item3.Value);
				}
				utf8JsonWriter.WriteEndObject();
				utf8JsonWriter.WritePropertyName("tiles");
				utf8JsonWriter.WriteStartArray();
				for (int num = 0; num < tileEntries.Length; num++)
				{
					var (value, value2) = tileEntries[num];
					if (!string.IsNullOrEmpty(value) || !string.IsNullOrEmpty(value2))
					{
						int num2 = num % numCols;
						int num3 = num / numCols;
						utf8JsonWriter.WriteStartArray();
						utf8JsonWriter.WriteNumberValue(num2 * 23);
						utf8JsonWriter.WriteNumberValue(num3 * 23);
						utf8JsonWriter.WriteStringValue(value);
						utf8JsonWriter.WriteStringValue(value2);
						utf8JsonWriter.WriteEndArray();
					}
				}
				utf8JsonWriter.WriteEndArray();
				utf8JsonWriter.WriteEndObject();
				utf8JsonWriter.Flush();
				base.LogMessage($"[AutoExile] Tile dump written: {text} ({new FileInfo(text).Length / 1024}KB)");
			}
			catch (Exception ex)
			{
				base.LogMessage("[AutoExile] TileDump write error: " + ex.Message);
			}
		});
	}

	private void RenderBossMarker()
	{
	}

	private void RenderTileSignatures()
	{
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_010b: Unknown result type (might be due to invalid IL or missing references)
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Unknown result type (might be due to invalid IL or missing references)
		//IL_0261: Unknown result type (might be due to invalid IL or missing references)
		if (_tileSignatures.Count == 0)
		{
			return;
		}
		GameController gameController = base.GameController;
		if (((gameController != null) ? gameController.Player : null) == null)
		{
			return;
		}
		Camera camera = gameController.IngameState.Camera;
		base.Graphics.DrawText($"Tile Signatures: {_tileSignatures.Count} in {_tileSignatureArea} (F8 to clear)", new Vector2(100f, 110f), Color.Gold);
		foreach (TileSignature tileSignature in _tileSignatures)
		{
			Color val = (Color)(tileSignature.Tier switch
			{
				SignatureTier.Unique => Color.Gold, 
				SignatureTier.VeryRare => Color.OrangeRed, 
				SignatureTier.Rare => Color.Cyan, 
				SignatureTier.Clustered => Color.LimeGreen, 
				_ => Color.White, 
			});
			foreach (Vector2 nearPosition in tileSignature.NearPositions)
			{
				Vector2 gridPos = nearPosition + new Vector2(11.5f, 11.5f);
				Vector3 vector = Pathfinding.GridToWorld3D(gameController, gridPos);
				base.Graphics.DrawCircleInWorld(vector, 80f, val, 2f);
				Vector2 vector2 = camera.WorldToScreen(vector);
				if (vector2.X > 0f && vector2.Y > 0f)
				{
					string text = tileSignature.Key;
					int num = text.LastIndexOf('/');
					if (num >= 0 && num < text.Length - 1)
					{
						string text2 = text;
						int num2 = num + 1;
						text = text2.Substring(num2, text2.Length - num2);
					}
					string text3 = $"[{tileSignature.Tier}] {text} ({tileSignature.TotalCount})";
					base.Graphics.DrawText(text3, new Vector2(vector2.X - 60f, vector2.Y - 20f), val);
				}
			}
		}
	}
}
