////////////////////////////////////////////////////////////////////////
// Pico Enterprise StartActivity adapter for Bertec.LaunchAppImpl.NativeActivityLauncher
//
// Why this exists:
// On Pico 4 Enterprise (and other Pico Enterprise-class devices), the Kinamoto_Loader runs as a normal Android application.
// When the user is currently inside a scene APK (e.g. SampleScene), the Loader's own Activity is no longer foreground.
// Android 10+ Background Activity Start (BAS) restrictions then block any startActivity() call that originates from the
// Loader's UID -- including the call we make to launch the next scene. This produces multi-minute hangs that resolve only
// when the user manually wakes/sleeps the headset (which momentarily restores foreground state to the Loader).
//
// Pico's Tob (To-Business) Enterprise SDK exposes PXR_Enterprise.StartActivity(...), which routes the launch through
// system_server via a privileged binder service (pbsStartActivity). Because the request originates from a system process,
// it is not subject to BAS, and the target activity is launched immediately.
//
// We register this adapter as the LaunchAppImpl.NativeActivityLauncher fast path. The framework will try this first, and
// fall back to the standard activity.startActivity() call if the Pico path is unavailable or returns failure.
//
// Hardware/firmware requirement: Pico 4 Enterprise / Pico Neo3 Pro running system version 5.4.0+. The current production
// units are on 5.9.9 which fully supports this API.

#if UNITY_ANDROID

using System;
using PXR_System = Unity.XR.PICO.TOBSupport.PXR_Enterprise;

namespace BertecHMD
{
	internal static class PicoEnterpriseLauncher
	{
		private const int FLAG_ACTIVITY_NEW_TASK = 0x10000000;

		private static bool _registered;

		// Health flag for the Pico privileged launch path. True iff the most recent PXR_Enterprise.StartActivity
		// call returned 0 (success); false on first call ever, on a non-zero return code, or on an exception.
		// Used to gate KillPreviousScene: we only force-quit the prior scene when we have positive evidence the
		// privileged launch will succeed. If the last call failed (Pico binder transient state, SDK error, etc.)
		// we skip the kill -- better to fall through to Pico's FEAT_SINGLE3D dialog (annoying but recoverable)
		// or leave the prior scene running while the new launch fails (user stays where they were) than to
		// kill the prior scene and then fail to launch the new one, leaving the user with no VR app at all.
		// Volatile because callers may be on Unity main, broadcast-dispatcher, or thread-pool threads.
		private static volatile bool _lastPicoStartActivitySucceeded = false;

		// Tracks the last scene APK that was launched (via ANY launcher path -- Pico privileged
		// StartActivity OR the standard Activity.startActivity fallback). KillPreviousScene reads this on
		// the next launch to force-quit the prior scene before invoking PXR_Enterprise.StartActivity,
		// which suppresses Pico's "Exit / Cancel" FEAT_SINGLE3D confirmation prompt: Pico cannot ask the
		// user to exit a VR app that is no longer running.
		//
		// IMPORTANT: this field is updated only via NotifyScenePackageLaunched, which is called by the
		// launch orchestrator (LaunchAppImpl.RequestAppLaunch) AFTER a launch has succeeded by whatever
		// path. We deliberately do NOT update it from inside TryStartActivity's success branch -- doing
		// so would leave the tracker stale whenever the Pico path returned non-zero / threw and the
		// fallback path succeeded, and the next KillPreviousScene call would either kill the wrong
		// package or fail to kill the one actually running.
		//
		// THREAD-SAFETY: writes are paired (Interlocked.Exchange in NotifyScenePackageLaunched provides
		// release semantics; Volatile.Read in KillPreviousScene provides the matching acquire). Callers
		// can be on the Unity main thread, the Android broadcast-dispatcher thread, or a thread-pool
		// worker depending on how the launch was initiated, and Pico Enterprise hardware is ARM64 with
		// weak memory ordering -- a plain field load would have no guarantee of seeing the most recent
		// write.
		private static string _lastLaunchedScenePackage;

		internal static void Register()
		{
			if (_registered)
				return;

			// We register unconditionally on Android+Pico builds. The PXRServiceBridge.bridgeConnected flag is set from
			// the BindEnterpriseService callback, which historically does not always fire even when the underlying binder
			// is actually connected (see the comment in PXRServiceBridge.Init). Rather than gating on a flag we know is
			// unreliable, we let TryStartActivity attempt the call: if Pico's binder service is not actually available,
			// PXR_Enterprise.StartActivity will throw or return non-zero, and the framework transparently falls back to
			// Activity.startActivity() via the existing code path in LaunchAppImpl.HandleAppLauchRequestMessage.
			Bertec.LaunchApp.NativeActivityLauncher = TryStartActivity;
			// Hook the scene-launched notification too, so the launch orchestrator (which lives in the
			// core framework assembly and therefore cannot reference this class directly) can update our
			// "currently running scene" tracker after EITHER our privileged path OR the standard
			// startActivity fallback succeeds. See the doc on Bertec.LaunchApp.NativeScenePackageLaunchedNotification
			// for the assembly-direction rationale.
			Bertec.LaunchApp.NativeScenePackageLaunchedNotification = NotifyScenePackageLaunched;
			_registered = true;
			Bertec.ExDebug.Log("PicoEnterpriseLauncher: registered as NativeActivityLauncher fast path.");
		}

