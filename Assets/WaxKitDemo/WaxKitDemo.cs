using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityWaxKit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>Unity WAX Kit demo. Builds its own uGUI so the scene needs nothing else.</summary>
public class WaxKitDemo : MonoBehaviour
{
    // Networks, app name, theme, wait times and the WebAuth requestor are set on the WAX Kit Manager in the scene.
    const string Memo = "";

    const decimal SendAmount = 0.0001m;

    // The kit's own dark theme, so the demo matches the wallet login UI.
    static readonly WaxUiTheme Theme = WaxUiTheme.Dark;
    static readonly Color BackgroundColor = new Color32(0x12, 0x12, 0x12, 0xFF);
    static readonly Color InsetColor = new Color32(0x16, 0x16, 0x18, 0xFF);
    static readonly Color CardColor = Color.Lerp(Theme.Panel, Theme.Secondary, 0.5f);
    static readonly Color MutedColor = new Color32(0x8E, 0x8E, 0x93, 0xFF);
    static readonly Color ErrorColor = new Color(0.96f, 0.47f, 0.45f);
    static readonly Color OnlineColor = new Color(0.3f, 0.8f, 0.45f);
    static readonly Color OfflineColor = MutedColor;
    static readonly Color WaxOrange = new Color32(0xF8, 0x90, 0x22, 0xFF);

    // Each network's badge code and colors (ring, fill, code), by network name. The networks and their order come from
    // the WAX Kit Manager's Networks list.
    static readonly NetworkBadge[] Badges =
    {
        new NetworkBadge("WAX Testnet", "WAX", WaxOrange, Theme.Panel, WaxOrange, true),
        new NetworkBadge("WAX Mainnet", "WAX", WaxOrange, WaxOrange, new Color32(0x12, 0x12, 0x12, 0xFF), false),
        new NetworkBadge("Jungle 4", "EOS", new Color32(0x2E, 0x7D, 0x5B, 0xFF), true),
        new NetworkBadge("XPR Testnet", "XPR", new Color32(0x75, 0x43, 0xE3, 0xFF), true),
    };

    IReadOnlyList<WaxNetwork> networks;
    readonly DefaultControls.Resources resources = new DefaultControls.Resources();
    Sprite roundedSprite;
    Sprite circleSprite;
    InputField toInput;
    Text sendButtonText;
    Button anchorButton;
    Button myCloudWalletButton;
    Button webAuthButton;
    Button webAuthWebButton;
    Transform captionBadge;
    Image sessionDot;
    Text sessionText;
    float sessionTextWidth;
    Text statusText;

    void Start()
    {
        // The WAX Kit Manager has already initialized the kit with its Active Network (it runs first).
        networks = FindAnyObjectByType<WaxKitManager>().CreateNetworks();
        BuildUi();
        ShowNetwork();
        ShowSession();
        SetStatus("Ready.");
        // Picks up the saved session, or finishes a login the player approved while the game was closed.
        Restore();
    }

    // ---- Actions ----

    void OnNetworkChanged(int index)
    {
        WaxKit.Initialize(networks[index], WaxKit.AppName);
        ShowNetwork();
        ShowSession();
        // Load this network's saved session, as the Restore button does.
        Restore();
    }

    void Login(WalletType wallet) => Run($"Logging in with {wallet}...", async () =>
    {
        var session = await WaxKit.Login(wallet);
        return $"Logged in as {session.Actor}.";
    });

    void Restore() => Run("Restoring...", async () =>
    {
        var session = await WaxKit.RestoreSession();
        return session == null ? "No saved session for this network." : $"Restored {session.Actor}.";
    });

    void Logout() => Run("Logging out...", async () =>
    {
        await WaxKit.Logout();
        return "Logged out.";
    });

