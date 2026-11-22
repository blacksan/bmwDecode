#!/usr/bin/env bash
# One-time setup of the private bmwDecode data server on the ftnbilling VPS (Caddy already installed there).
#
#   sudo bash setup-data-server.sh [domain]          (default domain: tinker.ftw.co.ke)
#
# - Serves /srv/bmwdecode-data over HTTPS at the domain, with its own Let's Encrypt certificate.
# - Every request needs the password (HTTP basic auth, user "bmwdecode"); you type it here, it is stored only as
#   a bcrypt hash in /etc/caddy/conf.d/bmwdecode-data.caddy. No listing of the folder.
# - Leaves ftnbilling alone: the site is a separate file that the Caddyfile imports. ftnbilling's install.sh rewrites
#   the Caddyfile, so run this script again after reinstalling ftnbilling (it is safe to re-run; it keeps the data).
#
# Upload the data afterwards from the Windows laptop with installer\upload-data.ps1.
set -euo pipefail

domain="${1:-tinker.ftw.co.ke}"
dir=/srv/bmwdecode-data
site=/etc/caddy/conf.d/bmwdecode-data.caddy
caddyfile=/etc/caddy/Caddyfile

die() { echo "error: $*" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || die "run as root (sudo bash $0)"
command -v caddy >/dev/null || die "Caddy is not installed (it comes with ftnbilling)"
[ -f "$caddyfile" ] || die "$caddyfile not found"

# DNS must already point here, or Let's Encrypt cannot issue the certificate.
ip=$(getent ahostsv4 "$domain" | awk 'NR==1{print $1}') || true
echo "DNS: $domain -> ${ip:-not found}"

if [ -f "$site" ] && [ "${RESET_PASSWORD:-0}" != 1 ]; then
  echo "Keeping the existing password (RESET_PASSWORD=1 to change it)."
  hash=$(sed -n 's/^[[:space:]]*bmwdecode[[:space:]]\+\(\S\+\)[[:space:]]*$/\1/p' "$site" | head -1)
  [ -n "$hash" ] || die "could not read the existing password hash from $site; run with RESET_PASSWORD=1"
else
  read -rsp "Password for $domain: " pw; echo
  read -rsp "Same password again: " pw2; echo
  [ -n "$pw" ] || die "empty password"
  [ "$pw" = "$pw2" ] || die "passwords differ"
  hash=$(caddy hash-password --plaintext "$pw")
  unset pw pw2
fi

# Caddy 2.8 renamed basicauth to basic_auth; both versions are still around.
ver=$(caddy version | sed -n 's/^v\([0-9]\+\)\.\([0-9]\+\).*/\1 \2/p')
read -r major minor <<<"${ver:-2 0}"
auth=basicauth; { [ "$major" -gt 2 ] || { [ "$major" -eq 2 ] && [ "$minor" -ge 8 ]; }; } && auth=basic_auth

mkdir -p "$dir" "$(dirname "$site")"
chmod 755 "$dir"

tmp=$(mktemp)
cat >"$tmp" <<CADDY
# bmwDecode private data (Ecu files + data pack). Written by setup-data-server.sh.
$domain {
	$auth {
		bmwdecode $hash
	}
	root * $dir
	file_server
	header {
		Cache-Control "no-store"
		X-Robots-Tag "noindex, nofollow"
	}
	log {
		output discard
	}
}
CADDY
install -m 644 "$tmp" "$site"
rm -f "$tmp"

grep -qF "import /etc/caddy/conf.d/*.caddy" "$caddyfile" || printf '\nimport /etc/caddy/conf.d/*.caddy\n' >>"$caddyfile"

caddy validate --adapter caddyfile --config "$caddyfile" >/dev/null || die "Caddy rejected the configuration; nothing was reloaded"
systemctl reload caddy

echo
echo "Done. https://$domain serves $dir (user bmwdecode + your password)."
echo "Now upload the data from the Windows laptop:  .\\installer\\upload-data.ps1"
