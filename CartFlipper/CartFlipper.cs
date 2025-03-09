using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using System;
using System.Collections;
using System.IO;
using Valheim;
using ServerSync;
using System.Net;

namespace CartFlipperMod
{
    public static class Constants
    {
        public const string ModVersion = "1.1.0";
    }

    [BepInPlugin("wylker.cartflipper", "Cart Flipper", Constants.ModVersion)]
    public class CartFlipper : BaseUnityPlugin
    {
        private ConfigEntry<KeyCode> configFlipKey;
        private ConfigEntry<int> configMaxRetries;
        private static readonly ConfigSync ConfigSync = new("wylker.cartflipper") { DisplayName = "Cart Flipper", CurrentVersion = Constants.ModVersion, MinimumRequiredVersion = Constants.ModVersion };
        public static Interactable CurrentHoveredInteractable;
        public static KeyCode ConfigFlipKey { get; private set; }
        private Harmony harmony;

        private void Awake()
        {
            Logger.LogInfo("CartFlipper mod loaded (v" + Constants.ModVersion + ").");

            // Bind configuration settings.
            configFlipKey = Config.Bind("General", "FlipKey", KeyCode.O, "The key used to flip the cart.");
            configMaxRetries = Config.Bind("General", "MaxRetries", 3, "Carts are often stubborn about being flipped. Set the maximum number of automatic flip attempts per keypress.");
            ConfigSync.AddConfigEntry(configFlipKey);
            ConfigSync.AddConfigEntry(configMaxRetries);

            // Store the configured key for use in our Harmony patch.
            ConfigFlipKey = configFlipKey.Value;

            harmony = new Harmony("com.wylker.cartflipper.patch");
            harmony.PatchAll();
        }

        private void OnDestroy()
        {
            harmony.UnpatchSelf();
        }

        private IEnumerator Start()
        {
            // Wait until the global RPC system is ready.
            yield return new WaitUntil(() => ZRoutedRpc.instance != null);

            // Register RPC handler
            ZRoutedRpc.instance.Register("RPC_FlipCart", new Action<long, ZPackage>(RPC_FlipCart));
            Logger.LogInfo("Registered RPC_FlipCart.");
        }

        private void Update()
        {
            // Check for the configured key press.
            if (!Application.isBatchMode && Input.GetKeyDown(configFlipKey.Value))
            {
                var hoveredObject = Player.m_localPlayer?.GetHoverObject();
                if (hoveredObject != null)
                {
                    if (IsCart(hoveredObject.transform))
                    {
                        Logger.LogInfo("Targeted object is a cart. Initiating flip.");
                        FlipCart(hoveredObject);
                    }
                    else
                    {
                        Logger.LogInfo("Targeted object is not a cart.");
                    }
                }
                else
                {
                    Logger.LogInfo("No acceptable object found.");
                }
            }
        }

        // Chect tagetted object - Is it a cart?
        private bool IsCart(Transform t)
        {
            return t.name.Contains("Cart");
        }

        // Is it upside down?
        private bool IsFlipped(Transform t)
        {
            float angle = Vector3.Angle(t.up, Vector3.up);
            Logger.LogInfo($"{t.name} angle to upright: {angle}");
            return angle > 90f;
        }

        // converting ZDOID to long
        private long ZDOIDToLong(ZDOID id)
        {
            ZPackage tempPkg = new ZPackage();
            tempPkg.Write(id);
            tempPkg.SetPos(0);
            return tempPkg.ReadLong();
        }

        // Send RPC to server for flip request
        private void FlipCart(GameObject cartObject)
        {
            var cartNetView = cartObject.GetComponent<ZNetView>();
            if (cartNetView == null)
            {
                Logger.LogWarning("Cart does not have a ZNetView.");
                return;
            }
            var zdo = cartNetView.GetZDO();
            if (zdo == null)
            {
                Logger.LogWarning("Cart has no ZDO.");
                return;
            }
            Logger.LogInfo("Preparing to flip cart with UID: " + zdo.m_uid);

            if (Player.m_localPlayer != null)
            {
                var playerNetView = Player.m_localPlayer.GetComponent<ZNetView>();
                if (playerNetView != null)
                {
                    long senderId = ZDOIDToLong(playerNetView.GetZDO().m_uid);
                    // Send the cart's UID as a string.
                    string cartUidStr = zdo.m_uid.ToString();  // e.g., "1:14355"
                    ZPackage pkg = new ZPackage();
                    pkg.Write(cartUidStr);
                    Logger.LogInfo("Sending RPC with sender id: " + senderId + " for cart UID: " + cartUidStr);
                    ZRoutedRpc.instance.InvokeRoutedRPC(senderId, "RPC_FlipCart", pkg);
                    Logger.LogInfo("RPC invoked via ZRoutedRpc instance.");
                }
                else
                {
                    Logger.LogWarning("Local player's ZNetView not found.");
                }
            }
            else
            {
                Logger.LogWarning("Local player is null. Cannot send RPC.");
            }
        }