    void Send() => Run("Waiting for the wallet...", async () =>
    {
        var to = toInput.text.Trim().ToLowerInvariant();
        if (to.Length == 0) return "Enter a 'To' account first.";

        var result = await WaxKit.Transact(new WaxAction("eosio.token", "transfer", new
        {
            from = WaxKit.Current?.Actor,
            to,
            quantity = CoreAmount(SendAmount),
            memo = Memo,
        }));
        return $"Sent. Transaction id: {result.TransactionId}";
    });

    void ReadBalance() => Run("Reading balance...", async () =>
    {
        if (!WaxKit.IsLoggedIn) return "Log in first.";

        var result = await WaxKit.GetTableRows<JObject>(new WaxTableQuery
        {
            Code = "eosio.token",
            Scope = WaxKit.Current.Actor,
            Table = "accounts",
        });
        // An account can hold several eosio.token tokens: take the core token's row.
        var balance = result.Rows.Select(r => (string)r["balance"])
            .FirstOrDefault(b => b != null && b.EndsWith(" " + WaxKit.Network.CoreSymbol)) ?? CoreAmount(0);
        return $"Balance of {WaxKit.Current.Actor}: {balance}";
    });

    void ReadNfts() => Run("Reading NFTs...", async () =>
    {
        if (!WaxKit.IsLoggedIn) return "Log in first.";
        if (WaxKit.Network.AtomicEndpoints.Count == 0) return $"No AtomicAssets API set for {WaxKit.Network.Name}.";

        var assets = await WaxKit.AtomicAssets.GetAssets(new WaxAtomicQuery { Owner = WaxKit.Current.Actor, Limit = 5, Sort = "asset_id", Order = "desc" });
        if (assets.Count == 0) return $"{WaxKit.Current.Actor} has no NFTs on {WaxKit.Network.Name}.";
        return "NFTs: " + string.Join(", ", assets.Select(a => $"{a.Name ?? a.AssetId} ({a.Collection?.CollectionName})"));
    });

    async void Run(string busyText, Func<Task<string>> action)
    {
        SetStatus(busyText);
        try
        {
            SetStatus(await action());
        }
        catch (WaxKitException e) when (e.Kind == WaxErrorKind.Cancelled)
        {
            // The player changed their mind: not an error.
            SetStatus("Cancelled.");
        }
        catch (WaxKitException e)
        {
            SetStatus($"Error ({e.Kind}): {e.Message}", true);
        }
        catch (Exception e)
        {
            SetStatus($"Error: {e.Message}", true);
        }
        ShowSession();
    }

    /// <summary>An amount of the current network's core token, e.g. "0.00010000 WAX" or "0.0001 EOS".</summary>
    static string CoreAmount(decimal amount) =>
        $"{amount.ToString("F" + WaxKit.Network.CorePrecision, CultureInfo.InvariantCulture)} {WaxKit.Network.CoreSymbol}";

    void ShowNetwork()
    {
        sendButtonText.text = $"Send {SendAmount.ToString(CultureInfo.InvariantCulture)} {WaxKit.Network.CoreSymbol}";
        ((Text)toInput.placeholder).text = WaxKit.Network.CoreSymbol == "WAX" ? "To (WAX account)" : "To (account)";
        ShowBadge(captionBadge, BadgeOf(WaxKit.Network));

        // Next to Anchor: My Cloud Wallet on WAX (enabled on mainnet only), WebAuth App and WebAuth Web on XPR Network,
        // none elsewhere (Anchor then takes the whole row). Wallets that are not registered (turned off in the WAX Kit
        // Manager or removed with WaxKit.UnregisterWallet) keep their buttons hidden.
        anchorButton.gameObject.SetActive(IsRegistered("anchor"));
        myCloudWalletButton.gameObject.SetActive(IsRegistered("mycloudwallet") && WaxKit.Network.CoreSymbol == "WAX");
        webAuthButton.gameObject.SetActive(IsRegistered("webauth") && WaxKit.Network.CoreSymbol == "XPR");
        webAuthWebButton.gameObject.SetActive(IsRegistered("webauthweb") && WaxKit.Network.CoreSymbol == "XPR");
        SetInteractable(myCloudWalletButton, WalletSupportsNetwork("mycloudwallet"));
        SetInteractable(webAuthButton, WalletSupportsNetwork("webauth"));
        SetInteractable(webAuthWebButton, WalletSupportsNetwork("webauthweb"));
    }

