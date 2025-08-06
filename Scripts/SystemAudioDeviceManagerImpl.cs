// This class is just a wrapper around the Pico sdk functions to get/set the audio volume level on Pico devices.

#if UNITY_ANDROID
using UnityEngine;

namespace BertecHMD
{
	internal class SystemAudioDeviceManagerImpl : Bertec.SystemAudioDeviceManagerInterface
	{
		internal static void Init()
		{
			Debug.Log("HMD SystemAudioDeviceManagerImpl.Init");
			Bertec.SystemAudioDeviceManager.managerInterface = new BertecHMD.SystemAudioDeviceManagerImpl();
			Bertec.SystemAudioDeviceManager.interfaceRequiresMainThread = true;
		}

		public void GetAudioVolumeInfo(out int _min, out int _max, out int _level)
		{
			// The default values for the min/max are always constant
			_min = 0;
			_max = 15;
			_level = Unity.XR.PXR.PXR_System.GetCurrentVolumeNumber();
		}

		public void SetAudioVolumeLevel(int _newlevel)
		{
			Unity.XR.PXR.PXR_System.SetVolumeNum(_newlevel);
		}
	}
}

#endif
