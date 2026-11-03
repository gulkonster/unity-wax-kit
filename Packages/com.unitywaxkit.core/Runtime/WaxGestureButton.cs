using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityWaxKit
{
    /// <summary>What a <see cref="WaxGestureButton"/> does when pressed.</summary>
    public enum WaxGestureAction
    {
        /// <summary>Answers a Continue step (<see cref="WaxUiRequest.Continue"/>).</summary>
        Continue,
        /// <summary>Opens the wallet app (<see cref="WaxUiRequest.OpenAnchor"/>), the "Open Anchor" button.</summary>
        OpenAnchor,
    }

    /// <summary>
    /// Put on a uGUI Button that continues a wallet step or opens the wallet app. Browsers only let a page open a
    /// window or an app link inside a real browser event, and Unity clicks are not one. So on WebGL this arms the action
    /// on pointer down and the browser's next pointerup / touchend runs it. On other platforms the click runs it.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public sealed class WaxGestureButton : MonoBehaviour, IPointerDownHandler
    {
        /// <summary>Set in the Inspector or in code: Continue or OpenAnchor.</summary>
        public WaxGestureAction action;

        string ActionName => action == WaxGestureAction.Continue ? "continue" : "openAnchor";

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>Arms the action; the browser's next pointerup / touchend runs it.</summary>
        public void OnPointerDown(PointerEventData eventData)
        {
            if (GetComponent<Button>().IsInteractable()) Platform.WaxWebGLPlatform.Arm(ActionName);
        }
#else
        /// <summary>Nothing on this platform: the button's click runs the action.</summary>
        public void OnPointerDown(PointerEventData eventData)
        {
        }

        void Awake() => GetComponent<Button>().onClick.AddListener(Run);

        void Run()
        {
            var request = WaxUiRequest.Active;
            if (request == null) return;
            if (ActionName == "continue") request.Continue();
            else request.OpenAnchor();
        }
#endif
    }
}
