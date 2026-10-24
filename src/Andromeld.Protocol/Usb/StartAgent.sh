PKG=com.catchingnow.andfiles.helper
DIR=/data/local/tmp/AndroMeldAgent
APK=$(pm path $PKG | sed -n 's/^package://p' | head -n 1)
[ -n "$APK" ] || { echo agent_start=missing_apk; exit 1; }
mkdir -p $DIR
chmod 700 $DIR
PID=$(pidof com.catchingnow.andfiles.helper:device_agent 2>/dev/null | tr ' ' '\n' | head -n 1)
if [ -z "$PID" ]; then
  rm -f $DIR/state.json
  ( trap "" HUP; export CLASSPATH=$APK;
    if command -v setsid >/dev/null 2>&1; then exec setsid /system/bin/app_process / --nice-name=com.catchingnow.andfiles.helper:device_agent com.catchingnow.andfiles.helper.DeviceAgentMain;
    else exec /system/bin/app_process / --nice-name=com.catchingnow.andfiles.helper:device_agent com.catchingnow.andfiles.helper.DeviceAgentMain; fi
  ) </dev/null >>$DIR/agent-stderr.log 2>&1 &
fi
i=0
while [ $i -lt 10 ]; do
  if [ -s $DIR/state.json ]; then
    printf 'device_agent_pid=%s\n' "$(pidof com.catchingnow.andfiles.helper:device_agent 2>/dev/null | tr ' ' '\n' | head -n 1)"
    exit 0
  fi
  sleep 1
  i=$((i+1))
done
echo agent_start=failed
exit 1
