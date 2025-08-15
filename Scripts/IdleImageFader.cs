// This is used by the DomeIdleImage.prefab/Panel object
using UnityEngine;

/// <summary>
/// Fades the alpha of a UI Image component when enabled, used by the DomeIdleImage.prefab/Panel object.
/// </summary>
public class IdleImageFader : MonoBehaviour
{
	private UnityEngine.UI.Image _idleImage;

	private void Awake()
	{
		if (_idleImage == null)
		{
			_idleImage = GetComponent<UnityEngine.UI.Image>();
			if (_idleImage == null)
			{
				Debug.LogError("IdleImageFader: No Image component found on the GameObject.");
			}
		}
	}

	private void OnEnable()
	{
		Bertec.Utils.Coroutines.Start(FadeAlpha(_idleImage, 0f, 0f));
		Bertec.Utils.Coroutines.Start(FadeAlpha(_idleImage, 1f, 0.25f));
	}

	/// <summary>
	/// Coroutine to fade the alpha of a UI Image to a target value over a duration.
	/// </summary>
	/// <param name="_image">The Image to fade.</param>
	/// <param name="targetAlpha">The target alpha value.</param>
	/// <param name="duration">The duration of the fade in seconds.</param>
	/// <returns>An IEnumerator for coroutine execution.</returns>
	private System.Collections.IEnumerator FadeAlpha(UnityEngine.UI.Image _image, float targetAlpha, float duration)
	{
		if (_image != null)
		{
			Color color = _image.color;
			float startAlpha = color.a;
			float elapsedTime = 0f;

			// Set the alpha and lerp it
			// This is done as follows because we do not want to have DOTween or any other tweening library dependence
			while ((elapsedTime < duration) && (_image != null))
			{
				elapsedTime += Bertec.Utils.DeltaTime;
				float newAlpha = Mathf.Lerp(startAlpha, targetAlpha, elapsedTime / duration);
				if (_image != null)
					_image.color = new Color(color.r, color.g, color.b, newAlpha);
				yield return null;
			}

			// Ensure the final alpha is set
			if (_image != null)
				_image.color = new Color(color.r, color.g, color.b, targetAlpha);
		}
	}
}