    void ShowSession()
    {
        sessionDot.color = WaxKit.IsLoggedIn ? OnlineColor : OfflineColor;
        sessionText.text = WaxKit.IsLoggedIn
            ? $"Session: {WaxKit.Current} on {WaxKit.Network.Name}"
            : $"Session: not logged in ({WaxKit.Network.Name})";
        // One line: a long session line gets a smaller font instead of wrapping.
        sessionText.fontSize = 24;
        while (sessionText.fontSize > 12 && sessionText.preferredWidth > sessionTextWidth) sessionText.fontSize--;
    }

    static bool IsRegistered(string walletId) => WaxKit.Wallets.Any(w => w.Id == walletId);

    static bool WalletSupportsNetwork(string walletId)
    {
        var wallet = WaxKit.Wallets.FirstOrDefault(w => w.Id == walletId);
        return wallet != null && wallet.SupportsChain(WaxKit.Network);
    }

    void SetStatus(string message, bool isError = false)
    {
        statusText.text = message;
        statusText.color = isError ? ErrorColor : Theme.Text;
        if (isError) Debug.LogWarning(message);
    }

    // ---- UI ----

    void BuildUi()
    {
        roundedSprite = CreateSprite(44, 20, 20);
        circleSprite = CreateSprite(64, 32, 0);

        if (FindAnyObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        // Expand keeps the whole 720x1000 layout on screen in both portrait (phone) and landscape (desktop).
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(720, 1000);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasObject.transform, false);
        Stretch((RectTransform)background.transform, 0);
        background.GetComponent<Image>().color = BackgroundColor;

        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        panel.transform.SetParent(canvasObject.transform, false);
        SetRounded(panel.GetComponent<Image>(), Theme.Panel, 1);
        var panelRect = (RectTransform)panel.transform;
        panelRect.sizeDelta = new Vector2(680, 960);
        var layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 24, 24);
        layout.spacing = 8;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        AddText(panel.transform, "Unity WAX Kit", 34, 40).fontStyle = FontStyle.Bold;
        AddText(panel.transform, "Demo", 20, 26).color = MutedColor;

        AddSectionLabel(panel.transform, "NETWORK");
        AddNetworkDropdown(panel.transform);

        AddSectionLabel(panel.transform, "WALLET");
        var loginRow = AddRow(panel.transform);
        anchorButton = AddButton(loginRow, "Login Anchor", () => Login(WalletType.Anchor), true);
        myCloudWalletButton = AddButton(loginRow, "Login My Cloud Wallet", () => Login(WalletType.MyCloudWallet));
        webAuthButton = AddButton(loginRow, "WebAuth App", () => Login(WalletType.WebAuth));
        webAuthWebButton = AddButton(loginRow, "WebAuth Web", () => Login(WalletType.WebAuthWeb));

        var sessionRow = AddRow(panel.transform);
        AddButton(sessionRow, "Restore", Restore);
        AddButton(sessionRow, "Logout", Logout);

