using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if WAXKIT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Gulkonster.WaxKit
{
    /// <summary>The kit's built-in wallet UI: its own overlay canvas on top of the game, built in code.</summary>
    public sealed class WaxDefaultUI : MonoBehaviour, IWaxWalletUI
    {
        const float MaxPanelWidth = 720;
        const float ButtonHeight = 96;
        const float Padding = 40;
        const float Spacing = 24;
        // Room for everything but the QR, so the panel fits a landscape screen.
        const float ReservedHeight = 620;

        static readonly Color Dim = new Color(0, 0, 0, 0.6f);
        static readonly Color PanelColor = new Color(0.97f, 0.97f, 0.98f);
        static readonly Color PrimaryColor = new Color(0.22f, 0.4f, 0.85f);
        static readonly Color SecondaryColor = new Color(0.86f, 0.87f, 0.9f);
        static readonly Color TextColor = new Color(0.1f, 0.1f, 0.12f);

        CanvasScaler scaler;
        RectTransform safeArea;
        RectTransform panel;
        TMP_Text title;
        LayoutElement qrBox;
        RawImage qrImage;
        TMP_Text body;
        TMP_Text countdown;
        Button primary;
        TMP_Text primaryLabel;
        WaxGestureButton primaryGesture;
        TMP_Text secondaryLabel;

        WaxUiRequest request;
        Rect appliedSafeArea;

        internal static WaxDefaultUI Create()
        {
            var go = new GameObject(nameof(WaxDefaultUI));
            DontDestroyOnLoad(go);
            var ui = go.AddComponent<WaxDefaultUI>();
            ui.Build();
            go.SetActive(false);
            return ui;
        }

        public void Show(WaxUiRequest next)
        {
            ClearQr();
            request = next;

            var isAnchor = next.Kind == WaxUiKind.AnchorSign;
            SetText(title, next.Title);
            SetText(body, next.Text);

            qrImage.texture = isAnchor ? next.CreateQrTexture() : null;
            qrBox.gameObject.SetActive(qrImage.texture != null);
            countdown.gameObject.SetActive(isAnchor && next.ExpiresAt.HasValue);

            var showPrimary = next.Kind == WaxUiKind.Continue || (isAnchor && next.CanOpenAnchor);
            primary.gameObject.SetActive(showPrimary);
            primaryLabel.text = isAnchor ? "Open Anchor" : "Continue";
            primaryGesture.action = isAnchor ? WaxGestureAction.OpenAnchor : WaxGestureAction.Continue;
            secondaryLabel.text = next.Kind == WaxUiKind.Error ? "Close" : "Cancel";

            EnsureEventSystem();
            gameObject.SetActive(true);
            LateUpdate();
        }

        public void Close()
        {
            request = null;
            ClearQr();
            gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            // Portrait or landscape reference, following the current orientation.
            scaler.referenceResolution = Screen.width >= Screen.height ? new Vector2(1920, 1080) : new Vector2(1080, 1920);

            if (Screen.safeArea != appliedSafeArea)
            {
                appliedSafeArea = Screen.safeArea;
                var screen = new Vector2(Screen.width, Screen.height);
                safeArea.anchorMin = appliedSafeArea.position / screen;
                safeArea.anchorMax = (appliedSafeArea.position + appliedSafeArea.size) / screen;
            }

            var area = safeArea.rect;
            var width = Mathf.Min(area.width * 0.9f, MaxPanelWidth);
            panel.sizeDelta = new Vector2(width, panel.sizeDelta.y);
            qrBox.preferredHeight = Mathf.Max(0, Mathf.Min(width - Padding * 2, area.height - ReservedHeight));

            if (request?.ExpiresAt is DateTime expiresAt && countdown.gameObject.activeSelf)
            {
                var left = expiresAt - DateTime.UtcNow;
                countdown.text = left > TimeSpan.Zero ? $"Expires in {left:m\\:ss}" : "Expired";
            }
        }

        void ClearQr()
        {
            if (qrImage.texture != null) Destroy(qrImage.texture);
            qrImage.texture = null;
        }

        void OnSecondary()
        {
            var current = request;
            Close();
            if (current != null && current.Kind != WaxUiKind.Error) current.Cancel();
        }

        // ---- Building ----

        void Build()
        {
            if (TMP_Settings.instance == null)
                Debug.LogError("Unity WAX Kit: TextMeshPro resources are missing, so the wallet UI has no text. " +
                               "Import them with Window > TextMeshPro > Import TMP Essential Resources.");

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            var dim = NewChild("Dim", transform, typeof(Image));
            Stretch(dim);
            dim.GetComponent<Image>().color = Dim;

            safeArea = NewChild("SafeArea", transform);
            Stretch(safeArea);

            panel = NewChild("Panel", safeArea, typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.GetComponent<Image>().color = PanelColor;
            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)Padding, (int)Padding, (int)Padding, (int)Padding);
            layout.spacing = Spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            panel.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            title = AddText(48, FontStyles.Bold);

            qrBox = NewChild("Qr", panel, typeof(LayoutElement)).GetComponent<LayoutElement>();
            var qr = NewChild("Image", qrBox.transform, typeof(RawImage), typeof(AspectRatioFitter));
            qrImage = qr.GetComponent<RawImage>();
            var fitter = qr.GetComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = 1;

            body = AddText(34, FontStyles.Normal);
            countdown = AddText(30, FontStyles.Italic);

            primary = AddButton("Primary", PrimaryColor, Color.white, out primaryLabel);
            // Continue / Open Anchor run only through the gesture, inside the browser's pointerup.
            primaryGesture = primary.gameObject.AddComponent<WaxGestureButton>();

            AddButton("Secondary", SecondaryColor, TextColor, out secondaryLabel).onClick.AddListener(OnSecondary);
        }

        TMP_Text AddText(float size, FontStyles style)
        {
            var text = NewChild("Text", panel, typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.fontStyle = style;
            text.color = TextColor;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            return text;
        }

        Button AddButton(string name, Color color, Color textColor, out TMP_Text label)
        {
            var rect = NewChild(name, panel, typeof(Image), typeof(Button), typeof(LayoutElement));
            rect.GetComponent<Image>().color = color;
            rect.GetComponent<LayoutElement>().minHeight = ButtonHeight;

            label = NewChild("Label", rect, typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            Stretch((RectTransform)label.transform);
            label.fontSize = 38;
            label.color = textColor;
            label.alignment = TextAlignmentOptions.Center;
            return rect.GetComponent<Button>();
        }

        static void SetText(TMP_Text text, string value)
        {
            text.text = value ?? "";
            text.gameObject.SetActive(!string.IsNullOrEmpty(value));
        }

        static RectTransform NewChild(string name, Transform parent, params Type[] components)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            foreach (var component in components) go.AddComponent(component);
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        // Uses the scene's EventSystem; creates one only when there is none.
        static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem));
#if WAXKIT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
            DontDestroyOnLoad(go);
        }
    }
}
