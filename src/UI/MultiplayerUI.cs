using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.XR;

namespace Crawlspace2MP
{
    /// <summary>
    /// World-space VR UI panel for multiplayer controls in the Home scene.
    /// Fully VR-native — laser pointer interaction, no keyboard needed.
    /// </summary>
    public class MultiplayerUI : MonoBehaviour
    {
        private Canvas _canvas;
        private GameObject _panelRoot;
        private MPManager _manager;

        // UI refs
        private Text _statusText;
        private Text _tutorialHint;
        private Text _playerNameText;
        private Text _connectedPlayersText;
        private Text _lobbyCodeText;
        private Button _hostButton;
        private Button _disconnectButton;
        private Button _showCodeButton;
        private Button _copyCodeButton;
        private Button _pasteJoinButton;
        private Button _voiceToggleButton;
        private Text _micInfoText;
        private GameObject _notConnectedPanel;
        private GameObject _connectedPanel;
        private GameObject _joiningPanel;
        private GameObject _lobbyCodePanel;

        // Friends list (not-connected: join)
        private GameObject _friendsPanel;
        private GameObject _friendsScrollContent;
        private ScrollRect _friendsScroll;
        private Text _friendsHeaderText;
        private List<GameObject> _friendEntries = new List<GameObject>();

        // Friends list (connected: invite)
        private GameObject _inviteFriendsPanel;
        private GameObject _inviteFriendsContent;
        private ScrollRect _inviteFriendsScroll;
        private Text _inviteHeaderText;
        private List<GameObject> _inviteEntries = new List<GameObject>();

        // State
        private bool _showLobbyCode = false;
        private float _copiedTime = -10f;
        private float _lastFriendsRefresh = 0f;
        private bool _initialized = false;
        private Font _cachedFont;
        private Dictionary<ulong, float> _inviteSentTime = new Dictionary<ulong, float>();
        private string _lastStatusMsg = "";
        private float _statusSetTime = 0f;

        // World-space canvas
        private const float CANVAS_W = 550f;
        private const float CANVAS_H = 800f;
        private const float WORLD_SCALE = 0.00092f;

        // Palette
        static readonly Color C_BG       = new Color(0.07f, 0.07f, 0.12f, 0.92f);
        static readonly Color C_HEADER   = new Color(0.11f, 0.13f, 0.22f, 1f);
        static readonly Color C_BTN      = new Color(0.22f, 0.36f, 0.58f, 1f);
        static readonly Color C_HOST     = new Color(0.16f, 0.48f, 0.26f, 1f);
        static readonly Color C_DC       = new Color(0.58f, 0.16f, 0.16f, 1f);
        static readonly Color C_INVITE   = new Color(0.22f, 0.42f, 0.68f, 1f);
        static readonly Color C_JOIN     = new Color(0.18f, 0.52f, 0.30f, 1f);
        static readonly Color C_TEXT     = new Color(0.92f, 0.92f, 0.96f, 1f);
        static readonly Color C_DIM      = new Color(0.50f, 0.50f, 0.55f, 1f);
        static readonly Color C_OK       = new Color(0.45f, 0.95f, 0.45f, 1f);
        static readonly Color C_WARN     = new Color(1f, 0.85f, 0.45f, 1f);
        static readonly Color C_ERR      = new Color(1f, 0.45f, 0.45f, 1f);
        static readonly Color C_ROW      = new Color(0.12f, 0.13f, 0.20f, 0.85f);
        static readonly Color C_ROW_ALT  = new Color(0.14f, 0.15f, 0.22f, 0.85f);
        static readonly Color C_LINE     = new Color(0.28f, 0.32f, 0.45f, 0.35f);

        // =====================================================================
        // PUBLIC
        // =====================================================================

        public void Initialize(MPManager manager)
        {
            _manager = manager;
            if (_initialized && _panelRoot != null)
            {
                _panelRoot.SetActive(true);
                RefreshUI();
                RefreshFriendsList();
                return;
            }
            CreateUI();
            SetupLaser();
            RefreshUI();
            RefreshFriendsList();
            _initialized = true;
        }

        public void Hide()
        {
            if (_panelRoot != null) _panelRoot.SetActive(false);
        }

        // =====================================================================
        // UPDATE
        // =====================================================================

        private void Update()
        {
            if (!_initialized || _panelRoot == null || !_panelRoot.activeSelf) return;
            if (_rightHand == null) _rightHand = FindHand();
            RefreshUI();
            UpdateLaser();
            if (Time.realtimeSinceStartup - _lastFriendsRefresh > 2f)
                RefreshFriendsList();
            if (_copyCodeButton != null)
                SetLabel(_copyCodeButton, (Time.realtimeSinceStartup - _copiedTime) < 2f ? "Copied!" : "Copy");
        }

