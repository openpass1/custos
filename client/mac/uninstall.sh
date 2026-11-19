#!/bin/sh
# Removes CUSTOS from this Mac:
#   sudo "/Applications/Custos.app/Contents/Resources/uninstall.sh"          the program; tasks and logs stay
#   sudo "/Applications/Custos.app/Contents/Resources/uninstall.sh" --all    also the tasks, settings, logs and the key
#                                                                           of the stored passwords
# The copies already made stay where they are.
if [ "$(/usr/bin/id -u)" != 0 ]; then
  echo "Run it with sudo: sudo \"$0\" $*" >&2
  exit 1
fi

label=org.custos.agent
/bin/launchctl bootout "system/$label" 2> /dev/null
for uid in $(/bin/ps -axo uid=,args= | /usr/bin/awk '$2 == "/Applications/Custos.app/Contents/MacOS/Custos" && $1 != 0 { print $1 }' | /usr/bin/sort -u); do
  /bin/launchctl bootout "gui/$uid/org.custos.menu" 2> /dev/null
done
for pid in $(/bin/ps -axo pid=,args= | /usr/bin/awk '$2 == "/Applications/Custos.app/Contents/MacOS/Custos" { print $1 }'); do
  /bin/kill "$pid" 2> /dev/null
done

/bin/rm -f "/Library/LaunchDaemons/$label.plist" /Library/LaunchAgents/org.custos.menu.plist /var/run/custos.sock
/bin/rm -rf /Applications/Custos.app
/usr/sbin/pkgutil --forget org.custos.pkg > /dev/null 2>&1

if [ "${1:-}" = --all ]; then
  /bin/rm -rf "/Library/Application Support/CUSTOS"
  /usr/bin/security delete-generic-password -s "$label" -a credentials-key /Library/Keychains/System.keychain > /dev/null 2>&1
  echo "CUSTOS is removed, with its tasks, settings and logs."
else
  echo "CUSTOS is removed. Tasks, settings and logs stay in /Library/Application Support/CUSTOS (--all removes them too)."
fi
exit 0
