using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using HarmonyLib;
using SDG.Unturned;
using Steamworks;

namespace Arrastar
{
	public class ArrastarPlugin : RocketPlugin<ArrastarConfiguration>
	{
		internal const string HarmonyId = "arrastar.surrenderdrag";

		public static ArrastarPlugin Instance { get; private set; }

		private Harmony _harmony;

		protected override void Load()
		{
			Instance = this;
			Logger.Log("[Arrastar] Iniciando plugin");
			SurrenderDragPatch.SanitizeConfiguration(Configuration?.Instance);

			_harmony = new Harmony(HarmonyId);
			_harmony.PatchAll();
			Provider.onEnemyDisconnected += OnEnemyDisconnected;

			Logger.Log("[Arrastar] Patches aplicados");
		}

		protected override void Unload()
		{
			Provider.onEnemyDisconnected -= OnEnemyDisconnected;
			SurrenderDragPatch.ReleaseAllCustomDrags();
			SurrenderDragPatch.ResetRuntimeState();

			_harmony?.UnpatchAll(HarmonyId);
			_harmony = null;

			Instance = null;
			Logger.Log("[Arrastar] Plugin descarregado");
		}

		private static void OnEnemyDisconnected(SteamPlayer steamPlayer)
		{
			CSteamID steamId = steamPlayer?.playerID?.steamID ?? CSteamID.Nil;
			if (steamId != CSteamID.Nil)
			{
				SurrenderDragPatch.HandlePlayerDisconnected(steamId);
			}
		}
	}
}