        // =====================================================================
        // BUILD UI
        // =====================================================================

        private void CreateUI()
        {
            _panelRoot = new GameObject("MP_WorldUI");
            _panelRoot.transform.SetParent(transform);
            _panelRoot.transform.position = new Vector3(-0.2434f, 1.782f, -3.8182f);
            _panelRoot.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            _canvas = _panelRoot.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 100;

            var crt = _canvas.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(CANVAS_W, CANVAS_H);
            crt.localScale = Vector3.one * WORLD_SCALE;

            _panelRoot.AddComponent<GraphicRaycaster>();
            var scaler = _panelRoot.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;
            scaler.referencePixelsPerUnit = 100f;

            // BG
            var bg = MkObj("BG", crt);
            bg.AddComponent<Image>().color = C_BG;
            Fill(bg);
            var ol = bg.AddComponent<Outline>();
            ol.effectColor = new Color(0.3f, 0.4f, 0.6f, 0.5f);
            ol.effectDistance = new Vector2(2, 2);

            // Header bar
            var hdr = MkObj("Header", crt);
            hdr.AddComponent<Image>().color = C_HEADER;
            var hr = hdr.GetComponent<RectTransform>();
            hr.anchorMin = new Vector2(0, 1); hr.anchorMax = new Vector2(1, 1);
            hr.pivot = new Vector2(0.5f, 1);
            hr.sizeDelta = new Vector2(0, 48); hr.anchoredPosition = Vector2.zero;

            _playerNameText = MkText("Name", hr, "", 12, TextAnchor.MiddleCenter);
            _playerNameText.color = new Color(0.65f, 0.8f, 1f);
            var nr = _playerNameText.GetComponent<RectTransform>();
            nr.anchorMin = new Vector2(0, 0.65f); nr.anchorMax = new Vector2(1, 1);
            nr.offsetMin = nr.offsetMax = Vector2.zero;

            var title = MkText("Title", hr, "Crawlspace 2 MP", 20, TextAnchor.MiddleCenter);
            title.color = C_TEXT; title.fontStyle = FontStyle.Bold;
            var tr = title.GetComponent<RectTransform>();
            tr.anchorMin = new Vector2(0, 0); tr.anchorMax = new Vector2(1, 0.62f);
            tr.offsetMin = tr.offsetMax = Vector2.zero;

            // Content area
            var body = MkObj("Body", crt);
            var br = body.GetComponent<RectTransform>();
            br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one;
            br.offsetMin = new Vector2(10, 16);
            br.offsetMax = new Vector2(-10, -50);

            var vl = body.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 2; vl.padding = new RectOffset(0, 0, 0, 0);
            vl.childAlignment = TextAnchor.UpperCenter;
            vl.childControlWidth = true; vl.childControlHeight = false;
            vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;

            // Status line (compact)
            _statusText = AddLabel(br, "", 12, TextAnchor.MiddleCenter, 16);
            _statusText.color = C_TEXT;

            // Tutorial hint (hidden by default, only shown in Intro scenes)
            _tutorialHint = AddLabel(br, "", 12, TextAnchor.MiddleCenter, 0);
            _tutorialHint.color = C_WARN;
            _tutorialHint.gameObject.SetActive(false);

            // Connected players (hidden — merged into status)
            _connectedPlayersText = AddLabel(br, "", 1, TextAnchor.MiddleCenter, 0);
            _connectedPlayersText.gameObject.SetActive(false);
            _connectedPlayersText.color = C_OK;

            // --- NOT CONNECTED panel ---
            _notConnectedPanel = MkPanel(br, "NotConnected");
            var ncrt = _notConnectedPanel.GetComponent<RectTransform>();

            // Host + Paste row
            var hostRow = MkObj("HostRow", ncrt);
            var hostHL = hostRow.AddComponent<HorizontalLayoutGroup>();
            hostHL.spacing = 6; hostHL.childControlWidth = true; hostHL.childControlHeight = true;
            hostHL.childForceExpandWidth = true; hostHL.childForceExpandHeight = false;
            hostRow.AddComponent<LayoutElement>().preferredHeight = 28;

            _hostButton = MkBtn(hostRow.GetComponent<RectTransform>(),
                "Host Game", C_HOST, 28, () =>
                {
                    if (_manager.IsRunning || _manager.IsJoining)
                        _manager.DisconnectFromLobby();
                    _manager.HostGame();
                });

            _pasteJoinButton = MkBtn(hostRow.GetComponent<RectTransform>(),
                "Paste & Join", C_BTN, 28, () =>
                {
                    // If already connected, disconnect first
                    if (_manager.IsRunning || _manager.IsJoining)
                    {
                        _manager.DisconnectFromLobby();
                    }
                    string clip = GUIUtility.systemCopyBuffer;
                    if (!string.IsNullOrEmpty(clip))
                    {
                        clip = clip.Trim();
                        if (ulong.TryParse(clip, out ulong lobbyId) && lobbyId > 0)
                        {
                            string currentLobby = _manager.Steam?.GetLobbyId() ?? "";
                            string lastLobby = _manager.LastLobbyId ?? "";
                            string idStr = lobbyId.ToString();
                            if (idStr == currentLobby || idStr == lastLobby)
                            {
                                _manager.StatusMessage = "Can't join your own lobby";
                                return;
                            }
                            _manager.Steam?.JoinFriendGame(lobbyId);
                            _manager.StatusMessage = "Joining from code...";
                        }
                        else
                            _manager.StatusMessage = "Invalid lobby code";
                    }
                    else
                        _manager.StatusMessage = "Clipboard empty";
                });

            AddLine(ncrt);

            _friendsHeaderText = AddLabel(ncrt, "Friends Playing", 11, TextAnchor.MiddleLeft, 14);
            _friendsHeaderText.color = C_DIM;

            BuildScrollList(ncrt, out _friendsPanel, out _friendsScroll, out _friendsScrollContent, 0);
            _friendsPanel.GetComponent<LayoutElement>().flexibleHeight = 1;

            // --- CONNECTED panel ---
            _connectedPanel = MkPanel(br, "Connected");
            var crt2 = _connectedPanel.GetComponent<RectTransform>();

            // Voice + Disconnect row
            var actionRow = MkObj("ActionRow", crt2);
            var actionHL = actionRow.AddComponent<HorizontalLayoutGroup>();
            actionHL.spacing = 4; actionHL.childControlWidth = true; actionHL.childControlHeight = true;
            actionHL.childForceExpandWidth = true; actionHL.childForceExpandHeight = false;
            actionRow.AddComponent<LayoutElement>().preferredHeight = 24;

            _voiceToggleButton = MkBtn(actionRow.GetComponent<RectTransform>(),
                "Mic: ON", C_BTN, 24, () =>
                {
                    if (_manager.VoiceChat != null)
                    {
                        _manager.VoiceChat.Enabled = !_manager.VoiceChat.Enabled;
                    }
                });

            _disconnectButton = MkBtn(actionRow.GetComponent<RectTransform>(),
                "Disconnect", C_DC, 24, () => _manager.DisconnectFromLobby());

            // Mic input info (tiny, one line)
            _micInfoText = AddLabel(crt2, "", 9, TextAnchor.MiddleLeft, 12);
            _micInfoText.color = C_DIM;

            BuildCodeRow(crt2);
            AddLine(crt2);

            _inviteHeaderText = AddLabel(crt2, "Invite Friends", 11, TextAnchor.MiddleLeft, 14);
            _inviteHeaderText.color = C_DIM;

            BuildScrollList(crt2, out _inviteFriendsPanel, out _inviteFriendsScroll, out _inviteFriendsContent, 0);
            _inviteFriendsPanel.GetComponent<LayoutElement>().flexibleHeight = 1;

            // --- JOINING panel ---
            _joiningPanel = MkPanel(br, "Joining");
            var jt = AddLabel(_joiningPanel.GetComponent<RectTransform>(),
                "Joining lobby...", 16, TextAnchor.MiddleCenter, 24);
            jt.color = C_WARN;
            MkBtn(_joiningPanel.GetComponent<RectTransform>(),
                "Cancel", C_DC, 30, () => _manager.DisconnectFromLobby());

            // Footer
            var ftr = MkObj("Footer", crt);
            var fr = ftr.GetComponent<RectTransform>();
            fr.anchorMin = Vector2.zero; fr.anchorMax = new Vector2(1, 0);
            fr.pivot = new Vector2(0.5f, 0);
            fr.sizeDelta = new Vector2(0, 14); fr.anchoredPosition = new Vector2(0, 1);
            var ft = ftr.AddComponent<Text>();
            ft.font = GetFont(); ft.fontSize = 10;
            ft.alignment = TextAnchor.MiddleCenter;
            ft.color = new Color(0.35f, 0.35f, 0.4f, 0.7f);
            ft.text = $"v{PluginInfo.PLUGIN_VERSION} | Steam Voice";
        }

