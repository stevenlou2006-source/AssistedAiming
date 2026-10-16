using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Cnxc.AssistedAim
{
	[BepInPlugin("cnxc.assistedaim", "Assisted Aim", "0.1.0")]
	public class AssistedAimPlugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;
		internal static ConfigEntry<bool> EnableAim;
		internal static ConfigEntry<KeyCode> ToggleKey;
		internal static ConfigEntry<int> AimCone;          // 仅保留角度配置
		internal static ConfigEntry<bool> DebugLog;

		private Harmony _harmony;

		private void Awake()
		{
			Log = Logger;
			EnableAim = Config.Bind("General", "EnableAim", false, "启用自瞄（默认关闭）");
			ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.P, "开关按键");
			AimCone = Config.Bind("General", "AimCone", 180,
				new ConfigDescription("锁定角度（度，整数）", new AcceptableValueRange<int>(1, 360)));
			DebugLog = Config.Bind("General", "DebugLog", false, "启用调试日志（每5秒输出状态）");

			_harmony = new Harmony("cnxc.assistedaim");
			_harmony.PatchAll();
			Log.LogInfo("Assisted Aim 已加载（固定锁定距离 ≤ 10m）");
		}

		private void OnDestroy()
		{
			_harmony?.UnpatchAll("cnxc.assistedaim");
		}
	}

	// ---- InputBlocker：仅在真正锁定时才劫持鼠标 ----
	[HarmonyPatch(typeof(InputManager))]
	public static class InputBlocker
	{
		private static bool _shouldBlock = false;

		public static void SetBlock(bool value) => _shouldBlock = value;

		[HarmonyPrefix]
		[HarmonyPatch("GetMouseX")]
		public static bool OverrideMouseX(ref float __result)
		{
			if (!_shouldBlock) return true;
			__result = 0f;
			return false;
		}

		[HarmonyPrefix]
		[HarmonyPatch("GetMouseY")]
		public static bool OverrideMouseY(ref float __result)
		{
			if (!_shouldBlock) return true;
			__result = 0f;
			return false;
		}
	}

	// ---- 核心自瞄补丁 ----
	[HarmonyPatch(typeof(PlayerAvatar))]
	public static class AimingPatch
	{
		// 硬编码锁定距离上限（10米）
		private const float MAX_LOCK_DISTANCE = 10f;

		private static bool _active = true;
		private static Enemy _trackedEnemy = null;
		private static float _nextStatusLog = 0f;
		private const float StatusLogInterval = 5f;

		private static float _nextLockLog = 0f;
		private const float LockLogInterval = 1f;

		private static FieldInfo _localFlagField;
		private static FieldInfo _deadFlagField;
		private static FieldInfo _enemyRefField;

		[HarmonyPostfix]
		[HarmonyPatch("Update")]
		public static void OnPlayerUpdate(PlayerAvatar __instance)
		{
			if (!AssistedAimPlugin.EnableAim.Value) return;

			// ---- 初始化反射 ----
			if (_localFlagField == null)
			{
				_localFlagField = typeof(PlayerAvatar).GetField("isLocal", BindingFlags.Instance | BindingFlags.NonPublic);
				_deadFlagField = typeof(PlayerAvatar).GetField("deadSet", BindingFlags.Instance | BindingFlags.NonPublic);

				var enemyParentType = typeof(EnemyParent);
				var fields = enemyParentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (var f in fields)
				{
					if (f.FieldType == typeof(Enemy) || f.FieldType.IsSubclassOf(typeof(Enemy)))
					{
						_enemyRefField = f;
						break;
					}
				}
			}

			if (_localFlagField == null || _deadFlagField == null) return;

			// ---- 玩家状态 ----
			bool isLocal = (bool)_localFlagField.GetValue(__instance);
			bool dead = (bool)_deadFlagField.GetValue(__instance);
			bool inGame = SemiFunc.RunIsLevel() || SemiFunc.RunIsShop() || SemiFunc.RunIsLobby();

			// ---- 按键切换 ----
			if (Input.GetKeyDown(AssistedAimPlugin.ToggleKey.Value))
			{
				_active = !_active;
				AssistedAimPlugin.Log.LogInfo("自瞄" + (_active ? "启用" : "禁用"));
				if (!_active)
				{
					_trackedEnemy = null;
					InputBlocker.SetBlock(false); // 禁用时释放鼠标
				}
			}

			// ---- 状态日志（每5秒） ----
			if (AssistedAimPlugin.DebugLog.Value)
			{
				float now = Time.time;
				if (now >= _nextStatusLog)
				{
					_nextStatusLog = now + StatusLogInterval;
					LogStatus(__instance);
				}
			}

			// ---- 核心逻辑：仅在启用且本地玩家存活且游戏中 ----
			if (!_active || !isLocal || dead || !inGame)
			{
				_trackedEnemy = null;
				InputBlocker.SetBlock(false);
				return;
			}

			var director = EnemyDirector.instance;
			if (director == null) return;
			var enemies = director.enemiesSpawned;
			if (enemies == null || enemies.Count == 0)
			{
				_trackedEnemy = null;
				InputBlocker.SetBlock(false);
				return;
			}

			// 获取活敌列表
			var alive = new List<Enemy>();
			foreach (var parent in enemies)
			{
				if (parent == null) continue;
				Enemy e = null;
				if (_enemyRefField != null)
					e = _enemyRefField.GetValue(parent) as Enemy;
				if (e == null)
					e = parent.GetComponentInChildren<Enemy>();
				if (e != null && !IsDead(e))
					alive.Add(e);
			}

			if (alive.Count == 0)
			{
				_trackedEnemy = null;
				InputBlocker.SetBlock(false);
				return;
			}

			// ---- 选择最佳目标（距离 ≤ 10m 且角度在范围内，取最近） ----
			var target = FindBestTarget(alive);
			if (target != null)
			{
				// 只有目标变化时才输出锁定日志（限频）
				bool isNewTarget = _trackedEnemy == null || _trackedEnemy.name != target.name;
				if (isNewTarget)
				{
					_trackedEnemy = target;
					if (AssistedAimPlugin.DebugLog.Value)
					{
						float now = Time.time;
						if (now >= _nextLockLog)
						{
							_nextLockLog = now + LockLogInterval;
							Vector3 tPos = target.CenterTransform != null ? target.CenterTransform.position : target.transform.position;
							Vector3 pPos = PlayerController.instance != null ? PlayerController.instance.transform.position : Vector3.zero;
							float dist = Vector3.Distance(pPos, tPos);
							AssistedAimPlugin.Log.LogInfo($"锁定目标: {target.name}，距离 {dist:F1}m");
						}
					}
				}
				// 锁定视角（阻止鼠标输入）
				InputBlocker.SetBlock(true);
				ApplyLock(__instance, target);
			}
			else
			{
				// 没有符合条件的目标 → 释放鼠标
				if (_trackedEnemy != null)
				{
					_trackedEnemy = null;
					if (AssistedAimPlugin.DebugLog.Value)
						AssistedAimPlugin.Log.LogInfo("目标脱离锁定范围（>10m），释放鼠标");
				}
				InputBlocker.SetBlock(false);
			}
		}

		// ---- 日志辅助 ----
		private static void LogStatus(PlayerAvatar __instance)
		{
			Camera cam = Camera.main;
			string camAngle = cam != null ? cam.transform.eulerAngles.y.ToString("F1") : "N/A";

			var allAlive = GetAllAliveEnemies();
			Enemy nearest = null;
			float nearestDist = float.MaxValue;
			float nearestAngle = 0f;
			if (allAlive != null && allAlive.Count > 0)
			{
				Vector3 playerPos = PlayerController.instance != null ? PlayerController.instance.transform.position : Vector3.zero;
				Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
				Vector3 camForward = cam != null ? cam.transform.forward : Vector3.forward;
				foreach (var e in allAlive)
				{
					Vector3 ePos = e.CenterTransform != null ? e.CenterTransform.position : e.transform.position;
					float d = Vector3.Distance(playerPos, ePos);
					if (d < nearestDist)
					{
						nearestDist = d;
						nearest = e;
						if (cam != null)
						{
							Vector3 dir = ePos - camPos;
							nearestAngle = Vector3.Angle(camForward, dir);
						}
					}
				}
			}

			string targetInfo;
			if (_trackedEnemy != null)
			{
				Vector3 tPos = _trackedEnemy.CenterTransform != null ? _trackedEnemy.CenterTransform.position : _trackedEnemy.transform.position;
				Vector3 pPos = PlayerController.instance != null ? PlayerController.instance.transform.position : Vector3.zero;
				float dist = Vector3.Distance(pPos, tPos);
				float ang = 0f;
				if (cam != null)
				{
					Vector3 dir = tPos - cam.transform.position;
					ang = Vector3.Angle(cam.transform.forward, dir);
				}
				targetInfo = $"锁定 {_trackedEnemy.name}，距离 {dist:F1}m，角度 {ang:F1}°";
			}
			else
			{
				if (nearest != null)
					targetInfo = $"无锁定，最近敌人 {nearest.name}，距离 {nearestDist:F1}m，角度 {nearestAngle:F1}°（锁定距离上限 {MAX_LOCK_DISTANCE}m）";
				else
					targetInfo = "无锁定，无活敌";
			}

			int count = EnemyDirector.instance?.enemiesSpawned?.Count ?? 0;
			AssistedAimPlugin.Log.LogInfo(
				$"自瞄状态 | 摄像机Y角:{camAngle}° | 活敌数:{count} | {targetInfo}"
			);
		}

		private static List<Enemy> GetAllAliveEnemies()
		{
			var list = new List<Enemy>();
			var director = EnemyDirector.instance;
			if (director == null) return list;
			var enemies = director.enemiesSpawned;
			if (enemies == null) return list;
			foreach (var parent in enemies)
			{
				if (parent == null) continue;
				Enemy e = null;
				if (_enemyRefField != null)
					e = _enemyRefField.GetValue(parent) as Enemy;
				if (e == null)
					e = parent.GetComponentInChildren<Enemy>();
				if (e != null && !IsDead(e))
					list.Add(e);
			}
			return list;
		}

		// ---- 目标选择：只锁定距离 ≤ 10m 且角度在范围内的敌人，取最近 ----
		private static Enemy FindBestTarget(List<Enemy> enemies)
		{
			float maxAngle = (float)AssistedAimPlugin.AimCone.Value;
			Enemy best = null;
			float bestDist = float.MaxValue;

			Camera cam = Camera.main;
			if (cam == null) return null;

			Vector3 camPos = cam.transform.position;
			Vector3 forward = cam.transform.forward;
			Vector3 playerPos = PlayerController.instance != null ? PlayerController.instance.transform.position : camPos;

			foreach (var e in enemies)
			{
				if (e == null) continue;
				Vector3 pos = e.CenterTransform != null ? e.CenterTransform.position : e.transform.position;
				if (pos.sqrMagnitude < 0.01f) continue;

				float distance = Vector3.Distance(playerPos, pos);

				// 关键：只接受距离 ≤ 10m 的目标
				if (distance > MAX_LOCK_DISTANCE)
					continue;

				// 角度过滤
				Vector3 dirFromCam = pos - camPos;
				if (dirFromCam.sqrMagnitude < 0.001f) continue;
				float angle = Vector3.Angle(forward, dirFromCam);
				if (angle > maxAngle)
					continue;

				// 选距离最近的
				if (distance < bestDist)
				{
					bestDist = distance;
					best = e;
				}
			}
			return best;
		}

		// ---- 锁定执行 ----
		private static void ApplyLock(PlayerAvatar player, Enemy target)
		{
			if (player == null || target == null)
			{
				_trackedEnemy = null;
				InputBlocker.SetBlock(false);
				return;
			}

			Transform targetTransform = target.CenterTransform ?? target.transform;
			if (targetTransform == null)
			{
				_trackedEnemy = null;
				InputBlocker.SetBlock(false);
				return;
			}

			PlayerController ctrl = PlayerController.instance;
			if (ctrl == null || ctrl.cameraGameObject == null)
			{
				_trackedEnemy = null;
				InputBlocker.SetBlock(false);
				return;
			}

			Camera cam = Camera.main;
			if (cam == null) return;

			Vector3 dir = targetTransform.position - cam.transform.position;
			if (dir.sqrMagnitude < 0.001f)
				return;

			Quaternion targetRot = Quaternion.LookRotation(dir, Vector3.up);
			ctrl.cameraGameObject.transform.localRotation = targetRot;
		}

		// ---- 死亡检测 ----
		private static bool IsDead(Enemy e)
		{
			if (e == null) return true;
			var health = e.GetComponent<EnemyHealth>();
			if (health != null)
			{
				var deadField = typeof(EnemyHealth).GetField("dead", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (deadField != null)
					return (bool)deadField.GetValue(health);
			}
			return false;
		}
	}
}