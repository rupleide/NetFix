import Foundation

public enum BypassScripts {
    private static let library = """
    tpws_alive() {
      local binary="$1"
      /bin/ps -ax -o command= | /usr/bin/grep -F "$binary" | /usr/bin/grep -v "netfix-" | /usr/bin/grep -v grep >/dev/null
    }

    start_tpws() {
      local src="$1"
      local binary="$2"
      /bin/launchctl bootout system/com.netfix.tpws >/dev/null 2>&1 || true
      sleep 0.3
      /bin/cp "$src" /Library/LaunchDaemons/com.netfix.tpws.plist
      /usr/sbin/chown root:wheel /Library/LaunchDaemons/com.netfix.tpws.plist
      /bin/chmod 644 /Library/LaunchDaemons/com.netfix.tpws.plist
      /usr/bin/xattr -dr com.apple.quarantine "$binary" >/dev/null 2>&1 || true
      /bin/chmod 755 "$binary"
      /usr/bin/codesign -s - --force "$binary" >/dev/null 2>&1 || true
      /bin/mkdir -p /Library/Logs/NetFix
      /bin/chmod 755 /Library/Logs/NetFix
      : > /Library/Logs/NetFix/tpws.err.log || true
      : > /Library/Logs/NetFix/tpws.out.log || true
      if ! /bin/launchctl bootstrap system /Library/LaunchDaemons/com.netfix.tpws.plist; then
        echo "launchctl не смог загрузить службу tpws" >&2
        /usr/bin/tail -n 40 /Library/Logs/NetFix/tpws.err.log >&2 || true
        return 1
      fi
      /bin/launchctl enable system/com.netfix.tpws >/dev/null 2>&1 || true
      /bin/launchctl kickstart -k system/com.netfix.tpws >/dev/null 2>&1 || true
      sleep 0.8
      if ! tpws_alive "$binary"; then
        sleep 0.8
      fi
      if ! tpws_alive "$binary"; then
        echo "tpws не остался запущенным" >&2
        /usr/bin/tail -n 50 /Library/Logs/NetFix/tpws.err.log >&2 || true
        return 1
      fi
      return 0
    }

    enable_pf() {
      local src="$1"
      /bin/cp "$src" /etc/pf.anchors/netfix
      /bin/chmod 644 /etc/pf.anchors/netfix
      if [[ ! -s /var/run/netfix.pf.token ]]; then
        local out tok
        out=$(/sbin/pfctl -E 2>&1 || true)
        tok=$(printf '%s\\n' "$out" | /usr/bin/awk '/Token/ {print $NF}' | /usr/bin/tail -n 1)
        if [[ -z "${tok:-}" ]]; then
          echo "Не удалось включить PF. $out" >&2
          return 1
        fi
        printf '%s' "$tok" > /var/run/netfix.pf.token
      fi
      /sbin/pfctl -a com.apple/netfix -f /etc/pf.anchors/netfix
    }

    disable_all() {
      /bin/launchctl bootout system/com.netfix.tpws >/dev/null 2>&1 || true
      /bin/rm -f /Library/LaunchDaemons/com.netfix.tpws.plist
      /sbin/pfctl -a com.apple/netfix -F all >/dev/null 2>&1 || true
      if [[ -f /var/run/netfix.pf.token ]]; then
        /sbin/pfctl -X "$(/bin/cat /var/run/netfix.pf.token)" >/dev/null 2>&1 || true
        /bin/rm -f /var/run/netfix.pf.token
      fi
    }
    """

    public static let start = """
    #!/bin/bash
    set -u
    \(library)
    enable_pf "$2" || exit 1
    if ! start_tpws "$1" "$3"; then
      disable_all
      exit 1
    fi
    """

    public static let stop = """
    #!/bin/bash
    set -u
    \(library)
    disable_all
    exit 0
    """

    public static let scan = """
    #!/bin/bash
    set -u
    \(library)
    SCAN_ROOT="$1"
    RESULTS="$2"
    PF_SRC="$3"
    BINARY="$4"
    shopt -s nullglob
    CONSOLE_USER=$(/usr/bin/stat -f '%Su' /dev/console)
    CONSOLE_UID=$(/usr/bin/id -u "$CONSOLE_USER")
    URLS=(
      "https://www.youtube.com"
      "https://discord.com"
      "https://web.telegram.org"
    )

    probe() {
      local url="$1"
      local line
      line=$(/bin/launchctl asuser "$CONSOLE_UID" /usr/bin/curl -L --max-time 8 -o /dev/null -s -w "%{http_code} %{time_total}" "$url" || true)
      if [[ -z "${line:-}" ]]; then
        line="000 8"
      fi
      printf '%s' "$line"
    }

    enable_pf "$PF_SRC" || exit 1
    best=""
    best_rank=-1000000000
    best_plist=""
    for dir in "$SCAN_ROOT"/*; do
      [[ -f "$dir/daemon.plist" ]] || continue
      id=$(/usr/bin/basename "$dir")
      if ! start_tpws "$dir/daemon.plist" "$BINARY"; then
        echo "$id 0 3 8000" >> "$RESULTS"
        continue
      fi
      ok=0
      fail=0
      sum=0
      count=0
      for url in "${URLS[@]}"; do
        line=$(probe "$url")
        code="${line%% *}"
        t="${line##* }"
        ms=$(printf '%s\\n' "$t" | /usr/bin/awk '{ printf "%d", ($1+0) * 1000 }')
        [[ -n "${ms:-}" ]] || ms=8000
        sum=$((sum + ms))
        count=$((count + 1))
        if [[ "$code" =~ ^[0-9]+$ ]] && [ "$code" -ge 200 ] && [ "$code" -lt 400 ]; then
          ok=$((ok + 1))
        else
          fail=$((fail + 1))
        fi
      done
      avg=$((sum / count))
      echo "$id $ok $fail $avg" >> "$RESULTS"
      rank=$((ok * 100000 - avg))
      if [ "$ok" -gt 0 ] && [ "$rank" -gt "$best_rank" ]; then
        best_rank=$rank
        best=$id
        best_plist="$dir/daemon.plist"
      fi
    done

    if [[ -n "$best_plist" ]]; then
      if start_tpws "$best_plist" "$BINARY"; then
        echo "BEST $best" >> "$RESULTS"
      else
        disable_all
        echo "BEST none" >> "$RESULTS"
        exit 1
      fi
    else
      disable_all
      echo "BEST none" >> "$RESULTS"
    fi
    """
}
