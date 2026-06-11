// This class impliments the screen brightness, head rotation reset, and most importantly, the passthrough functions for Pico headsets.
// Note that with the way the Pico works, for passthrough mode to work properly, you must disable the main scene (so nothing is rendered) and set the main camera to clear to a solid color with zero alpha.

#if UNITY_ANDROID

using UnityEngine;
using Unity.XR.PXR;
// Aliases to keep things a little simpler
using PXR_System = Unity.XR.PICO.TOBSupport.PXR_Enterprise;


namespace BertecHMD
{
	internal class SystemDisplayDeviceManagerImpl : Bertec.SystemDisplayDeviceManagerInterface
	{
		// Copied from the PassThroughViewContainer in MonoStart
		private GameObject Scene = null;
		private bool AffectSceneOnPassthroughToggle = true;
		private Camera XRRigMainCamera = null;
		private bool AllowControlerButtonBypass = true;

		private CameraClearFlags orignalMainCamClearFlags;   // initial states so can be reset when passthrough is toggled.
		private Color orignalMainCamBackgroundColor;
		private bool orignalMainCamAllowHDR;
		private int _needPassChange = 0;
		private bool _cameraBackgroundChangeSubscribed = false;
		private static readonly Color PassthroughMainCamBackgroundColor = new Color(0f, 0f, 0f, 0f);

		// A simple button reader for the passthrough to track the x/a buttons as press-release
		private class ButtonReader
		{
			public ButtonReader(bool isRightController)
			{
				Bertec.ExecuteOnMainThread.AddAction(() =>
				{
					controllerDevice = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(isRightController ? UnityEngine.XR.XRNode.RightHand : UnityEngine.XR.XRNode.LeftHand);
					if (!controllerDevice.isValid && !UnityEngine.Application.isEditor)
					{
						string s = isRightController ? "right" : "left";
						Bertec.ExDebug.LogError($"SystemDisplayDeviceManagerImpl unable to get {s} controller");
					}
				});
			}

			// Returns true when the buttons presses and then releases
			public bool buttonToggled()
			{
				if (!controllerDevice.isValid)
					return false;

				bool isDown = false;
				controllerDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out isDown);
				if (isDown != currentlyDown)
				{
					currentlyDown = isDown;
					if (!isDown)
						return true;
				}

				return false;
			}

			private bool currentlyDown = false;
			private UnityEngine.XR.InputDevice controllerDevice = default;
		}

		private static ButtonReader leftButton = null;
		private static ButtonReader rightButton = null;

		internal static void Init()
		{
			Bertec.ExDebug.Log("HMD SystemDisplayDeviceManagerImpl.Init");
			Bertec.SystemDisplayDeviceManager.managerInterface = new BertecHMD.SystemDisplayDeviceManagerImpl();
			Bertec.SystemDisplayDeviceManager.interfaceRequiresMainThread = true;
			Bertec.SystemDisplayDeviceManager.OnEnableHeadsetPassthrough += _EnableHeadsetPassthrough;
		}

		public int GetCurrentFPS()
		{
			return Unity.XR.PXR.PXR_Plugin.System.UPxr_GetConfigInt(Unity.XR.PXR.ConfigType.RenderFPS);
		}

