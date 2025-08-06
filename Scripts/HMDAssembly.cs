// This class is mainly to just force the subassembly to always be linked in so the FrameworkInit attributes can be properly processed.
// We also use it to init the service bridge and implementations in the correct order.

#if UNITY_ANDROID

using UnityEngine;

[assembly: UnityEngine.Scripting.AlwaysLinkAssembly] // You need this otherwise this subassembly will get optimized out and your FrameworkInit attributes will not be processed

namespace BertecHMD
{
	public class HMDAssembly
	{
		// The static init must be marked as PXRServiceBridgeInit otherwise the framework will fail to properly connect up the PICO SDK
		[Bertec.PXRServiceBridgeInit]
		internal static void PicoInit()
		{
			Debug.Log("HMDAssembly.PXRServiceBridgeInit");
			PXRServiceBridge.Init();
		}

		[Bertec.FrameworkInit(Bertec.FrameworkInitType.AfterSubsystem)]
		public static void Init()
		{
			Debug.Log("HMDAssembly.AfterSubsystems");
			SystemAudioDeviceManagerImpl.Init();
			SystemDisplayDeviceManagerImpl.Init();
		}
	}
}

#endif