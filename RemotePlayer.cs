using UnityEngine;

namespace Crawlspace2MP
{
    public class RemotePlayer
    {
        public int PeerId { get; }
        public GameObject Head { get; private set; }
        public GameObject FaceIndicator { get; private set; }
        public GameObject LeftHand { get; private set; }
        public GameObject RightHand { get; private set; }
        public Light Flashlight { get; private set; }
        public GameObject FlashlightCone { get; private set; }
        public GameObject LeftBattery { get; private set; }
        public GameObject RightBattery { get; private set; }
        
        // Track if we're using real models or placeholders
        private bool _usingRealHands = false;
        
        // Ghost state
        private bool _isGhost = false;
        public bool IsGhost => _isGhost;
        
        private bool _isStanding;
        private Vector3 _targetHeadPos;
        private Quaternion _targetHeadRot;
        private Vector3 _targetLeftHandPos;
        private Quaternion _targetLeftHandRot;
        private Vector3 _targetRightHandPos;
        private Quaternion _targetRightHandRot;
        
        private float _lerpSpeed = 25f; // Faster interpolation for smoother tracking
        private bool _hasReceivedData = false;
        private int _logCounter = 0;

        public RemotePlayer(int peerId)
        {
            PeerId = peerId;
            CreateVisual();
        }

        private void CreateVisual()
        {
            if (Plugin.HelmetPrefab != null)
            {
                Head = Object.Instantiate(Plugin.HelmetPrefab);
                Head.name = $"RemotePlayer_{PeerId}_Head";
                Head.transform.localScale = new Vector3(0.15f, 0.15f, 0.15f);
                
                foreach (var col in Head.GetComponentsInChildren<Collider>())
                    Object.Destroy(col);
                foreach (var mb in Head.GetComponentsInChildren<MonoBehaviour>())
                    Object.Destroy(mb);
                foreach (var renderer in Head.GetComponentsInChildren<Renderer>())
                    renderer.enabled = true;
            }
            else
            {
                Head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Head.name = $"RemotePlayer_{PeerId}_Head";
                Head.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
                Object.Destroy(Head.GetComponent<Collider>());
                SetColor(Head, Color.yellow);
            }
            
            var headTrigger = Head.AddComponent<SphereCollider>();
            headTrigger.radius = 0.5f;
            headTrigger.isTrigger = true;
            
            if (Plugin.HelmetPrefab == null)
            {
                FaceIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                FaceIndicator.name = "FaceIndicator";
                FaceIndicator.transform.SetParent(Head.transform);
                FaceIndicator.transform.localPosition = new Vector3(0, 0, 0.5f);
                FaceIndicator.transform.localScale = new Vector3(0.3f, 0.3f, 0.5f);
                Object.Destroy(FaceIndicator.GetComponent<Collider>());
                SetColor(FaceIndicator, Color.white);
            }
            
            SetupHandVisuals();
            SetupFlashlight();
            SetupBatteryVisuals();
            
            Head.transform.position = Vector3.zero;
            LeftHand.transform.position = Vector3.zero;
            RightHand.transform.position = Vector3.zero;
            
            Plugin.Log.LogInfo($"Created remote player visual for peer {PeerId}");
        }
        
