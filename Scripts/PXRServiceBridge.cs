////////////////////////////////////////////////////////////////////////
// This class provides needed functionality to properly init the PICO SDK and connect in the UI handlers.
// Without this class being tied into the Framework Initialization system, the PICO SDK will not be properly initialized and your scene will not work.

#if UNITY_ANDROID

using System.Threading;
using UnityEngine;

// Aliases to keep things a little simpler
using PXR_System = Unity.XR.PICO.TOBSupport.PXR_Enterprise;
using SystemInfoEnum = Unity.XR.PICO.TOBSupport.SystemInfoEnum;
using SystemFunctionSwitchEnum = Unity.XR.PICO.TOBSupport.SystemFunctionSwitchEnum;
using SwitchEnum = Unity.XR.PICO.TOBSupport.SwitchEnum;

namespace BertecHMD
{
	internal class PXRServiceBridge
	{
		internal static bool bridgeConnected = false;

		internal static bool initilized = false;

		internal static void Init()
		{
			Debug.Log("HMD PXRServiceBridge.Init");
			if (initilized)
				return;
			initilized = true;

			try
			{
#if !UNITY_EDITOR
				PXR_System.InitEnterpriseService();	// skip this when using the editor to avoid error messages
#endif

				// Pico is supposed to invoke this callback, but it never seems to happen
				PXR_System.BindEnterpriseService(f =>
				{
					bridgeConnected = f;
				});

				Thread.Sleep(95); // yielding the thread for a little bit seems to make everything magic (probably because it's expecting BindEnterpriseService to finish and it's not)

				Unity.XR.PXR.PXR_Plugin.System.UPxr_InitAudioDevice(); // this needs to be called before messing with the screen brightness, volume, or power

				try
				{
					var trackingState = (Unity.XR.PXR.TrackingStateCode)Unity.XR.PXR.PXR_MotionTracking.WantEyeTrackingService();

					Unity.XR.PXR.EyeTrackingStartInfo info = new Unity.XR.PXR.EyeTrackingStartInfo();
					info.needCalibration = 0;
					info.mode = Unity.XR.PXR.EyeTrackingMode.PXR_ETM_BOTH;
					int r = Unity.XR.PXR.PXR_MotionTracking.StartEyeTracking(ref info);
				}
				catch (System.Exception ex)
				{
					Debug.LogError("PXRServiceBridge Exception while calling StartEyeTracking " + ex.ToString());
				}

				// Eye tracking init will block the startup by 300ms, which is unacceptable for most applications.
				// If your application simply must have it, uncomment out this line but be aware of the increased startup time.
				///PXR_Plugin.System.UPxr_InitEyeTracking();

				PXR_System.AcquireWakeLock();

				// Keep this app active so the system won't force-quit the app even if it's in the background (which happens around 10 minutes
				// when the headset is on battery).
				PXR_System.AppKeepAlive(Bertec.DeviceInformation.ApplicationPackageName, true, 0);

				// Keep the wifi on even when the headset is asleep (which should allow the main program to always connect)
				PXR_System.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_POWER_CTRL_WIFI_ENABLE, SwitchEnum.S_ON);

				string deviceName = "";
				string serialNumber = "";
#if UNITY_EDITOR
				deviceName = SystemInfo.deviceName; // for Unity dev test runs, just the desktop os name
#else
				try
				{
					using (var build = new AndroidJavaClass("android.os.Build"))
					{
						deviceName = build.GetStatic<string>("DEVICE"); // same as adb devices -l
					}
				}
				catch (System.Exception ex)
				{
					Debug.LogError("PXRServiceBridge Exception " + ex.ToString());
				}

				// Read and update the serial numbers and versions
				serialNumber = PXR_System.StateGetDeviceInfo(SystemInfoEnum.EQUIPMENT_SN); // same as adb devices -l
				if (serialNumber == "")
				{
					// Sometimes the call to StateGetDeviceInfo will return an empty string, and you need to try a few times to get it
					for (int i = 0; i < 20; ++i)
					{
						if (serialNumber == "")
						{
							Thread.Sleep(15);
							serialNumber = PXR_System.StateGetDeviceInfo(SystemInfoEnum.EQUIPMENT_SN);
						}
						else
							break;
					}
				}

#endif

				string APIversion = Unity.XR.PXR.PXR_Plugin.System.UPxr_GetAPIVersion().ToString("x");  // this is in hex

				string SDKversion = Unity.XR.PXR.PXR_Plugin.System.UPxr_GetSDKVersion();

				string firmwareVersion = PXR_System.StateGetDeviceInfo(SystemInfoEnum.PUI_VERSION);

				Bertec.DeviceInformation.UpdateDeviceSerialSdk(serialNumber, deviceName, APIversion, SDKversion, firmwareVersion);

				Bertec.SystemAudioDeviceManager.managerInterface = new SystemAudioDeviceManagerImpl();

			}
			catch (System.Exception ex)
			{
				Debug.LogError("PXRServiceBridge.Init exception: " + ex.ToString());
			}


		

	
		}


	

	}

}

#endif