        private void BuildCodeRow(RectTransform parent)
        {
            _lobbyCodePanel = MkObj("CodePanel", parent);
            var hl = _lobbyCodePanel.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 4; hl.childControlWidth = true; hl.childControlHeight = true;
            hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
            _lobbyCodePanel.AddComponent<LayoutElement>().preferredHeight = 18;

            _showCodeButton = MkBtn(_lobbyCodePanel.GetComponent<RectTransform>(),
                "Show Code", C_BTN, 18, () => _showLobbyCode = !_showLobbyCode);
            _showCodeButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 80;

            _copyCodeButton = MkBtn(_lobbyCodePanel.GetComponent<RectTransform>(),
                "Copy", C_BTN, 18, () =>
                {
                    string code = _manager.Steam?.GetLobbyId() ?? "";
                    if (!string.IsNullOrEmpty(code))
                    {
                        GUIUtility.systemCopyBuffer = code;
                        _copiedTime = Time.realtimeSinceStartup;
                    }
                });
            _copyCodeButton.gameObject.AddComponent<LayoutElement>().preferredWidth = 45;

            var cObj = MkObj("CodeText", _lobbyCodePanel.GetComponent<RectTransform>());
            var cLE = cObj.AddComponent<LayoutElement>();
            cLE.flexibleWidth = 1; cLE.preferredHeight = 18;
            _lobbyCodeText = cObj.AddComponent<Text>();
            _lobbyCodeText.font = GetFont(); _lobbyCodeText.fontSize = 9;
            _lobbyCodeText.alignment = TextAnchor.MiddleLeft;
            _lobbyCodeText.color = new Color(0.9f, 0.9f, 0.55f);
        }

