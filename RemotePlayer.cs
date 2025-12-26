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
            // Head - sphere (BRIGHT YELLOW)
            Head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Head.name = $"RemotePlayer_{PeerId}_Head";
            Head.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            Object.Destroy(Head.GetComponent<Collider>());
            SetColor(Head, Color.yellow);
            
            // Face indicator - small sphere on front of head (WHITE "nose")
            FaceIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            FaceIndicator.name = "FaceIndicator";
            FaceIndicator.transform.SetParent(Head.transform);
            FaceIndicator.transform.localPosition = new Vector3(0, 0, 0.5f); // In front of head
            FaceIndicator.transform.localScale = new Vector3(0.3f, 0.3f, 0.5f); // Slightly elongated forward
            Object.Destroy(FaceIndicator.GetComponent<Collider>());
            SetColor(FaceIndicator, Color.white);
            
            // Try to clone real hand models, fall back to spheres
            SetupHandVisuals();
            
            // Setup flashlight on head
            SetupFlashlight();
            
            // Setup battery visuals (attached to hands)
            SetupBatteryVisuals();
            
            // Start at origin - will move when data arrives
            Head.transform.position = Vector3.zero;
            LeftHand.transform.position = Vector3.zero;
            RightHand.transform.position = Vector3.zero;
            
            Plugin.Log.LogInfo($"Created remote player visual for peer {PeerId}");
        }
        
        private void SetupHandVisuals()
        {
            // The game's hand meshes are:
            // Left: leftcontrollerv2 -> offsettu -> customhandleft
            // Right: rightconv2 -> offsettu -> customhandright
            
            GameObject leftHandSource = null;
            GameObject rightHandSource = null;
            
            // First try: Find by exact names "customhandleft" and "customhandright"
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Searching for customhandleft and customhandright...");
            
            var allTransforms = Object.FindObjectsOfType<Transform>(true);
            foreach (var t in allTransforms)
            {
                string nameLower = t.name.ToLower();
                
                if (nameLower == "customhandleft" && leftHandSource == null)
                {
                    leftHandSource = t.gameObject;
                    Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Found customhandleft!");
                }
                else if (nameLower == "customhandright" && rightHandSource == null)
                {
                    rightHandSource = t.gameObject;
                    Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Found customhandright!");
                }
                
                if (leftHandSource != null && rightHandSource != null)
                    break;
            }
            
            // Second try: Search via BackpackControl hierarchy
            if (leftHandSource == null || rightHandSource == null)
            {
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Searching via BackpackControl hierarchy...");
                var backpack = Object.FindObjectOfType<BackpackControl>();
                if (backpack != null)
                {
                    // Left hand: leftHand -> offsettu -> customhandleft
                    if (backpack.leftHand != null && leftHandSource == null)
                    {
                        var offsettu = backpack.leftHand.transform.Find("offsettu");
                        if (offsettu != null)
                        {
                            var customHand = offsettu.Find("customhandleft");
                            if (customHand != null)
                            {
                                leftHandSource = customHand.gameObject;
                                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Found left hand via hierarchy: {leftHandSource.name}");
                            }
                        }
                        
                        // Also try direct child search
                        if (leftHandSource == null)
                        {
                            leftHandSource = FindChildByNameContains(backpack.leftHand.transform, "customhand");
                        }
                    }
                    
                    // Right hand: rightHand -> offsettu -> customhandright
                    if (backpack.rightHand != null && rightHandSource == null)
                    {
                        var offsettu = backpack.rightHand.transform.Find("offsettu");
                        if (offsettu != null)
                        {
                            var customHand = offsettu.Find("customhandright");
                            if (customHand != null)
                            {
                                rightHandSource = customHand.gameObject;
                                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Found right hand via hierarchy: {rightHandSource.name}");
                            }
                        }
                        
                        // Also try direct child search
                        if (rightHandSource == null)
                        {
                            rightHandSource = FindChildByNameContains(backpack.rightHand.transform, "customhand");
                        }
                    }
                }
            }
            
            // Third try: Find any GameObject with "customhand" in name
            if (leftHandSource == null || rightHandSource == null)
            {
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Searching for any customhand objects...");
                foreach (var t in allTransforms)
                {
                    string nameLower = t.name.ToLower();
                    if (!nameLower.Contains("customhand"))
                        continue;
                    
                    Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Found: {t.name} (parent: {(t.parent != null ? t.parent.name : "none")})");
                    
                    if (nameLower.Contains("left") && leftHandSource == null)
                    {
                        leftHandSource = t.gameObject;
                    }
                    else if (nameLower.Contains("right") && rightHandSource == null)
                    {
                        rightHandSource = t.gameObject;
                    }
                }
            }
            
            // Clone the hand meshes if found
            if (leftHandSource != null && rightHandSource != null)
            {
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Cloning hand models: L={leftHandSource.name}, R={rightHandSource.name}");
                LeftHand = CloneHandMesh(leftHandSource, $"RemotePlayer_{PeerId}_LeftHand");
                RightHand = CloneHandMesh(rightHandSource, $"RemotePlayer_{PeerId}_RightHand");
                
                _usingRealHands = true;
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Successfully cloned real hand models!");
                return;
            }
            
            Plugin.Log.LogWarning($"[RemotePlayer {PeerId}] Could not find hand meshes (L={leftHandSource != null}, R={rightHandSource != null}), using fallback spheres");
            CreateFallbackHands();
        }
        
        private GameObject FindChildByNameContains(Transform parent, string nameContains)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.ToLower().Contains(nameContains.ToLower()))
                {
                    Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] FindChildByNameContains found: {child.name}");
                    return child.gameObject;
                }
            }
            return null;
        }
        
        private GameObject FindHandMeshInChildren(Transform parent, string handSide)
        {
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Searching children of {parent.name} for {handSide} hand mesh...");
            
            // Search all descendants for a mesh renderer
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
            {
                string nameLower = renderer.gameObject.name.ToLower();
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}]   Found renderer: {renderer.gameObject.name}");
                
                // Skip if it's clearly not a hand (like UI elements)
                if (nameLower.Contains("ui") || nameLower.Contains("canvas") || nameLower.Contains("text"))
                    continue;
                
                // If it has "hand" in name or is a skinned mesh, it's likely the hand
                if (nameLower.Contains("hand") || nameLower.Contains("palm") || nameLower.Contains("glove") ||
                    renderer is SkinnedMeshRenderer)
                {
                    Plugin.Log.LogInfo($"[RemotePlayer {PeerId}]   -> Using as {handSide} hand mesh!");
                    return renderer.gameObject;
                }
            }
            
            // If no specific hand mesh found, try to find any mesh that's not a controller model
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer || renderer.GetComponent<MeshFilter>() != null)
                {
                    Plugin.Log.LogInfo($"[RemotePlayer {PeerId}]   -> Fallback: using {renderer.gameObject.name} as {handSide} hand");
                    return renderer.gameObject;
                }
            }
            
            return null;
        }
        
        private GameObject CloneHandMesh(GameObject source, string newName)
        {
            var clone = Object.Instantiate(source);
            clone.name = newName;
            
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
            
            // Make sure renderers are enabled
            foreach (var renderer in clone.GetComponentsInChildren<Renderer>())
                renderer.enabled = true;
            
            return clone;
        }
        
        private void CreateFallbackHands()
        {
            // Left hand - sphere (BRIGHT GREEN)
            LeftHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            LeftHand.name = $"RemotePlayer_{PeerId}_LeftHand";
            LeftHand.transform.localScale = new Vector3(0.12f, 0.12f, 0.12f);
            Object.Destroy(LeftHand.GetComponent<Collider>());
            SetColor(LeftHand, Color.green);
            
            // Right hand - sphere (BRIGHT RED)
            RightHand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            RightHand.name = $"RemotePlayer_{PeerId}_RightHand";
            RightHand.transform.localScale = new Vector3(0.12f, 0.12f, 0.12f);
            Object.Destroy(RightHand.GetComponent<Collider>());
            SetColor(RightHand, Color.red);
            
            _usingRealHands = false;
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
            // Try to clone the actual battery model from BackpackControl
            var backpack = Object.FindObjectOfType<BackpackControl>();
            
            if (backpack != null && backpack.batteryLeft != null && backpack.batteryRight != null)
            {
                // Clone the real battery model for left hand - use batteryLeft, scaled up 2.5x
                LeftBattery = Object.Instantiate(backpack.batteryLeft);
                LeftBattery.name = $"RemotePlayer_{PeerId}_LeftBattery";
                LeftBattery.transform.SetParent(LeftHand.transform);
                LeftBattery.transform.localPosition = Vector3.zero;
                LeftBattery.transform.localRotation = Quaternion.identity;
                LeftBattery.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f); // 2.5x bigger
                // Remove any colliders from the clone
                foreach (var col in LeftBattery.GetComponentsInChildren<Collider>())
                {
                    Object.Destroy(col);
                }
                LeftBattery.SetActive(false);
                
                // Clone the real battery model for right hand - use batteryRight, scaled up 2.5x
                RightBattery = Object.Instantiate(backpack.batteryRight);
                RightBattery.name = $"RemotePlayer_{PeerId}_RightBattery";
                RightBattery.transform.SetParent(RightHand.transform);
                RightBattery.transform.localPosition = Vector3.zero;
                RightBattery.transform.localRotation = Quaternion.identity;
                RightBattery.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f); // 2.5x bigger
                // Remove any colliders from the clone
                foreach (var col in RightBattery.GetComponentsInChildren<Collider>())
                {
                    Object.Destroy(col);
                }
                RightBattery.SetActive(false);
                
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Cloned real battery models for hand visuals (2.5x scale)");
            }
            else if (backpack != null && backpack.batteryRight != null)
            {
                // Fallback: use batteryRight for both if batteryLeft is null
                LeftBattery = Object.Instantiate(backpack.batteryRight);
                LeftBattery.name = $"RemotePlayer_{PeerId}_LeftBattery";
                LeftBattery.transform.SetParent(LeftHand.transform);
                LeftBattery.transform.localPosition = Vector3.zero;
                LeftBattery.transform.localRotation = Quaternion.identity;
                LeftBattery.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f);
                foreach (var col in LeftBattery.GetComponentsInChildren<Collider>())
                    Object.Destroy(col);
                LeftBattery.SetActive(false);
                
                RightBattery = Object.Instantiate(backpack.batteryRight);
                RightBattery.name = $"RemotePlayer_{PeerId}_RightBattery";
                RightBattery.transform.SetParent(RightHand.transform);
                RightBattery.transform.localPosition = Vector3.zero;
                RightBattery.transform.localRotation = Quaternion.identity;
                RightBattery.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f);
                foreach (var col in RightBattery.GetComponentsInChildren<Collider>())
                    Object.Destroy(col);
                RightBattery.SetActive(false);
                
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Cloned batteryRight for both hands (2.5x scale)");
            }
            else
            {
                // Fallback to orange cubes if we can't find the real battery
                Plugin.Log.LogWarning($"[RemotePlayer {PeerId}] Could not find BackpackControl.batteryRight, using fallback cubes");
                
                // Left hand battery - orange cube fallback (bigger)
                LeftBattery = GameObject.CreatePrimitive(PrimitiveType.Cube);
                LeftBattery.name = "LeftBattery";
                LeftBattery.transform.SetParent(LeftHand.transform);
                LeftBattery.transform.localPosition = new Vector3(0, 0, 0.15f);
                LeftBattery.transform.localScale = new Vector3(0.15f, 0.22f, 0.28f); // 2.5x bigger
                Object.Destroy(LeftBattery.GetComponent<Collider>());
                SetColor(LeftBattery, new Color(1f, 0.5f, 0f)); // Orange
                LeftBattery.SetActive(false);
                
                // Right hand battery - orange cube fallback (bigger)
                RightBattery = GameObject.CreatePrimitive(PrimitiveType.Cube);
                RightBattery.name = "RightBattery";
                RightBattery.transform.SetParent(RightHand.transform);
                RightBattery.transform.localPosition = new Vector3(0, 0, 0.15f);
                RightBattery.transform.localScale = new Vector3(0.15f, 0.22f, 0.28f); // 2.5x bigger
                Object.Destroy(RightBattery.GetComponent<Collider>());
                SetColor(RightBattery, new Color(1f, 0.5f, 0f)); // Orange
                RightBattery.SetActive(false);
            }
        }
        
        // Called after scene load to try upgrading placeholder visuals to real ones
        public void TryUpgradeVisuals()
        {
            TryUpgradeHandVisuals();
            TryUpgradeBatteryVisuals();
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
                    if (backpack.leftHand != null && leftHandSource == null)
                        leftHandSource = FindHandMeshInChildren(backpack.leftHand.transform, "left");
                    if (backpack.rightHand != null && rightHandSource == null)
                        rightHandSource = FindHandMeshInChildren(backpack.rightHand.transform, "right");
                }
            }
            
            if (leftHandSource == null || rightHandSource == null) return;
            
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Upgrading placeholder hands to real models");
            
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
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Hand visuals upgraded to real models");
        }
        
        private void TryUpgradeBatteryVisuals()
        {
            var backpack = Object.FindObjectOfType<BackpackControl>();
            if (backpack == null || backpack.batteryRight == null) return;
            
            // Check if we're using placeholder cubes (they have MeshFilter with cube mesh)
            bool needsUpgrade = false;
            if (LeftBattery != null)
            {
                var meshFilter = LeftBattery.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null && meshFilter.sharedMesh.name.Contains("Cube"))
                {
                    needsUpgrade = true;
                }
            }
            
            if (!needsUpgrade) return;
            
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Upgrading placeholder batteries to real model");
            
            bool leftWasActive = LeftBattery != null && LeftBattery.activeSelf;
            bool rightWasActive = RightBattery != null && RightBattery.activeSelf;
            
            // Destroy old placeholders
            if (LeftBattery != null) Object.Destroy(LeftBattery);
            if (RightBattery != null) Object.Destroy(RightBattery);
            
            // Clone real batteries with 2.5x scale - use correct source for each hand
            GameObject leftSource = backpack.batteryLeft != null ? backpack.batteryLeft : backpack.batteryRight;
            LeftBattery = Object.Instantiate(leftSource);
            LeftBattery.name = $"RemotePlayer_{PeerId}_LeftBattery";
            LeftBattery.transform.SetParent(LeftHand.transform);
            LeftBattery.transform.localPosition = Vector3.zero;
            LeftBattery.transform.localRotation = Quaternion.identity;
            LeftBattery.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f);
            foreach (var col in LeftBattery.GetComponentsInChildren<Collider>())
            {
                Object.Destroy(col);
            }
            LeftBattery.SetActive(leftWasActive);
            
            RightBattery = Object.Instantiate(backpack.batteryRight);
            RightBattery.name = $"RemotePlayer_{PeerId}_RightBattery";
            RightBattery.transform.SetParent(RightHand.transform);
            RightBattery.transform.localPosition = Vector3.zero;
            RightBattery.transform.localRotation = Quaternion.identity;
            RightBattery.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f);
            foreach (var col in RightBattery.GetComponentsInChildren<Collider>())
            {
                Object.Destroy(col);
            }
            RightBattery.SetActive(rightWasActive);
            
            _usingRealBatteries = true;
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Battery visuals upgraded to real model (2.5x scale)");
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
                    Plugin.Log.LogInfo($"Set {obj.name} color to {color} using shader {shader.name}");
                }
                else
                {
                    // Just use the default material and set color
                    renderer.material.color = color;
                    Plugin.Log.LogWarning($"No shader found, using default material for {obj.name}");
                }
                
                // Make sure it renders
                renderer.enabled = true;
            }
            else
            {
                Plugin.Log.LogError($"No renderer on {obj.name}!");
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
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] First data! Head={headPos}, LHand={leftHandPos}, RHand={rightHandPos}");
                
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
                    Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] SNAPPED (dist={headDist:F2})");
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
                Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Head={Head.transform.position}, LHand={LeftHand.transform.position}, RHand={RightHand.transform.position}");
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
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Flashlight set to {isOn}");
        }
        
        public void SetBatteryState(bool leftHolding, bool rightHolding)
        {
            if (LeftBattery != null)
            {
                LeftBattery.SetActive(leftHolding);
            }
            if (RightBattery != null)
            {
                RightBattery.SetActive(rightHolding);
            }
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
            
            Plugin.Log.LogInfo($"[RemotePlayer {PeerId}] Ghost state set to {isGhost} (alpha={alpha})");
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