        private void SetupHandVisuals()
        {
            // The game's hand meshes are at:
            // BackpackControl.leftHand -> offsettu -> customhandleft
            // OR inside OVR's "[LeftHand Controller] Model Parent" for Quest hands
            
            GameObject leftHandSource = null;
            GameObject rightHandSource = null;
            
            var backpack = Object.FindObjectOfType<BackpackControl>();
            if (backpack == null)
            {
                Plugin.Log.LogWarning($"[RemotePlayer {PeerId}] BackpackControl not found! Using fallback hands.");
                CreateFallbackHands();
                return;
            }
            
            Plugin.LogDebug($"[RemotePlayer {PeerId}] BackpackControl found. leftHand={backpack.leftHand != null}, rightHand={backpack.rightHand != null}");
            
            // DEBUG: Log full hierarchy of both hands (only in debug builds)
            Plugin.LogDebug($"[RemotePlayer {PeerId}] === LEFT HAND HIERARCHY ===");
            if (backpack.leftHand != null)
            {
                LogHandHierarchyDetailed(backpack.leftHand.transform, 0);
            }
            
            Plugin.LogDebug($"[RemotePlayer {PeerId}] === RIGHT HAND HIERARCHY ===");
            if (backpack.rightHand != null)
            {
                LogHandHierarchyDetailed(backpack.rightHand.transform, 0);
            }
            
            // Left hand - try specific path first
            if (backpack.leftHand != null)
            {
                var offsettu = backpack.leftHand.transform.Find("offsettu");
                if (offsettu != null)
                {
                    var customHand = offsettu.Find("customhandleft");
                    if (customHand != null)
                    {
                        leftHandSource = customHand.gameObject;
                        Plugin.LogDebug($"[RemotePlayer {PeerId}] Found left hand at specific path: customhandleft");
                    }
                }
                
                // Try OVR Model Parent path (Quest hands)
                if (leftHandSource == null)
                {
                    var modelParent = backpack.leftHand.transform.Find("[LeftHand Controller] Model Parent");
                    if (modelParent != null)
                    {
                        Plugin.LogDebug($"[RemotePlayer {PeerId}] Found [LeftHand Controller] Model Parent, searching for renderers...");
                        // Search for any mesh renderer inside Model Parent
                        foreach (var renderer in modelParent.GetComponentsInChildren<Renderer>(true))
                        {
                            Plugin.LogDebug($"[RemotePlayer {PeerId}]   Model Parent renderer: {renderer.gameObject.name}, type={renderer.GetType().Name}, active={renderer.gameObject.activeInHierarchy}, enabled={renderer.enabled}");
                            if (leftHandSource == null)
                            {
                                leftHandSource = renderer.gameObject;
                                Plugin.LogDebug($"[RemotePlayer {PeerId}] Using as left hand source: {renderer.gameObject.name}");
                            }
                        }
                    }
                }
                
                // Fallback: search for any skinned mesh renderer in the hand hierarchy
                if (leftHandSource == null)
                {
                    foreach (var smr in backpack.leftHand.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (!smr.gameObject.name.ToLower().Contains("battery") && 
                            !smr.gameObject.name.ToLower().Contains("energy"))
                        {
                            leftHandSource = smr.gameObject;
                            Plugin.LogDebug($"[RemotePlayer {PeerId}] Found left hand via SkinnedMeshRenderer: {smr.gameObject.name}");
                            break;
                        }
                    }
                }
                
                // Last resort: any MeshRenderer
                if (leftHandSource == null)
                {
                    foreach (var mr in backpack.leftHand.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (!mr.gameObject.name.ToLower().Contains("battery") && 
                            !mr.gameObject.name.ToLower().Contains("energy") &&
                            !mr.gameObject.name.ToLower().Contains("indicator"))
                        {
                            leftHandSource = mr.gameObject;
                            Plugin.LogDebug($"[RemotePlayer {PeerId}] Found left hand via MeshRenderer: {mr.gameObject.name}");
                            break;
                        }
                    }
                }
            }
            
            // Right hand - try specific path first
            if (backpack.rightHand != null)
            {
                var offsettu = backpack.rightHand.transform.Find("offsettu");
                if (offsettu != null)
                {
                    var customHand = offsettu.Find("customhandright");
                    if (customHand != null)
                    {
                        rightHandSource = customHand.gameObject;
                        Plugin.LogDebug($"[RemotePlayer {PeerId}] Found right hand at specific path: customhandright");
                    }
                }
                
                // Try OVR Model Parent path (Quest hands)
                if (rightHandSource == null)
                {
                    var modelParent = backpack.rightHand.transform.Find("[RightHand Controller] Model Parent");
                    if (modelParent != null)
                    {
                        Plugin.LogDebug($"[RemotePlayer {PeerId}] Found [RightHand Controller] Model Parent, searching for renderers...");
                        // Search for any mesh renderer inside Model Parent
                        foreach (var renderer in modelParent.GetComponentsInChildren<Renderer>(true))
                        {
                            Plugin.LogDebug($"[RemotePlayer {PeerId}]   Model Parent renderer: {renderer.gameObject.name}, type={renderer.GetType().Name}, active={renderer.gameObject.activeInHierarchy}, enabled={renderer.enabled}");
                            if (rightHandSource == null)
                            {
                                rightHandSource = renderer.gameObject;
                                Plugin.LogDebug($"[RemotePlayer {PeerId}] Using as right hand source: {renderer.gameObject.name}");
                            }
                        }
                    }
                }
                
                // Fallback: search for any skinned mesh renderer in the hand hierarchy
                if (rightHandSource == null)
                {
                    foreach (var smr in backpack.rightHand.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (!smr.gameObject.name.ToLower().Contains("battery") && 
                            !smr.gameObject.name.ToLower().Contains("energy"))
                        {
                            rightHandSource = smr.gameObject;
                            Plugin.LogDebug($"[RemotePlayer {PeerId}] Found right hand via SkinnedMeshRenderer: {smr.gameObject.name}");
                            break;
                        }
                    }
                }
                
                // Last resort: any MeshRenderer
                if (rightHandSource == null)
                {
                    foreach (var mr in backpack.rightHand.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (!mr.gameObject.name.ToLower().Contains("battery") && 
                            !mr.gameObject.name.ToLower().Contains("energy") &&
                            !mr.gameObject.name.ToLower().Contains("indicator"))
                        {
                            rightHandSource = mr.gameObject;
                            Plugin.LogDebug($"[RemotePlayer {PeerId}] Found right hand via MeshRenderer: {mr.gameObject.name}");
                            break;
                        }
                    }
                }
            }
            
            // Clone the hand meshes if found
            if (leftHandSource != null && rightHandSource != null)
            {
                LeftHand = CloneHandMesh(leftHandSource, $"RemotePlayer_{PeerId}_LeftHand");
                RightHand = CloneHandMesh(rightHandSource, $"RemotePlayer_{PeerId}_RightHand");
                _usingRealHands = true;
                Plugin.LogDebug($"[RemotePlayer {PeerId}] Cloned hand models from: L={leftHandSource.name}, R={rightHandSource.name}");
                return;
            }
            
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Could not find hand meshes, using fallback spheres");
            CreateFallbackHands();
        }
        
