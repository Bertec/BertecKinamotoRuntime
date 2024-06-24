package Bertec.Android;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.util.Log;
import java.lang.reflect.Method;


public class BroadcastReceiverForwarder extends BroadcastReceiver
{
   public IBroadcastReceiver receiver;

   public IntentFilter intentFilter;

   public BroadcastReceiverForwarder()
   {
      intentFilter = new IntentFilter();
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

   public void RegisterReciever(Context ctx, String intentString)
   {
      intentFilter.addAction(intentString);
      ctx.registerReceiver(this, intentFilter);
   }
}
