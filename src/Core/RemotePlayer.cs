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
        
        // Retry mechanism for hand mesh upgrade
        private float _handUpgradeRetryTime = 0f;
        private int _handUpgradeRetryCount = 0;
        private const int MAX_HAND_UPGRADE_RETRIES = 100; // Increased from 30 - try for ~10 seconds
        private const float HAND_UPGRADE_RETRY_INTERVAL = 0.1f;
        
        // Ghost state
        private bool _isGhost = false;
        public bool IsGhost => _isGhost;
        
        // Hand pose state (for animation)
        private float _targetLeftGrip = 0f;
        private float _targetLeftTrigger = 0f;
        private float _targetRightGrip = 0f;
        private float _targetRightTrigger = 0f;
        private Animator _leftHandAnimator;
        private Animator _rightHandAnimator;
        private int _animParamFlex = -1;
        private int _animParamPinch = -1;
        
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
        }
        
        private void SetupHandVisuals()
        {
            // Try to find hand meshes immediately, fall back to spheres if not ready
            // The upgrade retry system will keep trying to find real hands
            
            GameObject leftHandSource = null;
            GameObject rightHandSource = null;
            
            // Search the entire scene for hand objects by name
            var allTransforms = Object.FindObjectsOfType<Transform>(true);
            foreach (var t in allTransforms)
            {
                if (t.name == "CustomHandLeft" && leftHandSource == null)
                {
                    var parent = t.parent;
                    if (parent != null && parent.name.ToLower().Contains("offsettu"))
                        leftHandSource = parent.gameObject;
                    else
                        leftHandSource = t.gameObject;
                }
                else if (t.name == "CustomHandRight" && rightHandSource == null)
                {
                    var parent = t.parent;
                    if (parent != null && parent.name.ToLower().Contains("offsettu"))
                        rightHandSource = parent.gameObject;
                    else
                        rightHandSource = t.gameObject;
                }
            }
            
            // Verify renderers exist
            if (leftHandSource != null && rightHandSource != null)
            {
                bool hasLeftRenderer = leftHandSource.GetComponentInChildren<Renderer>(true) != null;
                bool hasRightRenderer = rightHandSource.GetComponentInChildren<Renderer>(true) != null;
                
                if (hasLeftRenderer && hasRightRenderer)
                {
                    LeftHand = CloneHandMesh(leftHandSource, $"RemotePlayer_{PeerId}_LeftHand");
                    RightHand = CloneHandMesh(rightHandSource, $"RemotePlayer_{PeerId}_RightHand");
                    _usingRealHands = true;
                    TryFindHandAnimators();
                    return;
                }
            }
            
            CreateFallbackHands();
        }
        
        private void LogHandHierarchyDetailed(Transform parent, int depth)
        {
            // Debug only - intentionally empty in release
        }
        
        private GameObject FindChildByNameContains(Transform parent, string nameContains)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.ToLower().Contains(nameContains.ToLower()))
                    return child.gameObject;
            }
            return null;
        }
        
        private GameObject FindHandMeshInChildren(Transform parent, string handSide)
        {
            // Search all descendants for a mesh renderer
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
            {
                string nameLower = renderer.gameObject.name.ToLower();
                
                // Skip if it's clearly not a hand (like UI elements)
                if (nameLower.Contains("ui") || nameLower.Contains("canvas") || nameLower.Contains("text"))
                    continue;
                
                // If it has "hand" in name or is a skinned mesh, it's likely the hand
                if (nameLower.Contains("hand") || nameLower.Contains("palm") || nameLower.Contains("glove") ||
                    renderer is SkinnedMeshRenderer)
                    return renderer.gameObject;
            }
            
            // If no specific hand mesh found, try to find any mesh that's not a controller model
            foreach (var renderer in parent.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer || renderer.GetComponent<MeshFilter>() != null)
                    return renderer.gameObject;
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
            
            // Remove MonoBehaviours that might interfere, but KEEP Animators (needed for hand pose sync)
            // Note: Animator inherits from Behaviour, not MonoBehaviour, so it won't be in this loop.
            // We explicitly preserve Animator by not destroying it separately.
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
                    child.gameObject.SetActive(false);
                }
                
                // CRITICAL: Disable/destroy battery objects that come with the hand clone
                // We create our own battery objects in SetupBatteryVisuals
                // The actual battery model is called "handEnergyCell"
                if (nameLower.Contains("battery") || nameLower.Contains("energycell"))
                {
                    Object.Destroy(child.gameObject);
                }
                
                // Destroy Canvas (UI elements like minimap, icons) - we don't need these on remote player
                if (nameLower == "canvas")
                {
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
            }
        }
        
        // Called after scene load to try upgrading placeholder visuals to real ones
        public void TryUpgradeVisuals()
        {
            // Reset retry counter on scene load to give it a fresh chance
            _handUpgradeRetryCount = 0;
            _handUpgradeRetryTime = Time.time;
            
            TryUpgradeHandVisuals();
        }
        
        // Called from UpdateInterpolation to keep retrying hand upgrade
        private void TryHandUpgradeWithRetry()
        {
            if (_usingRealHands) return; // Already using real hands
            
            // Give up after max retries
            if (_handUpgradeRetryCount >= MAX_HAND_UPGRADE_RETRIES)
            {
                return;
            }
            
            // Only retry every HAND_UPGRADE_RETRY_INTERVAL seconds
            if (Time.time - _handUpgradeRetryTime < HAND_UPGRADE_RETRY_INTERVAL) return;
            
            _handUpgradeRetryTime = Time.time;
            _handUpgradeRetryCount++;
            
            TryUpgradeHandVisuals();
        }
        
        private void TryFindHandAnimators()
        {
            // Try to find Animator components on the hand objects
            if (LeftHand != null && _leftHandAnimator == null)
            {
                _leftHandAnimator = LeftHand.GetComponentInChildren<Animator>();
            }
            
            if (RightHand != null && _rightHandAnimator == null)
            {
                _rightHandAnimator = RightHand.GetComponentInChildren<Animator>();
            }
            
            // Cache animator parameter hashes
            if ((_leftHandAnimator != null || _rightHandAnimator != null) && _animParamFlex == -1)
            {
                _animParamFlex = Animator.StringToHash("Flex");
                _animParamPinch = Animator.StringToHash("Pinch");
            }
        }
        
        private void TryUpgradeHandVisuals()
        {
            if (_usingRealHands) return; // Already using real hands
            
            GameObject leftHandSource = null;
            GameObject rightHandSource = null;
            
            // Strategy: Search the ENTIRE SCENE for hand mesh objects by name
            // The hand hierarchy is: offsettu -> CustomHandLeft -> (unnamed) -> l_hand_skeletal_lowres
            // But these may not be children of BackpackControl.leftHand
            
            // Search for CustomHandLeft/CustomHandRight first (most reliable)
            var allTransforms = Object.FindObjectsOfType<Transform>(true);
            foreach (var t in allTransforms)
            {
                if (t.name == "CustomHandLeft" && leftHandSource == null)
                {
                    var parent = t.parent;
                    if (parent != null && parent.name.ToLower().Contains("offsettu"))
                        leftHandSource = parent.gameObject;
                    else
                        leftHandSource = t.gameObject;
                }
                else if (t.name == "CustomHandRight" && rightHandSource == null)
                {
                    var parent = t.parent;
                    if (parent != null && parent.name.ToLower().Contains("offsettu"))
                        rightHandSource = parent.gameObject;
                    else
                        rightHandSource = t.gameObject;
                }
            }
            
            // Fallback: search for l_hand_skeletal_lowres / r_hand_skeletal_lowres
            if (leftHandSource == null || rightHandSource == null)
            {
                foreach (var t in allTransforms)
                {
                    string nameLower = t.name.ToLower();
                    if (nameLower.Contains("l_hand_skeletal") && leftHandSource == null)
                        leftHandSource = t.gameObject;
                    else if (nameLower.Contains("r_hand_skeletal") && rightHandSource == null)
                        rightHandSource = t.gameObject;
                }
            }
            
            // Fallback: try BackpackControl path
            if (leftHandSource == null || rightHandSource == null)
            {
                var backpack = Object.FindObjectOfType<BackpackControl>();
                if (backpack != null)
                {
                    if (leftHandSource == null && backpack.leftHand != null)
                    {
                        var offsettu = backpack.leftHand.transform.Find("offsettu");
                        if (offsettu != null)
                            leftHandSource = offsettu.gameObject;
                        else
                        {
                            var smr = backpack.leftHand.GetComponentInChildren<SkinnedMeshRenderer>(true);
                            if (smr != null)
                                leftHandSource = smr.gameObject;
                        }
                    }
                    if (rightHandSource == null && backpack.rightHand != null)
                    {
                        var offsettu = backpack.rightHand.transform.Find("offsettu");
                        if (offsettu != null)
                            rightHandSource = offsettu.gameObject;
                        else
                        {
                            var smr = backpack.rightHand.GetComponentInChildren<SkinnedMeshRenderer>(true);
                            if (smr != null)
                                rightHandSource = smr.gameObject;
                        }
                    }
                }
            }
            
            // Last resort: find any HandAnim components in the scene
            if (leftHandSource == null || rightHandSource == null)
            {
                var handAnims = Object.FindObjectsOfType<HandAnim>(true);
                foreach (var ha in handAnims)
                {
                    string nameLower = ha.gameObject.name.ToLower();
                    if ((nameLower.Contains("left") || nameLower.Contains("l_")) && leftHandSource == null)
                        leftHandSource = ha.gameObject;
                    else if ((nameLower.Contains("right") || nameLower.Contains("r_")) && rightHandSource == null)
                        rightHandSource = ha.gameObject;
                }
            }
            
            if (leftHandSource == null || rightHandSource == null)
                return;
            
            // Verify the source has renderers before proceeding
            bool hasLeftRenderer = leftHandSource.GetComponentInChildren<Renderer>(true) != null;
            bool hasRightRenderer = rightHandSource.GetComponentInChildren<Renderer>(true) != null;
            if (!hasLeftRenderer || !hasRightRenderer)
                return;
            
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
            
            // Try to find animators on the new hands
            TryFindHandAnimators();
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
                // No renderer on this object
            }
        }

        public void SetTargets(bool isStanding, Vector3 bodyPos, Quaternion bodyRot, 
                               Vector3 headPos, Quaternion headRot,
                               Vector3 leftHandPos, Quaternion leftHandRot,
                               Vector3 rightHandPos, Quaternion rightHandRot,
                               float leftGrip, float leftTrigger, float rightGrip, float rightTrigger)
        {
            _isStanding = isStanding;
            _targetHeadPos = headPos;
            _targetHeadRot = headRot;
            _targetLeftHandPos = leftHandPos;
            _targetLeftHandRot = leftHandRot;
            _targetRightHandPos = rightHandPos;
            _targetRightHandRot = rightHandRot;
            
            // Store hand pose targets
            _targetLeftGrip = leftGrip;
            _targetLeftTrigger = leftTrigger;
            _targetRightGrip = rightGrip;
            _targetRightTrigger = rightTrigger;
            
            if (!_hasReceivedData)
            {
                _hasReceivedData = true;
                
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
            
            // Try to upgrade hand visuals if still using placeholders
            TryHandUpgradeWithRetry();
            
            // Try to find hand animators if we haven't yet
            if (_animParamFlex == -1 && _usingRealHands)
            {
                TryFindHandAnimators();
            }
            
            // Apply hand poses to animators
            if (_leftHandAnimator != null && _animParamFlex != -1)
            {
                _leftHandAnimator.SetFloat(_animParamFlex, _targetLeftGrip);
                _leftHandAnimator.SetFloat(_animParamPinch, _targetLeftTrigger);
            }
            if (_rightHandAnimator != null && _animParamFlex != -1)
            {
                _rightHandAnimator.SetFloat(_animParamFlex, _targetRightGrip);
                _rightHandAnimator.SetFloat(_animParamPinch, _targetRightTrigger);
            }
            
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