		// Notification entry point for the launch orchestrator. Called by LaunchAppImpl.RequestAppLaunch
		// after a launch has succeeded via EITHER this class's TryStartActivity (Pico privileged path)
		// OR the standard Activity.startActivity fallback path. Records the package so the next launch's
		// KillPreviousScene can force-quit it before PXR_Enterprise.StartActivity fires.
		//
		// Why this is an externally-driven notification rather than self-updating from TryStartActivity's
		// success branch: the orchestrator has a richer picture of "did the launch actually succeed"
		// because it knows whether the Pico path succeeded, whether the fallback was tried, and whether
		// either of them ultimately initiated a launch. Centralising the tracker write here keeps it
		// consistent across all launch paths -- without this, a Pico-failed -> fallback-succeeded launch
		// would silently leave the tracker stale and the next launch's KillPreviousScene would target
		// the wrong package.
		//
		// Interlocked.Exchange is used here for the release-side of the cross-thread publication;
		// KillPreviousScene on the read side uses Volatile.Read for the matching acquire. Plain
		// reference reads/writes are atomic on .NET, but atomicity does NOT imply ordering: on
		// weakly-ordered hardware (ARM64 / Pico Enterprise) a peer thread reading the field without
		// an acquire barrier could observe a stale value indefinitely. Callers may be on the Unity
		// main thread, the Android broadcast-dispatcher thread, or a thread-pool worker depending
		// on how the launch was initiated, so this is not a theoretical concern.
		internal static void NotifyScenePackageLaunched(string packageName)
		{
			if (string.IsNullOrEmpty(packageName))
				return;
			System.Threading.Interlocked.Exchange(ref _lastLaunchedScenePackage, packageName);
		}

		// Matches the signature expected by LaunchAppImpl.NativeActivityLauncher:
		//   Func<packageName, className, action, extraJson, launchFlags, bool>
		//
		// See Bertec.LaunchApp.NativeActivityLauncher in Framework/LaunchApp.cs for the full parameter
		// contract. Short version: extraJson is the pre-built payload produced by
		// LaunchAppImpl.BuildNativeLauncherExtraJson -- a flat JSON object with top-level primitives
		// (PackageName/ClassName/IntentAction/LaunchFlags) plus a "BertecOptions" String containing the
		// nested options dict pre-serialised. pbsStartActivity in system_server auto-deserialises this
		// payload and maps the top-level fields as Intent extras; the receiving APK's
		// LaunchAppImpl.ApplyLaunchOptionsFromCurrentActivityIntent recovers the options via
		// Intent.getStringExtra("BertecOptions").
		//
		// We pass extraJson straight through and do NOT inspect or re-wrap it here -- callers are
		// expected to have built it via BuildNativeLauncherExtraJson. If a future caller passes a raw
		// options dict instead, Pico will flatten the keys to individual extras and the receiving APK's
		// flattened-extras fallback will still recover them (see TryApplyFlattenedExtras), but the
		// BertecOptions path is the canonical contract.
		private static bool TryStartActivity(string packageName, string className, string action, string extraJson, int launchFlags)
		{
			if (string.IsNullOrEmpty(packageName))
				return false;

			// Kill the previously-launched scene first. Pico OS shows an "Exit / Cancel" FEAT_SINGLE3D
			// confirmation prompt whenever a new VR app launches while another VR app is running -- because
			// the prior app must be force-quit to satisfy the single-VR-app policy. By explicitly tearing down
			// the prior scene here we eliminate the race: when StartActivity fires there is no other VR app
			// for Pico to ask the user about. The Loader itself is unaffected (it is not a VR app and is the
			// process running this code).
			//
			// KillPreviousScene is internally gated on _lastPicoStartActivitySucceeded so the force-quit only
			// happens when we have positive evidence the privileged launch will succeed. If the last call
			// failed (Pico binder transient state, SDK error, etc.) we leave the prior scene up so the user
			// stays where they were rather than ending up with no VR app running.
			KillPreviousScene(packageName);

			try
			{
				// pbsStartActivity returns 0 on success; non-zero is an error code from the Pico service.
				// Flag handling: callers historically did not pass FLAG_ACTIVITY_NEW_TASK on the Loader broadcast (because
				// the framework added it via addFlags on the resolved Intent), so we OR it in here. Without NEW_TASK the
				// privileged launcher will refuse to start an activity from a non-Activity context.
				int flags = launchFlags | FLAG_ACTIVITY_NEW_TASK;

				// Pico's API takes className as an explicit second parameter. If the caller did not supply one, passing
				// null tells the service to resolve the package's default LAUNCHER activity, which matches the behavior of
				// Android's getLaunchIntentForPackage().
				string resolvedClass = string.IsNullOrEmpty(className) ? null : className;
				string resolvedAction = string.IsNullOrEmpty(action) ? null : action;

				// pbsStartActivity de-serialises this JSON and maps top-level primitives onto the new
				// activity's Intent as individual extras. The BertecOptions String extra survives this
				// flattening (it's a top-level String, not a nested object), which is what
				// LaunchAppImpl.ApplyLaunchOptionsFromCurrentActivityIntent reads on the receiving side.
				// See LaunchAppImpl.BuildNativeLauncherExtraJson for the exact shape.
				int result = PXR_System.StartActivity(
					packageName,
					resolvedClass,
					resolvedAction,
					extraJson,
					null,                       // categories
					new[] { flags },
					0);                         // ext

				// Update the privileged-path health flag based on the call result. This drives the next
				// call's KillPreviousScene gate -- positive evidence of success here is the only condition
				// under which we'll force-quit a prior scene on the next launch.
				_lastPicoStartActivitySucceeded = (result == 0);

				if (result == 0)
				{
					// Tracker update is the orchestrator's responsibility -- see NotifyScenePackageLaunched.
					// LaunchAppImpl.RequestAppLaunch calls that method after observing launchedDirectly==true,
					// which covers both this success path and the standard-startActivity fallback path.
					Bertec.ExDebug.Log($"PicoEnterpriseLauncher: PXR_Enterprise.StartActivity succeeded for {packageName}");
					return true;
				}

				Bertec.ExDebug.LogWarning($"PicoEnterpriseLauncher: PXR_Enterprise.StartActivity returned {result} for {packageName}, will fall back. (Next launch will skip the prior-scene kill until a Pico StartActivity succeeds.)");
				return false;
			}
			catch (Exception ex)
			{
				_lastPicoStartActivitySucceeded = false;
				Bertec.ExDebug.LogWarning("PicoEnterpriseLauncher: PXR_Enterprise.StartActivity threw, will fall back: " + ex.ToString());
				return false;
			}
		}

