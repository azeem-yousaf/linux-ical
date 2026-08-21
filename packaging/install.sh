#!/usr/bin/env bash
set -euo pipefail

package_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
application_dir="${HOME}/.local/lib/linux-icloud-calendar"
service_dir="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"
widget_dir="${XDG_DATA_HOME:-${HOME}/.local/share}/plasma/plasmoids/com.github.azeem-yousaf.linux-ical"
applications_dir="${XDG_DATA_HOME:-${HOME}/.local/share}/applications"
icons_dir="${XDG_DATA_HOME:-${HOME}/.local/share}/icons/hicolor/scalable/apps"

command -v secret-tool >/dev/null || { echo "Install libsecret-tools before continuing." >&2; exit 1; }
command -v kpackagetool6 >/dev/null || { echo "Plasma 6 kpackagetool6 is required." >&2; exit 1; }

mkdir -p "${application_dir}" "${service_dir}" "${applications_dir}" "${icons_dir}"
cp -a "${package_dir}/app/." "${application_dir}/"
install -m 0755 "${package_dir}/open-calendar.sh" "${application_dir}/open-calendar.sh"
install -m 0755 "${package_dir}/update-calendar.sh" "${application_dir}/update-calendar.sh"
install -m 0644 "${package_dir}/linux-icloud-calendar.service" "${service_dir}/linux-icloud-calendar.service"
install -m 0644 "${package_dir}/linux-icloud-calendar.desktop" "${applications_dir}/linux-icloud-calendar.desktop"
install -m 0644 "${package_dir}/linux-icloud-calendar.svg" "${icons_dir}/linux-icloud-calendar.svg"
sed -i "s|@APPLICATION_DIR@|${application_dir}|g" "${applications_dir}/linux-icloud-calendar.desktop"
xdg-mime default linux-icloud-calendar.desktop x-scheme-handler/icloud-calendar

if [[ -d "${widget_dir}" ]]; then
  kpackagetool6 --type Plasma/Applet --upgrade "${package_dir}/plasma-widget"
else
  kpackagetool6 --type Plasma/Applet --install "${package_dir}/plasma-widget"
fi

systemctl --user daemon-reload
systemctl --user enable --now linux-icloud-calendar.service
if command -v update-desktop-database >/dev/null; then
  update-desktop-database "${applications_dir}"
fi
if command -v gtk-update-icon-cache >/dev/null; then
  gtk-update-icon-cache --force --ignore-theme-index "${XDG_DATA_HOME:-${HOME}/.local/share}/icons/hicolor" >/dev/null 2>&1 || true
fi
echo "Installed. Open ‘iCloud Calendar’ from the application menu or visit http://127.0.0.1:5088/."
echo "Add ‘iCloud Agenda’ from Plasma’s widget picker for an always-visible glance."
