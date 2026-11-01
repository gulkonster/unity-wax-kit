using System;
using System.Linq;
using System.Threading.Tasks;
using Gulkonster.WaxKit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>Unity WAX Kit demo. Builds its own uGUI so the scene needs nothing else.</summary>
public class WaxKitDemo : MonoBehaviour
{
    // Wallets show the app name as an account-style name: up to 12 characters, a-z, 1-5 and dots.
    const string AppName = "waxkitdemo";
    const string Memo = "";

    static readonly WaxNetwork[] Networks = { WaxNetwork.Testnet, WaxNetwork.Mainnet };

    readonly DefaultControls.Resources resources = new DefaultControls.Resources();
    InputField toInput;
    Button myCloudWalletButton;
    Text sessionText;
    Text statusText;

    void Start()
    {
        BuildUi();
        WaxKit.Initialize(Networks[0], AppName);
        ShowSession();
        SetStatus("Ready.");
        // Picks up the saved session, or finishes a login the player approved while the game was closed.
        Restore();
    }

    // ---- Actions ----

    void OnNetworkChanged(int index)
    {
        WaxKit.Initialize(Networks[index], AppName);
        ShowSession();
        SetStatus($"Network: {WaxKit.Network.Name}");
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
            quantity = "0.00010000 WAX",
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
        var balance = result.Rows.Count > 0 ? (string)result.Rows[0]["balance"] : "0.00000000 WAX";
        return $"Balance of {WaxKit.Current.Actor}: {balance}";
    });

    void ReadNfts() => Run("Reading NFTs...", async () =>
    {
        if (!WaxKit.IsLoggedIn) return "Log in first.";

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

    void ShowSession()
    {
        // Only offer wallets that work on this network (My Cloud Wallet: WAX mainnet only).
        var myCloudWallet = WaxKit.Wallets.FirstOrDefault(w => w.Id == "mycloudwallet");
        myCloudWalletButton.interactable = myCloudWallet != null && myCloudWallet.SupportsChain(WaxKit.Network);

        sessionText.text = WaxKit.IsLoggedIn
            ? $"Session: {WaxKit.Current} on {WaxKit.Network.Name}"
            : $"Session: not logged in ({WaxKit.Network.Name})";
    }

    void SetStatus(string message, bool isError = false)
    {
        statusText.text = message;
        statusText.color = isError ? new Color(0.75f, 0.1f, 0.1f) : Color.black;
        if (isError) Debug.LogWarning(message);
    }

    // ---- UI ----

    void BuildUi()
    {
        if (FindAnyObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        // Expand keeps the whole 720x1000 layout on screen in both portrait (phone) and landscape (desktop).
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(720, 1000);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        panel.transform.SetParent(canvasObject.transform, false);
        panel.GetComponent<Image>().color = new Color(0.93f, 0.94f, 0.96f);
        var panelRect = (RectTransform)panel.transform;
        panelRect.sizeDelta = new Vector2(680, 960);
        var layout = panel.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(20, 20, 20, 20);
        layout.spacing = 12;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        AddText(panel.transform, "Unity WAX Kit Demo", 34, 56).fontStyle = FontStyle.Bold;

        var dropdown = DefaultControls.CreateDropdown(resources).GetComponent<Dropdown>();
        AddToLayout(dropdown.gameObject, panel.transform, 64);
        dropdown.ClearOptions();
        dropdown.AddOptions(new System.Collections.Generic.List<string> { "Testnet", "Mainnet" });
        dropdown.captionText.fontSize = 24;
        SizeDropdownList(dropdown, 24, 64);
        dropdown.onValueChanged.AddListener(OnNetworkChanged);

        var loginRow = AddRow(panel.transform);
        AddButton(loginRow, "Login Anchor", () => Login(WalletType.Anchor));
        myCloudWalletButton = AddButton(loginRow, "Login My Cloud Wallet", () => Login(WalletType.MyCloudWallet));

        var sessionRow = AddRow(panel.transform);
        AddButton(sessionRow, "Restore", Restore);
        AddButton(sessionRow, "Logout", Logout);

        sessionText = AddText(panel.transform, "", 24, 64);

        toInput = DefaultControls.CreateInputField(resources).GetComponent<InputField>();
        AddToLayout(toInput.gameObject, panel.transform, 64);
        foreach (var text in toInput.GetComponentsInChildren<Text>(true)) text.fontSize = 24;
        ((Text)toInput.placeholder).text = "To (WAX account)";
        toInput.characterLimit = 12;
        KeepTypedText(toInput);

        var actionRow = AddRow(panel.transform);
        AddButton(actionRow, "Send 0.0001 WAX", Send);
        AddButton(actionRow, "Read balance", ReadBalance);
        AddButton(actionRow, "Read NFTs", ReadNfts);

        statusText = AddText(panel.transform, "", 22, 220);
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
        AddToLayout(row, parent, 72);
        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 12;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = true;
        return row.transform;
    }

    Button AddButton(Transform row, string label, Action onClick)
    {
        var button = DefaultControls.CreateButton(resources).GetComponent<Button>();
        button.transform.SetParent(row, false);
        var text = button.GetComponentInChildren<Text>();
        text.text = label;
        text.fontSize = 24;
        button.onClick.AddListener(() => onClick());
        return button;
    }

    /// <summary>The default dropdown list is tiny (14 px text, 20 px rows): make its rows as big as the other controls.</summary>
    static void SizeDropdownList(Dropdown dropdown, int fontSize, float rowHeight)
    {
        var template = dropdown.template;
        var item = dropdown.itemText.transform.parent as RectTransform;
        var content = item.parent as RectTransform;

        dropdown.itemText.fontSize = fontSize;
        item.sizeDelta = new Vector2(item.sizeDelta.x, rowHeight);
        content.sizeDelta = new Vector2(content.sizeDelta.x, rowHeight);
        // Room for every option without scrolling.
        template.sizeDelta = new Vector2(template.sizeDelta.x, rowHeight * dropdown.options.Count + 8);

        // The checkmark and the text indent scale with the row.
        var checkmark = item.Find("Item Checkmark") as RectTransform;
        if (checkmark != null) checkmark.sizeDelta = new Vector2(rowHeight * 0.6f, rowHeight * 0.6f);
        var label = dropdown.itemText.rectTransform;
        label.offsetMin = new Vector2(rowHeight * 0.8f, label.offsetMin.y);
    }

    Text AddText(Transform parent, string value, int fontSize, float height)
    {
        var text = DefaultControls.CreateText(resources).GetComponent<Text>();
        AddToLayout(text.gameObject, parent, height);
        text.text = value;
        text.fontSize = fontSize;
        text.color = Color.black;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    static void AddToLayout(GameObject child, Transform parent, float height)
    {
        child.transform.SetParent(parent, false);
        child.AddComponent<LayoutElement>().preferredHeight = height;
    }
}
