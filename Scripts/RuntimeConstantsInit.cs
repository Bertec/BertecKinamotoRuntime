namespace Bertec
{
	/// <summary>
	/// Initializes runtime constants for platform and build configuration detection.
	/// </summary>
	public static class RuntimeConstantsInit
	{
		/// <summary>
		/// Initializes the <see cref="RuntimeConstants"/> values based on the current build platform and scripting backend.
		/// </summary>
		[FrameworkInit(FrameworkInitType.RegisterObjectStructs), FrameworkInit(FrameworkInitType.PrebuildExec)]
		public static void Init()
		{
			RuntimeConstants.UrpEnabled =
#if UNITY_URP
			true;
#else
			false;
#endif

			RuntimeConstants.IL2CPP =
#if ENABLE_IL2CPP
			true;
#else
			false;
#endif

			RuntimeConstants.IsWindows =
#if UNITY_STANDALONE_WIN
			true;
#else
			false;
#endif

			RuntimeConstants.IsAndroid =
#if UNITY_ANDROID
			true;
#else
			false;
#endif
		}
	}
}