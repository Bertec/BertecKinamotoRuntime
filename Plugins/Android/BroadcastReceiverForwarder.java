package Bertec.Android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.os.Build;


public class BroadcastReceiverForwarder extends BroadcastReceiver
{
   // API 33 symbols inlined so this file compiles against any project with compileSdk >= 26
   // (the API level where the 5-arg registerReceiver overload itself was introduced). Using
   // Build.VERSION_CODES.TIRAMISU / Context.RECEIVER_EXPORTED / Context.RECEIVER_NOT_EXPORTED
   // directly would force every consumer of this plugin to bump compileSdk to 33+ even though
   // the runtime check guards the actual call -- compile-time symbol resolution doesn't care
   // about runtime guards. The values are documented in the public Android API and stable.
   private static final int TIRAMISU_API_LEVEL          = 33;     // Build.VERSION_CODES.TIRAMISU
   private static final int RECEIVER_EXPORTED_FLAG      = 0x2;    // Context.RECEIVER_EXPORTED
   private static final int RECEIVER_NOT_EXPORTED_FLAG  = 0x4;    // Context.RECEIVER_NOT_EXPORTED

   public IBroadcastReceiver receiver;

   public BroadcastReceiverForwarder()
   {
   }

   public void onReceive(Context context, Intent intent)
   {
      if (receiver != null)
         receiver.OnReceived(context, intent);
   }

   public void SetReceiver(IBroadcastReceiver proxy)
   {
      receiver = proxy;
   }

   // 2-arg overload: register for a SYSTEM / PLATFORM broadcast (CONNECTIVITY_CHANGE, SCREEN_ON/OFF,
   // BATTERY_CHANGED, ACTION_SHUTDOWN, DEVICE_IDLE_MODE_CHANGED, POWER_SAVE_MODE_CHANGED, etc.). On
   // Android 13+ this binding is RECEIVER_NOT_EXPORTED so ONLY the system process can deliver to it;
   // non-system senders cannot spoof these broadcasts to trigger our handlers (most notably
   // ProtocolRPC.Disconnect on ACTION_SHUTDOWN / ACTION_DEVICE_IDLE_MODE_CHANGED, which would
   // otherwise be a denial-of-service vector). Note this is the OPPOSITE export setting from the
   // 3-arg overload below -- the call-site split in UnityBroadcastReceiver.Init.cs lines up
   // exactly with the desired surface: system actions use this 2-arg form, the launch-app action
   // uses the 3-arg form.
   public void RegisterReciever(Context ctx, String intentString)
   {
      IntentFilter filter = new IntentFilter(intentString);
      if (Build.VERSION.SDK_INT >= TIRAMISU_API_LEVEL)
      {
         ctx.registerReceiver(this, filter, null, null, RECEIVER_NOT_EXPORTED_FLAG);
      }
      else
      {
         ctx.registerReceiver(this, filter, null, null);
      }
   }

   // 3-arg overload: register for a CROSS-PACKAGE broadcast (the framework's launch-app action). On
   // Android 13+ the binding is RECEIVER_EXPORTED because the launch-app surface is the framework's
   // public extension point -- senders include the Loader, Bertec-built scene APKs, and customer
   // scene APKs built against the Bertec SDK under customer-owned signing certs. See the long
   // comment on LaunchAppImpl.LaunchAppBroadcastPermission for the deployment-model rationale.
   //
   // requiredSenderPermission: if non-null, Android only delivers the broadcast to this receiver if
   // the sender holds the named permission. Null in the public framework build (Bertec-provisioned
   // clinical appliance; install set is controlled at provisioning time). Plumbed through here so
   // private Loader builds can opt into filtering.
   //
   // Fresh IntentFilter per call (vs. accumulating actions on a shared field) is required to avoid
   // duplicate-onReceive bug: Android keeps every registerReceiver call as an independent binding,
   // so a re-registered receiver with a growing filter would fire onReceive once per binding that
   // matched. UnityBroadcastReceiver.Init makes nine RegisterReciever calls, so the accumulating
   // pattern would have produced up to nine onReceive calls per CONNECTIVITY_CHANGE broadcast.
   public void RegisterReciever(Context ctx, String intentString, String requiredSenderPermission)
   {
      IntentFilter filter = new IntentFilter(intentString);
      if (Build.VERSION.SDK_INT >= TIRAMISU_API_LEVEL)
      {
         ctx.registerReceiver(this, filter, requiredSenderPermission, null, RECEIVER_EXPORTED_FLAG);
      }
      else
      {
         ctx.registerReceiver(this, filter, requiredSenderPermission, null);
      }
   }
}