        private void BuildScrollList(RectTransform parent, out GameObject panel,
            out ScrollRect scroll, out GameObject content, float height)
        {
            panel = MkObj("ScrollPanel", parent);
            var le = panel.AddComponent<LayoutElement>();
            if (height > 0) le.preferredHeight = height;
            le.flexibleHeight = 0;

            // RectMask2D clips children without needing a visible Image
            panel.AddComponent<RectMask2D>();

            scroll = panel.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            content = MkObj("Content", panel.GetComponent<RectTransform>());
            var cr = content.GetComponent<RectTransform>();
            cr.anchorMin = new Vector2(0, 1); cr.anchorMax = new Vector2(1, 1);
            cr.pivot = new Vector2(0.5f, 1); cr.sizeDelta = new Vector2(0, 0);

            var cl = content.AddComponent<VerticalLayoutGroup>();
            cl.spacing = 2; cl.padding = new RectOffset(0, 0, 1, 1);
            cl.childControlWidth = true; cl.childControlHeight = false;
            cl.childForceExpandWidth = true; cl.childForceExpandHeight = false;

            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = cr;
        }

        // =====================================================================
        // LASER POINTER (renders ON TOP of UI)
        // =====================================================================

        private Transform _rightHand;
        private LineRenderer _laserLine;
        private GameObject _laserDot;
        private GameObject _laserObj;
        private bool _trigDown, _trigWas;
        private GameObject _hovered;

