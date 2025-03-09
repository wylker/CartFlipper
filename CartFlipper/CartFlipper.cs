using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using System;
using System.Collections;
using System.IO;
using Valheim;

namespace CartFlipperMod
{
    public static class Constants
    {
        public const string ModVersion = "1.0.0";
    }

    [BepInPlugin("wylker.cartflipper", "Cart Flipper", Constants.ModVersion)]
    public class CartFlipper : BaseUnityPlugin
    {
        // Configuration
        private ConfigEntry<KeyCode> configFlipKey;
        private ConfigEntry<int> configMaxRetries;

        // Store the currently hovered interactable.
        public static Interactable CurrentHoveredInteractable;
        public static KeyCode ConfigFlipKey { get; private set; }

        private Harmony harmony;

        private void Awake()
        {
            Logger.LogInfo("CartFlipper mod loaded (v"+Constants.ModVersion+")");
            // Bind configuration settings.
            configFlipKey = Config.Bind("General", "FlipKey", KeyCode.O, "The key used to flip the cart.");
            configMaxRetries = Config.Bind("General", "MaxRetries", 3, "Carts are often stubborn about being flipped. Set the maximum number of automatic flip attempts per keypress.");

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
            ConfigFlipKey = configFlipKey.Value;
            ZRoutedRpc.instance.Register("RPC_FlipCart", new Action<long, ZPackage>(RPC_FlipCart));
            // Logger.LogInfo("Registered RPC_FlipCart."); // Debug loading.
            if (IsServer())
            {
                // Server registers to receive client versions.
                ZRoutedRpc.instance.Register("RPC_SendModVersion", new Action<long, ZPackage>(RPC_SendModVersion));
                // Wait a few seconds for peers to connect, then request mod versions.
                yield return new WaitForSeconds(5f);
                RequestModVersionFromAllPeers();
            }
            else
            {
                // Client registers to respond to version requests.
                ZRoutedRpc.instance.Register("RPC_RequestModVersion", new Action<long, ZPackage>(RPC_RequestModVersion));
            }
        }