		// Kill the previously-launched scene, if any, and if it is not the same as the new target. We track
		// the last-launched scene rather than enumerate Pico's running VR apps because the Loader is the sole
		// orchestrator of scene launches: it knows what it asked for last. Resetting the field on Loader process
		// restart is acceptable -- when the Loader restarts, Pico has already torn down its child scene activity.
		private static void KillPreviousScene(string newTargetPackage)
		{
			// Health gate: don't kill if the Pico privileged path is in an unknown / known-bad state.
			// Killing the prior scene before a launch that has no chance of succeeding leaves the user
			// with no VR app running (prior dead, new not started, fallback Activity.startActivity is
			// likely BAS-blocked because the Loader was backgrounded by the dead prior scene). False on
			// first call ever, or after the last PXR_Enterprise.StartActivity returned non-zero / threw.
			// In those cases we skip the kill and let either Pico's FEAT_SINGLE3D dialog handle the
			// transition the slow way, or the launch fail with the user still in their current scene.
			if (!_lastPicoStartActivitySucceeded)
				return;

			// Volatile.Read provides the acquire semantics that pair with the Interlocked.Exchange
			// write in NotifyScenePackageLaunched (release semantics). Without it, a plain field load
			// would have no ordering guarantee on weakly-ordered hardware (ARM64 / Pico Enterprise),
			// so this could observe a stale value and either fail to kill the actually-running prior
			// scene (FEAT_SINGLE3D dialog resurfaces) or kill the wrong package.
			string prior = System.Threading.Volatile.Read(ref _lastLaunchedScenePackage);
			if (string.IsNullOrEmpty(prior))
				return;
			if (string.Equals(prior, newTargetPackage, StringComparison.OrdinalIgnoreCase))
				return;

			try
			{
				PXR_System.KillAppsByPidOrPackageName(null, new[] { prior }, 0);
				Bertec.ExDebug.Log($"PicoEnterpriseLauncher: killed prior scene {prior} before launching {newTargetPackage} (FEAT_SINGLE3D dialog suppression).");
			}
			catch (Exception ex)
			{
				// Non-fatal: if the kill fails we still attempt the launch. The dialog may reappear in that case
				// but the user will at least be able to proceed.
				Bertec.ExDebug.LogWarning($"PicoEnterpriseLauncher: KillAppsByPidOrPackageName({prior}) threw (non-fatal): " + ex.ToString());
			}
		}
	}
}

#endif
