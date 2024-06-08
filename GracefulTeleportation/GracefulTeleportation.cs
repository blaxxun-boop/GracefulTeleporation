using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace GracefulTeleportation;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class GracefulTeleportation : BaseUnityPlugin
{
	private const string ModName = "GracefulTeleportation";
	private const string ModVersion = "1.0.0";
	private const string ModGUID = "org.bepinex.plugins.gracefulteleportation";

	private static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<int> graceDuration = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0,
	}

	public void Awake()
	{
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		graceDuration = config("1 - General", "Grace Duration", 60, new ConfigDescription("Maximum time the grace period lasts in seconds.", new AcceptableValueRange<int>(1, 300)));
		graceDuration.SettingChanged += (_, _) => AddStatusEffect.SetValues();

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	[HarmonyPatch]
	private static class CancelBuffOnActionTaken
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(ZInput), nameof(ZInput.GetButton)),
			AccessTools.DeclaredMethod(typeof(ZInput), nameof(ZInput.GetButtonDown)),
		};
		
		private static void Postfix(ref bool __result)
		{
			if (__result && Player.m_localPlayer?.IsTeleporting() == false)
			{
				Player.m_localPlayer?.GetSEMan().RemoveStatusEffect("Grace".GetStableHashCode());
			}
		}
	}
	
	[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
	public class AddStatusEffect
	{
		private static StatusEffect? grace;

		private static void Postfix(ObjectDB __instance)
		{
			grace = ScriptableObject.CreateInstance<StatusEffect>();
			grace.name = "Grace";
			grace.m_name = "Grace";
			grace.m_icon = loadSprite("grace.png", 64, 64);
			SetValues();
			__instance.m_StatusEffects.Add(grace);
		}

		public static void SetValues()
		{
			if (grace is not null)
			{
				grace.m_tooltip = "You recently teleported and are protected until you start to take action.";
				grace.m_ttl = graceDuration.Value;
			}
		}
	}
	
	private static byte[] ReadEmbeddedFileBytes(string name)
	{
		using MemoryStream stream = new();
		Assembly.GetExecutingAssembly().GetManifestResourceStream("GracefulTeleportation." + name)?.CopyTo(stream);
		return stream.ToArray();
	}

	private static Texture2D loadTexture(string name)
	{
		Texture2D texture = new(0, 0);
		texture.LoadImage(ReadEmbeddedFileBytes("icons." + name));
		return texture;
	}

	private static Sprite loadSprite(string name, int width, int height) => Sprite.Create(loadTexture(name), new Rect(0, 0, width, height), Vector2.zero);

	[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.FindEnemy))]
	private static class LeaveMeAlone
	{
		private static readonly FieldInfo skipTarget = AccessTools.DeclaredField(typeof(Character), nameof(Character.m_aiSkipTarget));
		private static bool hasGrace(Character character, bool originalValue) => originalValue || (character is Player player && player.GetSEMan().HaveStatusEffect("Grace".GetStableHashCode()));
		
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.LoadsField(skipTarget))
				{
					yield return new CodeInstruction(OpCodes.Dup);
					yield return instruction;
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(LeaveMeAlone), nameof(hasGrace)));
				}
				else
				{
					yield return instruction;
				}
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
	private static class SetStatusEffect
	{
		private static void Postfix(Player __instance, ref bool __result)
		{
			if (__result)
			{
				__instance.GetSEMan().AddStatusEffect("Grace".GetStableHashCode());
			}
		}
	}
}
