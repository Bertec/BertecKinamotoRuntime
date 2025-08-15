using System;
using UnityEngine;

/// <summary>
/// Trivial addin class to let other classes get notified when an object is enabled or disabled
/// </summary>
public class OnEnableDisableExt : MonoBehaviour
{
	/// <summary>
	/// Event triggered when the enabled state of the object changes.
	/// </summary>
	/// <param name="enabled">True if the object is enabled; false if disabled.</param>
	public event Action<bool> OnStateChanged;

	/// <summary>
	/// Called by Unity when the object becomes enabled and active.
	/// </summary>
	void OnEnable()
	{
		OnStateChanged?.Invoke(true); // Notify that the object is enabled
	}

	/// <summary>
	/// Called by Unity when the object becomes disabled or inactive.
	/// </summary>
	void OnDisable()
	{
		OnStateChanged?.Invoke(false); // Notify that the object is disabled
	}
}