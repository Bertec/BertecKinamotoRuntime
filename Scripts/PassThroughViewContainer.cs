using System.Collections.Generic;
using UnityEngine;


namespace Bertec
{
	/// <summary>
	/// Manages passthrough view state, including hiding/showing the main scene and handling passthrough camera logic.
	/// </summary>
	public class PassThroughViewContainer : MonoBehaviour
	{
		public static PassThroughViewContainer ActiveInstance { get; private set; }

		public static event System.Action<PassThroughViewContainer> ActiveInstanceChanged = delegate { };

		/// <summary>
		/// The main scene to hide when the passthrough is turned on; if null, then both the passthrough and scene are visible.
		/// </summary>
		/// <remarks>If not set, the Framework will automatically use the gameobject named "MainScene", if any.</remarks>
		[Tooltip("If not set, defaults to 'MainScene' game object")]
		public GameObject Scene = null;

		/// <summary>
		/// Whether the scene should be affected (hidden/shown) when passthrough is toggled.
		/// </summary>
		[Tooltip("Whether the scene should be affected (hidden/shown) when passthrough is toggled")]
		public bool AffectSceneOnPassthroughToggle = true;

		/// <summary>
		/// The headset eye camera; this is not the same as the 'other' main camera or the external camera hardware.
		/// </summary>
		/// <remarks>If not set, the Framework will automatically find the camera object. Required so that passthrough works correctly.</remarks>
		public Camera XRRigMainCamera = null;

		/// <summary>
		/// Determines whether the controller button bypass is allowed. Only clear this if your scene requires all buttons.
		/// </summary>
		public bool AllowControlerButtonBypass = true;

		/// <summary>
		/// When enabled, the package will swap the managed cameras' culling masks while passthrough/idle is active.
		/// This is useful for scenes that do not use a custom passthrough runtime controller.
		/// </summary>
		[Tooltip("Swap managed camera culling masks while passthrough/idle is active")]
		public bool UpdateCameraCullingMasksOnPassthrough = true;

		/// <summary>
		/// Whether dome perspective cameras reported by the Omnity package should automatically be added to the managed list.
		/// </summary>
		[Tooltip("Automatically register Omnity dome perspective cameras when available")]
		public bool ManageDomePerspectiveCameras = true;

		/// <summary>
		/// When enabled, managed cameras restore to <see cref="WorldCullingMask"/> instead of whatever was captured from the scene.
		/// </summary>
		[Tooltip("Restore an explicit world culling mask instead of the mask captured from the scene")]
		public bool OverrideWorldCullingMaskOnPassthroughExit = false;

		/// <summary>
		/// Optional explicit world mask used when <see cref="OverrideWorldCullingMaskOnPassthroughExit"/> is enabled,
		/// or as a fallback for cameras discovered while passthrough is already active.
		/// </summary>
		[Tooltip("Optional explicit world culling mask used on passthrough exit or as a fallback for late-registered cameras")]
		public LayerMask WorldCullingMask = 0;

		/// <summary>
		/// Optional explicit list of cameras to manage. If empty, the package will resolve the XR rig/main camera automatically.
		/// </summary>
		[Tooltip("Optional explicit list of cameras to manage; if empty, the package resolves the XR/main camera automatically")]
		public Camera[] ManagedCameras = null;

		/// <summary>
		/// Mask to apply while passthrough/idle is active. Defaults to UI only.
		/// </summary>
		[Tooltip("Culling mask applied while passthrough/idle is active; defaults to UI only")]
		public LayerMask PassthroughCullingMask = 1 << 5;

		// Per-camera "world" masks that should be restored when passthrough exits.
		// This cache intentionally stores the last known non-passthrough mask, not simply
		// whatever mask happened to be on the camera most recently. That distinction matters
		// because cameras can be registered while passthrough is already active, and in that
		// case their current mask may already be the UI/passthrough mask rather than the scene mask.
		private readonly Dictionary<Camera, int> _cachedWorldCullingMasks = new();

		// Tracks the layer state this container believes it has already applied to the managed cameras.
		// This is separate from SystemDisplayDeviceManager.IsPassthrough because transitions may be
		// observed late or missed transiently; the reconciliation pass below uses this flag to decide
		// whether it needs to repair camera masks.
		private bool _managedCameraLayersInPassthrough = false;

		// Unity's default Camera mask. This is only used as a last-resort repair target when the
		// only available value is the passthrough/UI mask, which would otherwise leave HMD users in sky-only.
		private const int EverythingCullingMask = ~0;

