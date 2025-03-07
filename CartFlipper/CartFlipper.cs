extern alias Weaved;
using BepInEx;
using UnityEngine;
using System;
using System.Collections;
using System.IO; // For MemoryStream and BinaryReader.
using ZNetType = Weaved::ZNet;             // Global network manager.
using ZNetViewType = Weaved::ZNetView;       // Network component that has GetZDO().
using ZPackageType = Weaved::ZPackage;       // Aliased ZPackage.
using ZRoutedRpcType = Weaved::ZRoutedRpc;   // Aliased ZRoutedRpc.
using ZNetScene = Weaved::ZNetScene;
using ZDOMan = Weaved::ZDOMan;
using ZDO = Weaved::ZDO;
using ZDOID = Weaved::ZDOID;
using UnityEngine.Windows; // If needed.
using Player = Weaved::Player;

namespace CartFlipperMod
{
    [BepInPlugin("com.wylker.cartflipper", "Cart Flipper", "0.1.10")]
    public class CartFlipper : BaseUnityPlugin
    {
        // Press 'O' to flip the cart on the client.
        private KeyCode flipKey = KeyCode.O;
        // Increase raycast detection distance.
        private readonly float detectionDistance = 8f;

        // Cache whether the "Cart" tag exists.
        private static bool hasCartTag = false;
        private static bool initializedHasCartTag = false;

        private void Awake()
        {
            Logger.LogInfo("CartFlipper mod loaded.");
        }

        private IEnumerator Start()
        {
            yield return new WaitUntil(() => ZRoutedRpcType.instance != null);

            if (IsServer())
            {
                //Logger.LogInfo("CartFlipper: Detected as server.");
                ZRoutedRpcType.instance.Register("RPC_FlipCart", new Action<long, ZPackageType>(RPC_FlipCart));
                //Logger.LogInfo("CartFlipper: Registered RPC_FlipCart on the server.");
            }
            else
            {
                //Logger.LogInfo("CartFlipper: Detected as client.");
            }
        }

        private void Update()
        {
            if (!Application.isBatchMode)
            {
                if (UnityEngine.Input.GetKeyDown(flipKey))
                {
                    Logger.LogInfo("CartFlipper: Flip key pressed");
                    TryFlipCartClient();
                }
            }
        }

        
        // convert ZDOID to long
        private long ZDOIDToLong(ZDOID id)
        {
            ZPackageType tempPkg = new ZPackageType();
            tempPkg.Write(id);
            tempPkg.SetPos(0);
            return tempPkg.ReadLong();
        }

        // Client-side: cast a ray to locate a cart, then send an RPC to flip it.
        // send the cart's UID as a string.
        private void TryFlipCartClient()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                Logger.LogWarning("No main camera found.");
                return;
            }

