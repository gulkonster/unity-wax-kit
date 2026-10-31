// Forwards C# calls to window.WaxKitJs (defined by waxkit.bundle.jspre) and
// sends results and wallet UI events back to the WaxKitBridge GameObject as JSON.
var WaxKitPlugin = {
  WaxKit_Init: function (configJson) {
    window.WaxKitJs.init(JSON.parse(UTF8ToString(configJson)), function (eventJson) {
      SendMessage('WaxKitBridge', 'OnUiEvent', eventJson);
    });
  },

  WaxKit_Call: function (requestId, method, argsJson) {
    var name = UTF8ToString(method);
    var args = JSON.parse(UTF8ToString(argsJson));
    window.WaxKitJs.call(name, args).then(function (result) {
      result.id = requestId;
      SendMessage('WaxKitBridge', 'OnResult', JSON.stringify(result));
    });
  },

  WaxKit_Answer: function (promptId, action) {
    window.WaxKitJs.answer(promptId, UTF8ToString(action));
  },

  WaxKit_Arm: function (action) {
    window.WaxKitJs.arm(UTF8ToString(action));
  },
};

mergeInto(LibraryManager.library, WaxKitPlugin);
