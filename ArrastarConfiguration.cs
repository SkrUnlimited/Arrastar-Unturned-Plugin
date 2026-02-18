using Rocket.API;

namespace Arrastar
{
	public class ArrastarConfiguration : IRocketPluginConfiguration
	{
		public string DragPermission;
		public float DragDistance;
		public ushort DragStrength;
		public float FollowDistance;
		public float FollowIntervalSeconds;
		public float FollowTeleportRateLimitSeconds;
		public float FollowTeleportThreshold;
		public bool RequireTargetSurrendered;
		public bool EnableVehicleDrag;
		public bool EnablePostReleaseEquipUseCooldown;
		public float PostReleaseEquipUseCooldownSeconds;
		public bool EnableDebugLogging;
		public string NoPermissionMessage;
		public string TargetNotFoundMessage;
		public string TargetNotSurrenderedMessage;
		public string TargetAlreadyDraggedMessage;
		public string CaptorAlreadyDraggingMessage;
		public string DragStartedMessage;
		public string DragStoppedMessage;
		public string VehicleNeedsTwoSeatsMessage;
		public string DraggedPlayerCannotExitVehicleMessage;

		public void LoadDefaults()
		{
			DragPermission = "arrastar.drag";
			DragDistance = 3f;
			DragStrength = 75;
			FollowDistance = 1.3f;
			FollowIntervalSeconds = 0.075f;
			FollowTeleportRateLimitSeconds = 0.075f;
			FollowTeleportThreshold = 0.35f;
			RequireTargetSurrendered = false;
			EnableVehicleDrag = true;
			EnablePostReleaseEquipUseCooldown = true;
			PostReleaseEquipUseCooldownSeconds = 0.3f;
			EnableDebugLogging = false;
			NoPermissionMessage = "Voce nao tem permissao para arrastar jogadores.";
			TargetNotFoundMessage = "Olhe diretamente para um jogador para arrastar.";
			TargetNotSurrenderedMessage = "Olhe para um jogador rendido para arrastar.";
			TargetAlreadyDraggedMessage = "Esse jogador ja esta sendo arrastado.";
			CaptorAlreadyDraggingMessage = "Voce ja esta arrastando um jogador.";
			DragStartedMessage = "Voce comecou a arrastar o jogador.";
			DragStoppedMessage = "Voce parou de arrastar o jogador.";
			VehicleNeedsTwoSeatsMessage = "Esse veiculo nao possui as duas vagas para arrastar um jogador.";
			DraggedPlayerCannotExitVehicleMessage = "Voce nao pode sair do veiculo enquanto estiver sendo arrastado.";
		}
	}
}

