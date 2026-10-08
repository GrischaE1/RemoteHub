using System.Runtime.InteropServices;

namespace RemoteHub.Rdp;

// Hand-written COM interop for mstscax.dll (Microsoft RDP ActiveX client).
// vtable order and DISPIDs verified against the type library shipped with Windows.
// Dual/dispatch interfaces (IMsRdpClient9, AdvancedSettings, SecuredSettings) are used late-bound via `dynamic`.

/// <summary>IMsRdpClientNonScriptable (derives from IMsTscNonScriptable) – password + SendKeys.</summary>
[ComImport, Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable
{
    // IMsTscNonScriptable
    void SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
    void SetPortablePassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortablePassword();
    void SetPortableSalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortableSalt();
    void SetBinaryPassword([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinaryPassword();
    void SetBinarySalt([MarshalAs(UnmanagedType.BStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinarySalt();
    void ResetPassword();

    // IMsRdpClientNonScriptable
    void NotifyRedirectDeviceChange(UIntPtr wParam, IntPtr lParam);
    void SendKeys(int numKeys,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] short[] pbArrayKeyUp,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] plKeyData);
}

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    void GetProperty([MarshalAs(UnmanagedType.BStr)] string name, out object value);
}

/// <summary>Event dispinterface of the RDP control (only the events we consume are declared).</summary>
[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(22)] void OnLogonError(int lError);
    [DispId(23)] void OnFocusReleased(int iDirection);
    [DispId(33)] void OnAutoReconnected();
    [DispId(34)] void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount);
}

/// <summary>Direct vtable calls for members that sit behind hidden slots (offsets from the typelib, x64).</summary>
internal static unsafe class VTable
{
    public static readonly Guid IID_NonScriptable5 = new("4F6996D5-D7B1-412C-B0FF-063718566907");
    public const int UseMultimonPut = 424;

    public static void PutBool(object com, Guid iid, int byteOffset, bool value)
    {
        var unk = Marshal.GetIUnknownForObject(com);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in iid, out var itf));
            try
            {
                var vtbl = *(IntPtr**)itf;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, short, int>)vtbl[byteOffset / IntPtr.Size];
                Marshal.ThrowExceptionForHR(fn(itf, value ? (short)-1 : (short)0));
            }
            finally { Marshal.Release(itf); }
        }
        finally { Marshal.Release(unk); }
    }
}