        private void SetupLaser()
        {
            try
            {
                _rightHand = FindHand();
                if (_rightHand == null) { Plugin.Log.LogWarning("[MPUI] No right hand"); return; }

                _laserObj = new GameObject("MP_Laser");
                _laserObj.transform.SetParent(transform);

                _laserLine = _laserObj.AddComponent<LineRenderer>();
                _laserLine.startWidth = 0.003f; _laserLine.endWidth = 0.001f;
                _laserLine.positionCount = 2; _laserLine.useWorldSpace = true;

                // Use Overlay shader so laser renders on top of everything including UI
                var mat = new Material(Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default"));
                mat.color = new Color(0.3f, 0.6f, 1f, 0.7f);
                mat.renderQueue = 4000; // Above UI (3000) and transparent (3000)
                _laserLine.material = mat;
                _laserLine.startColor = new Color(0.3f, 0.6f, 1f, 0.7f);
                _laserLine.endColor = new Color(0.3f, 0.6f, 1f, 0.3f);
                _laserLine.sortingOrder = 200; // Above canvas sortingOrder (100)

                _laserDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _laserDot.name = "MP_Dot";
                _laserDot.transform.localScale = Vector3.one * 0.005f;
                _laserDot.transform.SetParent(transform);
                var col = _laserDot.GetComponent<Collider>();
                if (col) UnityEngine.Object.Destroy(col);
                var dotMat = new Material(Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default"));
                dotMat.color = Color.white;
                dotMat.renderQueue = 4000;
                _laserDot.GetComponent<Renderer>().material = dotMat;

                _laserDot.SetActive(false);
                _laserLine.enabled = false;

                if (EventSystem.current == null)
                {
                    var es = new GameObject("MP_EventSystem");
                    es.AddComponent<EventSystem>();
                    es.AddComponent<StandaloneInputModule>();
                }
                Plugin.Log.LogInfo($"[MPUI] Laser on {_rightHand.name}");
            }
            catch (Exception ex) { Plugin.Log.LogError($"[MPUI] Laser err: {ex.Message}"); }
        }

        private Transform FindHand()
        {
            try
            {
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                    if (mb.GetType().Name == "OVRCameraRig")
                    {
                        var rh = mb.GetType().GetProperty("rightHandAnchor")?.GetValue(mb) as Transform;
                        if (rh != null) return rh;
                    }
            } catch { }
            try
            {
                var bp = UnityEngine.Object.FindObjectOfType<BackpackControl>();
                if (bp?.rightHand != null) return bp.rightHand.transform;
            } catch { }
            try
            {
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                {
                    string n = t.name.ToLower();
                    if (n.Contains("righthand") || n == "righthandanchor" || n.Contains("rightcontroller"))
                        return t;
                }
            } catch { }
            return null;
        }

        private void UpdateLaser()
        {
            if (_rightHand == null || _laserLine == null) return;

            _trigWas = _trigDown;
            _trigDown = ReadTrigger();
            bool pressed = _trigDown && !_trigWas;

            Vector3 origin = _rightHand.position;
            Vector3 dir = _rightHand.forward;
            bool hitUI = false;
            Vector3 hitPt = origin + dir * 5f;
            GameObject hitObj = null;

            if (_canvas != null)
            {
                var crt = _canvas.GetComponent<RectTransform>();
                var plane = new Plane(crt.forward, crt.position);
                var ray = new Ray(origin, dir);
                float enter;
                if (plane.Raycast(ray, out enter) && enter > 0 && enter < 5f)
                {
                    hitPt = origin + dir * enter;
                    var lp = crt.InverseTransformPoint(hitPt);
                    var sz = crt.sizeDelta;
                    if (Mathf.Abs(lp.x) <= sz.x * 0.5f && Mathf.Abs(lp.y) <= sz.y * 0.5f)
                    {
                        hitUI = true;
                        hitObj = FindHitGraphic(hitPt);
                    }
                }
            }

            if (hitUI)
            {
                _laserLine.enabled = true;
                _laserLine.SetPosition(0, origin);
                _laserLine.SetPosition(1, hitPt);
                _laserDot.SetActive(true);
                // Offset dot slightly toward camera so it renders in front of the panel
                _laserDot.transform.position = hitPt + (_canvas.GetComponent<RectTransform>().forward * 0.001f);
                DoHover(hitObj);

                if (pressed && hitObj != null)
                {
                    var btn = hitObj.GetComponentInParent<Button>();
                    if (btn != null && btn.interactable)
                    {
                        btn.onClick.Invoke();
                        _laserLine.startColor = _laserLine.endColor = Color.white;
                    }
                }
                else if (!_trigDown)
                {
                    _laserLine.startColor = new Color(0.3f, 0.6f, 1f, 0.7f);
                    _laserLine.endColor = new Color(0.3f, 0.6f, 1f, 0.3f);
                }
                DoThumbScroll();
            }
            else
            {
                _laserLine.enabled = false;
                _laserDot.SetActive(false);
                DoHover(null);
            }
        }

        private bool ReadTrigger()
        {
            var devs = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devs);
            foreach (var d in devs)
                if (d.TryGetFeatureValue(CommonUsages.trigger, out float v))
                    return v > 0.5f;
            return false;
        }

        private void DoThumbScroll()
        {
            ScrollRect active = null;
            if (_friendsScroll != null && _friendsPanel != null && _friendsPanel.activeInHierarchy)
                active = _friendsScroll;
            else if (_inviteFriendsScroll != null && _inviteFriendsPanel != null && _inviteFriendsPanel.activeInHierarchy)
                active = _inviteFriendsScroll;
            if (active == null) return;

            var devs = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devs);
            foreach (var d in devs)
                if (d.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 a))
                {
                    if (Mathf.Abs(a.y) > 0.2f)
                        active.verticalNormalizedPosition = Mathf.Clamp01(
                            active.verticalNormalizedPosition + a.y * Time.deltaTime * 2f);
                    break;
                }
        }

