using UnityEngine;


namespace Bertec
{
	/// <summary>
	/// Manages passthrough view state, including hiding/showing the main scene and handling passthrough camera logic.
	/// </summary>
	public class PassThroughViewContainer : MonoBehaviour
	{
		/// <summary>
		/// The main scene to hide when the passthrough is turned on; if null, then both the passthrough and scene are visible.
		/// </summary>
		/// <remarks>If not set, the Framework will automatically use the gameobject named "MainScene", if any.</remarks>
		[Tooltip("If not set, defaults to 'MainScene' game object")]
		public GameObject Scene = null;

		/// <summary>
		/// The headset eye camera; this is not the same as the 'other' main camera or the external camera hardware.
		/// </summary>
		/// <remarks>If not set, the Framework will automatically find the camera object. Required so that passthrough works correctly.</remarks>
		public Camera XRRigMainCamera = null;

		/// <summary>
		/// Determines whether the controller button bypass is allowed. Only clear this if your scene requires all buttons.
		/// </summary>
		public bool AllowControlerButtonBypass = true;

		private void Start()
		{
			Bertec.SystemDisplayDeviceManager.MonoStart(Scene, XRRigMainCamera, AllowControlerButtonBypass);
		}

		public void OnDestroy()
		{
			Bertec.SystemDisplayDeviceManager.MonoDestroy(Scene, XRRigMainCamera);
		}

		// called when the application is paused or resumed; will also be called when the app is launched
		void OnApplicationPause(bool pauseStatus)
		{
			if (!pauseStatus && Bertec.SystemDisplayDeviceManager.PassThroughEnabled)
				OnAppResume(); // The app has resumed from pause
		}

		// called when the application gains or loses focus
		void OnApplicationFocus(bool hasFocus)
		{
			if (hasFocus && Bertec.SystemDisplayDeviceManager.PassThroughEnabled)
				OnAppResume(); // The app has resumed from losing focus
		}

		void OnAppResume()
		{
			Debug.Log("App has resumed from standby or sleep mode, turning off passthrough");
			Bertec.SystemDisplayDeviceManager.TurnOffPassthrough();
		}

		void Update()
		{
			Bertec.SystemDisplayDeviceManager.MonoUpdate();
		}
	}
}