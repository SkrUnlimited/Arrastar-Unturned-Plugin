using System.Collections.Generic;
using HarmonyLib;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace Arrastar
{
	internal static class DragPermissionHelper
	{
		internal static bool HasDragPermission(Player player, out UnturnedPlayer unturnedPlayer, bool notify)
		{
			unturnedPlayer = UnturnedPlayer.FromPlayer(player);
			if (unturnedPlayer == null)
			{
				return false;
			}

			if (!SurrenderDragPatch.TryGetConfiguration(out ArrastarConfiguration cfg))
			{
				return false;
			}

			if (string.IsNullOrWhiteSpace(cfg.DragPermission) || unturnedPlayer.HasPermission(cfg.DragPermission))
			{
				return true;
			}

			if (notify && !string.IsNullOrWhiteSpace(cfg.NoPermissionMessage))
			{
				UnturnedChat.Say(unturnedPlayer, cfg.NoPermissionMessage, Color.red);
			}

			return false;
		}
	}

	[HarmonyPatch(typeof(PlayerAnimator), "ReceiveGestureRequest")]
	public static class SurrenderDragPatch
	{
		internal const float VehicleTransitionGraceSeconds = 1.25f;
		internal const float VehicleEntryAllowanceSeconds = 1.25f;
		private const float BlockedExitNoticeCooldownSeconds = 1.5f;
		private const float MinimumDirectionSqrMagnitude = 0.001f;
		private const float MinimumFollowDistance = 0.6f;
		private const float MaximumFollowDistance = 3f;
		private const float MinimumFollowIntervalSeconds = 0.02f;
		private const float MaximumFollowIntervalSeconds = 0.25f;
		private const float MinimumFollowTeleportThreshold = 0.05f;
		private const float MaximumFollowTeleportThreshold = 1.25f;
		private const float MinimumDragDistance = 1f;
		private const float MaximumDragDistance = 8f;
		private const ushort MinimumDragStrength = 1;
		private const ushort MaximumDragStrength = 200;
		private const float MinimumPostReleaseEquipUseCooldownSeconds = 0f;
		private const float MaximumPostReleaseEquipUseCooldownSeconds = 3f;

		private static readonly HashSet<CSteamID> AllowedVehicleExitOnce = new HashSet<CSteamID>();
		private static readonly Dictionary<CSteamID, float> PendingReleaseByCaptor = new Dictionary<CSteamID, float>();
		private static readonly Dictionary<CSteamID, VehicleEntryAllowance> ForcedVehicleEntryByTarget = new Dictionary<CSteamID, VehicleEntryAllowance>();
		private static readonly Dictionary<CSteamID, float> ExitBlockNoticeCooldownByTarget = new Dictionary<CSteamID, float>();
		private static readonly Dictionary<CSteamID, CSteamID> DraggedTargetByCaptor = new Dictionary<CSteamID, CSteamID>();
		private static readonly Dictionary<CSteamID, CSteamID> CaptorByDraggedTarget = new Dictionary<CSteamID, CSteamID>();
		private static readonly Dictionary<CSteamID, float> NextFollowTeleportByTarget = new Dictionary<CSteamID, float>();
		private static readonly Dictionary<CSteamID, float> PostReleaseEquipUseCooldownByTarget = new Dictionary<CSteamID, float>();

		private struct VehicleEntryAllowance
		{
			public InteractableVehicle Vehicle;
			public float ExpiresAt;
		}

		[HarmonyPostfix]
		public static void ReceiveGestureRequest_Postfix(PlayerAnimator __instance, EPlayerGesture newGesture)
		{
			if (!Provider.isServer || __instance?.player == null)
			{
				return;
			}

			TryGetConfiguration(out ArrastarConfiguration cfg);
			CSteamID captorId = GetSteamId(__instance.player);

			switch (newGesture)
			{
				case EPlayerGesture.SURRENDER_START:
					if (__instance.gesture == EPlayerGesture.SURRENDER_START)
					{
						ClearPendingRelease(captorId);
						TryStartDrag(__instance.player);
					}
					break;
				case EPlayerGesture.SURRENDER_STOP:
					if (IsVehicleDragEnabled(cfg) && TryGetDraggedTargetByCaptor(__instance.player, out _))
					{
						SchedulePendingRelease(captorId, Time.realtimeSinceStartup, VehicleTransitionGraceSeconds);
						return;
					}

					if (!ShouldKeepDragLinkedInVehicle(__instance.player))
					{
						ReleaseCustomDragsByCaptor(captorId, true);
					}
					break;
			}
		}

		internal static void ResetRuntimeState()
		{
			AllowedVehicleExitOnce.Clear();
			PendingReleaseByCaptor.Clear();
			ForcedVehicleEntryByTarget.Clear();
			ExitBlockNoticeCooldownByTarget.Clear();
			DraggedTargetByCaptor.Clear();
			CaptorByDraggedTarget.Clear();
			NextFollowTeleportByTarget.Clear();
			PostReleaseEquipUseCooldownByTarget.Clear();
		}

		internal static void SanitizeConfiguration(ArrastarConfiguration cfg)
		{
			if (cfg == null)
			{
				return;
			}

			cfg.DragDistance = Mathf.Clamp(cfg.DragDistance, MinimumDragDistance, MaximumDragDistance);
			cfg.DragStrength = (ushort)Mathf.Clamp(cfg.DragStrength, MinimumDragStrength, MaximumDragStrength);
			cfg.FollowDistance = Mathf.Clamp(cfg.FollowDistance, MinimumFollowDistance, MaximumFollowDistance);
			cfg.FollowIntervalSeconds = Mathf.Clamp(cfg.FollowIntervalSeconds, MinimumFollowIntervalSeconds, MaximumFollowIntervalSeconds);
			cfg.FollowTeleportRateLimitSeconds = Mathf.Clamp(cfg.FollowTeleportRateLimitSeconds, MinimumFollowIntervalSeconds, MaximumFollowIntervalSeconds);
			cfg.FollowTeleportThreshold = Mathf.Clamp(cfg.FollowTeleportThreshold, MinimumFollowTeleportThreshold, MaximumFollowTeleportThreshold);
			cfg.PostReleaseEquipUseCooldownSeconds = Mathf.Clamp(cfg.PostReleaseEquipUseCooldownSeconds, MinimumPostReleaseEquipUseCooldownSeconds, MaximumPostReleaseEquipUseCooldownSeconds);
		}

		internal static bool TryGetConfiguration(out ArrastarConfiguration cfg)
		{
			cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
			if (cfg == null)
			{
				return false;
			}

			SanitizeConfiguration(cfg);
			return true;
		}

		internal static void LogDebug(string message)
		{
			if (!TryGetConfiguration(out ArrastarConfiguration cfg) || !cfg.EnableDebugLogging)
			{
				return;
			}

			Rocket.Core.Logging.Logger.Log($"[Arrastar][Debug] {message}");
		}

		internal static bool IsVehicleDragEnabled(ArrastarConfiguration cfg)
		{
			return cfg != null && cfg.EnableVehicleDrag;
		}

		internal static CSteamID GetSteamId(Player player)
		{
			if (player?.channel?.owner == null)
			{
				return CSteamID.Nil;
			}

			return player.channel.owner.playerID.steamID;
		}

		internal static Player FindPlayerBySteamId(CSteamID steamId)
		{
			if (steamId == CSteamID.Nil)
			{
				return null;
			}

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				if (steamPlayer?.playerID?.steamID == steamId)
				{
					return steamPlayer.player;
				}
			}

			return null;
		}

		internal static bool IsCustomDragTarget(Player target)
		{
			if (target?.animator == null)
			{
				return false;
			}

			CSteamID targetId = GetSteamId(target);
			if (targetId == CSteamID.Nil)
			{
				return false;
			}

			if (!CaptorByDraggedTarget.TryGetValue(targetId, out CSteamID linkedCaptorId) || linkedCaptorId == CSteamID.Nil)
			{
				return false;
			}

			return target.animator.captorItem == 0 && target.animator.captorID == linkedCaptorId;
		}

		internal static void ForceDequip(Player player)
		{
			if (player?.equipment == null)
			{
				return;
			}

			player.equipment.dequip();
		}

		internal static void ReleaseAllCustomDrags()
		{
			if (!Provider.isServer)
			{
				return;
			}

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				ReleaseCustomDragTarget(steamPlayer.player);
			}
		}

		internal static void ReleaseCustomDragsByCaptor(CSteamID captorId, bool notifyCaptor)
		{
			if (captorId == CSteamID.Nil)
			{
				return;
			}

			ClearPendingRelease(captorId);

			bool releasedAny = false;
			if (DraggedTargetByCaptor.TryGetValue(captorId, out CSteamID mappedTargetId))
			{
				Player mappedTarget = FindPlayerBySteamId(mappedTargetId);
				if (mappedTarget != null && IsCustomDragTarget(mappedTarget) && mappedTarget.animator.captorID == captorId)
				{
					ReleaseCustomDragTarget(mappedTarget);
					releasedAny = true;
				}
			}

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player target = steamPlayer.player;
				if (!IsCustomDragTarget(target) || target.animator.captorID != captorId)
				{
					continue;
				}

				ReleaseCustomDragTarget(target);
				releasedAny = true;
			}

			UnlinkByCaptor(captorId);

			if (!notifyCaptor || !releasedAny)
			{
				return;
			}

			UnturnedPlayer captorUnturned = UnturnedPlayer.FromCSteamID(captorId);
			TryGetConfiguration(out ArrastarConfiguration cfg);
			if (captorUnturned != null && cfg != null && !string.IsNullOrWhiteSpace(cfg.DragStoppedMessage))
			{
				UnturnedChat.Say(captorUnturned, cfg.DragStoppedMessage, Color.green);
			}
		}

		internal static bool TryGetDraggedTargetByCaptor(Player captor, out Player target)
		{
			target = FindDraggedTargetByCaptor(GetSteamId(captor));
			return target != null;
		}

		internal static int CountFreeSeats(InteractableVehicle vehicle)
		{
			Passenger[] passengers = vehicle?.passengers;
			if (passengers == null)
			{
				return 0;
			}

			int freeSeats = 0;
			for (int i = 0; i < passengers.Length; i++)
			{
				Passenger passenger = passengers[i];
				if (passenger != null && passenger.player == null)
				{
					freeSeats++;
				}
			}

			return freeSeats;
		}

		internal static void AllowVehicleExitOnce(CSteamID steamId)
		{
			if (steamId != CSteamID.Nil)
			{
				AllowedVehicleExitOnce.Add(steamId);
			}
		}

		internal static bool ConsumeVehicleExitAllowance(CSteamID steamId)
		{
			if (steamId == CSteamID.Nil)
			{
				return false;
			}

			return AllowedVehicleExitOnce.Remove(steamId);
		}

		internal static void RevokeVehicleExitAllowance(CSteamID steamId)
		{
			if (steamId != CSteamID.Nil)
			{
				AllowedVehicleExitOnce.Remove(steamId);
			}
		}

		internal static bool ShouldNotifyBlockedVehicleExit(CSteamID steamId, float now, float cooldownSeconds = BlockedExitNoticeCooldownSeconds)
		{
			if (steamId == CSteamID.Nil)
			{
				return false;
			}

			if (ExitBlockNoticeCooldownByTarget.TryGetValue(steamId, out float nextAllowedAt) && now < nextAllowedAt)
			{
				return false;
			}

			ExitBlockNoticeCooldownByTarget[steamId] = now + Mathf.Clamp(cooldownSeconds, 0.25f, 5f);
			return true;
		}

		internal static void ClearBlockedVehicleExitNotice(CSteamID steamId)
		{
			if (steamId != CSteamID.Nil)
			{
				ExitBlockNoticeCooldownByTarget.Remove(steamId);
			}
		}

		internal static bool IsPostReleaseEquipUseCooldownActive(Player player, float now)
		{
			if (player == null || !TryGetConfiguration(out ArrastarConfiguration cfg) || !cfg.EnablePostReleaseEquipUseCooldown)
			{
				return false;
			}

			if (cfg.PostReleaseEquipUseCooldownSeconds <= 0f)
			{
				return false;
			}

			CSteamID targetId = GetSteamId(player);
			if (targetId == CSteamID.Nil || !PostReleaseEquipUseCooldownByTarget.TryGetValue(targetId, out float blockUntil))
			{
				return false;
			}

			if (now < blockUntil)
			{
				return true;
			}

			PostReleaseEquipUseCooldownByTarget.Remove(targetId);
			return false;
		}

		internal static void SchedulePostReleaseEquipUseCooldown(Player player)
		{
			if (player?.life == null || player.life.isDead || !TryGetConfiguration(out ArrastarConfiguration cfg) || !cfg.EnablePostReleaseEquipUseCooldown)
			{
				return;
			}

			float cooldownSeconds = Mathf.Clamp(cfg.PostReleaseEquipUseCooldownSeconds, MinimumPostReleaseEquipUseCooldownSeconds, MaximumPostReleaseEquipUseCooldownSeconds);
			if (cooldownSeconds <= 0f)
			{
				return;
			}

			CSteamID targetId = GetSteamId(player);
			if (targetId == CSteamID.Nil)
			{
				return;
			}

			PostReleaseEquipUseCooldownByTarget[targetId] = Time.realtimeSinceStartup + cooldownSeconds;
		}

		internal static void ClearPostReleaseEquipUseCooldown(CSteamID steamId)
		{
			if (steamId != CSteamID.Nil)
			{
				PostReleaseEquipUseCooldownByTarget.Remove(steamId);
			}
		}

		private static void LinkDragPair(CSteamID captorId, CSteamID targetId)
		{
			if (captorId == CSteamID.Nil || targetId == CSteamID.Nil)
			{
				return;
			}

			if (DraggedTargetByCaptor.TryGetValue(captorId, out CSteamID previousTarget) && previousTarget != targetId)
			{
				CaptorByDraggedTarget.Remove(previousTarget);
			}

			if (CaptorByDraggedTarget.TryGetValue(targetId, out CSteamID previousCaptor) && previousCaptor != captorId)
			{
				DraggedTargetByCaptor.Remove(previousCaptor);
			}

			DraggedTargetByCaptor[captorId] = targetId;
			CaptorByDraggedTarget[targetId] = captorId;
		}

		private static void UnlinkByCaptor(CSteamID captorId)
		{
			if (captorId == CSteamID.Nil)
			{
				return;
			}

			if (DraggedTargetByCaptor.TryGetValue(captorId, out CSteamID targetId))
			{
				CaptorByDraggedTarget.Remove(targetId);
			}

			DraggedTargetByCaptor.Remove(captorId);
		}

		private static void UnlinkByTarget(CSteamID targetId)
		{
			if (targetId == CSteamID.Nil)
			{
				return;
			}

			if (CaptorByDraggedTarget.TryGetValue(targetId, out CSteamID captorId))
			{
				DraggedTargetByCaptor.Remove(captorId);
			}

			CaptorByDraggedTarget.Remove(targetId);
		}

		internal static void HandlePlayerDisconnected(CSteamID steamId)
		{
			if (steamId == CSteamID.Nil)
			{
				return;
			}

			LogDebug($"Limpando estado de desconexao para {steamId}.");
			ReleaseCustomDragsByCaptor(steamId, false);
			UnlinkByTarget(steamId);
			UnlinkByCaptor(steamId);
			RevokeVehicleExitAllowance(steamId);
			RevokeForcedVehicleEntryAllowance(steamId);
			ClearPendingRelease(steamId);
			ClearBlockedVehicleExitNotice(steamId);
			ClearPostReleaseEquipUseCooldown(steamId);
			NextFollowTeleportByTarget.Remove(steamId);
		}

		internal static void CleanupStaleRuntimeState(float now)
		{
			if (!Provider.isServer)
			{
				return;
			}

			try
			{
				List<SteamPlayer> clients;
				try
				{
					clients = Provider.clients;
				}
				catch (System.NullReferenceException)
				{
					CleanupExpiredVehicleEntryAllowances(now);
					return;
				}

				if (clients == null)
				{
					CleanupExpiredVehicleEntryAllowances(now);
					return;
				}

				HashSet<CSteamID> activePlayers = new HashSet<CSteamID>();
				for (int i = 0; i < clients.Count; i++)
				{
					SteamPlayer steamPlayer = clients[i];
					var playerId = steamPlayer?.playerID;
					if (playerId != null && playerId.steamID != CSteamID.Nil)
					{
						activePlayers.Add(playerId.steamID);
					}
				}

				List<CSteamID> stale = null;

				foreach (CSteamID steamId in AllowedVehicleExitOnce)
				{
					if (activePlayers.Contains(steamId))
					{
						continue;
					}

					if (stale == null)
					{
						stale = new List<CSteamID>();
					}

					stale.Add(steamId);
				}

				if (stale != null)
				{
					foreach (CSteamID steamId in stale)
					{
						AllowedVehicleExitOnce.Remove(steamId);
					}
				}

				stale = null;
				foreach (KeyValuePair<CSteamID, float> pair in PendingReleaseByCaptor)
				{
					if (activePlayers.Contains(pair.Key))
					{
						continue;
					}

					if (stale == null)
					{
						stale = new List<CSteamID>();
					}

					stale.Add(pair.Key);
				}

				if (stale != null)
				{
					foreach (CSteamID steamId in stale)
					{
						PendingReleaseByCaptor.Remove(steamId);
					}
				}

				stale = null;
				foreach (KeyValuePair<CSteamID, float> pair in ExitBlockNoticeCooldownByTarget)
				{
					if (activePlayers.Contains(pair.Key) && pair.Value >= now)
					{
						continue;
					}

					if (stale == null)
					{
						stale = new List<CSteamID>();
					}

					stale.Add(pair.Key);
				}

				if (stale != null)
				{
					foreach (CSteamID steamId in stale)
					{
						ExitBlockNoticeCooldownByTarget.Remove(steamId);
					}
				}

				stale = null;
				foreach (KeyValuePair<CSteamID, float> pair in NextFollowTeleportByTarget)
				{
					if (activePlayers.Contains(pair.Key) && pair.Value >= now)
					{
						continue;
					}

					if (stale == null)
					{
						stale = new List<CSteamID>();
					}

					stale.Add(pair.Key);
				}

				if (stale != null)
				{
					foreach (CSteamID steamId in stale)
					{
						NextFollowTeleportByTarget.Remove(steamId);
					}
				}

				stale = null;
				foreach (KeyValuePair<CSteamID, float> pair in PostReleaseEquipUseCooldownByTarget)
				{
					if (activePlayers.Contains(pair.Key) && pair.Value >= now)
					{
						continue;
					}

					if (stale == null)
					{
						stale = new List<CSteamID>();
					}

					stale.Add(pair.Key);
				}

				if (stale != null)
				{
					foreach (CSteamID steamId in stale)
					{
						PostReleaseEquipUseCooldownByTarget.Remove(steamId);
					}
				}

				stale = null;
				foreach (KeyValuePair<CSteamID, CSteamID> pair in DraggedTargetByCaptor)
				{
					if (activePlayers.Contains(pair.Key) && activePlayers.Contains(pair.Value))
					{
						continue;
					}

					if (stale == null)
					{
						stale = new List<CSteamID>();
					}

					stale.Add(pair.Key);
				}

				if (stale != null)
				{
					foreach (CSteamID captorId in stale)
					{
						UnlinkByCaptor(captorId);
					}
				}

				stale = null;
				foreach (KeyValuePair<CSteamID, CSteamID> pair in CaptorByDraggedTarget)
				{
					if (activePlayers.Contains(pair.Key) && activePlayers.Contains(pair.Value))
					{
						continue;
					}

					if (stale == null)
					{
						stale = new List<CSteamID>();
					}

					stale.Add(pair.Key);
				}

				if (stale != null)
				{
					foreach (CSteamID targetId in stale)
					{
						UnlinkByTarget(targetId);
					}
				}
			}
			catch (System.NullReferenceException ex)
			{
				LogDebug($"CleanupStaleRuntimeState ignorou NullReferenceException: {ex.Message}");
			}
			finally
			{
				CleanupExpiredVehicleEntryAllowances(now);
			}
		}

		private static bool CanTeleportDraggedTarget(CSteamID targetId, float now, ArrastarConfiguration cfg)
		{
			if (targetId == CSteamID.Nil)
			{
				return true;
			}

			float configuredRateLimit = cfg != null && cfg.FollowTeleportRateLimitSeconds > 0f
				? cfg.FollowTeleportRateLimitSeconds
				: cfg?.FollowIntervalSeconds ?? MinimumFollowIntervalSeconds;
			float rateLimitSeconds = Mathf.Clamp(configuredRateLimit, MinimumFollowIntervalSeconds, MaximumFollowIntervalSeconds);
			if (NextFollowTeleportByTarget.TryGetValue(targetId, out float nextAllowedTeleportAt) && now < nextAllowedTeleportAt)
			{
				return false;
			}

			NextFollowTeleportByTarget[targetId] = now + rateLimitSeconds;
			return true;
		}

		internal static void GrantForcedVehicleEntryAllowance(CSteamID targetId, InteractableVehicle vehicle, float now, float durationSeconds)
		{
			if (targetId == CSteamID.Nil || vehicle == null)
			{
				return;
			}

			ForcedVehicleEntryByTarget[targetId] = new VehicleEntryAllowance
			{
				Vehicle = vehicle,
				ExpiresAt = now + Mathf.Clamp(durationSeconds, 0.1f, 3f)
			};
		}

		internal static void RevokeForcedVehicleEntryAllowance(CSteamID targetId)
		{
			if (targetId != CSteamID.Nil)
			{
				ForcedVehicleEntryByTarget.Remove(targetId);
			}
		}

		internal static bool HasForcedVehicleEntryAllowance(CSteamID targetId, InteractableVehicle vehicle, float now)
		{
			if (targetId == CSteamID.Nil || vehicle == null)
			{
				return false;
			}

			if (!ForcedVehicleEntryByTarget.TryGetValue(targetId, out VehicleEntryAllowance allowance))
			{
				return false;
			}

			if (allowance.Vehicle == null || allowance.ExpiresAt < now)
			{
				ForcedVehicleEntryByTarget.Remove(targetId);
				return false;
			}

			return allowance.Vehicle == vehicle;
		}

		internal static void CleanupExpiredVehicleEntryAllowances(float now)
		{
			List<CSteamID> toRemove = null;
			foreach (KeyValuePair<CSteamID, VehicleEntryAllowance> pair in ForcedVehicleEntryByTarget)
			{
				if (pair.Value.Vehicle != null && pair.Value.ExpiresAt >= now)
				{
					continue;
				}

				if (toRemove == null)
				{
					toRemove = new List<CSteamID>();
				}

				toRemove.Add(pair.Key);
			}

			if (toRemove == null)
			{
				return;
			}

			foreach (CSteamID steamId in toRemove)
			{
				ForcedVehicleEntryByTarget.Remove(steamId);
			}
		}

		internal static void SchedulePendingRelease(CSteamID captorId, float now, float graceSeconds)
		{
			if (captorId == CSteamID.Nil)
			{
				return;
			}

			PendingReleaseByCaptor[captorId] = now + Mathf.Clamp(graceSeconds, 0.1f, 3f);
		}

		internal static void ClearPendingRelease(CSteamID captorId)
		{
			if (captorId != CSteamID.Nil)
			{
				PendingReleaseByCaptor.Remove(captorId);
			}
		}

		internal static bool ShouldDelayRelease(CSteamID captorId, float now)
		{
			if (captorId == CSteamID.Nil)
			{
				return false;
			}

			if (!PendingReleaseByCaptor.TryGetValue(captorId, out float deadline))
			{
				return false;
			}

			if (now <= deadline)
			{
				return true;
			}

			PendingReleaseByCaptor.Remove(captorId);
			return false;
		}

		private static bool TryFindFreeSeatInternal(InteractableVehicle vehicle, byte blockedSeat, bool isArrested, bool allowDriverSeat, out byte seatIndex)
		{
			seatIndex = byte.MaxValue;
			Passenger[] passengers = vehicle?.passengers;
			if (passengers == null)
			{
				return false;
			}

			for (byte i = 0; i < passengers.Length; i++)
			{
				Passenger passenger = passengers[i];
				if (passenger == null || passenger.player != null)
				{
					continue;
				}

				if (i == blockedSeat)
				{
					continue;
				}

				if (isArrested && !allowDriverSeat && i == 0)
				{
					continue;
				}

				if (isArrested && passenger.turret != null)
				{
					continue;
				}

				seatIndex = i;
				return true;
			}

			return false;
		}

		internal static bool TryFindFreeSeat(InteractableVehicle vehicle, Player target, byte blockedSeat, out byte seatIndex)
		{
			bool isArrested = target?.animator != null && target.animator.gesture == EPlayerGesture.ARREST_START;
			if (TryFindFreeSeatInternal(vehicle, blockedSeat, isArrested, allowDriverSeat: false, out seatIndex))
			{
				return true;
			}

			if (isArrested)
			{
				return TryFindFreeSeatInternal(vehicle, blockedSeat, isArrested, allowDriverSeat: true, out seatIndex);
			}

			return false;
		}

		internal static bool EnsureCaptorDriverSeat(InteractableVehicle vehicle, Player captor, out byte captorSeat)
		{
			captorSeat = byte.MaxValue;
			if (vehicle == null || captor == null)
			{
				return false;
			}

			if (!vehicle.findPlayerSeat(captor, out captorSeat))
			{
				return false;
			}

			if (captorSeat == 0)
			{
				return true;
			}

			Passenger[] passengers = vehicle.passengers;
			if (passengers == null || passengers.Length == 0 || passengers[0] == null || passengers[0].player != null)
			{
				return true;
			}

			if (!vehicle.trySwapPlayer(captor, 0, out _))
			{
				vehicle.swapPlayer(captorSeat, 0);
			}

			return vehicle.findPlayerSeat(captor, out captorSeat);
		}

		internal static bool TrySeatDraggedTargetInVehicle(Player captor, Player target, InteractableVehicle vehicle, bool notifyCaptorOnFailure)
		{
			if (captor == null || target == null || vehicle == null || target.movement == null)
			{
				return false;
			}

			CSteamID targetId = GetSteamId(target);
			if (targetId == CSteamID.Nil)
			{
				return false;
			}

			InteractableVehicle targetVehicle = target.movement.getVehicle();
			if (targetVehicle == vehicle)
			{
				ClearPendingRelease(GetSteamId(captor));
				return true;
			}

			if (targetVehicle != null)
			{
				AllowVehicleExitOnce(targetId);
				if (!VehicleManager.forceRemovePlayer(targetId) && !target.movement.forceRemoveFromVehicle())
				{
					RevokeVehicleExitAllowance(targetId);
				}
			}

			float now = Time.realtimeSinceStartup;
			GrantForcedVehicleEntryAllowance(targetId, vehicle, now, VehicleEntryAllowanceSeconds);

			bool entered = VehicleManager.ServerForcePassengerIntoVehicle(target, vehicle);
			if (!entered && target.equipment != null)
			{
				target.equipment.dequip();
				GrantForcedVehicleEntryAllowance(targetId, vehicle, now, VehicleEntryAllowanceSeconds);
				entered = VehicleManager.ServerForcePassengerIntoVehicle(target, vehicle);
			}

			if (entered && target.movement.getVehicle() == vehicle)
			{
				ClearPendingRelease(GetSteamId(captor));
				RevokeForcedVehicleEntryAllowance(targetId);
				return true;
			}

			// If there is no valid seat for the dragged target, notify captor once.
			byte captorSeat = byte.MaxValue;
			vehicle.findPlayerSeat(captor, out captorSeat);
			if (notifyCaptorOnFailure && !TryFindFreeSeat(vehicle, target, captorSeat, out _))
			{
				TryGetConfiguration(out ArrastarConfiguration cfg);
				UnturnedPlayer captorUnturned = UnturnedPlayer.FromPlayer(captor);
				if (cfg != null && captorUnturned != null && !string.IsNullOrWhiteSpace(cfg.VehicleNeedsTwoSeatsMessage))
				{
					UnturnedChat.Say(captorUnturned, cfg.VehicleNeedsTwoSeatsMessage, Color.yellow);
				}
			}

			// Keep trying during follow updates with official server method.
			return true;
		}

		private static Player FindDraggedTargetByCaptor(CSteamID captorId)
		{
			if (captorId == CSteamID.Nil)
			{
				return null;
			}

			if (DraggedTargetByCaptor.TryGetValue(captorId, out CSteamID mappedTargetId))
			{
				Player mappedTarget = FindPlayerBySteamId(mappedTargetId);
				if (mappedTarget != null && IsCustomDragTarget(mappedTarget) && mappedTarget.animator.captorID == captorId)
				{
					return mappedTarget;
				}

				UnlinkByCaptor(captorId);
			}

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player target = steamPlayer.player;
				if (IsCustomDragTarget(target) && target.animator.captorID == captorId)
				{
					CSteamID targetId = GetSteamId(target);
					LinkDragPair(captorId, targetId);
					return target;
				}
			}

			return null;
		}

		private static void ReleaseCustomDragTarget(Player target)
		{
			if (!IsCustomDragTarget(target))
			{
				return;
			}

			CSteamID targetId = GetSteamId(target);
			UnlinkByTarget(targetId);
			ClearBlockedVehicleExitNotice(targetId);
			RevokeForcedVehicleEntryAllowance(targetId);
			RevokeVehicleExitAllowance(targetId);
			NextFollowTeleportByTarget.Remove(targetId);
			ForceDequip(target);
			SchedulePostReleaseEquipUseCooldown(target);
			target.animator.captorID = CSteamID.Nil;
			target.animator.captorItem = 0;
			target.animator.captorStrength = 0;
			target.animator.sendGesture(EPlayerGesture.ARREST_STOP, true);
		}

		internal static void ClearCustomDragTargetState(Player target, bool sendGestureStop)
		{
			if (target?.animator == null)
			{
				return;
			}

			CSteamID targetId = GetSteamId(target);
			CSteamID captorId = target.animator.captorID;
			if (captorId != CSteamID.Nil)
			{
				ClearPendingRelease(captorId);
			}

			UnlinkByTarget(targetId);
			ClearBlockedVehicleExitNotice(targetId);
			RevokeForcedVehicleEntryAllowance(targetId);
			RevokeVehicleExitAllowance(targetId);
			NextFollowTeleportByTarget.Remove(targetId);
			ForceDequip(target);
			SchedulePostReleaseEquipUseCooldown(target);

			target.animator.captorID = CSteamID.Nil;
			target.animator.captorItem = 0;
			target.animator.captorStrength = 0;

			if (sendGestureStop && target.life != null && !target.life.isDead)
			{
				target.animator.sendGesture(EPlayerGesture.ARREST_STOP, true);
			}
		}

		private static Player FindTargetInFront(Player captor, float maxDistance)
		{
			Transform captorAim = captor?.look?.aim;
			if (captorAim == null)
			{
				return null;
			}

			Ray ray = new Ray(captorAim.position, captorAim.forward);
			RaycastInfo raycastInfo = DamageTool.raycast(ray, maxDistance, RayMasks.DAMAGE_SERVER, captor);
			if (raycastInfo.player != null && raycastInfo.player != captor)
			{
				return raycastInfo.player;
			}

			Player bestTarget = null;
			float bestDot = 0.75f;
			float bestDistance = float.MaxValue;
			Vector3 origin = captorAim.position;
			Vector3 forward = captorAim.forward.normalized;

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player candidate = steamPlayer.player;
				if (candidate == null || candidate == captor || candidate.life == null || candidate.life.isDead || candidate.look?.aim == null)
				{
					continue;
				}

				Vector3 toTarget = candidate.look.aim.position - origin;
				float distance = toTarget.magnitude;
				if (distance <= 0.01f || distance > maxDistance)
				{
					continue;
				}

				float dot = Vector3.Dot(forward, toTarget / distance);
				if (dot < bestDot)
				{
					continue;
				}

				if (dot > bestDot || distance < bestDistance)
				{
					bestDot = dot;
					bestDistance = distance;
					bestTarget = candidate;
				}
			}

			return bestTarget;
		}

		private static bool ShouldKeepDragLinkedInVehicle(Player captor)
		{
			TryGetConfiguration(out ArrastarConfiguration cfg);
			if (!IsVehicleDragEnabled(cfg) || captor?.movement == null)
			{
				return false;
			}

			InteractableVehicle captorVehicle = captor.movement.getVehicle();
			if (captorVehicle == null)
			{
				return false;
			}

			if (!TryGetDraggedTargetByCaptor(captor, out Player target) || target?.movement == null)
			{
				return false;
			}

			return target.movement.getVehicle() == captorVehicle;
		}

		private static Vector3 SnapToGround(Vector3 desiredPosition, float fallbackY)
		{
			RaycastHit groundHit;
			if (Physics.Raycast(desiredPosition + (Vector3.up * 3f), Vector3.down, out groundHit, 8f, RayMasks.BLOCK_EXIT_FIND_GROUND))
			{
				desiredPosition.y = groundHit.point.y + 0.05f;
			}
			else
			{
				desiredPosition.y = fallbackY;
			}

			return desiredPosition;
		}

		private static void TryStartDrag(Player captor)
		{
			if (!DragPermissionHelper.HasDragPermission(captor, out UnturnedPlayer captorUnturned, false))
			{
				return;
			}

			if (!TryGetConfiguration(out ArrastarConfiguration cfg))
			{
				return;
			}

			CSteamID captorId = GetSteamId(captor);
			Player existingTarget = FindDraggedTargetByCaptor(captorId);
			if (existingTarget != null)
			{
				if (HandleExistingDragOnSurrenderStart(captor, existingTarget, captorUnturned, cfg))
				{
					return;
				}
			}

			Player target = FindTargetInFront(captor, cfg.DragDistance);
			if (target == null || target == captor)
			{
				if (!string.IsNullOrWhiteSpace(cfg.TargetNotFoundMessage))
				{
					UnturnedChat.Say(captorUnturned, cfg.TargetNotFoundMessage, Color.yellow);
				}
				return;
			}

			if (target.animator == null)
			{
				return;
			}

			bool targetAlreadyArrested = target.animator.gesture == EPlayerGesture.ARREST_START;
			if (IsCustomDragTarget(target))
			{
				if (!string.IsNullOrWhiteSpace(cfg.TargetAlreadyDraggedMessage))
				{
					UnturnedChat.Say(captorUnturned, cfg.TargetAlreadyDraggedMessage, Color.yellow);
				}
				return;
			}

			if (cfg.RequireTargetSurrendered && !targetAlreadyArrested && target.animator.gesture != EPlayerGesture.SURRENDER_START)
			{
				if (!string.IsNullOrWhiteSpace(cfg.TargetNotSurrenderedMessage))
				{
					UnturnedChat.Say(captorUnturned, cfg.TargetNotSurrenderedMessage, Color.yellow);
				}
				return;
			}

			if (!targetAlreadyArrested && target.animator.gesture != EPlayerGesture.SURRENDER_START)
			{
				target.animator.sendGesture(EPlayerGesture.SURRENDER_START, true);
			}

			CSteamID targetId = GetSteamId(target);
			if (targetId == CSteamID.Nil)
			{
				return;
			}

			ForceDequip(target);
			LinkDragPair(captorId, targetId);
			target.animator.captorID = captorId;
			target.animator.captorItem = 0;
			target.animator.captorStrength = cfg.DragStrength;
			target.animator.sendGesture(EPlayerGesture.ARREST_START, true);

			// If target is in a vehicle (driver or passenger), force them out to continue drag on foot.
			if (IsVehicleDragEnabled(cfg) && captor?.movement != null && captor.movement.getVehicle() == null && target.movement != null && target.movement.getVehicle() != null)
			{
				AllowVehicleExitOnce(targetId);
				if (!VehicleManager.forceRemovePlayer(targetId) && !target.movement.forceRemoveFromVehicle())
				{
					RevokeVehicleExitAllowance(targetId);
				}
			}

			if (!string.IsNullOrWhiteSpace(cfg.DragStartedMessage))
			{
				UnturnedChat.Say(captorUnturned, cfg.DragStartedMessage, Color.green);
			}
		}

		private static bool HandleExistingDragOnSurrenderStart(Player captor, Player target, UnturnedPlayer captorUnturned, ArrastarConfiguration cfg)
		{
			CSteamID captorId = GetSteamId(captor);
			if (target == null || target.life == null || target.life.isDead || target.animator == null || target.movement == null)
			{
				ReleaseCustomDragsByCaptor(captorId, false);
				return false;
			}

			bool vehicleDragEnabled = IsVehicleDragEnabled(cfg);
			if (vehicleDragEnabled && captor?.movement != null)
			{
				InteractableVehicle captorVehicle = captor.movement.getVehicle();
				InteractableVehicle targetVehicle = target.movement.getVehicle();
				if (captorVehicle == null && targetVehicle != null && captor.transform != null && targetVehicle.transform != null)
				{
					float resumeDistance = Mathf.Max(cfg.DragDistance + 2f, 5f);
					float distance = Vector3.Distance(captor.transform.position, targetVehicle.transform.position);
					if (distance > resumeDistance)
					{
						if (captorUnturned != null && !string.IsNullOrWhiteSpace(cfg.TargetNotFoundMessage))
						{
							UnturnedChat.Say(captorUnturned, cfg.TargetNotFoundMessage, Color.yellow);
						}
						return true;
					}

					CSteamID targetId = GetSteamId(target);
					if (targetId == CSteamID.Nil)
					{
						return true;
					}

					AllowVehicleExitOnce(targetId);
					if (!VehicleManager.forceRemovePlayer(targetId) && !target.movement.forceRemoveFromVehicle())
					{
						RevokeVehicleExitAllowance(targetId);
					}

					ClearPendingRelease(captorId);
					return true;
				}
			}

			if (captorUnturned != null && !string.IsNullOrWhiteSpace(cfg.CaptorAlreadyDraggingMessage))
			{
				UnturnedChat.Say(captorUnturned, cfg.CaptorAlreadyDraggingMessage, Color.yellow);
			}
			return true;
		}

		internal static void UpdateCustomDragFollow()
		{
			if (!Provider.isServer)
			{
				return;
			}

			if (!TryGetConfiguration(out ArrastarConfiguration cfg))
			{
				return;
			}

			bool vehicleDragEnabled = IsVehicleDragEnabled(cfg);
			float followDistance = Mathf.Clamp(cfg.FollowDistance, MinimumFollowDistance, MaximumFollowDistance);
			float followThreshold = Mathf.Clamp(cfg.FollowTeleportThreshold, MinimumFollowTeleportThreshold, MaximumFollowTeleportThreshold);
			float now = Time.realtimeSinceStartup;

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player target = steamPlayer.player;
				if (!IsCustomDragTarget(target) || target.movement == null)
				{
					continue;
				}

				ForceDequip(target);

				Player captor = FindPlayerBySteamId(target.animator.captorID);
				if (captor == null || captor.life == null || captor.life.isDead || captor.animator == null || captor.movement == null)
				{
					ClearPendingRelease(target.animator.captorID);
					RevokeForcedVehicleEntryAllowance(GetSteamId(target));
					ReleaseCustomDragTarget(target);
					continue;
				}

				CSteamID captorId = GetSteamId(captor);
				InteractableVehicle captorVehicle = captor.movement.getVehicle();
				InteractableVehicle targetVehicle = target.movement.getVehicle();
				if (vehicleDragEnabled && captorVehicle != null)
				{
					SchedulePendingRelease(captorId, now, VehicleTransitionGraceSeconds);
					TrySeatDraggedTargetInVehicle(captor, target, captorVehicle, false);
					continue;
				}

				if (vehicleDragEnabled && targetVehicle != null)
				{
					// Keep dragged player seated until captor resumes surrender nearby.
					continue;
				}

				if (captor.animator.gesture != EPlayerGesture.SURRENDER_START)
				{
					if (vehicleDragEnabled && ShouldDelayRelease(captorId, now))
					{
						continue;
					}

					RevokeForcedVehicleEntryAllowance(GetSteamId(target));
					ReleaseCustomDragTarget(target);
					continue;
				}
				ClearPendingRelease(captorId);

				if (captor.transform == null || target.transform == null)
				{
					continue;
				}

				Vector3 followDirection = captor.transform.forward;
				followDirection.y = 0f;
				if (followDirection.sqrMagnitude < MinimumDirectionSqrMagnitude && captor.look?.aim != null)
				{
					followDirection = captor.look.aim.forward;
					followDirection.y = 0f;
				}
				if (followDirection.sqrMagnitude < MinimumDirectionSqrMagnitude)
				{
					followDirection = Vector3.forward;
				}
				followDirection.Normalize();

				Vector3 desiredPosition = captor.transform.position - (followDirection * followDistance);
				desiredPosition = SnapToGround(desiredPosition, captor.transform.position.y);

				if (Vector3.Distance(target.transform.position, desiredPosition) < followThreshold)
				{
					continue;
				}

				CSteamID targetId = GetSteamId(target);
				if (!CanTeleportDraggedTarget(targetId, now, cfg))
				{
					continue;
				}

				if (!target.teleportToLocation(desiredPosition, captor.look.yaw))
				{
					LogDebug($"Fallback para teleportToLocationUnsafe em {targetId}.");
					target.teleportToLocationUnsafe(desiredPosition, captor.look.yaw);
				}
			}
		}
	}

	[HarmonyPatch(typeof(PlayerLife), "ReceiveDead")]
	public static class DragReleaseOnDeathPatch
	{
		[HarmonyPostfix]
		public static void ReceiveDead_Postfix(PlayerLife __instance)
		{
			if (!Provider.isServer || __instance?.player == null)
			{
				return;
			}

			Player deadPlayer = __instance.player;
			CSteamID deadPlayerId = SurrenderDragPatch.GetSteamId(deadPlayer);

			// If the dead player was dragging someone, release their dragged target(s).
			SurrenderDragPatch.ReleaseCustomDragsByCaptor(deadPlayerId, false);

			// If the dead player was being dragged, clear custom drag state immediately.
			if (SurrenderDragPatch.IsCustomDragTarget(deadPlayer))
			{
				SurrenderDragPatch.ClearCustomDragTargetState(deadPlayer, sendGestureStop: false);
			}
		}
	}

	[HarmonyPatch(typeof(PlayerLife), "ReceiveRevive")]
	public static class DragResetOnRevivePatch
	{
		[HarmonyPostfix]
		public static void ReceiveRevive_Postfix(PlayerLife __instance, Vector3 position, byte angle)
		{
			if (!Provider.isServer || __instance?.player == null)
			{
				return;
			}

			Player revivedPlayer = __instance.player;
			if (revivedPlayer.movement != null && revivedPlayer.movement.getVehicle() != null)
			{
				CSteamID revivedId = SurrenderDragPatch.GetSteamId(revivedPlayer);
				if (revivedId != CSteamID.Nil)
				{
					SurrenderDragPatch.AllowVehicleExitOnce(revivedId);
					if (!VehicleManager.forceRemovePlayer(revivedId) && !revivedPlayer.movement.forceRemoveFromVehicle())
					{
						SurrenderDragPatch.RevokeVehicleExitAllowance(revivedId);
					}
				}
			}

			if (revivedPlayer.animator == null)
			{
				return;
			}

			bool hasResidualCustomDrag = revivedPlayer.animator.captorID != CSteamID.Nil
				|| (revivedPlayer.animator.gesture == EPlayerGesture.ARREST_START && revivedPlayer.animator.captorItem == 0);
			if (hasResidualCustomDrag)
			{
				SurrenderDragPatch.ClearCustomDragTargetState(revivedPlayer, sendGestureStop: true);
			}
		}
	}

	[HarmonyPatch(typeof(Provider), "Update")]
	public static class CustomDragFollowPatch
	{
		private const float RuntimeStateCleanupIntervalSeconds = 2f;
		private static float _nextUpdateTime;
		private static float _nextStateCleanupTime;

		[HarmonyPostfix]
		public static void Update_Postfix()
		{
			if (!Provider.isServer)
			{
				return;
			}

			if (!SurrenderDragPatch.TryGetConfiguration(out ArrastarConfiguration cfg))
			{
				return;
			}

			float now = Time.realtimeSinceStartup;
			if (now >= _nextStateCleanupTime)
			{
				_nextStateCleanupTime = now + RuntimeStateCleanupIntervalSeconds;
				SurrenderDragPatch.CleanupStaleRuntimeState(now);
			}

			float interval = cfg.FollowIntervalSeconds;
			if (now < _nextUpdateTime)
			{
				return;
			}

			_nextUpdateTime = now + interval;
			SurrenderDragPatch.UpdateCustomDragFollow();
		}
	}

	[HarmonyPatch(typeof(InteractableVehicle), "tryAddPlayer")]
	public static class DragVehicleCapacityPatch
	{
		[HarmonyPrefix]
		public static bool TryAddPlayer_Prefix(InteractableVehicle __instance, ref byte seat, Player player, ref bool __result)
		{
			if (!Provider.isServer || __instance == null || player == null)
			{
				return true;
			}

			SurrenderDragPatch.TryGetConfiguration(out ArrastarConfiguration cfg);
			if (!SurrenderDragPatch.IsVehicleDragEnabled(cfg))
			{
				return true;
			}

			if (!SurrenderDragPatch.TryGetDraggedTargetByCaptor(player, out Player target) || target?.movement == null)
			{
				return true;
			}

			if (target.movement.getVehicle() == __instance)
			{
				return true;
			}

			if (SurrenderDragPatch.CountFreeSeats(__instance) >= 2)
			{
				return true;
			}

			UnturnedPlayer captorUnturned = UnturnedPlayer.FromPlayer(player);
			if (captorUnturned != null && !string.IsNullOrWhiteSpace(cfg.VehicleNeedsTwoSeatsMessage))
			{
				UnturnedChat.Say(captorUnturned, cfg.VehicleNeedsTwoSeatsMessage, Color.yellow);
			}

			__result = false;
			return false;
		}
	}

	[HarmonyPatch(typeof(InteractableVehicle), "addPlayer")]
	public static class DragVehicleSyncOnEnterPatch
	{
		[HarmonyPostfix]
		public static void AddPlayer_Postfix(InteractableVehicle __instance, byte seatIndex, CSteamID steamID)
		{
			if (!Provider.isServer || __instance == null || steamID == CSteamID.Nil)
			{
				return;
			}

			SurrenderDragPatch.TryGetConfiguration(out ArrastarConfiguration cfg);
			if (!SurrenderDragPatch.IsVehicleDragEnabled(cfg))
			{
				return;
			}

			Player captor = SurrenderDragPatch.FindPlayerBySteamId(steamID);
			if (captor == null || captor.movement == null)
			{
				return;
			}

			if (!SurrenderDragPatch.TryGetDraggedTargetByCaptor(captor, out Player target) || target == null)
			{
				return;
			}

			SurrenderDragPatch.TrySeatDraggedTargetInVehicle(captor, target, __instance, true);
		}
	}

	[HarmonyPatch(typeof(InteractableVehicle), "checkEnter", new[] { typeof(Player) })]
	public static class DragVehicleCheckEnterPatch
	{
		[HarmonyPrefix]
		public static bool CheckEnter_Prefix(InteractableVehicle __instance, Player player, ref bool __result)
		{
			if (!Provider.isServer || __instance == null || player == null)
			{
				return true;
			}

			CSteamID targetId = SurrenderDragPatch.GetSteamId(player);
			if (!SurrenderDragPatch.HasForcedVehicleEntryAllowance(targetId, __instance, Time.realtimeSinceStartup))
			{
				return true;
			}

			__result = true;
			return false;
		}
	}

	[HarmonyPatch(typeof(InteractableVehicle), "checkEnter", new[] { typeof(CSteamID), typeof(CSteamID) })]
	public static class DragVehicleCheckEnterByIdPatch
	{
		[HarmonyPrefix]
		public static bool CheckEnterById_Prefix(InteractableVehicle __instance, CSteamID enemyPlayer, CSteamID enemyGroup, ref bool __result)
		{
			if (!Provider.isServer || __instance == null || enemyPlayer == CSteamID.Nil)
			{
				return true;
			}

			if (!SurrenderDragPatch.HasForcedVehicleEntryAllowance(enemyPlayer, __instance, Time.realtimeSinceStartup))
			{
				return true;
			}

			__result = true;
			return false;
		}
	}

	[HarmonyPatch(typeof(VehicleManager), "askExitVehicle", new[] { typeof(CSteamID), typeof(Vector3) })]
	public static class DragVehicleExitRequestPatch
	{
		[HarmonyPrefix]
		private static bool AskExitVehicle_Prefix(CSteamID steamID, Vector3 velocity)
		{
			if (!Provider.isServer || steamID == CSteamID.Nil)
			{
				return true;
			}

			Player dragged = SurrenderDragPatch.FindPlayerBySteamId(steamID);
			if (dragged?.movement == null || dragged.movement.getVehicle() == null || !SurrenderDragPatch.IsCustomDragTarget(dragged))
			{
				return true;
			}

			SurrenderDragPatch.TryGetConfiguration(out ArrastarConfiguration cfg);
			UnturnedPlayer draggedUnturned = UnturnedPlayer.FromPlayer(dragged);
			if (cfg != null
				&& draggedUnturned != null
				&& !string.IsNullOrWhiteSpace(cfg.DraggedPlayerCannotExitVehicleMessage)
				&& SurrenderDragPatch.ShouldNotifyBlockedVehicleExit(steamID, Time.realtimeSinceStartup))
			{
				UnturnedChat.Say(draggedUnturned, cfg.DraggedPlayerCannotExitVehicleMessage, Color.yellow);
			}

			return false;
		}
	}

	[HarmonyPatch(typeof(VehicleManager), "ReceiveExitVehicleRequest")]
	public static class DragVehicleReceiveExitRequestPatch
	{
		[HarmonyPrefix]
		private static bool ReceiveExitVehicleRequest_Prefix(ref ServerInvocationContext context, Vector3 velocity)
		{
			if (!Provider.isServer)
			{
				return true;
			}

			Player dragged = context.GetPlayer();
			if (dragged?.movement == null || dragged.movement.getVehicle() == null || !SurrenderDragPatch.IsCustomDragTarget(dragged))
			{
				return true;
			}

			CSteamID draggedId = SurrenderDragPatch.GetSteamId(dragged);
			SurrenderDragPatch.TryGetConfiguration(out ArrastarConfiguration cfg);
			UnturnedPlayer draggedUnturned = UnturnedPlayer.FromPlayer(dragged);
			if (cfg != null
				&& draggedUnturned != null
				&& !string.IsNullOrWhiteSpace(cfg.DraggedPlayerCannotExitVehicleMessage)
				&& SurrenderDragPatch.ShouldNotifyBlockedVehicleExit(draggedId, Time.realtimeSinceStartup))
			{
				UnturnedChat.Say(draggedUnturned, cfg.DraggedPlayerCannotExitVehicleMessage, Color.yellow);
			}

			return false;
		}
	}

	[HarmonyPatch(typeof(InteractableVehicle), "removePlayer")]
	public static class DragVehicleExitPatch
	{
		private struct ExitInterceptState
		{
			public Player LeavingPlayer;
			public InteractableVehicle Vehicle;
			public bool KeepDraggedInside;
		}

		[HarmonyPrefix]
		private static bool RemovePlayer_Prefix(InteractableVehicle __instance, byte seatIndex, Vector3 point, byte angle, bool forceUpdate, ref ExitInterceptState __state)
		{
			__state = default;
			if (!Provider.isServer || __instance == null)
			{
				return true;
			}

			Passenger seat = __instance.GetSeatByIndex(seatIndex);
			Player leavingPlayer = seat?.player?.player;
			if (leavingPlayer == null)
			{
				return true;
			}

			__state.LeavingPlayer = leavingPlayer;
			__state.Vehicle = __instance;

			CSteamID leavingPlayerId = SurrenderDragPatch.GetSteamId(leavingPlayer);
			if (SurrenderDragPatch.ConsumeVehicleExitAllowance(leavingPlayerId))
			{
				return true;
			}

			SurrenderDragPatch.TryGetConfiguration(out ArrastarConfiguration cfg);
			if (!SurrenderDragPatch.IsVehicleDragEnabled(cfg))
			{
				return true;
			}

			if (SurrenderDragPatch.IsCustomDragTarget(leavingPlayer))
			{
				if (leavingPlayer.life != null && leavingPlayer.life.isDead)
				{
					SurrenderDragPatch.ClearCustomDragTargetState(leavingPlayer, sendGestureStop: false);
					return true;
				}

				__state.KeepDraggedInside = true;
				UnturnedPlayer draggedUnturned = UnturnedPlayer.FromPlayer(leavingPlayer);
				if (draggedUnturned != null
					&& !string.IsNullOrWhiteSpace(cfg.DraggedPlayerCannotExitVehicleMessage)
					&& SurrenderDragPatch.ShouldNotifyBlockedVehicleExit(leavingPlayerId, Time.realtimeSinceStartup))
				{
					UnturnedChat.Say(draggedUnturned, cfg.DraggedPlayerCannotExitVehicleMessage, Color.yellow);
				}
			}

			return true;
		}

		[HarmonyPostfix]
		private static void RemovePlayer_Postfix(ExitInterceptState __state)
		{
			if (!Provider.isServer || !__state.KeepDraggedInside)
			{
				return;
			}

			Player dragged = __state.LeavingPlayer;
			InteractableVehicle vehicle = __state.Vehicle;
			if (dragged == null || dragged.movement == null || vehicle == null)
			{
				return;
			}

			if (dragged.movement.getVehicle() == vehicle)
			{
				return;
			}

			CSteamID draggedId = SurrenderDragPatch.GetSteamId(dragged);
			if (draggedId == CSteamID.Nil)
			{
				return;
			}

			float now = Time.realtimeSinceStartup;
			SurrenderDragPatch.GrantForcedVehicleEntryAllowance(draggedId, vehicle, now, SurrenderDragPatch.VehicleEntryAllowanceSeconds);

			bool reEntered = VehicleManager.ServerForcePassengerIntoVehicle(dragged, vehicle);
			if (!reEntered && dragged.equipment != null)
			{
				dragged.equipment.dequip();
				SurrenderDragPatch.GrantForcedVehicleEntryAllowance(draggedId, vehicle, now, SurrenderDragPatch.VehicleEntryAllowanceSeconds);
				reEntered = VehicleManager.ServerForcePassengerIntoVehicle(dragged, vehicle);
			}

			if (reEntered && dragged.movement.getVehicle() == vehicle)
			{
				SurrenderDragPatch.RevokeForcedVehicleEntryAllowance(draggedId);
			}
		}
	}

	[HarmonyPatch]
	public static class DraggedPlayerEquipmentGuardPatches
	{
		private static bool ShouldBlock(PlayerEquipment equipment)
		{
			if (!Provider.isServer)
			{
				return false;
			}

			Player player = equipment?.player;
			if (!SurrenderDragPatch.IsCustomDragTarget(player)
				&& !SurrenderDragPatch.IsPostReleaseEquipUseCooldownActive(player, Time.realtimeSinceStartup))
			{
				return false;
			}

			SurrenderDragPatch.ForceDequip(player);
			return true;
		}

		[HarmonyPatch(typeof(PlayerEquipment), "ReceiveEquipRequest")]
		[HarmonyPrefix]
		private static bool ReceiveEquipRequest_Prefix(PlayerEquipment __instance, byte page, byte x, byte y)
		{
			return !ShouldBlock(__instance);
		}

		[HarmonyPatch(typeof(PlayerEquipment), "ServerEquip")]
		[HarmonyPrefix]
		private static bool ServerEquip_Prefix(PlayerEquipment __instance, byte page, byte x, byte y)
		{
			return !ShouldBlock(__instance);
		}

		[HarmonyPatch(typeof(PlayerEquipment), "use")]
		[HarmonyPrefix]
		private static bool Use_Prefix(PlayerEquipment __instance)
		{
			return !ShouldBlock(__instance);
		}

		[HarmonyPatch(typeof(PlayerEquipment), "simulate_UseableInput")]
		[HarmonyPrefix]
		private static bool SimulateUseableInput_Prefix(PlayerEquipment __instance, uint simulation, EAttackInputFlags inputPrimary, EAttackInputFlags inputSecondary, bool inputSteady)
		{
			return !ShouldBlock(__instance);
		}
	}
}
