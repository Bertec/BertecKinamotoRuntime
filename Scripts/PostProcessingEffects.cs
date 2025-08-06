using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Bertec
{
	public class PostProcessingEffects : MonoBehaviour
	{
		public PostProcessProfile postProcessProfile;

		public void Start()
		{
			if (postProcessProfile == null)
				Debug.LogError("PostProcessingEffects does not have the PostProcessProfile set, effects will not be available");

			// Make sure these are off when this loads to make sure nothing 'global' tracks over.
			SetVignetteIntensity(0);
			SetFilmGrainLevel(0);

			PostProcessingEffectsMoniter.PostProcessingEffectChanged += PostProcessingEffectChanged;
			SystemDisplayDeviceManager.OnPassthroughChanged += PassthroughStatusChanged;
		}

		public void OnDestroy()
		{
			PostProcessingEffectsMoniter.PostProcessingEffectChanged -= PostProcessingEffectChanged;
			SystemDisplayDeviceManager.OnPassthroughChanged -= PassthroughStatusChanged;
		}

		/// <summary>
		/// Fix for IL-2110
		/// When passthrough/idle mode is activated, all post-processing effects should be disabled.
		/// vice-versa, when exiting passthrough/idle mode, all post-processing effects should be enabled.
		/// This makes sure that the post-processing effects are not applied when the passthrough/idle mode is enabled.
		/// </summary>
		/// <param name="passthroughState"></param>
		private void PassthroughStatusChanged(bool passthroughState)
		{
			ExecuteOnMainThread.AddAction(() =>
			{
				if (postProcessProfile != null)
				{
					foreach (PostProcessEffectSettings postProcessEffectSettings in postProcessProfile.settings)
					{
						postProcessEffectSettings.active = !passthroughState;
					}
				}
			});
		}

		private void PostProcessingEffectChanged(PostProcessingEffectData data)
		{
			// If in Idle/Passthrough mode, do not apply the post-processing effects.
			if (!SystemDisplayDeviceManager.PassThroughEnabled)
			{
				ExecuteOnMainThread.AddAction(() =>
				{
					switch (data.Effect)
					{
						case PostProcessingEffect.None:
							SetVignetteIntensity(0);
							SetFilmGrainLevel(0);
							break;
						case PostProcessingEffect.Vignette:
							SetVignetteIntensity((int)data.Level);
							break;
						case PostProcessingEffect.FilmGrain:
							SetFilmGrainLevel((int)data.Level);
							break;
					}
				});
			}
		}

		private void SetVignetteIntensity(int presetIndex)
		{
			// If in Idle/Passthrough mode, do not apply the vignette effect.
			if (!SystemDisplayDeviceManager.PassThroughEnabled)
			{
				if (postProcessProfile != null)
				{
					if (postProcessProfile.TryGetSettings(out Vignette vignette))
					{
						vignette.active = (presetIndex > 0);
						switch (presetIndex)
						{
							case 0:
								vignette.intensity.Override(0);
								break;
							case 1:
								vignette.intensity.Override(0.50f);
								break;
							case 2:
								vignette.intensity.Override(0.65f);
								break;
							case 3:
								vignette.intensity.Override(0.80f);
								break;
							case 4:
								vignette.intensity.Override(1.0f);
								break;
						}
					}
				}
			}
		}

		private void SetFilmGrainLevel(int presetIndex)
		{
			// If in Idle/Passthrough mode, do not apply the film grain effect.
			if (!SystemDisplayDeviceManager.PassThroughEnabled)
			{
				if (postProcessProfile != null)
				{
					if (postProcessProfile.TryGetSettings(out Grain grain))
					{
						grain.active = (presetIndex > 0);
						// The presets originally came from IML but they were not linear and the lowest value was not apparent on the screen.
						// Changed to have a more noticeable difference between each level.
						switch (presetIndex)
						{
							case 0:
								grain.intensity.Override(0);
								grain.size.Override(0);
								break;
							case 1:
								grain.intensity.Override(0.5f);  // anything lower than this isn't very visible
								grain.size.Override(1.0f);
								break;
							case 2:
								grain.intensity.Override(0.5f);
								grain.size.Override(1.2f);
								break;
							case 3:
								grain.intensity.Override(0.75f);
								grain.size.Override(1.5f);
								break;
							case 4:
								grain.intensity.Override(1.0f);
								grain.size.Override(3.0f);
								break;
						}
					}
				}
			}
		}

	

	}

	///////////////////////////////////////////////////
	// Helper class used by dynamic visual systems to enable/disable/add post-processing effects on the main or subset cameras
	public class PostProcessingLayerHelper_Impl : Bertec.IPostProcessingLayerHelper
	{
		[FrameworkInit(FrameworkInitType.RegisterObjectStructs), FrameworkInit(FrameworkInitType.PrebuildExec)]
		public static void Init()
		{
			Bertec.PostProcessingLayerHelper.Instance = new PostProcessingLayerHelper_Impl();
		}

		public bool EnablePostProcessLayer(Camera mainCamera, bool enabled)
		{
			if (mainCamera != null)
			{
				PostProcessLayer ppl = mainCamera.GetComponent<PostProcessLayer>();
				if (ppl != null)
				{
					ppl.enabled = enabled;
					return true;   // return true if the component exists
				}
			}
			return false;
		}

		public IEnumerator AddPostProcessLayer(Camera mainCamera, GameObject cameraObject, Transform cameraTransform)
		{
			PostProcessLayer domePostProcessLayer = cameraObject.AddComponent<PostProcessLayer>();
			CopyClassValues(mainCamera.GetComponent<PostProcessLayer>(), domePostProcessLayer);
			domePostProcessLayer.volumeTrigger = cameraTransform;
			domePostProcessLayer.enabled = false;
			yield return Bertec.Utils.Coroutines.WaitForEndOfFrame;
			domePostProcessLayer.enabled = true;
		}

		internal static void CopyClassValues(PostProcessLayer sourceComp, PostProcessLayer targetComp)
		{
			FieldInfo[] sourceFields = sourceComp.GetType().GetFields(BindingFlags.Public |
																						 BindingFlags.NonPublic |
																						 BindingFlags.Instance);
			for (int i = 0; i < sourceFields.Length; ++i)
			{
				var value = sourceFields[i].GetValue(sourceComp);
				sourceFields[i].SetValue(targetComp, value);
			}
		}
	}
}