		private enum WorldMaskPreference
		{
			CurrentCamera,
			CachedWorld
		}

		public bool OwnsManagedCameraLayerState => UpdateCameraCullingMasksOnPassthrough;

		private void Start()
		{
			SetActiveInstance(this);
			Bertec.PassThroughViewContainer_Impl.RegisterPerspectiveCamerasWithActiveContainerCallback += RegisterPerspectiveCamerasWithActiveContainer;
			Bertec.PassThroughViewContainer_Impl.RegisterManagedCamerasCallback += HandleActivePassThroughViewContainerChanged;
			Bertec.SystemDisplayDeviceManager.MonoStart(Scene, AffectSceneOnPassthroughToggle, XRRigMainCamera, AllowControlerButtonBypass);
			Bertec.SystemDisplayDeviceManager.OnPassthroughChanged += HandlePassthroughChanged;
			RefreshManagedCameras();
			HandlePassthroughChanged(Bertec.SystemDisplayDeviceManager.IsPassthrough);
		}

		public void OnDestroy()
		{
			Bertec.PassThroughViewContainer_Impl.RegisterPerspectiveCamerasWithActiveContainerCallback -= RegisterPerspectiveCamerasWithActiveContainer;
			Bertec.PassThroughViewContainer_Impl.RegisterManagedCamerasCallback += HandleActivePassThroughViewContainerChanged;
			Bertec.SystemDisplayDeviceManager.OnPassthroughChanged -= HandlePassthroughChanged;
			RestoreManagedCameraCullingMasks();
			ClearActiveInstance(this);
			Bertec.SystemDisplayDeviceManager.MonoDestroy(Scene, AffectSceneOnPassthroughToggle, XRRigMainCamera);
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
			Bertec.ExDebug.Log("App has resumed from standby or sleep mode, turning off passthrough");
			Bertec.SystemDisplayDeviceManager.TurnOffPassthrough();
		}

		void Update()
		{
			Bertec.SystemDisplayDeviceManager.MonoUpdate();

			// Do not rely solely on OnPassthroughChanged.
			// In practice there are multiple passthrough writers in the system (idle scene, RPC, HMD runtime,
			// resume handling, etc.), so this per-frame reconciliation makes the camera layer state converge
			// back to the correct value even if a transition was delivered late or only partially completed.
			ReconcileManagedCameraLayerState();
		}

		private void HandlePassthroughChanged(bool enabled)
		{
			if (!UpdateCameraCullingMasksOnPassthrough)
			{
				return;
			}

			// Always refresh first so late-added cameras participate in the same transition.
			// Without this, a newly registered camera could miss the enter/exit event and stay
			// on the wrong culling mask until some unrelated reload occurs.
			RefreshManagedCameras();
			ApplyManagedCameraLayerState(enabled);
		}

		private void RefreshManagedCameras()
		{
			if (ManagedCameras != null && ManagedCameras.Length > 0)
			{
				for (int i = 0; i < ManagedCameras.Length; i++)
				{
					RegisterManagedCamera(ManagedCameras[i]);
				}
				return;
			}

			RegisterManagedCamera(XRRigMainCamera);
			RegisterManagedCamera(Bertec.CameraContainer_Impl.Instance?.MainCamera);
			RegisterManagedCamera(Camera.main);
		}

		public void RegisterPerspectiveCamerasWithActiveContainer(Camera[] perspectiveCameras)
		{
			PassThroughViewContainer activeContainer = PassThroughViewContainer.ActiveInstance;
			if (activeContainer == null || !activeContainer.ManageDomePerspectiveCameras)
			{
				return;
			}

			activeContainer.RegisterManagedCameras(perspectiveCameras);
		}


