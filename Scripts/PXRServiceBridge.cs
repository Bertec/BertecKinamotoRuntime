////////////////////////////////////////////////////////////////////////
// This class provides needed functionality to properly init the PICO SDK and connect in the UI handlers.
// Without this class being tied into the Framework Initialization system, the PICO SDK will not be properly initialized and your scene will not work.

#if UNITY_ANDROID

using System;
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
		// Set by Pico's BindEnterpriseService callback. Marked volatile because the callback fires on Pico's
		// binder thread while readers may be on Unity worker threads -- without volatile, the JIT can cache
		// the value and never observe the cross-thread write (which is exactly what was happening before).
		internal static volatile bool bridgeConnected = false;

		internal static bool initialized = false;

		internal static void Init()
		{
			// Guard first, BEFORE we allocate the stopwatch or emit any log lines. The Framework Init
			// machinery has historically called this more than once (e.g. when re-entering scenes that
			// re-invoke the Pico bootstrap path), and the prior "log begin, then check guard" ordering
			// produced a confusing "Init begin" line that never had a matching "Init done" line because
			// the guard fired immediately after. Skipped-because-already-initialized is a distinct
			// outcome from a fresh init, so it gets its own log line and we return without doing any
			// timing or allocation work.
			if (initialized)
			{
				Bertec.ExDebug.Log("HMD PXRServiceBridge.Init skipped (already initialized)");
				return;
			}
			initialized = true;

			var bootSw = System.Diagnostics.Stopwatch.StartNew();
			Bertec.ExDebug.Log("HMD PXRServiceBridge.Init begin");

			// Tracks whether the main init body completed without throwing. The final "Init done" log
			// is annotated with this flag's value, so a partial / failed init is no longer indistinguishable
			// from a successful one in logcat.
			bool initSucceeded = true;

			try
			{
				var phaseSw = System.Diagnostics.Stopwatch.StartNew();
#if !UNITY_EDITOR
				PXR_System.InitEnterpriseService();	// skip this when using the editor to avoid error messages
#endif

				// BindEnterpriseService is fire-and-forget; the callback eventually flips bridgeConnected (now
				// volatile so cross-thread reads observe the write). We deliberately do NOT block on it -- the
				// previous flat Thread.Sleep(95) and a follow-up bounded poll were both useless: in practice the
				// callback often fires AFTER subsequent Pico calls already succeed, and the next call returning
				// data is itself the proof that the binder is up. Any genuinely flaky early call (e.g. EQUIPMENT_SN
				// returning empty) is already handled by retry loops further down. Saving ~95 ms per cold start.
				PXR_System.BindEnterpriseService(f =>
				{
					bridgeConnected = f;
				});
				Bertec.ExDebug.Log($"PXRServiceBridge.Init: enterprise binder bind kicked off (phaseElapsed={phaseSw.ElapsedMilliseconds}ms)");
				phaseSw.Restart();

				Unity.XR.PXR.PXR_Plugin.System.UPxr_InitAudioDevice(); // this needs to be called before messing with the screen brightness, volume, or power
				Bertec.ExDebug.Log($"PXRServiceBridge.Init: UPxr_InitAudioDevice took {phaseSw.ElapsedMilliseconds}ms");
				phaseSw.Restart();

				// Eye tracking init blocks for ~300 ms when called on the main thread. Bertec scenes don't consume eye
				// data in the first frames of a scene (the user is still putting the headset on / blinking), so we kick
				// it off on a background thread and let it complete asynchronously. The Pico SDK calls themselves are
				// thread-safe; if anything queries eye tracking before the init finishes it just returns "not yet ready"
				// which is the same state we'd see on a hardware sensor that hasn't calibrated yet.
				ThreadPool.QueueUserWorkItem(_ =>
				{
					var eyeSw = System.Diagnostics.Stopwatch.StartNew();
					try
					{
						var trackingState = (Unity.XR.PXR.TrackingStateCode)Unity.XR.PXR.PXR_MotionTracking.WantEyeTrackingService();

						Unity.XR.PXR.EyeTrackingStartInfo info = new Unity.XR.PXR.EyeTrackingStartInfo();
						info.needCalibration = 0;
						info.mode = Unity.XR.PXR.EyeTrackingMode.PXR_ETM_BOTH;
						int r = Unity.XR.PXR.PXR_MotionTracking.StartEyeTracking(ref info);
						Bertec.ExDebug.Log($"PXRServiceBridge.Init: eye tracking init (background) took {eyeSw.ElapsedMilliseconds}ms (trackingState={trackingState}, startResult={r})");
					}
					catch (System.Exception ex)
					{
						Bertec.ExDebug.LogError("PXRServiceBridge Exception while calling StartEyeTracking " + ex.ToString());
					}
				});

				// UPxr_InitEyeTracking deliberately not called here; would add another ~300ms blocking on main thread.
				///PXR_Plugin.System.UPxr_InitEyeTracking();

				PXR_System.AcquireWakeLock();
				Bertec.ExDebug.Log($"PXRServiceBridge.Init: AcquireWakeLock + eye tracking dispatch took {phaseSw.ElapsedMilliseconds}ms");
				phaseSw.Restart();

				string thisPackage = Bertec.DeviceInformation.ApplicationPackageName;

				// DeviceInformation.ApplicationPackageName can be the empty string if VersionNumberResource.Init
				// threw or hasn't run yet. Calling PXR_Enterprise.AppKeepAlive with an empty package name is at
				// best a no-op and at worst undefined behaviour inside Pico's TobService (it indexes the package
				// in a system-side keep-alive map), so we skip the call and warn rather than passing "" through.
				// The cost of skipping on the Loader is that Pico OS will treat it like any other app and may
				// background-kill it under memory pressure; the resulting scene-switch hang is recoverable but
				// confusing, hence the explicit warning so the failure mode is greppable in logcat.
				if (string.IsNullOrEmpty(thisPackage))
				{
					Bertec.ExDebug.LogWarning("PXRServiceBridge: skipping AppKeepAlive -- ApplicationPackageName is empty (VersionNumberResource.Init may have failed). Pico OS will not be told to keep this APK resident; if this is the Loader, expect occasional background-kill under memory pressure.");
				}
				else
				{
					bool isLoaderPackage = thisPackage.IndexOf("loader", StringComparison.OrdinalIgnoreCase) >= 0;

					// Only the Loader is kept alive by Pico OS. Scene APKs are deliberately NOT marked keep-alive --
					// keeping multiple VR scenes resident at once is wasteful given the headset's RAM budget, and only
					// the active scene needs to be running. The exit-prompt and cold-start cost of cross-APK switches
					// is mitigated separately by PicoEnterpriseLauncher killing the prior scene before launching the
					// next one (Pico cannot show "Exit / Cancel" for an app that is already gone).
					PXR_System.AppKeepAlive(thisPackage, isLoaderPackage, 0);
					Bertec.ExDebug.Log($"PXRServiceBridge AppKeepAlive package={thisPackage}, enabled={isLoaderPackage}");
				}

				// Keep the wifi on even when the headset is asleep (which should allow the main program to always connect)
				PXR_System.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_POWER_CTRL_WIFI_ENABLE, SwitchEnum.S_ON);
				Bertec.ExDebug.Log($"PXRServiceBridge.Init: AppKeepAlive + WIFI on took {phaseSw.ElapsedMilliseconds}ms");
				phaseSw.Restart();

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
				Bertec.ExDebug.Log($"PXRServiceBridge.Init: device info + audio mgr took {phaseSw.ElapsedMilliseconds}ms");

			}
			catch (System.Exception ex)
			{
				initSucceeded = false;
				Bertec.ExDebug.LogError("PXRServiceBridge.Init exception: " + ex.ToString());
			}

			// Install the Pico Enterprise StartActivity adapter as the framework's NativeActivityLauncher fast path.
			// This lets the Loader bypass Android Background Activity Start (BAS) restrictions when launching scene APKs
			// while the Loader's own Activity is backgrounded. See PicoEnterpriseLauncher.cs for the full rationale.
			// We register unconditionally on Pico/Android builds; if the underlying binder is not actually available, the
			// adapter returns false and the framework falls back to the standard Activity.startActivity() path.
			try
			{
				PicoEnterpriseLauncher.Register();
			}
			catch (System.Exception ex)
			{
				Bertec.ExDebug.LogWarning("PXRServiceBridge.Init: PicoEnterpriseLauncher.Register failed (non-fatal): " + ex.ToString());
			}


			Bertec.PXRManager_Impl.CreatePicoPxrManager = (GameObject containerRig) =>
			{
				if (containerRig == null)
				{
					Bertec.ExDebug.LogError("PXRServiceBridge.CreatePicoPxrManager: containerRig is null");
					return null;
				}
				var realPxrManager = containerRig.AddComponent<Unity.XR.PXR.PXR_Manager>();
				if (realPxrManager == null)
				{
					Bertec.ExDebug.LogError("PXRServiceBridge.CreatePicoPxrManager: failed to add PXR_Manager component");
				}

				Bertec.ExDebug.Log("Unity.XR.PXR.PXR_Manager created");
				return realPxrManager;
			};

			string initStatus = initSucceeded ? "done" : "completed WITH ERRORS (see prior PXRServiceBridge.Init exception log)";
			Bertec.ExDebug.Log($"HMD PXRServiceBridge.Init {initStatus} in {bootSw.ElapsedMilliseconds}ms (eye tracking still completing on background thread)");
		}


	

	}

}

#endif
