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

		[HarmonyPostfix]
		public static void ReceiveGestureRequest_Postfix(PlayerAnimator __instance, EPlayerGesture newGesture)
		{
			if (!Provider.isServer || __instance?.player == null)
			{
				return;
			}

			switch (newGesture)
			{
				case EPlayerGesture.SURRENDER_START:
					if (__instance.gesture == EPlayerGesture.SURRENDER_START)
					{
						TryStartDrag(__instance.player);
					}
					break;
				case EPlayerGesture.SURRENDER_STOP:
					if (!ShouldKeepDragLinkedInVehicle(__instance.player))
					{
						ReleaseCustomDragsByCaptor(GetSteamId(__instance.player), true);
					}
					break;
			}
		}

		internal static void ResetRuntimeState()
		{
			AllowedVehicleExitOnce.Clear();
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
			return target?.animator != null
				&& target.animator.gesture == EPlayerGesture.ARREST_START
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

			bool releasedAny = false;

			foreach (SteamPlayer steamPlayer in Provider.clients)
			{
				Player target = steamPlayer.player;
				if (!IsCustomDragTarget(target))
				{
					continue;
				}

				if (target.animator.captorID != captorId)
				{
					continue;
				}

				ReleaseCustomDragTarget(target);
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
				if (passenger == null || passenger.player == null)
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

		internal static bool TrySeatDraggedTargetInVehicle(Player captor, Player target, InteractableVehicle vehicle, bool notifyCaptorOnFailure)
		{
			if (captor == null || target == null || vehicle == null || target.movement == null)
			{
				return false;
			}

			InteractableVehicle targetVehicle = target.movement.getVehicle();
			if (targetVehicle == vehicle)
			{
				return true;
			}

			CSteamID targetId = GetSteamId(target);
			if (targetVehicle != null)
			{
				AllowVehicleExitOnce(targetId);
				if (!target.movement.forceRemoveFromVehicle())
				{
					RevokeVehicleExitAllowance(targetId);
				}
			}

			byte seat;
			bool added = vehicle.tryAddPlayer(out seat, target);
			if (added)
			{
				return true;
			}

			if (notifyCaptorOnFailure)
			{
				ArrastarConfiguration cfg = ArrastarPlugin.Instance?.Configuration?.Instance;
				UnturnedPlayer captorUnturned = UnturnedPlayer.FromPlayer(captor);
				if (cfg != null && captorUnturned != null && !string.IsNullOrWhiteSpace(cfg.VehicleNeedsTwoSeatsMessage))
				{
					UnturnedChat.Say(captorUnturned, cfg.VehicleNeedsTwoSeatsMessage, Color.yellow);
				}
			}

			return false;
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

			target.animator.captorID = CSteamID.Nil;
			target.animator.captorItem = 0;
			target.animator.captorStrength = 0;
			target.animator.sendGesture(EPlayerGesture.ARREST_STOP, true);
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
			if (FindDraggedTargetByCaptor(captorId) != null)
			{
				ReleaseCustomDragsByCaptor(captorId, false);
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

			if (!string.IsNullOrWhiteSpace(cfg.DragStartedMessage))
			{
				UnturnedChat.Say(captorUnturned, cfg.DragStartedMessage, Color.green);
			}
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
					ReleaseCustomDragTarget(target);
					continue;
				}

				InteractableVehicle captorVehicle = captor.movement.getVehicle();
				InteractableVehicle targetVehicle = target.movement.getVehicle();
				if (vehicleDragEnabled && captorVehicle != null)
				{
					if (targetVehicle != captorVehicle)
					{
						TrySeatDraggedTargetInVehicle(captor, target, captorVehicle, false);
					}
					continue;
				}

				if (vehicleDragEnabled && targetVehicle != null)
				{
					CSteamID targetId = GetSteamId(target);
					AllowVehicleExitOnce(targetId);
					if (!target.movement.forceRemoveFromVehicle())
					{
						RevokeVehicleExitAllowance(targetId);
					}
				}

				if (captor.animator.gesture != EPlayerGesture.SURRENDER_START)
				{
					ReleaseCustomDragTarget(target);
					continue;
				}

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

			SurrenderDragPatch.ReleaseCustomDragsByCaptor(SurrenderDragPatch.GetSteamId(__instance.player), false);
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

			if (SurrenderDragPatch.TrySeatDraggedTargetInVehicle(captor, target, __instance, true))
			{
				return;
			}

			captor.movement.forceRemoveFromVehicle();
		}
	}

	[HarmonyPatch(typeof(InteractableVehicle), "removePlayer")]
	public static class DragVehicleExitPatch
	{
		[HarmonyPrefix]
		public static bool RemovePlayer_Prefix(InteractableVehicle __instance, byte seatIndex, Vector3 point, byte angle, bool forceUpdate)
		{
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
				Player captor = SurrenderDragPatch.FindPlayerBySteamId(leavingPlayer.animator.captorID);
				if (captor?.movement != null && captor.movement.getVehicle() == __instance)
				{
					UnturnedPlayer draggedUnturned = UnturnedPlayer.FromPlayer(leavingPlayer);
					if (draggedUnturned != null && !string.IsNullOrWhiteSpace(cfg.DraggedPlayerCannotExitVehicleMessage))
					{
						UnturnedChat.Say(draggedUnturned, cfg.DraggedPlayerCannotExitVehicleMessage, Color.yellow);
					}
					return false;
				}
			}

			if (SurrenderDragPatch.TryGetDraggedTargetByCaptor(leavingPlayer, out Player draggedTarget) && draggedTarget?.movement != null)
			{
				if (draggedTarget.movement.getVehicle() == __instance)
				{
					CSteamID draggedTargetId = SurrenderDragPatch.GetSteamId(draggedTarget);
					SurrenderDragPatch.AllowVehicleExitOnce(draggedTargetId);
					if (!draggedTarget.movement.forceRemoveFromVehicle())
					{
						SurrenderDragPatch.RevokeVehicleExitAllowance(draggedTargetId);
					}
				}
			}

			return true;
		}
	}
}