		public void RegisterManagedCamera(Camera camera)
		{
			if (camera == null)
			{
				return;
			}

			if (!_cachedWorldCullingMasks.ContainsKey(camera))
			{
				// First registration is the riskiest point because the camera may already be sitting in
				// passthrough when we discover it. ResolveWorldMask() prefers a known-good scene mask
				// over blindly caching the current mask.
				_cachedWorldCullingMasks[camera] = ResolveWorldMask(camera, camera.cullingMask, WorldMaskPreference.CurrentCamera);
			}
			else if (!Bertec.SystemDisplayDeviceManager.IsPassthrough && !OverrideWorldCullingMaskOnPassthroughExit && IsWorldMask(camera.cullingMask))
			{
				// While we are definitely out of passthrough, keep the cache fresh with the camera's live
				// world mask. This lets normal scene-side camera changes continue to work without requiring
				// the scene author to enable OverrideWorldCullingMaskOnPassthroughExit.
				// Do not learn an invalid/passthrough mask here; that is the exact corrupted state this
				// component is supposed to repair after a partial exit.
				_cachedWorldCullingMasks[camera] = camera.cullingMask;
			}

			if (UpdateCameraCullingMasksOnPassthrough && Bertec.SystemDisplayDeviceManager.IsPassthrough)
			{
				// If a camera shows up mid-passthrough, immediately align it with the rest of the managed set.
				camera.cullingMask = PassthroughCullingMask.value;
			}
		}

		public void HandleActivePassThroughViewContainerChanged(object container, IEnumerable<Camera> perspectiveCameras)
		{
			PassThroughViewContainer passThroughContainer = container as PassThroughViewContainer;
			if (passThroughContainer == null)
				return;
			if (!passThroughContainer.ManageDomePerspectiveCameras)
				return;

			passThroughContainer.RegisterManagedCameras(perspectiveCameras);
		}
		public void RegisterManagedCameras(IEnumerable<Camera> cameras)
		{
			if (cameras == null)
			{
				return;
			}

			foreach (Camera camera in cameras)
			{
				RegisterManagedCamera(camera);
			}
		}

		public void UnregisterManagedCamera(Camera camera)
		{
			if (camera == null)
			{
				return;
			}

			_cachedWorldCullingMasks.Remove(camera);
		}

		private void CaptureCurrentCullingMasks()
		{
			List<Camera> cameras = new(_cachedWorldCullingMasks.Keys);
			for (int i = 0; i < cameras.Count; i++)
			{
				Camera camera = cameras[i];
				if (camera == null)
				{
					continue;
				}

				// This is intentionally not "camera.cullingMask" anymore.
				// When passthrough is already active, some cameras may already be using the passthrough mask,
				// and caching that value would make exit restore the wrong thing. ResolveWorldMask()
				// preserves a known-good world mask whenever possible.
				_cachedWorldCullingMasks[camera] = ResolveWorldMask(camera, _cachedWorldCullingMasks[camera], WorldMaskPreference.CurrentCamera);
			}
		}

		private void ApplyPassthroughCullingMask()
		{
			foreach (KeyValuePair<Camera, int> entry in _cachedWorldCullingMasks)
			{
				if (entry.Key == null)
				{
					continue;
				}

				// Passthrough uses a narrow mask (typically UI only) so world geometry is not rendered on top
				// of the headset feed / idle presentation.
				entry.Key.cullingMask = PassthroughCullingMask.value;
			}
		}

		private void RestoreManagedCameraCullingMasks()
		{
			foreach (KeyValuePair<Camera, int> entry in _cachedWorldCullingMasks)
			{
				if (entry.Key == null)
				{
					continue;
				}

				// Exit must be deterministic: if the cached value looks invalid, fall back to a known world mask
				// instead of restoring a mask that would leave the scene partially invisible.
				entry.Key.cullingMask = ResolveWorldMask(entry.Key, entry.Value, WorldMaskPreference.CachedWorld);
			}
		}

		private void ApplyManagedCameraLayerState(bool enabled)
		{
			bool stateChanged = _managedCameraLayersInPassthrough != enabled;
			_managedCameraLayersInPassthrough = enabled;

			if (enabled)
			{
				if (stateChanged)
				{
					// Only capture a new baseline when we actually transition into passthrough.
					// Repeated enter applications during reconciliation should keep using the same
					// last-known world masks instead of overwriting them with passthrough values.
					CaptureCurrentCullingMasks();
				}

				ApplyPassthroughCullingMask();
				return;
			}

			RestoreManagedCameraCullingMasks();
		}

