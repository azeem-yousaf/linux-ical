#!/usr/bin/env bash
set -euo pipefail

calendar_url="http://127.0.0.1:5088/"
for browser in chromium chromium-browser google-chrome-stable google-chrome brave-browser microsoft-edge-stable; do
  if command -v "${browser}" >/dev/null 2>&1; then
    exec "${browser}" --app="${calendar_url}" --class=LinuxICloudCalendar
  fi
done

exec xdg-open "${calendar_url}"