        private GameObject FindHitGraphic(Vector3 wp)
        {
            var gs = _panelRoot.GetComponentsInChildren<Graphic>(false);
            GameObject best = null; float bestD = float.MaxValue;
            foreach (var g in gs)
            {
                if (!g.raycastTarget) continue;
                var lp = g.rectTransform.InverseTransformPoint(wp);
                if (!g.rectTransform.rect.Contains(new Vector2(lp.x, lp.y))) continue;
                float d = Vector3.Distance(wp, g.rectTransform.position);
                bool interactive = g.GetComponent<Button>() != null || g.GetComponentInParent<Button>() != null;
                if (interactive) d -= 100f;
                if (d < bestD) { bestD = d; best = g.gameObject; }
            }
            return best;
        }

        private void DoHover(GameObject obj)
        {
            if (_hovered != null && _hovered != obj && _hovered)
            {
                var p = _hovered.GetComponentInParent<Button>();
                if (p != null) { var i = p.targetGraphic as Image; if (i) i.color = p.colors.normalColor; }
            }
            if (obj != null)
            {
                var b = obj.GetComponentInParent<Button>();
                if (b != null && b.interactable) { var i = b.targetGraphic as Image; if (i) i.color = b.colors.highlightedColor; }
            }
            _hovered = obj;
        }

        // =====================================================================
        // REFRESH STATE
        // =====================================================================