        private void LogHandHierarchyDetailed(Transform parent, int depth)
        {
            string indent = new string(' ', depth * 2);
            
            // Get component info
            var renderer = parent.GetComponent<Renderer>();
            var smr = parent.GetComponent<SkinnedMeshRenderer>();
            var mr = parent.GetComponent<MeshRenderer>();
            var mf = parent.GetComponent<MeshFilter>();
            
            string components = "";
            if (smr != null) components += "[SkinnedMeshRenderer] ";
            if (mr != null) components += "[MeshRenderer] ";
            if (mf != null) components += "[MeshFilter] ";
            if (renderer != null && smr == null && mr == null) components += $"[{renderer.GetType().Name}] ";
            
            Plugin.LogDebug($"[Hand] {indent}{parent.name} {components}active={parent.gameObject.activeSelf}");
            
            // Only go 4 levels deep to avoid spam
            if (depth < 4)
            {
                foreach (Transform child in parent)
                {
                    LogHandHierarchyDetailed(child, depth + 1);
                }
            }
        }
        
        private GameObject FindChildByNameContains(Transform parent, string nameContains)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.ToLower().Contains(nameContains.ToLower()))
                {
                    Plugin.LogDebug($"[RemotePlayer {PeerId}] FindChildByNameContains found: {child.name}");
                    return child.gameObject;
                }
            }
            return null;
        }
        
        private GameObject FindHandMeshInChildren(Transform parent, string handSide)
        {
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Searching children of {parent.name} for {handSide} hand mesh...");
            
            // Search all descendants for a mesh renderer
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
            {
                string nameLower = renderer.gameObject.name.ToLower();
                Plugin.LogDebug($"[RemotePlayer {PeerId}]   Found renderer: {renderer.gameObject.name}");
                
                // Skip if it's clearly not a hand (like UI elements)
                if (nameLower.Contains("ui") || nameLower.Contains("canvas") || nameLower.Contains("text"))
                    continue;
                
                // If it has "hand" in name or is a skinned mesh, it's likely the hand
                if (nameLower.Contains("hand") || nameLower.Contains("palm") || nameLower.Contains("glove") ||
                    renderer is SkinnedMeshRenderer)
                {
                    Plugin.LogDebug($"[RemotePlayer {PeerId}]   -> Using as {handSide} hand mesh!");
                    return renderer.gameObject;
                }
            }
            
            // If no specific hand mesh found, try to find any mesh that's not a controller model
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer || renderer.GetComponent<MeshFilter>() != null)
                {
                    Plugin.LogDebug($"[RemotePlayer {PeerId}]   -> Fallback: using {renderer.gameObject.name} as {handSide} hand");
                    return renderer.gameObject;
                }
            }
            
            return null;
        }
        
        private GameObject CloneHandMesh(GameObject source, string newName)
        {
            var clone = Object.Instantiate(source);
            clone.name = newName;
            
            // Log the full hierarchy of what we cloned (debug only)
            Plugin.LogDebug($"[RemotePlayer] Cloned hand hierarchy for {newName}:");
            foreach (Transform child in clone.GetComponentsInChildren<Transform>(true))
            {
                Plugin.LogDebug($"[RemotePlayer]   - {child.name} (active={child.gameObject.activeSelf})");
            }
            
            // Remove any scripts that might interfere
            var handScript = clone.GetComponent<Hand>();
            if (handScript != null) Object.Destroy(handScript);
            
            // Remove all MonoBehaviours that might interfere (but keep the GameObject structure)
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>())
            {
                Object.Destroy(mb);
            }
            
            // Remove colliders
            foreach (var col in clone.GetComponentsInChildren<Collider>())
                Object.Destroy(col);
            
            // IMPORTANT: Disable/destroy unwanted objects from the cloned hands
            foreach (Transform child in clone.GetComponentsInChildren<Transform>(true))
            {
                string nameLower = child.name.ToLower();
                
                // Disable indicators, colliders, grab volumes (green dots, debug visuals)
                if (nameLower.Contains("indicator") || nameLower.Contains("tipindicator") || 
                    nameLower.Contains("fingertip") || nameLower.Contains("tip_") ||
                    nameLower.Contains("collidervisual") || nameLower.Contains("grabvolume") ||
                    nameLower.Contains("leftcollider") || nameLower.Contains("rightcollider") ||
                    nameLower.Contains("coll_hands"))
                {
                    Plugin.LogDebug($"[RemotePlayer] Disabling visual/collider: {child.name}");
                    child.gameObject.SetActive(false);
                }
                
                // CRITICAL: Disable/destroy battery objects that come with the hand clone
                // We create our own battery objects in SetupBatteryVisuals
                // The actual battery model is called "handEnergyCell"
                if (nameLower.Contains("battery") || nameLower.Contains("energycell"))
                {
                    Plugin.LogDebug($"[RemotePlayer] Destroying cloned battery/energycell from hand: {child.name}");
                    Object.Destroy(child.gameObject);
                }
                
                // Destroy Canvas (UI elements like minimap, icons) - we don't need these on remote player
                if (nameLower == "canvas")
                {
                    Plugin.LogDebug($"[RemotePlayer] Destroying cloned Canvas from hand: {child.name}");
                    Object.Destroy(child.gameObject);
                }
            }
            
            // Also check for any small sphere/cube primitives that might be debug visuals
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
            {
                // Check if it's a very small object (likely a debug indicator)
                if (renderer.bounds.size.magnitude < 0.05f)
                {
                    string nameLower = renderer.gameObject.name.ToLower();
                    // Skip the actual hand mesh
                    if (!nameLower.Contains("hand") && !nameLower.Contains("geom"))
                    {
                        Plugin.LogDebug($"[RemotePlayer] Disabling small renderer (possible indicator): {renderer.gameObject.name}, size={renderer.bounds.size.magnitude}");
                        renderer.enabled = false;
                    }
                }
            }
            
            // Make sure renderers are enabled (except indicators we just disabled)
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>())
            {
                // Skip if parent is disabled (like indicators)
                if (renderer.gameObject.activeInHierarchy)
                    renderer.enabled = true;
            }
            
            return clone;
        }
        
        private void CreateFallbackHands()
        {
            // Left hand - VISIBLE cyan sphere so we can see where hands are
            LeftHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            LeftHand.name = $"RemotePlayer_{PeerId}_LeftHand";
            LeftHand.transform.localScale = new Vector3(0.08f, 0.08f, 0.08f);
            Object.Destroy(LeftHand.GetComponent<Collider>());
            SetColor(LeftHand, new Color(0f, 0.8f, 1f, 0.8f)); // Cyan
            
            // Right hand - VISIBLE cyan sphere
            RightHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            RightHand.name = $"RemotePlayer_{PeerId}_RightHand";
            RightHand.transform.localScale = new Vector3(0.08f, 0.08f, 0.08f);
            Object.Destroy(RightHand.GetComponent<Collider>());
            SetColor(RightHand, new Color(0f, 0.8f, 1f, 0.8f)); // Cyan
            
            _usingRealHands = false;
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Using fallback hand spheres");
        }
        
        private void SetupFlashlight()
        {
            // Flashlight on HEAD (not hand) - this is how Crawlspace 2 works
            var flashlightObj = new GameObject("Flashlight");
            flashlightObj.transform.SetParent(Head.transform);
            flashlightObj.transform.localPosition = new Vector3(0, 0, 0.15f); // Slightly in front of head
            flashlightObj.transform.localRotation = Quaternion.identity;
            
            Flashlight = flashlightObj.AddComponent<Light>();
            Flashlight.type = LightType.Spot;
            Flashlight.color = new Color(1f, 0.95f, 0.8f); // Warm white
            Flashlight.intensity = 0.8f;
            Flashlight.range = 8f;
            Flashlight.spotAngle = 35f;
            Flashlight.innerSpotAngle = 20f;
            Flashlight.shadows = LightShadows.None;
            Flashlight.enabled = false;
            
            // Visual cone for flashlight - attached to head
            FlashlightCone = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            FlashlightCone.name = "FlashlightCone";
            FlashlightCone.transform.SetParent(Head.transform);
            FlashlightCone.transform.localPosition = new Vector3(0, 0, 0.4f);
            FlashlightCone.transform.localRotation = Quaternion.Euler(90, 0, 0);
            FlashlightCone.transform.localScale = new Vector3(0.15f, 0.25f, 0.15f);
            Object.Destroy(FlashlightCone.GetComponent<Collider>());
            SetColor(FlashlightCone, new Color(1f, 1f, 0.7f, 0.15f));
            FlashlightCone.SetActive(false);
        }
        
        private void SetupBatteryVisuals()
        {
            var backpack = Object.FindObjectOfType<BackpackControl>();
            
            if (backpack != null && backpack.batteryLeft != null && backpack.batteryRight != null)
            {
                // Clone the actual battery models
                LeftBattery = Object.Instantiate(backpack.batteryLeft);
                LeftBattery.name = $"RemotePlayer_{PeerId}_LeftBattery";
                LeftBattery.transform.SetParent(LeftHand.transform, false);
                foreach (var col in LeftBattery.GetComponentsInChildren<Collider>())
                    Object.Destroy(col);
                foreach (var mb in LeftBattery.GetComponentsInChildren<MonoBehaviour>())
                    Object.Destroy(mb);
                
                RightBattery = Object.Instantiate(backpack.batteryRight);
                RightBattery.name = $"RemotePlayer_{PeerId}_RightBattery";
                RightBattery.transform.SetParent(RightHand.transform, false);
                foreach (var col in RightBattery.GetComponentsInChildren<Collider>())
                    Object.Destroy(col);
                foreach (var mb in RightBattery.GetComponentsInChildren<MonoBehaviour>())
                    Object.Destroy(mb);
                
                // IMPORTANT: Force both batteries to be INACTIVE and disable all renderers
                LeftBattery.SetActive(false);
                RightBattery.SetActive(false);
                
                // Also explicitly disable all renderers to be safe
                foreach (var renderer in LeftBattery.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = false;
                foreach (var renderer in RightBattery.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = false;
                
                Plugin.LogDebug($"[RemotePlayer {PeerId}] Cloned battery models - L.active={LeftBattery.activeSelf}, R.active={RightBattery.activeSelf}");
            }
            else
            {
                // Fallback to simple cubes
                LeftBattery = GameObject.CreatePrimitive(PrimitiveType.Cube);
                LeftBattery.name = $"RemotePlayer_{PeerId}_LeftBattery";
                LeftBattery.transform.SetParent(LeftHand.transform);
                LeftBattery.transform.localPosition = new Vector3(0, 0, 0.05f);
                LeftBattery.transform.localScale = new Vector3(0.04f, 0.06f, 0.08f);
                Object.Destroy(LeftBattery.GetComponent<Collider>());
                SetColor(LeftBattery, new Color(1f, 0.5f, 0f));
                LeftBattery.SetActive(false);
                
                RightBattery = GameObject.CreatePrimitive(PrimitiveType.Cube);
                RightBattery.name = $"RemotePlayer_{PeerId}_RightBattery";
                RightBattery.transform.SetParent(RightHand.transform);
                RightBattery.transform.localPosition = new Vector3(0, 0, 0.05f);
                RightBattery.transform.localScale = new Vector3(0.04f, 0.06f, 0.08f);
                Object.Destroy(RightBattery.GetComponent<Collider>());
                SetColor(RightBattery, new Color(1f, 0.5f, 0f));
                RightBattery.SetActive(false);
                
                Plugin.LogDebug($"[RemotePlayer {PeerId}] Created fallback battery cubes");
            }
        }
        
        // Called after scene load to try upgrading placeholder visuals to real ones
        public void TryUpgradeVisuals()
        {
            TryUpgradeHandVisuals();
        }
        
        private void TryUpgradeHandVisuals()
        {
            if (_usingRealHands) return; // Already using real hands
            
            GameObject leftHandSource = null;
            GameObject rightHandSource = null;
            
            // Try Hand components first
            var hands = Object.FindObjectsOfType<Hand>();
            if (hands.Length >= 2)
            {
                foreach (var hand in hands)
                {
                    string nameLower = hand.gameObject.name.ToLower();
                    if (nameLower.Contains("left") || nameLower.Contains("l_"))
                        leftHandSource = hand.gameObject;
                    else if (nameLower.Contains("right") || nameLower.Contains("r_"))
                        rightHandSource = hand.gameObject;
                }
                
                if (leftHandSource == null || rightHandSource == null)
                {
                    leftHandSource = hands[0].gameObject;
                    rightHandSource = hands[1].gameObject;
                }
            }
            
            // Try BackpackControl children
            if (leftHandSource == null || rightHandSource == null)
            {
                var backpack = Object.FindObjectOfType<BackpackControl>();
                if (backpack != null)
                {
                    // Try OVR Model Parent first (Quest hands)
                    if (backpack.leftHand != null && leftHandSource == null)
                    {
                        var modelParent = backpack.leftHand.transform.Find("[LeftHand Controller] Model Parent");
                        if (modelParent != null)
                        {
                            foreach (var renderer in modelParent.GetComponentsInChildren<Renderer>(true))
                            {
                                if (renderer.gameObject.activeInHierarchy || renderer.enabled)
                                {
                                    leftHandSource = renderer.gameObject;
                                    Plugin.LogDebug($"[RemotePlayer {PeerId}] TryUpgrade: Found left hand in OVR Model Parent: {renderer.gameObject.name}");
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (backpack.rightHand != null && rightHandSource == null)
                    {
                        var modelParent = backpack.rightHand.transform.Find("[RightHand Controller] Model Parent");
                        if (modelParent != null)
                        {
                            foreach (var renderer in modelParent.GetComponentsInChildren<Renderer>(true))
                            {
                                if (renderer.gameObject.activeInHierarchy || renderer.enabled)
                                {
                                    rightHandSource = renderer.gameObject;
                                    Plugin.LogDebug($"[RemotePlayer {PeerId}] TryUpgrade: Found right hand in OVR Model Parent: {renderer.gameObject.name}");
                                    break;
                                }
                            }
                        }
                    }
                    
                    // Fallback to FindHandMeshInChildren
                    if (backpack.leftHand != null && leftHandSource == null)
                        leftHandSource = FindHandMeshInChildren(backpack.leftHand.transform, "left");
                    if (backpack.rightHand != null && rightHandSource == null)
                        rightHandSource = FindHandMeshInChildren(backpack.rightHand.transform, "right");
                }
            }
            
            if (leftHandSource == null || rightHandSource == null) return;
            
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Upgrading placeholder hands to real models");
            
            // Save current positions
            Vector3 leftPos = LeftHand.transform.position;
            Quaternion leftRot = LeftHand.transform.rotation;
            Vector3 rightPos = RightHand.transform.position;
            Quaternion rightRot = RightHand.transform.rotation;
            
            // Unparent batteries first
            if (LeftBattery != null) LeftBattery.transform.SetParent(null);
            if (RightBattery != null) RightBattery.transform.SetParent(null);
            
            // Destroy old placeholders
            Object.Destroy(LeftHand);
            Object.Destroy(RightHand);
            
            // Clone real hands
            LeftHand = CloneHandMesh(leftHandSource, $"RemotePlayer_{PeerId}_LeftHand");
            RightHand = CloneHandMesh(rightHandSource, $"RemotePlayer_{PeerId}_RightHand");
            
            // Restore positions
            LeftHand.transform.position = leftPos;
            LeftHand.transform.rotation = leftRot;
            RightHand.transform.position = rightPos;
            RightHand.transform.rotation = rightRot;
            
            // Re-parent batteries
            if (LeftBattery != null) LeftBattery.transform.SetParent(LeftHand.transform);
            if (RightBattery != null) RightBattery.transform.SetParent(RightHand.transform);
            
            _usingRealHands = true;
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Hand visuals upgraded to real models");
        }

        private void SetColor(GameObject obj, Color color)
        {
            var renderer = obj.GetComponent<Renderer>();
            if (renderer != null)
            {
                // Try multiple shader options
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Diffuse");
                if (shader == null) shader = Shader.Find("Mobile/Diffuse");
                
                if (shader != null)
                {
                    renderer.material = new Material(shader);
                    renderer.material.color = color;
                }
                else
                {
                    // Just use the default material and set color
                    renderer.material.color = color;
                }
                
                // Make sure it renders
                renderer.enabled = true;
            }
            else
            {
                Plugin.LogDebug($"No renderer on {obj.name}");
            }
        }

        public void SetTargets(bool isStanding, Vector3 bodyPos, Quaternion bodyRot, 
                               Vector3 headPos, Quaternion headRot,
                               Vector3 leftHandPos, Quaternion leftHandRot,
                               Vector3 rightHandPos, Quaternion rightHandRot)
        {
            _isStanding = isStanding;
            _targetHeadPos = headPos;
            _targetHeadRot = headRot;
            _targetLeftHandPos = leftHandPos;
            _targetLeftHandRot = leftHandRot;
            _targetRightHandPos = rightHandPos;
            _targetRightHandRot = rightHandRot;
            
            if (!_hasReceivedData)
            {
                _hasReceivedData = true;
                Plugin.LogDebug($"[RemotePlayer {PeerId}] First data! Head={headPos}, LHand={leftHandPos}, RHand={rightHandPos}");
                
                // Snap to position immediately on first data
                Head.transform.position = headPos;
                Head.transform.rotation = headRot;
                LeftHand.transform.position = leftHandPos;
                LeftHand.transform.rotation = leftHandRot;
                RightHand.transform.position = rightHandPos;
                RightHand.transform.rotation = rightHandRot;
            }
        }

        public void UpdateInterpolation()
        {
            if (Head == null) return;
            if (!_hasReceivedData) return;
            
            _logCounter++;
            
            // Use much faster interpolation
            float t = Mathf.Clamp01(Time.deltaTime * _lerpSpeed);
            
            // If too far from target, snap immediately (teleport threshold)
            float headDist = Vector3.Distance(Head.transform.position, _targetHeadPos);
            if (headDist > 0.5f)
            {
                // Snap to target immediately
                Head.transform.position = _targetHeadPos;
                Head.transform.rotation = _targetHeadRot;
                LeftHand.transform.position = _targetLeftHandPos;
                LeftHand.transform.rotation = _targetLeftHandRot;
                RightHand.transform.position = _targetRightHandPos;
                RightHand.transform.rotation = _targetRightHandRot;
                
                if (_logCounter % 60 == 0)
                    Plugin.LogDebug($"[RemotePlayer {PeerId}] SNAPPED (dist={headDist:F2})");
                return;
            }
            
            // HEAD
            Head.transform.position = Vector3.Lerp(Head.transform.position, _targetHeadPos, t);
            Head.transform.rotation = Quaternion.Slerp(Head.transform.rotation, _targetHeadRot, t);
            
            // Hands - use even faster lerp for hands (they move quickly in VR)
            float handT = Mathf.Clamp01(Time.deltaTime * _lerpSpeed * 1.5f);
            LeftHand.transform.position = Vector3.Lerp(LeftHand.transform.position, _targetLeftHandPos, handT);
            LeftHand.transform.rotation = Quaternion.Slerp(LeftHand.transform.rotation, _targetLeftHandRot, handT);
            RightHand.transform.position = Vector3.Lerp(RightHand.transform.position, _targetRightHandPos, handT);
            RightHand.transform.rotation = Quaternion.Slerp(RightHand.transform.rotation, _targetRightHandRot, handT);
            
            // Log occasionally with hand positions
            if (_logCounter % 300 == 0)
            {
                Plugin.LogDebug($"[RemotePlayer {PeerId}] Head={Head.transform.position}, LHand={LeftHand.transform.position}, RHand={RightHand.transform.position}");
            }
        }

        public void SetFlashlightState(bool isOn)
        {
            if (Flashlight != null)
            {
                Flashlight.enabled = isOn;
            }
            if (FlashlightCone != null)
            {
                FlashlightCone.SetActive(isOn);
            }
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Flashlight set to {isOn}");
        }
        
        public void SetBatteryState(bool leftHolding, bool rightHolding)
        {
            if (LeftBattery != null)
            {
                LeftBattery.SetActive(leftHolding);
                // Also explicitly control renderers
                foreach (var renderer in LeftBattery.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = leftHolding;
            }
            if (RightBattery != null)
            {
                RightBattery.SetActive(rightHolding);
                // Also explicitly control renderers
                foreach (var renderer in RightBattery.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = rightHolding;
            }
            
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Battery state: L={leftHolding}, R={rightHolding}");
        }
        
        public void SetGhostState(bool isGhost)
        {
            _isGhost = isGhost;
            
            // Make the player semi-transparent when they're a ghost
            float alpha = isGhost ? 0.4f : 1f;
            
            // Update head transparency
            SetObjectTransparency(Head, alpha);
            SetObjectTransparency(FaceIndicator, alpha);
            SetObjectTransparency(LeftHand, alpha);
            SetObjectTransparency(RightHand, alpha);
            
            Plugin.LogDebug($"[RemotePlayer {PeerId}] Ghost state set to {isGhost} (alpha={alpha})");
        }
        
        private void SetObjectTransparency(GameObject obj, float alpha)
        {
            if (obj == null) return;
            
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>())
            {
                if (renderer.material != null)
                {
                    // Enable transparency
                    Color color = renderer.material.color;
                    color.a = alpha;
                    renderer.material.color = color;
                    
                    // Set rendering mode to transparent if needed
                    if (alpha < 1f)
                    {
                        renderer.material.SetFloat("_Mode", 3); // Transparent mode
                        renderer.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        renderer.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        renderer.material.SetInt("_ZWrite", 0);
                        renderer.material.DisableKeyword("_ALPHATEST_ON");
                        renderer.material.EnableKeyword("_ALPHABLEND_ON");
                        renderer.material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        renderer.material.renderQueue = 3000;
                    }
                    else
                    {
                        renderer.material.SetFloat("_Mode", 0); // Opaque mode
                        renderer.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                        renderer.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                        renderer.material.SetInt("_ZWrite", 1);
                        renderer.material.DisableKeyword("_ALPHATEST_ON");
                        renderer.material.DisableKeyword("_ALPHABLEND_ON");
                        renderer.material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                        renderer.material.renderQueue = -1;
                    }
                }
            }
        }
        
        public void Destroy()
        {
            if (Head != null) Object.Destroy(Head);
            if (FaceIndicator != null) Object.Destroy(FaceIndicator);
            if (LeftHand != null) Object.Destroy(LeftHand);
            if (RightHand != null) Object.Destroy(RightHand);
            if (LeftBattery != null) Object.Destroy(LeftBattery);
            if (RightBattery != null) Object.Destroy(RightBattery);
        }
    }
}