		private void ReconcileManagedCameraLayerState()
		{
			if (!UpdateCameraCullingMasksOnPassthrough)
			{
				return;
			}

			RefreshManagedCameras();

			bool shouldBeInPassthrough = Bertec.SystemDisplayDeviceManager.IsPassthrough;
			if (_managedCameraLayersInPassthrough != shouldBeInPassthrough)
			{
				// Our local belief about camera state disagrees with the authoritative passthrough state.
				// Repair immediately rather than waiting for another event.
				ApplyManagedCameraLayerState(shouldBeInPassthrough);
				return;
			}

			if (shouldBeInPassthrough)
			{
				if (!AreManagedCamerasUsingPassthroughMask())
				{
					// A camera drifted out of passthrough unexpectedly; re-apply the passthrough mask.
					ApplyPassthroughCullingMask();
				}

				return;
			}

			if (!AreManagedCamerasUsingWorldMasks())
			{
				// We believe we are out of passthrough, but at least one camera is not using its expected
				// world mask. Restore again so a missed exit event does not leave the scene half-rendering.
				RestoreManagedCameraCullingMasks();
			}
		}

		private bool AreManagedCamerasUsingPassthroughMask()
		{
			foreach (KeyValuePair<Camera, int> entry in _cachedWorldCullingMasks)
			{
				if (entry.Key == null)
				{
					continue;
				}

				if (entry.Key.cullingMask != PassthroughCullingMask.value)
				{
					return false;
				}
			}

			return true;
		}

		private bool AreManagedCamerasUsingWorldMasks()
		{
			foreach (KeyValuePair<Camera, int> entry in _cachedWorldCullingMasks)
			{
				if (entry.Key == null)
				{
					continue;
				}

				if (entry.Key.cullingMask != ResolveWorldMask(entry.Key, entry.Value, WorldMaskPreference.CachedWorld))
				{
					return false;
				}
			}

			return true;
		}

		private int ResolveWorldMask(Camera camera, int cachedWorldMask, WorldMaskPreference preference)
		{
			// This is the single trust policy for world masks. The important rule is that the
			// passthrough/UI mask must never be learned as the scene mask, otherwise exit restores
			// to "sky + UI" and the actual scene stays invisible.
			if (OverrideWorldCullingMaskOnPassthroughExit && IsWorldMask(WorldCullingMask.value))
			{
				return WorldCullingMask.value;
			}

			if (preference == WorldMaskPreference.CurrentCamera && camera != null && IsWorldMask(camera.cullingMask))
			{
				return camera.cullingMask;
			}

			if (IsWorldMask(cachedWorldMask))
			{
				return cachedWorldMask;
			}

			if (IsWorldMask(WorldCullingMask.value))
			{
				return WorldCullingMask.value;
			}

			if (preference == WorldMaskPreference.CachedWorld && camera != null && IsWorldMask(camera.cullingMask))
			{
				return camera.cullingMask;
			}

			if (TryGetExistingWorldMask(out int existingWorldMask))
			{
				return existingWorldMask;
			}

			bool onlyKnownMaskIsPassthrough =
				cachedWorldMask == PassthroughCullingMask.value ||
				(camera != null && camera.cullingMask == PassthroughCullingMask.value);

			if (IsWorldMask(EverythingCullingMask) && onlyKnownMaskIsPassthrough)
			{
				return EverythingCullingMask;
			}

			return preference == WorldMaskPreference.CurrentCamera && camera != null
				? camera.cullingMask
				: cachedWorldMask;
		}

		private bool IsWorldMask(int cullingMask)
		{
			// "World mask" here simply means "not empty" and "not the dedicated passthrough mask".
			// This helper intentionally stays generic because scenes may use very different layer layouts.
			return cullingMask != 0 && cullingMask != PassthroughCullingMask.value;
		}

		private static void SetActiveInstance(PassThroughViewContainer instance)
		{
			if (ActiveInstance == instance)
			{
				return;
			}

			ActiveInstance = instance;
			ActiveInstanceChanged(instance);
			PassThroughViewContainer_Impl.RaiseActiveInstanceChanged(instance);
		}

		private static void ClearActiveInstance(PassThroughViewContainer instance)
		{
			if (ActiveInstance != instance)
			{
				return;
			}

			ActiveInstance = null;
			ActiveInstanceChanged(null);
			PassThroughViewContainer_Impl.RaiseActiveInstanceChanged(null);
		}

		private bool TryGetExistingWorldMask(out int worldMask)
		{
			// Any already-cached non-null camera with a valid world mask can provide a recovery baseline for another camera.
			foreach (KeyValuePair<Camera, int> entry in _cachedWorldCullingMasks)
			{
				if (entry.Key == null)
				{
					continue;
				}

				if (!IsWorldMask(entry.Value))
				{
					continue;
				}

				worldMask = entry.Value;
				return true;
			}

			worldMask = 0;
			return false;
		}
	}
}
