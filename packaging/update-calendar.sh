#!/usr/bin/env bash
set -euo pipefail

version="${1:-}"
runtime="${2:-}"
archive_url="${3:-}"
checksums_url="${4:-}"
asset="linux-icloud-calendar-v${version}-${runtime}.tar.gz"
release_base="https://github.com/azeem-yousaf/linux-ical/releases/download/v${version}"

[[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid update version." >&2; exit 2; }
[[ "${runtime}" == "linux-x64" || "${runtime}" == "linux-arm64" ]] || { echo "Unsupported update architecture." >&2; exit 2; }
[[ "${archive_url}" == "${release_base}/${asset}" ]] || { echo "Unexpected update archive URL." >&2; exit 2; }
[[ "${checksums_url}" == "${release_base}/SHA256SUMS" ]] || { echo "Unexpected checksum URL." >&2; exit 2; }

cache_base="${XDG_CACHE_HOME:-${HOME}/.cache}"
mkdir -p "${cache_base}"
update_root="$(mktemp -d "${cache_base}/linux-icloud-calendar-update.XXXXXX")"
cleanup() { rm -rf -- "${update_root}"; }
restart_on_failure() { systemctl --user restart linux-icloud-calendar.service >/dev/null 2>&1 || true; }
trap cleanup EXIT

curl --fail --location --proto '=https' --tlsv1.2 --silent --show-error "${archive_url}" --output "${update_root}/${asset}"
curl --fail --location --proto '=https' --tlsv1.2 --silent --show-error "${checksums_url}" --output "${update_root}/SHA256SUMS"
expected="$(awk -v name="${asset}" '$2 == name || $2 == "*" name { print $1 }' "${update_root}/SHA256SUMS")"
[[ "${expected}" =~ ^[0-9a-fA-F]{64}$ ]] || { echo "The release checksum is missing or invalid." >&2; exit 3; }
actual="$(sha256sum "${update_root}/${asset}" | awk '{print $1}')"
[[ "${actual,,}" == "${expected,,}" ]] || { echo "The downloaded update failed checksum verification." >&2; exit 3; }

if tar -tzf "${update_root}/${asset}" | awk 'BEGIN{bad=0} /^\// || /(^|\/)\.\.($|\/)/ {bad=1} END{exit bad ? 0 : 1}'; then
  echo "The update archive contains an unsafe path." >&2
  exit 3
fi
mkdir -p "${update_root}/package"
tar -xzf "${update_root}/${asset}" -C "${update_root}/package" --no-same-owner
[[ -x "${update_root}/package/install.sh" && -f "${update_root}/package/app/ICloudCalendar.Web" ]] || { echo "The update package is incomplete." >&2; exit 3; }

sleep 2
systemctl --user stop linux-icloud-calendar.service
trap restart_on_failure ERR
bash "${update_root}/package/install.sh"
systemctl --user restart linux-icloud-calendar.service
trap - ERR
