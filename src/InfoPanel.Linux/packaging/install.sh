#!/bin/sh
# InfoPanel para Linux: instala (o actualiza) el ejecutable en /opt/infopanel y lo deja corriendo como servicio
# de systemd que arranca con el equipo. Usa el ejecutable que esté junto a este script; si no hay, descarga
# el último publicado.
#
#   curl -fsSL https://github.com/Brunomoyano4/InfoPanel-releases/releases/latest/download/install.sh | sudo sh
#   sudo sh install.sh               instalar o actualizar
#   sudo sh install.sh --uninstall   desinstalar (borra también la configuración y los dashboards)
set -eu

DIR=/opt/infopanel
SERVICE=/etc/systemd/system/infopanel.service
URL=https://github.com/Brunomoyano4/InfoPanel-releases/releases/latest/download/InfoPanel-linux-x64

if [ "$(id -u)" -ne 0 ]; then
  echo "Ejecutalo como root: sudo sh $0" >&2
  exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
  systemctl disable --now infopanel 2>/dev/null || true
  rm -f "$SERVICE" /usr/local/bin/infopanel
  systemctl daemon-reload
  rm -rf "$DIR"
  echo "InfoPanel desinstalado."
  exit 0
fi

if [ "$(uname -m)" != "x86_64" ]; then
  echo "Por ahora InfoPanel para Linux solo está disponible para x86_64 (este equipo es $(uname -m))." >&2
  exit 1
fi
if ! command -v systemctl >/dev/null 2>&1; then
  echo "Este sistema no usa systemd: copiá el ejecutable y arrancalo a mano." >&2
  exit 1
fi

HERE=$(cd "$(dirname "$0")" 2>/dev/null && pwd || pwd)
TMP=""
if [ -f "$HERE/InfoPanel-linux-x64" ]; then
  SRC="$HERE/InfoPanel-linux-x64"
elif [ -f "$HERE/InfoPanel" ]; then
  SRC="$HERE/InfoPanel"
else
  TMP=$(mktemp)
  echo "Descargando InfoPanel..."
  if command -v curl >/dev/null 2>&1; then curl -fL --progress-bar -o "$TMP" "$URL"
  elif command -v wget >/dev/null 2>&1; then wget -q --show-progress -O "$TMP" "$URL"
  else echo "Hace falta curl o wget para descargar InfoPanel." >&2; exit 1
  fi
  SRC="$TMP"
fi

systemctl stop infopanel 2>/dev/null || true
mkdir -p "$DIR"
install -m 755 "$SRC" "$DIR/InfoPanel"
[ -n "$TMP" ] && rm -f "$TMP"
ln -sf "$DIR/InfoPanel" /usr/local/bin/infopanel

# Corre como root: lee el consumo del CPU (RAPL), controla ventiladores, energía y el inicio con el sistema.
# Restart=always: al actualizarse desde la app, el proceso termina y systemd arranca la versión nueva.
cat > "$SERVICE" <<EOF
[Unit]
Description=InfoPanel: telemetría y control del equipo desde el celular
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$DIR/InfoPanel
WorkingDirectory=$DIR
Restart=always
RestartSec=3
Environment=HOME=/root

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now infopanel >/dev/null
sleep 2

echo
echo "InfoPanel instalado y corriendo (arranca solo con el equipo)."
echo
"$DIR/InfoPanel" pin || true
echo
echo "Abrí una de las direcciones desde el celu (misma red) e ingresá el PIN, o escaneá el QR."
echo "Más tarde: 'infopanel pin' vuelve a mostrar esto · 'journalctl -u infopanel -f' muestra el registro."

# Algunos datos usan herramientas opcionales
MISSING=""
command -v lspci >/dev/null 2>&1 || MISSING="$MISSING pciutils"
command -v dmidecode >/dev/null 2>&1 || MISSING="$MISSING dmidecode"
if [ -n "$MISSING" ]; then
  echo "Opcional: instalá$MISSING para ver los nombres de las placas de video y los módulos de RAM."
fi
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
  echo "El firewall (ufw) está activo: permití los puertos con 'ufw allow 5055:5057/tcp && ufw allow 5057/udp'."
fi
