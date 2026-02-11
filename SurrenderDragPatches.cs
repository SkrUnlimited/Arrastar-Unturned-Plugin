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

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
			if (cfg == null)
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
		private static readonly HashSet<CSteamID> AllowedVehicleExitOnce = new HashSet<CSteamID>();
		private static readonly Dictionary<CSteamID, float> PendingReleaseByCaptor = new Dictionary<CSteamID, float>();
		private static readonly Dictionary<CSteamID, VehicleEntryAllowance> ForcedVehicleEntryByTarget = new Dictionary<CSteamID, VehicleEntryAllowance>();
		private static readonly Dictionary<CSteamID, float> ExitBlockNoticeCooldownByTarget = new Dictionary<CSteamID, float>();

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

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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
						SchedulePendingRelease(captorId, Time.realtimeSinceStartup, 1.25f);
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
			// Gesture can change while seated in vehicles, so custom drag state is tracked by captor fields.
			return target?.animator != null
				&& target.animator.captorItem == 0
				&& target.animator.captorID != CSteamID.Nil;
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
			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player target = steamPlayer.player;
				if (!IsCustomDragTarget(target) || target.animator.captorID != captorId)
				{
					continue;
				}

				ReleaseCustomDragTarget(target);
				CSteamID targetId = GetSteamId(target);
				RevokeForcedVehicleEntryAllowance(targetId);
				ClearBlockedVehicleExitNotice(targetId);
				releasedAny = true;
			}

			if (!notifyCaptor || !releasedAny)
			{
				return;
			}

			UnturnedPlayer captorUnturned = UnturnedPlayer.FromCSteamID(captorId);
			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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

		internal static bool ShouldNotifyBlockedVehicleExit(CSteamID steamId, float now, float cooldownSeconds = 1.5f)
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
			GrantForcedVehicleEntryAllowance(targetId, vehicle, now, 1.25f);

			bool entered = VehicleManager.ServerForcePassengerIntoVehicle(target, vehicle);
			if (!entered && target.equipment != null)
			{
				target.equipment.dequip();
				GrantForcedVehicleEntryAllowance(targetId, vehicle, now, 1.25f);
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
				ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player target = steamPlayer.player;
				if (IsCustomDragTarget(target) && target.animator.captorID == captorId)
				{
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

			ClearBlockedVehicleExitNotice(GetSteamId(target));
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

			ClearBlockedVehicleExitNotice(targetId);
			RevokeForcedVehicleEntryAllowance(targetId);
			RevokeVehicleExitAllowance(targetId);

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
			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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
			if (!DragPermissionHelper.HasDragPermission(captor, out UnturnedPlayer captorUnturned, true))
			{
				return;
			}

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
			if (cfg == null)
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

			if (target.animator.gesture == EPlayerGesture.ARREST_START || target.animator.captorID != CSteamID.Nil)
			{
				if (!string.IsNullOrWhiteSpace(cfg.TargetAlreadyDraggedMessage))
				{
					UnturnedChat.Say(captorUnturned, cfg.TargetAlreadyDraggedMessage, Color.yellow);
				}
				return;
			}

			if (cfg.RequireTargetSurrendered && target.animator.gesture != EPlayerGesture.SURRENDER_START)
			{
				if (!string.IsNullOrWhiteSpace(cfg.TargetNotSurrenderedMessage))
				{
					UnturnedChat.Say(captorUnturned, cfg.TargetNotSurrenderedMessage, Color.yellow);
				}
				return;
			}

			if (target.animator.gesture != EPlayerGesture.SURRENDER_START)
			{
				target.animator.sendGesture(EPlayerGesture.SURRENDER_START, true);
			}

			target.animator.captorID = captorId;
			target.animator.captorItem = 0;
			target.animator.captorStrength = cfg.DragStrength;
			target.animator.sendGesture(EPlayerGesture.ARREST_START, true);

			// If target is in a vehicle (driver or passenger), force them out to continue drag on foot.
			if (IsVehicleDragEnabled(cfg) && captor?.movement != null && captor.movement.getVehicle() == null && target.movement != null && target.movement.getVehicle() != null)
			{
				CSteamID targetId = GetSteamId(target);
				if (targetId != CSteamID.Nil)
				{
					AllowVehicleExitOnce(targetId);
					if (!VehicleManager.forceRemovePlayer(targetId) && !target.movement.forceRemoveFromVehicle())
					{
						RevokeVehicleExitAllowance(targetId);
					}
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

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
			if (cfg == null)
			{
				return;
			}

			bool vehicleDragEnabled = IsVehicleDragEnabled(cfg);
			float followDistance = Mathf.Clamp(cfg.FollowDistance, 0.6f, 3f);
			float followThreshold = Mathf.Clamp(cfg.FollowTeleportThreshold, 0.05f, 1.25f);
			float now = Time.realtimeSinceStartup;

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player target = steamPlayer.player;
				if (!IsCustomDragTarget(target) || target.movement == null)
				{
					continue;
				}

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
					SchedulePendingRelease(captorId, now, 1.25f);
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
				if (followDirection.sqrMagnitude < 0.001f && captor.look?.aim != null)
				{
					followDirection = captor.look.aim.forward;
					followDirection.y = 0f;
				}
				if (followDirection.sqrMagnitude < 0.001f)
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

				if (!target.teleportToLocation(desiredPosition, captor.look.yaw))
				{
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
		private static float _nextUpdateTime;

		[HarmonyPostfix]
		public static void Update_Postfix()
		{
			if (!Provider.isServer)
			{
				return;
			}

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
			if (cfg == null)
			{
				return;
			}

			float now = Time.realtimeSinceStartup;
			float interval = Mathf.Clamp(cfg.FollowIntervalSeconds, 0.02f, 0.25f);
			if (now < _nextUpdateTime)
			{
				return;
			}

			_nextUpdateTime = now + interval;
			SurrenderDragPatch.CleanupExpiredVehicleEntryAllowances(now);
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

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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
			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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

			ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
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
			SurrenderDragPatch.GrantForcedVehicleEntryAllowance(draggedId, vehicle, now, 1.25f);

			bool reEntered = VehicleManager.ServerForcePassengerIntoVehicle(dragged, vehicle);
			if (!reEntered && dragged.equipment != null)
			{
				dragged.equipment.dequip();
				SurrenderDragPatch.GrantForcedVehicleEntryAllowance(draggedId, vehicle, now, 1.25f);
				reEntered = VehicleManager.ServerForcePassengerIntoVehicle(dragged, vehicle);
			}

			if (reEntered && dragged.movement.getVehicle() == vehicle)
			{
				SurrenderDragPatch.RevokeForcedVehicleEntryAllowance(draggedId);
			}
		}
	}
}
