using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gulkonster.WaxKit
{
    public enum WaxGestureAction
    {
        Continue,
        OpenAnchor,
    }

    /// <summary>
    /// Put on a uGUI Button that continues a wallet step or opens Anchor. Browsers only let a page open a
    /// window or an app link inside a real browser event, and Unity clicks are not one. So on pointer down this
    /// arms the action, and the browser's next pointerup / touchend runs it.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public sealed class WaxGestureButton : MonoBehaviour, IPointerDownHandler
    {
        public WaxGestureAction action;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!GetComponent<Button>().IsInteractable()) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            WebGLWalletProvider.Arm(action == WaxGestureAction.Continue ? "continue" : "openAnchor");
#endif
        }
    }
}
