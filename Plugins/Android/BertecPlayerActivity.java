package Bertec.Android;

import android.app.Activity;
import android.app.Application;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import java.lang.reflect.Method;
import com.unity3d.player.UnityPlayer;
import com.unity3d.player.UnityPlayerActivity;

public class BertecPlayerActivity extends UnityPlayerActivity 
{
   public IPlayerActivityReceiver receiver;

   public void SetReceiver(IPlayerActivityReceiver proxy)
   {
      receiver = proxy;
   }

   // When Unity player unloaded move task to background (we may want to instead quit)
   // This apparently is not called?
   @Override public void onUnityPlayerUnloaded()
   {
      super.onUnityPlayerUnloaded(); // calls  moveTaskToBack(true);
   }
    
   @Override
   protected void onNewIntent(Intent intent)
   {
      super.onNewIntent(intent); // calls setIntent and then UnityPlayer.newIntent, which updates UnityPlayer.LaunchUrl (see getLaunchURL())
      if (receiver != null)
         receiver.OnNewIntent(intent);
   }
}

