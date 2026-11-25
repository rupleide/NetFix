import Foundation

public enum BypassScripts {
    private static let library = """
    PRIV_DIR="/Library/Application Support/NetFix"
    PRIV_BIN="/Library/Application Support/NetFix/tpws"
    DAEMON_PLIST="/Library/LaunchDaemons/com.netfix.tpws.plist"

    tpws_alive() {
      /bin/ps -ax -o command= | /usr/bin/grep -F "$PRIV_BIN" | /usr/bin/grep -v "netfix-" | /usr/bin/grep -v grep >/dev/null
    }

    install_tpws() {
      local src="$1"
      [[ -f "$src" && ! -L "$src" ]] || { echo "tpws не найден: $src" >&2; return 1; }
      [[ ! -L "$PRIV_DIR" ]] || { echo "каталог службы является ссылкой" >&2; return 1; }
      /bin/mkdir -p "$PRIV_DIR"
      /usr/sbin/chown root:wheel "$PRIV_DIR"
      /bin/chmod 755 "$PRIV_DIR"
      local tmp="$PRIV_DIR/tpws.install"
      /bin/rm -f "$tmp"
      /bin/cp "$src" "$tmp"
      /usr/sbin/chown root:wheel "$tmp"
      /bin/chmod 755 "$tmp"
      /usr/bin/xattr -dr com.apple.quarantine "$tmp" >/dev/null 2>&1 || true
      /usr/bin/codesign -s - --force "$tmp" >/dev/null 2>&1 || true
      /bin/mv -f "$tmp" "$PRIV_BIN"
      /usr/sbin/chown root:wheel "$PRIV_BIN"
      /bin/chmod 755 "$PRIV_BIN"
    }

    seal_daemon_plist() {
      local src="$1"
      /bin/cp "$src" "$DAEMON_PLIST"
      /usr/libexec/PlistBuddy -c "Delete :Program" "$DAEMON_PLIST" >/dev/null 2>&1 || true
      local key
      for key in UserName GroupName WorkingDirectory RootDirectory EnvironmentVariables WatchPaths QueueDirectories Sockets inetdCompatibility MachServices StartCalendarInterval AbandonProcessGroup; do
        /usr/libexec/PlistBuddy -c "Delete :$key" "$DAEMON_PLIST" >/dev/null 2>&1 || true
      done
      /usr/libexec/PlistBuddy -c "Set :Label com.netfix.tpws" "$DAEMON_PLIST"
      /usr/libexec/PlistBuddy -c "Set :ProgramArguments:0 $PRIV_BIN" "$DAEMON_PLIST" || return 1
      /usr/libexec/PlistBuddy -c "Set :RunAtLoad true" "$DAEMON_PLIST"
      /usr/libexec/PlistBuddy -c "Set :KeepAlive true" "$DAEMON_PLIST"
      /usr/libexec/PlistBuddy -c "Set :ThrottleInterval 2" "$DAEMON_PLIST"
      /usr/libexec/PlistBuddy -c "Set :StandardOutPath /Library/Logs/NetFix/tpws.out.log" "$DAEMON_PLIST"
      /usr/libexec/PlistBuddy -c "Set :StandardErrorPath /Library/Logs/NetFix/tpws.err.log" "$DAEMON_PLIST"
      local i=1
      local arg file name copy
      while arg=$(/usr/libexec/PlistBuddy -c "Print :ProgramArguments:$i" "$DAEMON_PLIST" 2>/dev/null); do
        case "$arg" in
          --hostlist=*|--hostlist-exclude=*)
            file="${arg#*=}"
            if [[ -f "$file" && ! -L "$file" ]]; then
              name=$(/usr/bin/basename "$file")
              copy="$PRIV_DIR/${i}-${name}"
              /bin/cp "$file" "$copy"
              /usr/sbin/chown root:wheel "$copy"
              /bin/chmod 644 "$copy"
              /usr/libexec/PlistBuddy -c "Set :ProgramArguments:$i ${arg%%=*}=$copy" "$DAEMON_PLIST"
            fi
            ;;
        esac
        i=$((i + 1))
      done
      /usr/sbin/chown root:wheel "$DAEMON_PLIST"
      /bin/chmod 644 "$DAEMON_PLIST"
    }

    start_tpws() {
      local src="$1"
      local binary="$2"
      /bin/launchctl bootout system/com.netfix.tpws >/dev/null 2>&1 || true
      sleep 0.3
      install_tpws "$binary" || return 1
      seal_daemon_plist "$src" || return 1
      /bin/mkdir -p /Library/Logs/NetFix
      /bin/chmod 755 /Library/Logs/NetFix
      : > /Library/Logs/NetFix/tpws.err.log || true
      : > /Library/Logs/NetFix/tpws.out.log || true
      if ! /bin/launchctl bootstrap system "$DAEMON_PLIST"; then
        echo "launchctl не смог загрузить службу tpws" >&2
        /usr/bin/tail -n 40 /Library/Logs/NetFix/tpws.err.log >&2 || true
        return 1
      fi
      /bin/launchctl enable system/com.netfix.tpws >/dev/null 2>&1 || true
      /bin/launchctl kickstart -k system/com.netfix.tpws >/dev/null 2>&1 || true
      sleep 0.8
      if ! tpws_alive; then
        sleep 0.8
      fi
      if ! tpws_alive; then
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
      /bin/rm -f "$DAEMON_PLIST"
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
      if [[ -z "${CONSOLE_USER:-}" || "$CONSOLE_USER" == "root" || "${CONSOLE_UID:-0}" == "0" ]]; then
        printf '%s' "000 8"
        return
      fi
      # asuser не меняет UID. sudo -u нужен, чтобы пакет шёл не от root и попал в правила PF.
      line=$(/bin/launchctl asuser "$CONSOLE_UID" /usr/bin/sudo -u "$CONSOLE_USER" -- /usr/bin/curl -L --max-time 8 -o /dev/null -s -w "%{http_code} %{time_total}" "$url" || true)
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
