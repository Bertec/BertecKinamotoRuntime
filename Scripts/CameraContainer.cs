using UnityEngine;

namespace Bertec
{
	public class CameraContainer : MonoBehaviour
	{
		// The reason behind this message is that the 'Camera' object with Omnity.cs attached to it should appear BEFORE the XR Rig object - because both have a Main Camera!
		[Tooltip("If not set, defaults to Camera.main (so ordering is important)")]
		public Camera _mainCamera = null;

		[Tooltip("Defaults to gameobject this script is on")]
		public GameObject _mainContainer = null;

		public GameObject picoPVR = null;

		[HideInInspector]
		public Camera MainCamera => _mainCamera;

		private CameraContainer_Impl _impl;
		private static CameraContainer _instance = null;

		public void Awake()
		{
			_instance = this;

			if (_mainCamera == null)
				_mainCamera = Camera.main;

			if (_mainContainer == null)
				_mainContainer = this.gameObject;

#if UNITY_ANDROID
			if (picoPVR != null)
			{
#if DEBUG
				Debug.Log("CameraContainer using PVR projection; disabling main camera and setting PVR active");
#endif
				GameObject mc = MainCamera.gameObject;
				if (!mc.transform.IsChildOf(picoPVR.transform))  // don't do this if the main camera is the child of the pvr (this should actually never happen now)
					mc.SetActive(false);
				picoPVR.SetActive(true);
				Bertec.CameraDisplayModeStatus.Mode = Bertec.CameraDisplayModeStatus.ModeType.HMD;
			}
			else
			{
				Debug.LogError("PicoPVR object not set properly, the scene may not render like you expect.");
			}
#else
			picoPVR = null;   // always disable this
			if (Bertec.CameraDisplayModeStatus.Mode == Bertec.CameraDisplayModeStatus.ModeType.HMD)
				Bertec.CameraDisplayModeStatus.Mode = Bertec.CameraDisplayModeStatus.ModeType.FlatPanel;
#endif

			_impl = new CameraContainer_Impl(this, _mainCamera, _mainContainer, picoPVR);
		}

		public void Start()
		{
			_impl.Start();
		}

		public void OnDestroy()
		{
			if (_instance == this)
				_instance = null;
			_impl.OnDestroy();
		}

		static public void ChangeCameraBackgroundColor(Color color, bool revertClearFlagsToSkybox = false)
		{
			_instance?._impl?.ChangeCameraBackgroundColor(color, revertClearFlagsToSkybox);
		}
	}
}