        // Convert UID string into a ZDOID. Expected format: "userId:randomId"
        private ZDOID ParseZDOID(string uidStr)
        {
            string[] parts = uidStr.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int userId) &&
                int.TryParse(parts[1], out int randomId))
            {
                return new ZDOID((uint)userId, (uint)randomId);
            }
            throw new Exception("Invalid ZDOID string: " + uidStr);
        }


        // Server-side RPC handler. Receives the cart's UID (as a string), converts it to a ZDOID, starts coroutine to flip the cart
        public void RPC_FlipCart(long sender, ZPackage pkg)
        {
            Logger.LogInfo("RPC_FlipCart received on server. Sender: " + sender);
            string uidStr = pkg.ReadString();
            Logger.LogInfo("Received cart UID (as string): " + uidStr);

            ZDOID zdoId;
            try
            {
                zdoId = ParseZDOID(uidStr);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to convert UID string to ZDOID: " + ex.Message);
                return;
            }
            Logger.LogInfo("Converted cart UID to ZDOID: " + zdoId);

            ZDO zdo = ZDOMan.instance.GetZDO(zdoId);
            if (zdo == null)
            {
                Logger.LogWarning("No ZDO found for UID " + uidStr);
                return;
            }
            Logger.LogInfo("Retrieved ZDO for UID: " + uidStr);

            ZNetView cartView = ZNetScene.instance.FindInstance(zdo);
            if (cartView == null)
            {
                Logger.LogWarning("Cart with UID " + uidStr + " was not found on the server.");
                return;
            }
            Logger.LogInfo("Found cart instance for UID: " + uidStr);

            GameObject cartObject = cartView.gameObject;
            var rb = cartObject.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Logger.LogWarning("Cart object '" + cartObject.name + "' has no Rigidbody.");
                return;
            }
            Logger.LogInfo("Found Rigidbody for cart object: " + cartObject.name);

            // Start the coroutine
            StartCoroutine(FlipCartCoroutine(cartObject));
        }


        // Retries up to the configured max retries
        private IEnumerator FlipCartCoroutine(GameObject cartObject)
        {
            int maxRetries = configMaxRetries.Value;
            int attempts = 0;
            Rigidbody rb = cartObject.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Logger.LogWarning("Cart object lost its Rigidbody during flip.");
                yield break;
            }

            // Store original drag values.
            float originalDrag = rb.drag;
            float originalAngularDrag = rb.angularDrag;
            // make it gentle
            rb.drag = originalDrag * 5f;
            rb.angularDrag = originalAngularDrag * 5f;
            Vector3 originalPos = rb.transform.position;

            float liftHeight = 0.75f;

            while (attempts < maxRetries)
            {
                float liftDuration = 1f;
                float liftTimer = 0f;
                Vector3 targetLiftPos = originalPos + Vector3.up * liftHeight;
                while (liftTimer < liftDuration)
                {
                    liftTimer += Time.deltaTime;
                    rb.MovePosition(Vector3.Lerp(originalPos, targetLiftPos, liftTimer / liftDuration));
                    yield return null;
                }

                Quaternion currentRot = rb.transform.rotation;
                Quaternion targetRot = Quaternion.Euler(0f, currentRot.eulerAngles.y, 0f);
                float rotateDuration = 3f;
                float rotateTimer = 0f;
                while (rotateTimer < rotateDuration)
                {
                    rotateTimer += Time.deltaTime;
                    Quaternion newRot = Quaternion.Lerp(currentRot, targetRot, rotateTimer / rotateDuration);
                    rb.MoveRotation(newRot);
                    yield return null;
                }

                float lowerDuration = 1f;
                float lowerTimer = 0f;
                while (lowerTimer < lowerDuration)
                {
                    lowerTimer += Time.deltaTime;
                    rb.MovePosition(Vector3.Lerp(targetLiftPos, originalPos, lowerTimer / lowerDuration));
                    yield return null;
                }

                // Reset velocities and settle it
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                yield return new WaitForSeconds(1f);

                float angle = Vector3.Angle(rb.transform.up, Vector3.up);
                Logger.LogInfo("Flip attempt " + (attempts + 1) + ": cart angle = " + angle);
                if (angle < 90f)
                {
                    Logger.LogInfo("Cart successfully flipped upright.");
                    break;
                }
                attempts++;
            }

            // Restore original drag values.
            rb.drag = originalDrag;
            rb.angularDrag = originalAngularDrag;

            if (attempts == maxRetries)
            {
                Logger.LogWarning("Flip attempts exhausted; cart remains flipped.");
            }
        }

        private bool IsServer()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

            SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
        {
            return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
        }
    }
    // Harmony patch to add a "Flip" option to the cart's context menu.
    [HarmonyPatch(typeof(Vagon), "GetHoverText")]
    public static class Vagon_GetHoverText_Patch
    {
        static void Postfix(Vagon __instance, ref string __result)
        {
            if (__instance.transform.name.Contains("Cart"))
            {
                float angle = Vector3.Angle(__instance.transform.up, Vector3.up);
                if (angle > 90f)
                {
                    __result += "\n[<color=yellow><b>" + CartFlipper.ConfigFlipKey.ToString() + "</b></color>] Flip";
                }
            }
        }
    }
}
