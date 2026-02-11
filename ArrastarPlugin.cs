using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using HarmonyLib;

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

			_harmony = new Harmony(HarmonyId);
			_harmony.PatchAll();

			Logger.Log("[Arrastar] Patches aplicados");
		}

		protected override void Unload()
		{
			SurrenderDragPatch.ReleaseAllCustomDrags();
			SurrenderDragPatch.ResetRuntimeState();

			_harmony?.UnpatchAll(HarmonyId);
			_harmony = null;

			Instance = null;
			Logger.Log("[Arrastar] Plugin descarregado");
		}
	}
}
