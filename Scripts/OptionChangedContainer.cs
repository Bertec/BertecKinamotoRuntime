using System;
using UnityEngine;

namespace Bertec
{
	public class OptionChangedContainer : MonoBehaviour
	{
		private ProtocolOptionChangedEventHandler eventHandler = null;
		private bool isDestroyed = false;

		/// <summary>
		/// This event is triggered when an option is changed.
		/// </summary>
		/// <param name="optionName">The name of the option.</param>
		/// <param name="optionValue">The new value of the option.</param>
		public event Action<string, object> OnOptionChanged = delegate { };

		/// <summary>
		/// Connects the specified action to the OnOptionChanged event.
		/// </summary>
		/// <param name="onoptionchanged">The action to be connected to the event.</param>
		public static bool Connect(Action<string, object> onoptionchanged)
		{
			var cntr = GameObject.FindObjectOfType<Bertec.OptionChangedContainer>();
			if (cntr != null)
			{
				// Make sure the event handler is set up so the event can be connected to; since this is unexpected, log it as an error
				if (cntr.OptionEvents == null)
					Debug.LogError("OptionChangedContainer.Connect possibly called after OnDestroy");
				cntr.OnOptionChanged += onoptionchanged;
				return true;
			}
			else
			{
				Debug.LogError("Unable to locate a OptionChangedContainer; please check your scene prefabs!");
				return false;
			}
		}

		/// <summary>
		/// Disconnects the specified action from the OnOptionChanged event. Typically not needed since the class manages it.
		/// </summary>
		/// <param name="onoptionchanged">The action to be disconnected from the event.</param>
		public static void Disconnect(Action<string, object> onoptionchanged)
		{
			var cntr = GameObject.FindObjectOfType<Bertec.OptionChangedContainer>();
			if (cntr != null)
				cntr.OnOptionChanged -= onoptionchanged;
		}

		public void Awake()
		{
			Init();
		}

		public void Start()
		{
			Init();
		}

		public void Init()
		{
			if (eventHandler == null && !isDestroyed)
			{
				eventHandler = new ProtocolOptionChangedEventHandler();
				eventHandler.OnOptionChanged += _OnOptionChanged;
			}
		}

		public void Shutdown()
		{
			if (eventHandler != null)
			{
				eventHandler.OnOptionChanged -= _OnOptionChanged;
				eventHandler.ClearAllEventHandlers();
				eventHandler = null;
			}
		}

		/// <summary>
		/// Gets the ProtocolOptionChangedEventHandler instance.
		/// </summary>
		public ProtocolOptionChangedEventHandler OptionEvents
		{
			get
			{
				Init();
				return eventHandler;
			}
		}

		public void OnDestroy()
		{
			Shutdown();
			isDestroyed = true;
			OnOptionChanged = delegate { };    // make sure everyone is disconnected so we don't get memory leaks
		}

		// This just makes sure to invoke both the event and the virtual override
		private void _OnOptionChanged(string optionName, object optionValue)
		{
			OnOptionChanged(optionName, optionValue);
		}
	}
}