        private void Update()
        {
            // Check for the key press using the config value.
            if (!Application.isBatchMode && Input.GetKeyDown(configFlipKey.Value))
            {
                // Logger.LogInfo("Flip key pressed."); // Debug Key Detection
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

        // Check if the transform's name contains "Cart".
        private bool IsCart(Transform t)
        {
            return t.name.Contains("Cart");
        }

        
        // Returns true if the object's up vector forms an angle > 90° with Vector3.up.
        private bool IsFlipped(Transform t)
        {
            float angle = Vector3.Angle(t.up, Vector3.up);
            Logger.LogInfo($"{t.name} angle to upright: {angle}");
            return angle > 90f;
        }

        
        // Convert a ZDOID to a long representation.
        private long ZDOIDToLong(ZDOID id)
        {
            ZPackage tempPkg = new ZPackage();
            tempPkg.Write(id);
            tempPkg.SetPos(0);
            return tempPkg.ReadLong();
        }

        
        // Sends an RPC to flip the specified cart.
        
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
                    string cartUidStr = zdo.m_uid.ToString();  // Format e.g., "1:14355"
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

        
        // Parses a UID string (e.g., "1:14355") into a ZDOID.
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

        
        // Server-side RPC handler. Receives the cart's UID (as a string), converts it to a ZDOID,
        // retrieves the cart's ZDO, and starts a coroutine to flip the cart upright.
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

            // Start the coroutine to attempt flipping the cart with retries.
            StartCoroutine(FlipCartCoroutine(cartObject));
        }


        // Coroutine that attempts to flip the cart upright.
        // Retries up to the configured max retries with a delay between attempts and temporarily increases drag.
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

            // Store original drag and position values.
            float originalDrag = rb.drag;
            float originalAngularDrag = rb.angularDrag;
            Vector3 originalPos = rb.transform.position;

            // Increase drag to help the cart settle.
            rb.drag = originalDrag * 5f;
            rb.angularDrag = originalAngularDrag * 5f;

            while (attempts < maxRetries)
            {
                // Step 1: Lift the cart upward by 1.5 units over 1 second.
                float liftDuration = 1f;
                float liftTimer = 0f;
                Vector3 targetLiftPos = originalPos + Vector3.up * 1.5f;
                while (liftTimer < liftDuration)
                {
                    liftTimer += Time.deltaTime;
                    rb.MovePosition(Vector3.Lerp(originalPos, targetLiftPos, liftTimer / liftDuration));
                    yield return null;
                }

                // Step 2: Gradually rotate the cart to be upright over 3 seconds (preserving yaw).
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

                // Step 3: Gently lower the cart back to its original position
                float lowerDuration = 1f;
                float lowerTimer = 0f;
                while (lowerTimer < lowerDuration)
                {
                    lowerTimer += Time.deltaTime;
                    rb.MovePosition(Vector3.Lerp(targetLiftPos, originalPos, lowerTimer / lowerDuration));
                    yield return null;
                }

                // Reset velocities.
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;

                // Wait a moment to let physics settle.
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
        /// <summary>
        /// Server-side RPC handler. Receives the mod version from a client, and disconnects the client if the version doesn't match.
        /// </summary>
        public void RPC_SendModVersion(long sender, ZPackage pkg)
        {
            string clientVersion = pkg.ReadString();
            Logger.LogInfo("Server received mod version from client: " + clientVersion);
            if (clientVersion != Constants.ModVersion)
            {
                Logger.LogWarning("Mod version mismatch! Server: " + Constants.ModVersion + " vs. Client: " + clientVersion);
                // Disconnect the client.
                if (ZNet.instance != null)
                {
                    Logger.LogWarning("Disconnecting client: " + sender);
                    ZNetPeer peer = ZNet.instance.GetPeer(sender);
                    if (peer != null)
                    {
                        ZNet.instance.Disconnect(peer);
                    }
                    else
                    {
                        Logger.LogWarning("Peer not found for sender: " + sender);
                    }
                }
            }
            else
            {
                Logger.LogInfo("Mod version match confirmed.");
            }
        }

        /// <summary>
        /// Client-side RPC handler: when the server requests the mod version, respond with our version.
        /// </summary>
        public void RPC_RequestModVersion(long sender, ZPackage pkg)
        {
            Logger.LogInfo("Client received mod version request from server.");
            ZPackage response = new ZPackage();
            response.Write(Constants.ModVersion);
            long senderId = ZDOIDToLong(Player.m_localPlayer.GetComponent<ZNetView>().GetZDO().m_uid);
            ZRoutedRpc.instance.InvokeRoutedRPC(senderId, "RPC_SendModVersion", response);
            Logger.LogInfo("Client sent mod version: " + Constants.ModVersion);
        }

        /// <summary>
        /// Server-side method to request mod versions from all connected peers.
        /// </summary>
        private void RequestModVersionFromAllPeers()
        {
            foreach (var peer in ZNet.instance.m_peers)
            {
                try
                {
                    ZPackage pkg = new ZPackage();
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "RPC_RequestModVersion", pkg);
                    Logger.LogInfo("Server requested mod version from peer: " + peer.m_uid);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Failed to request mod version from peer " + peer.m_uid + ": " + ex.Message);
                }
            }
        }
        private bool IsServer()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }
}


    // Harmony patch to add a hover text indicator for carts that are eligible for flipping.
    [HarmonyPatch(typeof(Vagon), "GetHoverText")]
    public static class Vagon_GetHoverText_Patch
    {
        static void Postfix(Vagon __instance, ref string __result)
        {
            // Cast the instance to Component to access transform.
            if (__instance is Component comp && comp.transform != null)
            {
                if (comp.transform.name.Contains("Cart"))
                {
                    // Calculate angle.
                    float angle = Vector3.Angle(comp.transform.up, Vector3.up);
                    if (angle > 90f)
                    {
                        __result += "\n[<color=yellow><b>" + CartFlipper.ConfigFlipKey.ToString() + "</b></color>] Flip";
                    }
                }
            }
        }
    }
}
