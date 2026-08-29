using System.Runtime.InteropServices;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Memory;
using SwiftlyS2.Shared.Services;

namespace SwiftOnlineMusicPlayerSW2;

internal sealed class CustomHudNativeBridge : IDisposable
{
    private const string SetDialogVariableStringForPlayerSignatureName = "SwiftOnlineMusicPlayerSW2::SetDialogVariableStringForPlayer";
    private const string SetHasClassForPlayerSignatureName = "SwiftOnlineMusicPlayerSW2::SetHasClassForPlayer";
    private const string SetInputCaptureEnabledSignatureName = "SwiftOnlineMusicPlayerSW2::SetInputCaptureEnabled";
    private const string CustomHudClickedReceiverSignatureName = "SwiftOnlineMusicPlayerSW2::CustomHudClickedReceiver";

    private readonly IUnmanagedFunction<SetDialogVariableStringForPlayerDelegate> _setDialogVariableStringForPlayer;
    private readonly IUnmanagedFunction<SetHasClassForPlayerDelegate> _setHasClassForPlayer;
    private readonly IUnmanagedFunction<SetInputCaptureEnabledDelegate> _setInputCaptureEnabled;
    private readonly IUnmanagedFunction<CustomHudClickedReceiverDelegate> _customHudClickedReceiver;
    private Guid? _customHudClickHook;

    private CustomHudNativeBridge(
        IUnmanagedFunction<SetDialogVariableStringForPlayerDelegate> setDialogVariableStringForPlayer,
        IUnmanagedFunction<SetHasClassForPlayerDelegate> setHasClassForPlayer,
        IUnmanagedFunction<SetInputCaptureEnabledDelegate> setInputCaptureEnabled,
        IUnmanagedFunction<CustomHudClickedReceiverDelegate> customHudClickedReceiver)
    {
        _setDialogVariableStringForPlayer = setDialogVariableStringForPlayer;
        _setHasClassForPlayer = setHasClassForPlayer;
        _setInputCaptureEnabled = setInputCaptureEnabled;
        _customHudClickedReceiver = customHudClickedReceiver;
    }

    public static CustomHudNativeBridge Create(IGameDataService gameData, IMemoryService memory) => new(
        Resolve<SetDialogVariableStringForPlayerDelegate>(gameData, memory, SetDialogVariableStringForPlayerSignatureName),
        Resolve<SetHasClassForPlayerDelegate>(gameData, memory, SetHasClassForPlayerSignatureName),
        Resolve<SetInputCaptureEnabledDelegate>(gameData, memory, SetInputCaptureEnabledSignatureName),
        Resolve<CustomHudClickedReceiverDelegate>(gameData, memory, CustomHudClickedReceiverSignatureName));

    public void SetDialogVariableStringForPlayer(nint layout, int playerSlot, string dialogId, string variableName, string value)
    {
        using var dialog = new UtlStringArgument(dialogId);
        using var variable = new UtlStringArgument(variableName);
        using var text = new UtlStringArgument(value);
        _ = _setDialogVariableStringForPlayer.Call(layout, playerSlot, dialog.Address, variable.Address, text.Address);
    }

    public void SetHasClassForPlayer(nint layout, int playerSlot, string dialogId, string className, bool hasClass)
    {
        using var dialog = new UtlStringArgument(dialogId);
        using var classArgument = new UtlStringArgument(className);
        _ = _setHasClassForPlayer.Call(layout, playerSlot, dialog.Address, classArgument.Address, hasClass ? 1u : 0u);
    }

    public void SetInputCaptureEnabled(nint layout, int playerSlot, bool enabled) =>
        _ = _setInputCaptureEnabled.Call(layout, playerSlot, enabled ? (byte)1 : (byte)0);

    public void HookCustomHudClicks(Action<nint, nint, string> handler, Action<Exception> exceptionHandler)
    {
        if (_customHudClickHook is not null)
        {
            throw new InvalidOperationException("The Custom HUD click dispatch is already hooked.");
        }

        _customHudClickHook = _customHudClickedReceiver.AddHook(next =>
            (pulseBinding, playerController, layout, buttonId) =>
            {
                next()(pulseBinding, playerController, layout, buttonId);
                try
                {
                    handler(playerController, layout, ReadUtlString(buttonId));
                }
                catch (Exception exception)
                {
                    exceptionHandler(exception);
                }
            });
    }

    public void Dispose()
    {
        if (_customHudClickHook is { } hook)
        {
            _customHudClickedReceiver.RemoveHook(hook);
            _customHudClickHook = null;
        }
    }

    private static IUnmanagedFunction<TDelegate> Resolve<TDelegate>(IGameDataService gameData, IMemoryService memory, string signatureName)
        where TDelegate : Delegate
    {
        if (!gameData.TryGetSignature(signatureName, out var address) || address == nint.Zero)
        {
            throw new InvalidOperationException(
                $"CCSCustomHudLayout GameData signature '{signatureName}' was not loaded. " +
                "Verify resources/gamedata/signatures.jsonc for the active server.dll build.");
        }

        return memory.GetUnmanagedFunctionByAddress<TDelegate>(address);
    }

    private static string ReadUtlString(nint stringObject)
    {
        if (stringObject == nint.Zero)
        {
            return string.Empty;
        }

        var chars = Marshal.ReadIntPtr(stringObject);
        return chars == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(chars) ?? string.Empty;
    }

    private sealed class UtlStringArgument : IDisposable
    {
        private readonly nint _utf8;
        public nint Address { get; }

        public UtlStringArgument(string value)
        {
            _utf8 = Marshal.StringToCoTaskMemUTF8(value);
            Address = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Address, _utf8);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Address);
            Marshal.FreeCoTaskMem(_utf8);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SetDialogVariableStringForPlayerDelegate(nint layout, int playerSlot, nint dialogId, nint variableName, nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SetHasClassForPlayerDelegate(nint layout, int playerSlot, nint dialogId, nint className, uint hasClass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SetInputCaptureEnabledDelegate(nint layout, int playerSlot, byte enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CustomHudClickedReceiverDelegate(nint pulseBinding, nint playerController, nint layout, nint buttonId);
}
