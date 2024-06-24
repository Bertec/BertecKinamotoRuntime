// Class that acts as a bridge to the Pico SDK functions that are needed by PassThroughViewContainer

namespace Bertec
{

	public class PXRPassThroughView : PassThroughViewContainer
	{
#if UNITY_ANDROID
		/// <inheritdoc/>
		protected override UnityEngine.Camera FindXRRigMainCamera()
		{
			var found = FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>();
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

		/// <inheritdoc/>
		protected override void EnableHeadsetPassthrough(bool enable)
		{
			try
			{
				Unity.XR.PXR.PXR_Boundary.EnableSeeThroughManual(enable);
			}
			catch (System.Exception ex)
			{
				UnityEngine.Debug.LogException(ex);
				UnityEngine.Debug.LogError("Exception when trying to set the passthrough flag to " + enable);
			}
		}

		/// <inheritdoc/>
		public override PassthroughTrackingState GetPassthroughTrackingState()
		{
			return (PassthroughTrackingState)Unity.XR.PXR.PXR_Boundary.GetSeeThroughTrackingState();
		}

		/// <inheritdoc/>
		protected override int GetCurrentFPS()
		{
			return Unity.XR.PXR.PXR_Plugin.System.UPxr_GetConfigInt(Unity.XR.PXR.ConfigType.RenderFPS);
		}
#endif
	}
}

