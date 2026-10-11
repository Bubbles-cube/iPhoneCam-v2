using System;
using System.Runtime.InteropServices;
using DirectN;
using VCamNetSampleSource.Utilities;

namespace VCamNetSampleSource
{
    /// <summary>
    /// 虛擬鏡頭啟動器基底：橫向（Landscape）與直向（Portrait）各註冊一個 CLSID，
    /// 共用同一份 MediaSource 實作，差別只在輸出畫布方向（供 OBS／Discord 各選一個）。
    /// </summary>
    public abstract class ActivatorBase : MFAttributes, IMFActivateImpl, ICustomQueryInterface
    {
        protected abstract bool Portrait { get; }

        protected ActivatorBase()
        {
            EventProvider.LogInfo($"ctor portrait:{Portrait} commandLine:{Environment.CommandLine}");
            SetDefaultAttributes(this);
        }

        private void SetDefaultAttributes(IMFAttributes attributes)
        {
            attributes.SetUINT32(MFConstants.MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1).ThrowOnError();
            attributes.SetGUID(MFConstants.MFT_TRANSFORM_CLSID_Attribute, GetType().GUID).ThrowOnError();
        }

        public HRESULT ActivateObject(Guid riid, out nint ppv)
        {
            try
            {
                EventProvider.LogInfo($"{riid}");
                if (riid == typeof(IMFMediaSourceEx).GUID || riid == typeof(IMFMediaSource).GUID)
                {
                    var source = new MediaSource(Portrait);
                    SetDefaultAttributes(source);
                    ppv = ComObject.QueryObjectInterface(source, riid, false);
                    if (ppv != IntPtr.Zero)
                        return HRESULTS.S_OK;
                }

                ppv = 0;
                EventProvider.LogInfo($"{riid} => E_NOINTERFACE");
                return HRESULTS.E_NOINTERFACE;
            }
            catch (Exception e)
            {
                EventProvider.LogError(e.ToString());
                throw;
            }
        }

        public HRESULT ShutdownObject()
        {
            EventProvider.LogInfo();
            return HRESULTS.S_OK;
        }

        public HRESULT DetachObject()
        {
            EventProvider.LogInfo();
            return HRESULTS.S_OK;
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
        {
            EventProvider.LogInfo($"iid{iid:B}");
            ppv = 0;
            return CustomQueryInterfaceResult.NotHandled;
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type type)
        {
            EventProvider.LogInfo("register type:" + type);
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type type)
        {
            EventProvider.LogInfo("unregister type:" + type);
        }
    }

    /// <summary>橫向虛擬鏡頭（畫布寬 &gt; 高）。</summary>
    [ComVisible(true), Guid(Shared.CLSID_VCamNet)]
    public class Activator : ActivatorBase
    {
        protected override bool Portrait => false;
    }

    /// <summary>直向虛擬鏡頭（畫布高 &gt; 寬）。</summary>
    [ComVisible(true), Guid(Shared.CLSID_VCamNetPortrait)]
    public class ActivatorPortrait : ActivatorBase
    {
        protected override bool Portrait => true;
    }
}
