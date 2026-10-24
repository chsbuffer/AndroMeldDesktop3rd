PROCESS=com.catchingnow.andfiles.helper:device_agent
DIR=/data/local/tmp/AndroMeldAgent
PIDS=$(pidof $PROCESS 2>/dev/null)
[ -z "$PIDS" ] || content call --uri content://com.catchingnow.andfiles.helper.agentwatch --method plannedExit --arg "$PIDS" >/dev/null 2>&1
for PID in $PIDS; do kill $PID 2>/dev/null || true; done
i=0
while [ $i -lt 20 ] && [ -n "$(pidof $PROCESS 2>/dev/null)" ]; do
  sleep 0.1
  i=$((i+1))
done
PIDS=$(pidof $PROCESS 2>/dev/null)
for PID in $PIDS; do kill -9 $PID 2>/dev/null || true; done
i=0
while [ $i -lt 20 ] && [ -n "$(pidof $PROCESS 2>/dev/null)" ]; do
  sleep 0.1
  i=$((i+1))
done
[ -z "$(pidof $PROCESS 2>/dev/null)" ] || { echo agent_restart=failed; exit 1; }
rm -f $DIR/state.json
echo agent_restart=stopped