        private void RefreshUI()
        {
            if (_manager == null) return;
            bool running = _manager.IsRunning;
            bool connected = _manager.IsConnected;
            bool host = _manager.IsHost;
            bool joining = _manager.IsJoining;

            if (_playerNameText != null)
                _playerNameText.text = _manager.Steam?.GetPlayerName() ?? "";

            if (_statusText != null)
            {
                string s = _manager.StatusMessage;
                
                // Track when status changes for auto-clear
                if (s != _lastStatusMsg)
                {
                    _lastStatusMsg = s;
                    _statusSetTime = Time.realtimeSinceStartup;
                }
                
                // Auto-clear status after 5 seconds
                if (!string.IsNullOrEmpty(s) && Time.realtimeSinceStartup - _statusSetTime > 5f)
                {
                    _manager.StatusMessage = "";
                    s = "";
                }
                
                if (connected && !string.IsNullOrEmpty(s))
                {
                    // When connected, show player count inline with status
                    int total = (_manager.Steam?.ConnectedPeerCount ?? 0) + 1;
                    _statusText.text = $"{s}  [{total}P]";
                }
                else if (connected)
                {
                    int total = (_manager.Steam?.ConnectedPeerCount ?? 0) + 1;
                    _statusText.text = $"Connected [{total}P]";
                }
                else
                {
                    _statusText.text = s;
                }
                
                _statusText.color = s.Contains("joined") || s.Contains("created") ? C_OK
                    : s.Contains("failed") || s.Contains("left") || s.Contains("Disconnected") ? C_ERR
                    : s.Contains("Joining") || s.Contains("Creating") ? C_WARN : C_TEXT;
            }

            if (_tutorialHint != null)
            {
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                bool inIntro = scene.IndexOf("Intro", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (inIntro)
                {
                    _tutorialHint.text = "You must complete Night 0\nbefore you can play multiplayer!";
                    _tutorialHint.gameObject.SetActive(true);
                }
                else
                    _tutorialHint.gameObject.SetActive(false);
            }

            if (_connectedPlayersText != null)
                _connectedPlayersText.gameObject.SetActive(false); // Merged into status line

            bool showNC = !running && !joining;
            bool showC = running && !joining;
            bool showJ = joining;

            if (_notConnectedPanel != null) _notConnectedPanel.SetActive(showNC);
            if (_connectedPanel != null) _connectedPanel.SetActive(showC);
            if (_joiningPanel != null) _joiningPanel.SetActive(showJ);

            if (showC)
            {
                if (_lobbyCodePanel != null) _lobbyCodePanel.SetActive(host);
                if (_inviteFriendsPanel != null) _inviteFriendsPanel.SetActive(host);
                if (_inviteHeaderText != null) _inviteHeaderText.gameObject.SetActive(host);

                if (_showLobbyCode && _lobbyCodeText != null)
                {
                    _lobbyCodeText.text = _manager.Steam?.GetLobbyId() ?? "";
                    _lobbyCodeText.gameObject.SetActive(true);
                }
                else if (_lobbyCodeText != null)
                    _lobbyCodeText.gameObject.SetActive(false);

                if (_showCodeButton != null)
                    SetLabel(_showCodeButton, _showLobbyCode ? "Hide" : "Show Code");
                
                if (_voiceToggleButton != null)
                {
                    bool voiceOn = _manager.VoiceChat?.Enabled ?? false;
                    SetLabel(_voiceToggleButton, voiceOn ? "Mic: ON" : "Mic: OFF");
                }
                
                if (_micInfoText != null)
                {
                    string micName = "Default";
                    var devices = Microphone.devices;
                    if (devices != null && devices.Length > 0)
                        micName = devices[0];
                    if (micName.Length > 30) micName = micName.Substring(0, 27) + "...";
                    _micInfoText.text = $"Input: {micName}";
                }
            }
        }

        // =====================================================================
        // FRIENDS LIST
        // =====================================================================

        private void RefreshFriendsList()
        {
            _lastFriendsRefresh = Time.realtimeSinceStartup;
            if (_manager?.Steam == null) return;

            if (_friendsScrollContent != null)
            {
                foreach (var e in _friendEntries) if (e != null) Destroy(e);
                _friendEntries.Clear();

                var playing = _manager.Steam.GetFriendsPlayingGame();
                if (_friendsHeaderText != null)
                    _friendsHeaderText.text = playing.Count > 0
                        ? $"Friends Playing ({playing.Count})" : "Friends Playing";

                if (playing.Count == 0)
                {
                    var empty = MkObj("Empty", _friendsScrollContent.GetComponent<RectTransform>());
                    empty.AddComponent<LayoutElement>().preferredHeight = 20;
                    var t = empty.AddComponent<Text>();
                    t.font = GetFont(); t.fontSize = 11;
                    t.alignment = TextAnchor.MiddleCenter; t.color = C_DIM;
                    t.text = "No friends playing Crawlspace 2";
                    _friendEntries.Add(empty);
                }
                else
                {
                    for (int i = 0; i < playing.Count; i++)
                        _friendEntries.Add(MkFriendRow(playing[i], i % 2 == 1, false,
                            _friendsScrollContent.GetComponent<RectTransform>()));
                }
            }

            if (_inviteFriendsContent != null && _manager.IsRunning && _manager.IsHost)
            {
                foreach (var e in _inviteEntries) if (e != null) Destroy(e);
                _inviteEntries.Clear();

                var online = _manager.Steam.GetAllOnlineFriends();
                if (_inviteHeaderText != null)
                    _inviteHeaderText.text = $"Invite Friends ({online.Count} online)";

                if (online.Count == 0)
                {
                    var empty = MkObj("Empty", _inviteFriendsContent.GetComponent<RectTransform>());
                    empty.AddComponent<LayoutElement>().preferredHeight = 20;
                    var t = empty.AddComponent<Text>();
                    t.font = GetFont(); t.fontSize = 11;
                    t.alignment = TextAnchor.MiddleCenter; t.color = C_DIM;
                    t.text = "No friends online";
                    _inviteEntries.Add(empty);
                }
                else
                {
                    for (int i = 0; i < online.Count; i++)
                        _inviteEntries.Add(MkFriendRow(online[i], i % 2 == 1, true,
                            _inviteFriendsContent.GetComponent<RectTransform>()));
                }
            }
        }

        private GameObject MkFriendRow(SteamTransport.FriendGameInfo friend, bool alt,
            bool inviteMode, RectTransform parent)
        {
            var entry = MkObj($"F_{friend.Name}", parent);
            entry.AddComponent<LayoutElement>().preferredHeight = 24;
            entry.AddComponent<Image>().color = alt ? C_ROW_ALT : C_ROW;

            var row = entry.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 4; row.padding = new RectOffset(8, 6, 2, 2);
            row.childControlWidth = true; row.childControlHeight = true;
            row.childForceExpandWidth = false; row.childForceExpandHeight = true;

            var nameObj = MkObj("Name", entry.GetComponent<RectTransform>());
            nameObj.AddComponent<LayoutElement>().flexibleWidth = 1;
            var nameText = nameObj.AddComponent<Text>();
            nameText.font = GetFont(); nameText.fontSize = 13;
            nameText.text = friend.Name;

            var stObj = MkObj("St", entry.GetComponent<RectTransform>());
            stObj.AddComponent<LayoutElement>().preferredWidth = 55;
            var stText = stObj.AddComponent<Text>();
            stText.font = GetFont(); stText.fontSize = 11; stText.color = C_DIM;
            stText.alignment = TextAnchor.MiddleLeft;

            if (inviteMode)
            {
                nameText.color = friend.IsInGame ? C_OK : C_TEXT;
                stText.text = friend.IsInGame ? "In Game" : "Online";

                ulong fid = friend.SteamId;
                string fname = friend.Name;
                bool recent = _inviteSentTime.ContainsKey(fid) &&
                    (Time.realtimeSinceStartup - _inviteSentTime[fid]) < 10f;

                var btn = MkBtn(entry.GetComponent<RectTransform>(),
                    recent ? "Sent!" : "Invite", C_INVITE, 26, () =>
                    {
                        bool ok = _manager.Steam.InviteFriend(new Steamworks.SteamId { Value = fid });
                        if (ok) { _inviteSentTime[fid] = Time.realtimeSinceStartup; _manager.StatusMessage = $"Invited {fname}"; }
                        else _manager.StatusMessage = $"Failed to invite {fname}";
                    });
                btn.gameObject.AddComponent<LayoutElement>().preferredWidth = 65;
                if (recent) btn.interactable = false;
            }
            else
            {
                nameText.color = friend.IsJoinable ? C_OK : C_TEXT;
                stText.text = friend.Status;

                if (friend.IsJoinable)
                {
                    ulong lid = friend.LobbyId;
                    string fname = friend.Name;
                    var btn = MkBtn(entry.GetComponent<RectTransform>(),
                        "Join", C_JOIN, 26, () =>
                        {
                            _manager.Steam?.JoinFriendGame(lid);
                            _manager.StatusMessage = $"Joining {fname}...";
                        });
                    btn.gameObject.AddComponent<LayoutElement>().preferredWidth = 65;
                }
            }
            return entry;
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        private GameObject MkObj(string name, RectTransform parent)
        {
            var o = new GameObject(name, typeof(RectTransform));
            o.transform.SetParent(parent, false);
            return o;
        }

        private GameObject MkPanel(RectTransform parent, string name)
        {
            var p = MkObj(name, parent);
            var vl = p.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 4; vl.childControlWidth = true; vl.childControlHeight = false;
            vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            return p;
        }

        private Text MkText(string name, RectTransform parent, string text, int size, TextAnchor align)
        {
            var o = MkObj(name, parent);
            var t = o.AddComponent<Text>();
            t.font = GetFont(); t.fontSize = size; t.alignment = align;
            t.text = text; t.color = C_TEXT;
            return t;
        }

        private Text AddLabel(RectTransform parent, string text, int size, TextAnchor align, float h)
        {
            var o = MkObj("Lbl", parent);
            o.AddComponent<LayoutElement>().preferredHeight = h;
            var t = o.AddComponent<Text>();
            t.font = GetFont(); t.fontSize = size; t.alignment = align;
            t.text = text; t.color = C_TEXT;
            return t;
        }

        private void AddLine(RectTransform parent)
        {
            var o = MkObj("Line", parent);
            o.AddComponent<LayoutElement>().preferredHeight = 1;
            o.AddComponent<Image>().color = C_LINE;
        }

        private Button MkBtn(RectTransform parent, string label, Color bg, float h,
            UnityEngine.Events.UnityAction click)
        {
            var o = MkObj("Btn", parent);
            o.AddComponent<LayoutElement>().preferredHeight = h;
            var img = o.AddComponent<Image>(); img.color = bg;

            var btn = o.AddComponent<Button>(); btn.targetGraphic = img;
            var c = btn.colors;
            c.normalColor = bg;
            c.highlightedColor = bg * 1.3f;
            c.pressedColor = bg * 0.65f;
            c.selectedColor = bg * 1.15f;
            c.disabledColor = bg * 0.35f;
            btn.colors = c;

            var tObj = MkObj("L", o.GetComponent<RectTransform>());
            Fill(tObj);
            var t = tObj.AddComponent<Text>();
            t.font = GetFont(); t.fontSize = 14;
            t.alignment = TextAnchor.MiddleCenter; t.color = C_TEXT; t.text = label;

            btn.onClick.AddListener(click);
            return btn;
        }

        private void SetLabel(Button b, string s)
        {
            var t = b?.GetComponentInChildren<Text>();
            if (t != null) t.text = s;
        }

        private void Fill(GameObject o)
        {
            var r = o.GetComponent<RectTransform>();
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        private Font GetFont()
        {
            if (_cachedFont != null) return _cachedFont;
            _cachedFont = Font.CreateDynamicFontFromOSFont("Arial", 16);
            if (_cachedFont == null)
                _cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return _cachedFont;
        }

        private void OnDestroy()
        {
            if (_panelRoot) Destroy(_panelRoot);
            if (_laserObj) Destroy(_laserObj);
            if (_laserDot) Destroy(_laserDot);
        }
    }
}