        var sessionCard = AddCard(panel.transform, CardColor, 64);
        var cardLayout = sessionCard.gameObject.AddComponent<HorizontalLayoutGroup>();
        cardLayout.padding = new RectOffset(20, 16, 0, 0);
        cardLayout.spacing = 14;
        cardLayout.childAlignment = TextAnchor.MiddleLeft;
        cardLayout.childControlWidth = cardLayout.childControlHeight = true;
        cardLayout.childForceExpandWidth = cardLayout.childForceExpandHeight = false;
        sessionDot = new GameObject("Dot", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        sessionDot.sprite = circleSprite;
        sessionDot.raycastTarget = false;
        AddToLayout(sessionDot.gameObject, sessionCard, 14).preferredWidth = 14;
        sessionText = AddText(sessionCard, "", 24, 64);
        sessionText.GetComponent<LayoutElement>().flexibleWidth = 1;
        sessionText.horizontalOverflow = HorizontalWrapMode.Overflow;

        AddSectionLabel(panel.transform, "ACTIONS");
        AddToInput(panel.transform);

        var actionRow = AddRow(panel.transform);
        var sendButton = AddButton(actionRow, "", Send, true);
        // Wider than the other two, so "Send 0.0001 WAX" keeps side padding.
        sendButton.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1.4f;
        sendButtonText = sendButton.GetComponentInChildren<Text>();
        AddButton(actionRow, "Read balance", ReadBalance);
        AddButton(actionRow, "Read NFTs", ReadNfts);

        AddSectionLabel(panel.transform, "STATUS");
        var statusCard = AddCard(panel.transform, InsetColor, 190);
        statusText = NewText(statusCard, "", 22, Theme.Text, TextAnchor.UpperLeft);
        Stretch(statusText.rectTransform, 16);
        statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
        statusText.verticalOverflow = VerticalWrapMode.Truncate;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // A windowed or full-screen Windows player has no other way out in the demo.
        AddButton(AddRow(panel.transform), "Quit", Application.Quit);
#endif

        // The panel has a fixed width: measure the room for the session line once.
        LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);
        sessionTextWidth = sessionText.rectTransform.rect.width;
    }

    void AddNetworkDropdown(Transform parent)
    {
        // Swap in a Dropdown subclass, so every list row can get its network's badge.
        var dropdownObject = DefaultControls.CreateDropdown(resources);
        var defaults = dropdownObject.GetComponent<Dropdown>();
        var template = defaults.template;
        var captionText = defaults.captionText;
        var itemText = defaults.itemText;
        DestroyImmediate(defaults);
        var dropdown = dropdownObject.AddComponent<NetworkDropdown>();
        dropdown.template = template;
        dropdown.captionText = captionText;
        dropdown.itemText = itemText;
        dropdown.targetGraphic = dropdownObject.GetComponent<Image>();
        dropdown.ItemCreated = (item, index) => ShowBadge(item.Find("Badge"), BadgeOf(networks[index]));

        AddToLayout(dropdownObject, parent, 64);
        SetPressable(dropdown, Theme.Secondary);
        dropdown.ClearOptions();
        dropdown.AddOptions(networks.Select(LabelOf).ToList());
        // Starts on the Manager's Active Network.
        dropdown.SetValueWithoutNotify(Math.Max(0, networks.Select(n => n.Name).ToList().IndexOf(WaxKit.Network.Name)));

        captionText.fontSize = 24;
        captionText.color = Theme.Text;
        captionText.alignment = TextAnchor.MiddleLeft;
        captionText.rectTransform.offsetMin = new Vector2(64, captionText.rectTransform.offsetMin.y);
        captionBadge = AddBadge(dropdownObject.transform);

        // A muted down chevron instead of the default arrow image (which has no sprite here).
        var arrow = (RectTransform)dropdownObject.transform.Find("Arrow");
        arrow.sizeDelta = new Vector2(24, 24);
        arrow.anchoredPosition = new Vector2(-28, 0);
        arrow.GetComponent<Image>().enabled = false;
        Stretch(NewText(arrow, "▼", 16, MutedColor, TextAnchor.MiddleCenter).rectTransform, 0);

        StyleDropdownList(dropdown, 64);
        dropdown.onValueChanged.AddListener(OnNetworkChanged);
    }

    void AddToInput(Transform parent)
    {
        toInput = DefaultControls.CreateInputField(resources).GetComponent<InputField>();
        AddToLayout(toInput.gameObject, parent, 64);
        // The root image is the 1 px outline; a darker fill sits inside it.
        SetPressable(toInput, Color.Lerp(Theme.Secondary, Theme.Text, 0.15f));
        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(toInput.transform, false);
        fill.transform.SetAsFirstSibling();
        Stretch((RectTransform)fill.transform, 1);
        var fillImage = fill.GetComponent<Image>();
        SetRounded(fillImage, Shade(Theme.Secondary, 0.75f), 1.5f);
        fillImage.raycastTarget = false;

        // InputField puts its caret and selection renderer first under the text's parent at runtime: in their own
        // area after the fill, they are drawn on top of it instead of under it.
        var textArea = new GameObject("Text Area", typeof(RectTransform));
        textArea.transform.SetParent(toInput.transform, false);
        Stretch((RectTransform)textArea.transform, 0);
        foreach (var text in toInput.GetComponentsInChildren<Text>(true))
        {
            text.transform.SetParent(textArea.transform, false);
            text.fontSize = 24;
            text.fontStyle = FontStyle.Normal;
            text.alignment = TextAnchor.MiddleLeft;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(18, 0);
            text.rectTransform.offsetMax = new Vector2(-18, 0);
        }
        toInput.textComponent.color = Theme.Text;
        // DefaultControls gives the placeholder no font at runtime, so it drew nothing.
        ((Text)toInput.placeholder).font = toInput.textComponent.font;
        toInput.placeholder.color = MutedColor;
        toInput.customCaretColor = true;
        toInput.caretColor = Theme.Text;
        toInput.caretWidth = 2;
        toInput.selectionColor = new Color(Theme.Primary.r, Theme.Primary.g, Theme.Primary.b, 0.4f);
        toInput.characterLimit = 12;
        KeepTypedText(toInput);
    }

    /// <summary>
    /// Phones: edit the text inside the field itself (the player can place the cursor and select text), and keep what
    /// was typed when the keyboard is closed without its OK key (back, a tap elsewhere). The keyboard reports that as a
    /// cancel, and InputField would put the old text back.
    /// </summary>
    static void KeepTypedText(InputField input)
    {
        input.shouldHideMobileInput = true;
        var typed = input.text;
        input.onValueChanged.AddListener(value =>
        {
            // The put-back happens after the field lost focus; only text typed while editing counts.
            if (input.isFocused) typed = value;
        });
        input.onEndEdit.AddListener(_ =>
        {
            if (input.wasCanceled && input.text != typed) input.SetTextWithoutNotify(typed);
        });
    }

    static Transform AddRow(Transform parent)
    {
        var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        AddToLayout(row, parent, 64);
        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 12;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = true;
        return row.transform;
    }

    Button AddButton(Transform row, string label, Action onClick, bool primary = false)
    {
        var button = DefaultControls.CreateButton(resources).GetComponent<Button>();
        button.transform.SetParent(row, false);
        // Disabled buttons fade as a whole (see SetInteractable).
        button.gameObject.AddComponent<CanvasGroup>();
        SetPressable(button, primary ? Theme.Primary : Theme.Secondary);
        var text = button.GetComponentInChildren<Text>();
        text.text = label;
        text.fontSize = 24;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = primary ? Theme.PrimaryText : Theme.SecondaryText;
        button.onClick.AddListener(() => onClick());
        return button;
    }

    static void SetInteractable(Button button, bool interactable)
    {
        button.interactable = interactable;
        button.GetComponent<CanvasGroup>().alpha = interactable ? 1f : 0.4f;
    }

    /// <summary>Rounded fill; pressed is slightly darker. Disabled fading is done by <see cref="SetInteractable"/>.</summary>
    void SetPressable(Selectable selectable, Color fill)
    {
        SetRounded(selectable.image, fill, 1.5f);
        var colors = ColorBlock.defaultColorBlock;
        colors.normalColor = colors.highlightedColor = colors.selectedColor = colors.disabledColor = Color.white;
        colors.pressedColor = new Color(0.82f, 0.82f, 0.82f);
        selectable.colors = colors;
    }

    /// <summary>
    /// The list: Panel-colored with rounded corners, rows as tall as the other controls, no scrollbar. Each row gets a
    /// badge; the default checkmark becomes a Primary-tinted background on the selected row.
    /// </summary>
    void StyleDropdownList(Dropdown dropdown, float rowHeight)
    {
        var template = dropdown.template;
        var item = dropdown.itemText.transform.parent as RectTransform;
        var content = item.parent as RectTransform;
        var scrollRect = template.GetComponent<ScrollRect>();

        SetRounded(template.GetComponent<Image>(), Theme.Panel, 1.5f);
        Destroy(scrollRect.verticalScrollbar.gameObject);
        scrollRect.verticalScrollbar = null;
        Stretch(scrollRect.viewport, 0);

        item.sizeDelta = new Vector2(item.sizeDelta.x, rowHeight);
        content.sizeDelta = new Vector2(content.sizeDelta.x, rowHeight);
        // Room for every option without scrolling.
        template.sizeDelta = new Vector2(template.sizeDelta.x, rowHeight * dropdown.options.Count);

        var toggle = item.GetComponent<Toggle>();
        SetRounded((Image)toggle.targetGraphic, Color.white, 1.5f);
        var colors = ColorBlock.defaultColorBlock;
        colors.normalColor = Theme.Panel;
        colors.highlightedColor = colors.selectedColor = Theme.Secondary;
        colors.pressedColor = Shade(Theme.Secondary, 0.82f);
        toggle.colors = colors;

        var selected = (Image)toggle.graphic;
        Stretch(selected.rectTransform, 0);
        SetRounded(selected, new Color(Theme.Primary.r, Theme.Primary.g, Theme.Primary.b, 0.35f), 1.5f);

        var label = dropdown.itemText;
        label.fontSize = 24;
        label.color = Theme.Text;
        label.alignment = TextAnchor.MiddleLeft;
        label.rectTransform.offsetMin = new Vector2(64, label.rectTransform.offsetMin.y);
        AddBadge(item);
    }

    /// <summary>
    /// A 36 px circle with the network code, at the left of its parent, in front of the name. The outer circle is a 2 px
    /// ring around the inner fill (same color for a solid badge).
    /// </summary>
    Transform AddBadge(Transform parent)
    {
        var badge = new GameObject("Badge", typeof(RectTransform), typeof(Image));
        badge.transform.SetParent(parent, false);
        var rect = (RectTransform)badge.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0, 0.5f);
        rect.sizeDelta = new Vector2(36, 36);
        rect.anchoredPosition = new Vector2(32, 0);
        var image = badge.GetComponent<Image>();
        image.sprite = circleSprite;
        image.raycastTarget = false;

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(badge.transform, false);
        Stretch((RectTransform)fill.transform, 2);
        var fillImage = fill.GetComponent<Image>();
        fillImage.sprite = circleSprite;
        fillImage.raycastTarget = false;

        var code = NewText(badge.transform, "", 14, Color.white, TextAnchor.MiddleCenter);
        code.fontStyle = FontStyle.Bold;
        Stretch(code.rectTransform, 0);
        return badge.transform;
    }

    /// <summary>The network's badge; a muted one with its core symbol for a network the demo has no badge for.</summary>
    static NetworkBadge BadgeOf(WaxNetwork network) =>
        Badges.FirstOrDefault(b => b.Name == network.Name) ?? new NetworkBadge(network.Name, network.CoreSymbol ?? "", MutedColor, false);

    /// <summary>The dropdown text: the name, plus a small muted "testnet" tag on testnets whose name does not say so.</summary>
    static string LabelOf(WaxNetwork network) =>
        BadgeOf(network).IsTestnet && network.Name.IndexOf("testnet", StringComparison.OrdinalIgnoreCase) < 0
            ? $"{network.Name}  <size=16><color=#{ColorUtility.ToHtmlStringRGBA(MutedColor)}>testnet</color></size>"
            : network.Name;

    static void ShowBadge(Transform badge, NetworkBadge network)
    {
        badge.GetComponent<Image>().color = network.RingColor;
        badge.Find("Fill").GetComponent<Image>().color = network.FillColor;
        var code = badge.GetComponentInChildren<Text>();
        code.text = network.Code;
        code.color = network.CodeColor;
    }

    void AddSectionLabel(Transform parent, string label)
    {
        var text = AddText(parent, label, 16, 24);
        text.fontStyle = FontStyle.Bold;
        text.color = MutedColor;
        text.alignment = TextAnchor.LowerLeft;
    }

    Transform AddCard(Transform parent, Color color, float height)
    {
        var card = new GameObject("Card", typeof(RectTransform), typeof(Image));
        AddToLayout(card, parent, height);
        SetRounded(card.GetComponent<Image>(), color, 1.5f);
        return card.transform;
    }

    Text AddText(Transform parent, string value, int fontSize, float height)
    {
        var text = NewText(parent, value, fontSize, Theme.Text, TextAnchor.MiddleLeft);
        AddToLayout(text.gameObject, parent, height);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    Text NewText(Transform parent, string value, int fontSize, Color color, TextAnchor alignment)
    {
        var text = DefaultControls.CreateText(resources).GetComponent<Text>();
        text.transform.SetParent(parent, false);
        text.text = value;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = alignment;
        text.raycastTarget = false;
        return text;
    }

    void SetRounded(Image image, Color color, float cornerScale)
    {
        image.sprite = roundedSprite;
        image.type = Image.Type.Sliced;
        // Above 1 makes the corners smaller (radius 20 / cornerScale).
        image.pixelsPerUnitMultiplier = cornerScale;
        image.color = color;
    }

    static LayoutElement AddToLayout(GameObject child, Transform parent, float height)
    {
        child.transform.SetParent(parent, false);
        var element = child.AddComponent<LayoutElement>();
        element.preferredHeight = height;
        return element;
    }

    static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }

    static Color Shade(Color color, float factor) => new Color(color.r * factor, color.g * factor, color.b * factor, color.a);

    /// <summary>A white anti-aliased rounded square (a circle when radius is half the size), 9-sliced by border.</summary>
    static Sprite CreateSprite(int size, float radius, int border)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var point = new Vector2(x + 0.5f, y + 0.5f);
            var center = new Vector2(Mathf.Clamp(point.x, radius, size - radius), Mathf.Clamp(point.y, radius, size - radius));
            var alpha = Mathf.Clamp01(radius - Vector2.Distance(point, center) + 0.5f);
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0,
            SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }

    /// <summary>The dropdown badge of a network, by network name.</summary>
    sealed class NetworkBadge
    {
        public readonly string Name;
        public readonly string Code;
        public readonly Color RingColor;
        public readonly Color FillColor;
        public readonly Color CodeColor;
        public readonly bool IsTestnet;

        /// <summary>A solid badge with white code.</summary>
        public NetworkBadge(string name, string code, Color badgeColor, bool isTestnet)
            : this(name, code, badgeColor, badgeColor, Color.white, isTestnet)
        {
        }

        public NetworkBadge(string name, string code, Color ringColor, Color fillColor, Color codeColor, bool isTestnet)
        {
            Name = name;
            Code = code;
            RingColor = ringColor;
            FillColor = fillColor;
            CodeColor = codeColor;
            IsTestnet = isTestnet;
        }
    }

    /// <summary>Dropdown that reports each list row it creates, in option order.</summary>
    sealed class NetworkDropdown : Dropdown
    {
        public Action<Transform, int> ItemCreated;
        int nextIndex;

        protected override GameObject CreateDropdownList(GameObject template)
        {
            nextIndex = 0;
            return base.CreateDropdownList(template);
        }

        protected override DropdownItem CreateItem(DropdownItem itemTemplate)
        {
            var item = base.CreateItem(itemTemplate);
            ItemCreated?.Invoke(item.transform, nextIndex++);
            return item;
        }
    }
}
