# Historial de versiones

Los mismos datos se muestran en la app (Control → App → Historial de versiones) y salen de `src/InfoPanel.Core/wwwroot/changelog.json`.

## 1.5.1 — 2026-10-28

- Las actualizaciones y la instalación en Linux se descargan del repositorio público de InfoPanel, que reemplaza al de releases. Las versiones 1.2.0 a 1.5.0 no encuentran esta: hay que reemplazar el ejecutable a mano una vez.

## 1.5.0 — 2026-10-27

- Versión para Linux: corre como servicio de systemd, también en servidores como Proxmox, y se instala con una sola línea que muestra la dirección, el PIN y un QR en la terminal.
- En Linux muestra CPU, memoria, GPU (NVIDIA, AMD e Intel), discos, red, ventiladores, batería y procesos, y permite reiniciar, apagar, suspender, cambiar el modo de energía, controlar ventiladores, cerrar procesos y elegir si arranca con el equipo.
- La app muestra en cada PC solo los controles que su sistema ofrece.
- Las PCs con Linux también se actualizan desde la app.

## 1.4.0 — 2026-10-27

- Celu en horizontal: las pestañas pasan a una barra lateral angosta de íconos y el encabezado queda en una sola fila baja, para dejarle el alto al contenido.
- Widgets chicos (2×2): el nombre ya no se corta por el dato secundario, las etiquetas de los datos no se pisan y se muestran solo los datos que entran sin aplastar la barra.

## 1.3.0 — 2026-10-27

- Historial de versiones en Control → App, y las novedades se muestran solas la primera vez que se abre una versión nueva.
- Tutorial guiado por la app: aparece la primera vez y se puede volver a ver desde Control → App.
- Widgets de 1 fila de alto: el nombre ya no se esconde (el dato secundario se achica primero) y, al editar, el nombre y el tamaño quedan en una fila entre ⚙ y ✕.

## 1.2.0 — 2026-10-27

- Actualizaciones desde la app: InfoPanel busca versiones nuevas al arrancar y cada 6 horas.
- Aviso en la bandeja, arriba del Panel y en Control → App, con las novedades y el botón Actualizar.
- Al actualizar, descarga la versión nueva, verifica que sea la publicada, se reemplaza y se reinicia conservando la configuración, el PIN, los certificados y los dashboards.
- Releases públicos en GitHub (InfoPanel-releases); el código sigue privado.

## 1.1.0 — 2026-10-27

- Widgets con posición libre: se pueden dejar en cualquier lugar; al arrastrar, una sombra marca dónde van a quedar y los demás se corren con animación.
- En el celu, mantener apretado para mover y barra de edición fija abajo.
- Dashboard predeterminado para celulares y para escritorio, y botón ⋯ para cambiar el nombre, duplicar o eliminar sin editar.
- Tamaño recomendado al agregar un widget y dashboard automático según la pantalla la primera vez.
- Red con gráfico, Discos con barra, Sistema y Ventiladores adaptables, títulos iguales en todos los tamaños y sin scroll innecesario.
- Gráficos de borde a borde con relleno degradado y detalle al pasar el cursor o tocar.
- Batería: indica si está enchufada o a batería.
- Editor de pantallas: mapa para ubicarlas, prender o apagar cada una y elegir la principal; el modo actual (Interna, Duplicar, Extender, Externa) queda marcado.
- Modo y plan de energía con su explicación; confirmación propia para suspender, hibernar, reiniciar y apagar; silenciar sin demora.
- Temperatura del CPU en Intel y otras marcas sin PawnIO (zonas térmicas de Windows).
- Buscador de procesos, sensores colapsados al entrar y tocar la pestaña actual vuelve arriba.
- Página de instalación según el dispositivo (Android, iPhone, Windows o Mac).

## 1.0.0 — 2026-10-27

- Primera versión: telemetría en vivo de la PC desde el celular por la red local (CPU, GPU, memoria, batería, ventiladores, red, discos y sistema).
- Dashboards armables con widgets de métricas, sensores, accesos a aplicaciones y controles, que se guardan en la PC.
- Pestañas de Sensores (con filtros por tipo) y Procesos (ordenables, con opción de cerrarlos).
- Controles: volumen, salida de audio, micrófono, brillo, multimedia, modo y plan de energía, pantallas (resolución, Hz, modos y presets) y bloquear, suspender, hibernar, reiniciar o apagar.
- Equipos ASUS: modo Silent/Balanced/Turbo, curvas de ventilador, TDP y límite de carga de la batería.
- Varias PCs desde la misma app, con detección automática en la red.
- App instalable en el celu (HTTPS con certificado propio de cada PC) y conexión por QR.
- Temas (Oscuro, AMOLED, Claro, ROG, Cyber, Océano y personalizado), ícono en la bandeja e inicio con Windows.