		// Resetting the headset position is also accomplished by holding the HOME button on the headset or controller.
		// Note that in all cases (software or via the button), the Pico will only reset the X axis; it will *NOT* reset the Y or Z rotations (nodding/tilting)
		// Fix: this needs to do its work on the main gui thread, not the websocket callback thread, so schedule it.
		public void ResetHeadsetPosition()
		{
			Bertec.ExecuteOnMainThread.AddAction(() =>
			{
				try
				{
					// both of these code options work exactly the same, so calling one vs the other doesn't seem to matter.
					// We're going to default to the Unity XR model instead of Pico since the Pico one is not well documented and might change.
#if false
                //PXR_Plugin.Sensor.UPxr_ResetSensor(ResetSensorOption.ResetRotation);
                PXR_Plugin.Sensor.UPxr_ResetSensor(ResetSensorOption.ResetRotationYOnly);  // behaves the same as ResetRotation so no benefit over ResetRotation
#else
					var i = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head).subsystem;
					var o = i.GetTrackingOriginMode();
					i.TrySetTrackingOriginMode(UnityEngine.XR.TrackingOriginModeFlags.Device);
					// Unbounded works pretty good, but resets the front-back translation in sensory
					// TrackingReference does not work at all
					// Floor is what Sensory has which is a problem for that scene
					// Device is what we had before and is what all the other scenes are using and also does the same as Unbounded
					i.TryRecenter();
					i.TrySetTrackingOriginMode(o);
#endif
				}
				catch (System.Exception ex)
				{
					Debug.LogException(ex);
				}
			});
		}

		protected UnityEngine.Camera FindXRRigMainCamera()
		{
			var found = UnityEngine.Object.FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>();
			if (found != null)
			{
				if (found.Length != 0)
				{
					var picoVR = found[0].gameObject;
					return picoVR.GetComponentInChildren<UnityEngine.Camera>();
				}
			}
			return null;
		}

		// Called from the PassThroughViewContainer.Start
		public void MonoStart(GameObject _scene, bool _affectSceneOnPassthroughToggle, Camera _xrRigMainCamera, bool _allowControlerButtonBypass)
		{
			_needPassChange = 0;

			AllowControlerButtonBypass = _allowControlerButtonBypass;

			Scene = _scene;
			if (Scene == null)
			{
				Scene = GameObject.Find("MainScene");
			}

			// Central PassthroughRuntimeController now manages render suppression; keep the scene active to avoid GPU flushes.
			AffectSceneOnPassthroughToggle = _affectSceneOnPassthroughToggle;

			XRRigMainCamera = _xrRigMainCamera;
			if (XRRigMainCamera == null)
			{
				XRRigMainCamera = FindXRRigMainCamera();
			}

			CacheCurrentMainCameraState();
			EnsureMainCameraBackgroundTracking();

			leftButton = new ButtonReader(false);
			rightButton = new ButtonReader(true);


			// Always set passthrough off and let omni know the current state of the passthrough setting so the ui button is tracking correctly.
			Bertec.SystemDisplayDeviceManager.IsPassthrough = Bertec.SystemDisplayDeviceManager.PassThroughEnabled = false;
			Bertec.SystemDisplayDeviceManager.SendCurrentPassthrough();
			Bertec.SystemDisplayDeviceManager.SendCurrentScreenOn();
		}

		public void MonoUpdate()
		{
			if (_needPassChange > 0)
			{
				--_needPassChange;
				if (_needPassChange == 0)
				{
					Bertec.ExDebug.Log("About to turn off scene, PassthroughTrackingState = " + GetPassthroughTrackingState());
					if (AffectSceneOnPassthroughToggle)
					{
						Scene?.SetActive(false);   // hide the scene in a bit
					}
				}
			}

			// Toggle the passthrough when the button on the headset or the controller's "A" or "X" button is pressed.
			//bool togglePassthroughKey = AllowControlerButtonBypass && ((Input.GetKeyDown(KeyCode.JoystickButton0) || Input.GetKeyDown(KeyCode.JoystickButton2)));
			bool togglePassthroughKey = false;

			if (AllowControlerButtonBypass)
			{
				togglePassthroughKey = leftButton.buttonToggled() || rightButton.buttonToggled();
				if (togglePassthroughKey)
					Bertec.SystemDisplayDeviceManager.PassThroughEnabled = !Bertec.SystemDisplayDeviceManager.PassThroughEnabled;
			}

			// If the mode changes either through button or SetPassthroughMode, switch things around
			if (Bertec.SystemDisplayDeviceManager.PassThroughEnabled != Bertec.SystemDisplayDeviceManager.IsPassthrough)
			{
				try
				{
					Bertec.ExDebug.Log("Changing passthrough mode to " + Bertec.SystemDisplayDeviceManager.PassThroughEnabled);

					if (!Bertec.SystemDisplayDeviceManager.IsPassthrough)
					{
						// Refresh the baseline from the active scene camera before passthrough is applied.
						CacheCurrentMainCameraState();
					}

					Bertec.SystemDisplayDeviceManager.IsPassthrough = Bertec.SystemDisplayDeviceManager.PassThroughEnabled;

					// the ordering of the calls is important to avoid ugly scene flickering; set the background/skybox first, then disable the scene, then set the passthrough
					if (XRRigMainCamera != null)
					{
						if (Bertec.SystemDisplayDeviceManager.IsPassthrough)
						{
							// Pico passthrough requires a solid clear with transparent black.
							XRRigMainCamera.backgroundColor = PassthroughMainCamBackgroundColor;
							XRRigMainCamera.clearFlags = CameraClearFlags.SolidColor;
							// With the built-in RP an HDR camera renders via an R11G11B10 intermediate (no alpha
							// channel), so the resolve writes opaque alpha into the eye buffer and the see-through
							// feed is fully occluded (black). Render directly to RGBA8 while passthrough is active.
							XRRigMainCamera.allowHDR = false;
						}
						else
						{
							XRRigMainCamera.clearFlags = orignalMainCamClearFlags;
							XRRigMainCamera.backgroundColor = orignalMainCamBackgroundColor;
							XRRigMainCamera.allowHDR = orignalMainCamAllowHDR;
						}
					}

					if (Bertec.SystemDisplayDeviceManager.IsPassthrough)
					{
						_needPassChange = 5;   // it takes about 5 frames for the cameras to turn on
					}
					else
					{
						_needPassChange = 0; // cancel any pending scene-hide from a previous enter
						if (AffectSceneOnPassthroughToggle)
						{
							Scene?.SetActive(true);
						}
					}

					Bertec.ExDebug.Log("About to change passthrough mode, PassthroughTrackingState = " + GetPassthroughTrackingState());
					Bertec.SystemDisplayDeviceManager.EnableHeadsetPassthrough(Bertec.SystemDisplayDeviceManager.IsPassthrough);
					Bertec.ExDebug.Log("EnableHeadsetPassthrough called, PassthroughTrackingState = " + GetPassthroughTrackingState());

					// Echo back the new setting
					Bertec.ProtocolRPC.IssueCommand(Bertec.RPCCommands.Cmd.PASSTHROUGHCHANGED,
						new Bertec.SystemDisplayDeviceManager.PassthroughChangedData(Bertec.SystemDisplayDeviceManager.IsPassthrough, togglePassthroughKey));

				}
				catch (System.Exception ex)
				{
					Debug.LogException(ex);
					Bertec.ExDebug.LogError("Exception when switching passthrough, trying to revert back without passthrough");

					TurnOffPassthrough();
				}

			}
		}

		public void TurnOffPassthrough()
		{
			try
			{
				if (XRRigMainCamera != null)
				{
					XRRigMainCamera.clearFlags = orignalMainCamClearFlags;
					XRRigMainCamera.backgroundColor = orignalMainCamBackgroundColor;
					XRRigMainCamera.allowHDR = orignalMainCamAllowHDR;
				}

				if (AffectSceneOnPassthroughToggle)
				{
					Scene?.SetActive(true);
				}

				Bertec.SystemDisplayDeviceManager.EnableHeadsetPassthrough(false);
				Bertec.ProtocolRPC.IssueCommand(Bertec.RPCCommands.Cmd.PASSTHROUGHCHANGED,
					new Bertec.SystemDisplayDeviceManager.PassthroughChangedData(false, false));

				Bertec.SystemDisplayDeviceManager.IsPassthrough = false;
				Bertec.SystemDisplayDeviceManager.PassThroughEnabled = false;
				_needPassChange = 0;
			}
			catch (System.Exception ex2)
			{
				Debug.LogException(ex2);
				Bertec.ExDebug.LogError("Exception when reverting passthrough, don't know how to proceed");
			}
		}

		// Android version handles this through the MonoUpdate
		public void PassthroughChanged(bool f)
		{
		}

		/// <inheritdoc/>
		public Bertec.SystemDisplayDeviceManagerInterface.PassthroughTrackingState GetPassthroughTrackingState()
		{
			return (Bertec.SystemDisplayDeviceManagerInterface.PassthroughTrackingState)Unity.XR.PXR.PXR_Boundary.GetSeeThroughTrackingState();
		}

		public void EnableSeeThroughManual(bool f)
		{
			// Keep this legacy entry point from bypassing the framework state machine.
			// Older scene scripts used to call the Pico API directly from LateUpdate, which could leave
			// requested state, active state, and camera-layer restoration disagreeing with each other.
			if (f)
			{
				// Do not call PXR_Boundary.EnableSeeThroughManual(true) here.
				// MonoUpdate owns the ordered transition: cache camera state, set transparent clear,
				// update IsPassthrough, notify render subscribers, then touch the Pico passthrough API.
				// Enabling the hardware directly skips those steps and can leave the scene rendering
				// only the passthrough/UI layer after exit.
				if (!Bertec.SystemDisplayDeviceManager.PassThroughEnabled)
				{
					Bertec.SystemDisplayDeviceManager.PassThroughEnabled = true;
				}

				return;
			}

			TurnOffPassthrough();
		}

		protected static void _EnableHeadsetPassthrough(bool enable)
		{
			try
			{
				Unity.XR.PXR.PXR_Boundary.EnableSeeThroughManual(enable);
			}
			catch (System.Exception ex)
			{
				Debug.LogException(ex);
				Bertec.ExDebug.LogError("Exception when trying to set the passthrough flag to " + enable);
			}
			Bertec.SystemDisplayDeviceManager.PassthroughChanged(enable);
		}

		public void SetScreenOnOff(bool screenon)
		{
			bool currentState = IsScreenOn();
			Bertec.ExDebug.Log("+++ IsScreenOn returned " + currentState);
			if (screenon != currentState)  // try to prevent cycling
			{
				if (screenon)
					PXR_System.ScreenOn();
				else
					PXR_System.ScreenOff();
			}
		}

		public void GetScreenBrightness(out int _min, out int _max, out int _level)
		{
			_min = 0;
			_max = 255;
			_level = Unity.XR.PXR.PXR_System.GetCommonBrightness();
		}

		public void SetScreenBrightness(int newlevel)
		{
			Unity.XR.PXR.PXR_System.SetScreenBrightnessLevel(0, newlevel);
		}

		public void GetScreenOnOff(out bool screenCurrentlyOn)
		{
			screenCurrentlyOn = IsScreenOn();
		}

		internal static bool IsScreenOn()
		{
			if (Application.isEditor)
				return true;
			try
			{
				if (BertecHMD.CurrentAndroidActivity.Valid)
				{
					using (var displayManager = BertecHMD.CurrentAndroidActivity.Activity.Call<AndroidJavaObject>("getSystemService", "display"))
					{
						if (displayManager == null)
						{
							Bertec.ExDebug.LogError("Unable to getSystemService display");
							return false;
						}

						var displays = displayManager.Call<AndroidJavaObject>("getDisplays");
						if (displays == null)
						{
							Bertec.ExDebug.LogError("Unable to getDisplays");
							return false;
						}

						// Use AndroidJNI to work with the Java array
						System.IntPtr displaysArray = displays.GetRawObject();
						int length = AndroidJNI.GetArrayLength(displaysArray);
						if (length == 0)
						{
							Bertec.ExDebug.LogError("getDisplays returned empty array");
							return false;
						}

						for (int i = 0; i < length; i++)
						{
							System.IntPtr displayPtr = AndroidJNI.GetObjectArrayElement(displaysArray, i);
							using (var display = new AndroidJavaObject(displayPtr))
							{
								if (display == null)
									continue;

								int state = display.Call<int>("getState");
								if (state != 1) // 1 == Display.STATE_OFF
									return true;
							}
							AndroidJNI.DeleteLocalRef(displayPtr);
						}
					}
				}
				return false;
			}
			catch (System.Exception ex)
			{
				Bertec.ExDebug.LogError("Exception with IsScreenOn " + ex.ToString());
				return false;
			}
		}

		private void CacheCurrentMainCameraState()
		{
			if (XRRigMainCamera == null)
			{
				return;
			}

			orignalMainCamClearFlags = XRRigMainCamera.clearFlags;
			orignalMainCamBackgroundColor = XRRigMainCamera.backgroundColor;
			orignalMainCamAllowHDR = XRRigMainCamera.allowHDR;
		}

		private void EnsureMainCameraBackgroundTracking()
		{
			if (_cameraBackgroundChangeSubscribed)
			{
				return;
			}

			Bertec.CameraContainer_Impl.CameraBackgroundColorChanged += HandleMainCameraBackgroundColorChanged;
			_cameraBackgroundChangeSubscribed = true;
		}

		private void HandleMainCameraBackgroundColorChanged(Color color, bool revertClearFlagsToSkybox)
		{
			CameraClearFlags clearFlags = revertClearFlagsToSkybox
				? CameraClearFlags.Skybox
				: CameraClearFlags.SolidColor;

			orignalMainCamClearFlags = clearFlags;
			orignalMainCamBackgroundColor = color;

			if (XRRigMainCamera == null || Bertec.SystemDisplayDeviceManager.IsPassthrough)
			{
				return;
			}

			XRRigMainCamera.clearFlags = clearFlags;
			XRRigMainCamera.backgroundColor = color;
		}
	}
}

#endif