            Ray ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, detectionDistance))
            {
                Logger.LogInfo("Raycast hit: " + hit.transform.name);

                if (IsCart(hit.transform))
                {
                    Rigidbody rb = hit.transform.GetComponent<Rigidbody>();
                    if (rb == null)
                    {
                        Logger.LogWarning("No Rigidbody found on hit object.");
                        return;
                    }

                    ZNetViewType cartNetView = hit.transform.GetComponent<ZNetViewType>();
                    if (cartNetView == null)
                    {
                        Logger.LogWarning("Target cart has no ZNetView.");
                        return;
                    }

                    var zdo = cartNetView.GetZDO();
                    if (zdo == null)
                    {
                        Logger.LogWarning("No ZDO found for this cart's ZNetView.");
                        return;
                    }

                    Logger.LogInfo("Found cart with UID: " + zdo.m_uid + ". Sending RPC.");

                    // Send the cart's UID as a string.
                    string cartUidStr = zdo.m_uid.ToString(); // e.g., "1:14354"
                    ZPackageType pkg = new ZPackageType();
                    pkg.Write(cartUidStr);

                    if (Player.m_localPlayer != null)
                    {
                        var playerNetView = Player.m_localPlayer.GetComponent<ZNetViewType>();
                        if (playerNetView != null)
                        {
                            // Use our helper to convert the player's full m_uid into a long.
                            long senderId = ZDOIDToLong(playerNetView.GetZDO().m_uid);
                            Logger.LogInfo("Sending RPC with sender id: " + senderId);
                            ZRoutedRpcType.instance.InvokeRoutedRPC(senderId, "RPC_FlipCart", pkg);
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
            }
            else
            {
                Logger.LogInfo("Raycast did not hit any object.");
            }
        }


        // Helper to parse a UID string in the format "UserID:RandomID" into a ZDOID.
        private ZDOID ParseZDOID(string uidStr)
        {
            string[] parts = uidStr.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int userId) &&
                int.TryParse(parts[1], out int randomId))
            {
                // Assuming ZDOID has a constructor that takes two uints:
                return new ZDOID((uint)userId, (uint)randomId);
            }
            throw new Exception("Invalid ZDOID string: " + uidStr);
        }

        // Server receives the cart's UID (as a string), converts it back to a ZDOID,
        // retrieves its ZDO via ZDOMan, finds the cart GameObject, and flips it upright.
        private void RPC_FlipCart(long sender, ZPackageType pkg)
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

            ZNetViewType cartView = ZNetScene.instance.FindInstance(zdo);
            if (cartView == null)
            {
                Logger.LogWarning("Cart with UID " + uidStr + " was not found on the server.");
                return;
            }
            Logger.LogInfo("Found cart instance for UID: " + uidStr);

            GameObject cartObject = cartView.gameObject;
            Rigidbody rb = cartObject.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Logger.LogWarning("Cart object '" + cartObject.name + "' has no Rigidbody.");
                return;
            }
            Logger.LogInfo("Found Rigidbody for cart object: " + cartObject.name);

            if (IsFlipped(rb.transform))
            {
                Logger.LogInfo("Cart is flipped. Proceeding to flip upright.");
                Vector3 currentEuler = rb.transform.rotation.eulerAngles;
                rb.transform.rotation = Quaternion.Euler(0f, currentEuler.y, 0f);
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.AddForce(Vector3.up * 2f, ForceMode.Impulse);
                Logger.LogInfo("Flipped cart upright on server: " + cartObject.name);
            }
            else
            {
                Logger.LogInfo("Cart is not flipped; no action taken.");
            }
        }

        //checks by name or tag if the target object is a cart
        private bool IsCart(Transform t)
        {
            bool nameContains = t.name.Contains("Cart");
            bool tagCheck = false;
            if (TagExists("Cart"))
            {
                tagCheck = t.CompareTag("Cart");
            }
            Logger.LogInfo($"IsCart check for {t.name}: nameContains={nameContains}, tagCheck={tagCheck}");
            return nameContains || tagCheck;
        }

        // Checks once if a given tag exists.
        private bool TagExists(string tag)
        {
            if (!initializedHasCartTag)
            {
                try
                {
                    GameObject[] objs = GameObject.FindGameObjectsWithTag(tag);
                    hasCartTag = (objs != null && objs.Length > 0);
                }
                catch (Exception)
                {
                    hasCartTag = false;
                }
                initializedHasCartTag = true;
            }
            return hasCartTag;
        }

        // Does the cart need flipped? uses delta of angle
        private bool IsFlipped(Transform t)
        {
            float angle = Vector3.Angle(t.up, Vector3.up);
            Logger.LogInfo($"{t.name} angle to upright: {angle}");
            return angle > 90f;
        }

        // Determines if we're running as a server - maybe not needed anymore?
        private bool IsServer()
        {
            bool isServer = ZNetType.instance != null && ZNetType.instance.IsServer();
            Logger.LogInfo($"IsServer check: {isServer}");
            return isServer;
        }
    }
}
