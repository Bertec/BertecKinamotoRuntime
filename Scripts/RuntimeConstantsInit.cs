namespace Bertec
{

	public static class RuntimeConstantsInit
	{
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