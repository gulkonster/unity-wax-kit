using System;
using TMPro;
using UnityEngine;

namespace UnityWaxKit
{
    /// <summary>Colors and font of <see cref="WaxDefaultUI"/>. Set it on <see cref="WaxKit.Theme"/>. The QR always stays black on white.</summary>
    [Serializable]
    public sealed class WaxUiTheme
    {
        /// <summary>Color that darkens the game behind the wallet panel (use alpha below 1).</summary>
        public Color Dim;
        /// <summary>Background of the wallet panel.</summary>
        public Color Panel;
        /// <summary>Background of the main button (Open Anchor / Continue).</summary>
        public Color Primary;
        /// <summary>Text color of the main button.</summary>
        public Color PrimaryText;
        /// <summary>Background of the second button (Cancel / Close).</summary>
        public Color Secondary;
        /// <summary>Text color of the second button.</summary>
        public Color SecondaryText;
        /// <summary>Color of titles and body text.</summary>
        public Color Text;
        /// <summary>Null uses the TextMeshPro default font.</summary>
        public TMP_FontAsset Font;

        /// <summary>A new dark theme each time, so changing it never changes the preset.</summary>
        public static WaxUiTheme Dark => new WaxUiTheme
        {
            Dim = new Color(0, 0, 0, 0.6f),
            // WAX brand: orange #F89022 on WAX's dark #121212.
            Panel = new Color32(0x1C, 0x1C, 0x1E, 0xFF),
            Primary = new Color32(0xF8, 0x90, 0x22, 0xFF),
            PrimaryText = new Color32(0x12, 0x12, 0x12, 0xFF),
            Secondary = new Color32(0x2C, 0x2C, 0x2E, 0xFF),
            SecondaryText = new Color32(0xF2, 0xF2, 0xF2, 0xFF),
            Text = new Color32(0xF2, 0xF2, 0xF2, 0xFF),
        };

        /// <summary>A new light theme each time, so changing it never changes the preset.</summary>
        public static WaxUiTheme Light => new WaxUiTheme
        {
            Dim = new Color(0, 0, 0, 0.6f),
            Panel = new Color(0.97f, 0.97f, 0.98f),
            Primary = new Color(0.22f, 0.4f, 0.85f),
            PrimaryText = Color.white,
            Secondary = new Color(0.86f, 0.87f, 0.9f),
            SecondaryText = new Color(0.1f, 0.1f, 0.12f),
            Text = new Color(0.1f, 0.1f, 0.12f),
        };
    }
}
