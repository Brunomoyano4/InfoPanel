# InfoPanel: documentación técnica

Cómo está hecho, cómo se compila y se publica, y el detalle de cada función. Para instalarlo y usarlo, ver el [README](README.md).

Un servicio en .NET corre en la PC, lee los sensores y sirve una web instalable (PWA) que se actualiza en vivo por WebSocket.

## Compilar y probar

Requisitos:

- Windows 10/11 y .NET 10 SDK.
- **Ejecutar como administrador** (el ejecutable lo pide solo, por el manifiesto): lo necesitan el TDP, los ventiladores y el inicio con Windows.
- Opcional: el driver **PawnIO** ([pawnio.eu](https://pawnio.eu)) para que LibreHardwareMonitor lea todos los sensores de CPU y placa (clock y temperatura por núcleo, voltajes). Sin él, la temperatura, el consumo y el clock del CPU salen de fuentes alternativas (ver "CPU sin PawnIO").
- Opcional: `ryzenadj.exe` (y sus DLL) en `src/InfoPanel.Windows/tools/` para controlar el TDP en equipos AMD que no son ASUS.

```bash
cd src/InfoPanel.Windows
dotnet build
```

Ejecutar `bin/Debug/net10.0-windows/InfoPanel.exe` (pedirá permisos de administrador). No abre ventana: aparece un **ícono en la bandeja** (junto al reloj) y un aviso con la dirección y el PIN. El menú del ícono tiene:

- La dirección (`http://192.168.x.x:5055`) y el PIN. Solo se listan IPs alcanzables desde la red: se descartan las `169.254.x.x` y los adaptadores virtuales (Wi-Fi Direct, Bluetooth, Hyper-V, VirtualBox, VMware, WSL), y primero va el adaptador conectado al router.
- **Conectar un celular (QR)…**: muestra un QR con la dirección `http://` de la página de instalación. La dirección se calcula al abrir la ventana, así toma la IP actual aunque InfoPanel haya arrancado antes de que se conectara el Wi-Fi. Si la página se abre dentro del visor interno de la cámara, muestra un botón **Abrir en Chrome**. Probé un QR con enlace `intent://` para forzar Chrome, pero no sirve: el lector de la cámara de Xiaomi lo muestra como texto.
- **Abrir panel** (también con doble clic) y **Copiar dirección**.
- **Iniciar con Windows** (tilde).
- **Salir**: detiene el servidor y devuelve los ventiladores de placa a automático.

Solo puede haber una instancia: si ya está abierto, avisa y se cierra.

En desarrollo, `dotnet build` copia `wwwroot/` (de InfoPanel.Core) junto al ejecutable y se sirve desde ahí, así se puede cambiar la web sin recompilar. Si esa carpeta no existe, se usa la copia embebida.

Puertos: **5055** HTTP, **5056** HTTPS y **5057** UDP (descubrimiento), todos configurables en `config.json`. La primera vez, Windows pregunta si permitir el acceso en el firewall: hay que aceptar en **redes privadas**. Esa regla de programa cubre los tres puertos. Si no aparece el aviso, se pueden crear las reglas a mano (como administrador):

```bash
netsh advfirewall firewall add rule name="InfoPanel" dir=in action=allow protocol=TCP localport=5055,5056 profile=private
```

```bash
netsh advfirewall firewall add rule name="InfoPanel descubrimiento" dir=in action=allow protocol=UDP localport=5057 profile=private
```

## Publicar una versión

```bash
dotnet publish src/InfoPanel.Windows -c Release -o publish/windows
dotnet publish src/InfoPanel.Linux -c Release -o publish/linux
```

- `publish/windows/InfoPanel.exe`: un solo archivo autocontenido (win-x64, ~63 MB, comprimido), con la web embebida.
- `publish/linux/InfoPanel` (se sube como `InfoPanel-linux-x64`): un solo archivo autocontenido (linux-x64, ~50 MB), con la web embebida, y `publish/linux/packaging/install.sh`. Se compila desde Windows sin nada extra.

Pasos: subir `<Version>` en `src/Directory.Build.props` (es la misma para Windows y Linux), agregar la versión al historial, publicar los dos ejecutables y crear el release con la etiqueta `vX.Y.Z` en este repositorio (es el que leen las copias instaladas), con tres archivos: `InfoPanel.exe`, `InfoPanel-linux-x64` e `install.sh`.

Historial de versiones: la fuente es `src/InfoPanel.Core/wwwroot/changelog.json` (va embebido en el exe y la app lo pide por `GET /api/changelog`, así cada PC muestra el suyo). `CHANGELOG.md` en la raíz tiene lo mismo para GitHub. Al preparar una versión: agregar su entrada arriba en el JSON con `date: null` mientras está en desarrollo, ponerle la fecha al publicarla y regenerar `CHANGELOG.md`.

## Estructura

Tres proyectos: lo común y uno por sistema (`dotnet build InfoPanel.slnx` compila los tres). La versión está en `src/Directory.Build.props`.

```
src/InfoPanel.Core/            Común: servidor, API, web, vinculación, certificados, descubrimiento,
│                              dashboards y actualizaciones
├── InfoPanelServer.cs         Servidor HTTP/HTTPS, web, WebSocket y todos los endpoints /api
├── Platform/Contracts.cs      Datos que viajan a la web e interfaces que implementa cada sistema
│                              (hardware, sistema, controles, ASUS, TDP, pantallas, inicio, apps, actualización)
├── Services/
│   ├── AuthService.cs         PIN, tokens y config.json
│   ├── CertificateService.cs  CA propia y certificado HTTPS (certs/)
│   ├── DiscoveryService.cs    Anuncio y descubrimiento de PCs por UDP
│   ├── DashboardService.cs    Dashboards personalizados (dashboards.json)
│   ├── ProcessSampler.cs      Procesos con su CPU y memoria
│   ├── TelemetryHub.cs        Clientes WebSocket
│   ├── TelemetryLoop.cs       Ciclo de 1 s que lee todo y lo envía
│   └── UpdateService.cs       Actualizaciones desde los releases de GitHub
└── wwwroot/                   Web (HTML/CSS/JS sin build)
    ├── app.js                 PCs y conexión, métricas, sensores, procesos, control, temas
    ├── dashboard.js           Dashboards: grilla, widgets, edición, configuración
    ├── setup.html             Página de instalación (certificado + app)
    ├── changelog.json         Historial de versiones (también en CHANGELOG.md)
    ├── sw.js                  Service worker de la app instalada
    └── icon-*.png             Íconos de la app (192, 512 y maskable)

src/InfoPanel.Windows/         InfoPanel.exe
├── Program.cs                 Arranque, instancia única, registro de lo de Windows y bandeja
├── WindowsPlatform.cs         Qué ofrece Windows y cómo se reinicia al actualizar
├── Native/
│   ├── Win32.cs               P/Invoke (ACPI, teclas, energía)
│   ├── DisplayConfig.cs       P/Invoke de pantallas (CCD y modos)
│   ├── PolicyConfig.cs        COM para cambiar la salida de audio
│   └── ShellImage.cs          Íconos del shell como PNG
├── Tray/
│   ├── TrayApp.cs             Ícono y menú de la bandeja
│   └── ConnectForm.cs         Ventana con el QR para conectar un celular
├── Services/
│   ├── HardwareService.cs     Sensores LibreHardwareMonitor + ventiladores de placa
│   ├── SystemService.cs       Discos, uptime, alimentación, info del sistema
│   ├── CpuFallbackService.cs  Temperatura/consumo/clock del CPU sin PawnIO
│   └── AutoStartService.cs    Tarea programada de inicio con Windows
└── Controls/
    ├── AsusAcpi.cs            Driver ACPI de ASUS (bajo nivel)
    ├── AsusControl.cs         Modos y curvas de ventilador ASUS
    ├── TdpControl.cs          TDP vía ASUS o ryzenadj
    ├── SystemControl.cs       Volumen, brillo, multimedia, energía, procesos
    ├── DisplayControl.cs      Pantallas, presets y modo de proyección
    └── AppLauncher.cs         Lista de apps instaladas, íconos y apertura sin admin

src/InfoPanel.Linux/           InfoPanel (se publica como InfoPanel-linux-x64)
├── Program.cs                 Arranque, comandos (pin, --help) y registro de lo de Linux
├── LinuxPlatform.cs           Qué ofrece este Linux, reinicio al actualizar e inicio con systemd
├── LinuxHardwareMonitor.cs    Sensores de /sys y /proc, nvidia-smi y ventiladores por PWM
├── LinuxSystemMonitor.cs      Discos, uptime, alimentación, info del sistema
├── LinuxSystemControl.cs      Audio, multimedia, brillo, energía, procesos
├── Sys.cs                     Lectura de /sys y /proc y ejecución de comandos
└── packaging/install.sh       Instalador: /opt/infopanel, servicio de systemd y comando infopanel
```

Para sumar algo que dependa del sistema: agregarlo a la interfaz en `Platform/Contracts.cs`, implementarlo en los dos proyectos (o con una implementación vacía como `NoDisplayControl`) y, si la web tiene que ocultarlo donde no existe, sumarlo a `PlatformCapsDto`.

### API

Todas las rutas `/api/*` (salvo `/api/pair` y `/api/hello`) requieren `Authorization: Bearer <token>`. Las rutas `/api/*` aceptan pedidos de cualquier origen (CORS), para que la app maneje varias PCs. Fuera de `/api` está `GET /ca.crt`, el certificado público de la CA de la PC.
El WebSocket `/ws` espera el token como primer mensaje y luego envía un JSON por segundo con `hardware`, `system`, `asus` y `controls`. `controls.caps` dice qué ofrece el sistema (`os`, `powerPlans`, `displays`, `media`, `powerActions`, `autoStartLabel`) para que la web muestre solo lo que funciona.

| Método | Ruta | Cuerpo |
|---|---|---|
| POST | `/api/pair` | `{ pin }` → `{ token }` |
| GET | `/api/hello` | — → `{ app, id, name, port, httpsPort, https[] }` (público) |
| GET | `/api/peers` | — → otras PCs con InfoPanel en la red |
| GET | `/api/info` | — → incluye `platform` (`windows` o `linux`) |
| POST | `/api/audio` | `{ volume?, mute? }` |
| POST | `/api/brightness` | `{ value }` |
| POST | `/api/media` | `{ action: playpause\|next\|prev\|stop }` |
| POST | `/api/power` | `{ action: lock\|sleep\|hibernate\|restart\|shutdown }` |
| POST | `/api/powermode` | `{ action: efficiency\|balanced\|performance }` |
| POST | `/api/powerplan` | `{ action: <guid> }` |
| POST | `/api/process/kill` | `{ value: pid }` |
| POST | `/api/fans/lhm` | `{ id, percent }` (`percent: null` = automático) |
| POST | `/api/asus/mode` | `{ value: 0 Balanced \| 1 Turbo \| 2 Silent }` |
| GET | `/api/asus/curves?mode=` | — |
| POST | `/api/asus/curve` | `{ fan: cpu\|gpu\|mid, temps[8], speeds[8] }` |
| POST | `/api/tdp` | `{ spl, sppt, fppt }` |
| POST | `/api/audio/output` | `{ action: <id del dispositivo> }` |
| POST | `/api/mic` | `{ mute }` |
| POST | `/api/asus/battery-limit` | `{ value: 40–100 }` |
| POST | `/api/display/mode` | `{ device, width, height, hz }` |
| POST | `/api/display/active` | `{ ids: [<id de pantalla>…] }` prende esas y apaga las demás |
| POST | `/api/display/layout` | `{ primary: <id>, displays: [{ id, x, y }…] }` |
| POST | `/api/display/topology` | `{ action: internal\|clone\|extend\|external }` |
| POST | `/api/display/preset/save` | `{ action: <nombre> }` |
| POST | `/api/display/preset/apply` | `{ action: <nombre> }` |
| POST | `/api/display/preset/delete` | `{ action: <nombre> }` |
| POST | `/api/autostart` | `{ enabled }` |
| GET | `/api/dashboards` | — |
| POST | `/api/dashboards/save` | `{ id, name, cols, rowH, widgets: [{ id, type, w, h, cfg }] }` |
| POST | `/api/dashboards/delete` | `{ action: <id> }` |
| GET | `/api/apps?refresh=true` | — (lista de apps instaladas) |
| GET | `/api/apps/icon?id=` | — (PNG) |
| POST | `/api/apps/launch` | `{ action: <id de la app> }` |
| POST | `/api/unpair-all` | — |
| GET | `/api/changelog` | Historial de versiones (`changelog.json`) |
| GET | `/api/update` | Estado: versión actual y nueva, novedades, progreso, error |
| POST | `/api/update/check` | Buscar ahora |
| POST | `/api/update/install` | Descargar, verificar, reemplazar y reiniciar |

## Funciones

### Actualizaciones

Desde la 1.2.0, InfoPanel se actualiza solo:

- Revisa los releases de este repositorio ([`Brunomoyano4/InfoPanel`](https://github.com/Brunomoyano4/InfoPanel/releases)) 30 segundos después de arrancar y cada 6 horas (o con **Buscar actualizaciones** en Control → App, o tocando la versión en el menú de la bandeja).
- Si hay una versión nueva, avisa con una notificación en la bandeja, un ítem **Actualizar a la versión X** en su menú, un aviso arriba del Panel (se puede ocultar hasta la próxima versión) y, en Control → App, las novedades con el botón **Actualizar**.
- Al actualizar: descarga el ejecutable de su sistema (`InfoPanel.exe` en Windows, `InfoPanel-linux-x64` en Linux), verifica que su SHA-256 coincida con la huella que publica GitHub para ese archivo (si no coincide o falta, no instala nada), renombra el ejecutable en uso a `InfoPanel.old.exe`, pone el nuevo en su lugar y lo arranca. El nuevo espera a que el anterior se cierre (argumento `--after-update`), avisa "InfoPanel actualizado" y borra el `.old`. En Linux, con el servicio de systemd, el proceso simplemente termina y systemd (`Restart=always`) arranca el ejecutable nuevo. La configuración, el PIN, los certificados y los dashboards están junto al ejecutable y no se tocan; los dispositivos vinculados se reconectan solos.
- Solo el ejecutable publicado se reemplaza a sí mismo; en desarrollo (`dotnet build`, con `InfoPanel.dll` al lado) se ve si hay versión nueva pero no se instala.
- Para probar sin GitHub, la variable de entorno `INFOPANEL_UPDATE_URL` apunta a otro JSON con el formato de la API de releases (`tag_name`, `body`, `assets[].name/size/digest/browser_download_url`).

### Tutorial e historial de versiones

- **Tutorial guiado**: la primera vez que un dispositivo abre la app (cuando llegan los primeros datos), un recorrido resalta cada parte (PC y conexión, dashboards, opciones, editar, gráficos, Sensores, Procesos y Control) con una tarjeta que la explica; se puede saltar o ir para atrás. Los pasos sin nada visible se saltean. Se vuelve a ver desde Control → App → **Ver el tutorial**.
- **Historial de versiones**: Control → App → **Historial de versiones** muestra las novedades de cada versión. Cuando una PC cambia de versión (por ejemplo, después de actualizarse), la app muestra una sola vez las novedades de la versión nueva.

### Inicio con Windows

Se activa desde el ícono de la bandeja o desde la app (Control → Sesión). Crea la tarea programada `InfoPanel`:

- Se ejecuta al iniciar sesión (10 s de demora), con privilegios elevados y sin pedir UAC.
- Arranca y sigue corriendo con batería, y sin límite de tiempo (las tareas de Windows, por defecto, no arrancan con batería y se cortan a las 72 h).
- Lanza `InfoPanel.exe --autostart`, que no muestra el aviso de inicio.
- Apunta a la ruta actual del ejecutable: si se mueve la carpeta, hay que desactivarla y volver a activarla.

La carpeta "Inicio" o la clave `Run` no sirven, porque Windows no lanza desde ahí apps que requieren administrador.

### Instalar como app (HTTPS)

Chrome (y Safari) solo instalan una web como app (pantalla completa, ícono propio, sin barra del navegador) si va por HTTPS. Sin servicios externos, InfoPanel resuelve el HTTPS así:

- Cada PC genera su propia **autoridad de certificación (CA)**, válida 10 años, y un **certificado de servidor** firmado por ella con sus IPs y nombres, válido 800 días. Se guardan en `certs/` junto al ejecutable.
- Si cambia la IP de la PC, al arrancar se emite un certificado de servidor nuevo con la misma CA, así que en el celu no hay que hacer nada.
- En el celu se instala la CA **una vez por PC**:
  1. Abrir la página de instalación: **Conectar un celular (QR)** en el ícono de la bandeja, o el aviso "Instalar como app" de la pestaña Panel (solo aparece por HTTP y se puede cerrar con ✕). Es `http://IP:5055/setup.html`.
  2. Descargar el certificado e instalarlo como **Certificado de CA**. La página detecta el dispositivo (Android, iPhone/iPad, Windows o Mac) y muestra solo sus pasos, también para instalar la app; los de otros dispositivos quedan a un toque.
  3. **Probar** la conexión segura y **Abrir InfoPanel seguro** (`https://IP:5056`).
  4. Vincular con el PIN y elegir **Instalar app** en el menú del navegador.
- La app instalada guarda su interfaz (service worker `sw.js`), así que abre aunque la PC no responda. Los datos en vivo nunca se guardan.

Notas:

- `certs/ca.pfx` contiene la clave privada de la CA: quien la tenga podría hacerse pasar por esa PC ante los celulares que confían en ella. No compartirla ni subirla a ningún lado. Si se borra la carpeta `certs/`, se generan certificados nuevos y hay que reinstalar la CA en los celulares.
- La app instalada queda atada a la dirección con la que se instaló (`https://IP:5056`). Si el router le da otra IP a la PC, hay que abrirla de nuevo con la IP nueva. Para evitarlo, conviene reservarle una IP fija a la PC en el router (reserva DHCP).
- Por HTTP todo sigue funcionando igual, solo que como página web.

### Varias PCs

Una sola app maneja todas las PCs. Tocando el nombre de la PC (arriba a la izquierda) se abre el selector:

- **Mis PCs**: las vinculadas. **Usar** cambia de PC; **✕** la quita de la lista.
- **En esta red**: otras PCs con InfoPanel abierto, encontradas automáticamente. **Vincular** pide el PIN de esa PC.
- **Agregar por dirección**: IP o `IP:puerto`, para redes donde el descubrimiento no llega (por ejemplo, otra subred o VPN).

Cómo funciona:

- **Descubrimiento**: cada PC anuncia por UDP broadcast (puerto 5057) su id, nombre y puertos cada 5 s, y guarda los anuncios de las demás. El navegador no puede escuchar la red, así que le pide la lista a la PC a la que está conectado (`/api/peers`). Una PC deja de aparecer a los 20 s sin anuncios.
- **Conexión directa**: la app habla directo con cada PC (API y WebSocket). Para eso las PCs aceptan pedidos de otros orígenes (CORS). No hay cookies: cada PC exige su propio token, que se obtiene con su PIN.
- **Por PC**: cada PC tiene sus propios dashboards; el dashboard elegido se recuerda por PC.
- Desde la app instalada (HTTPS), cada PC nueva necesita también su certificado instalado en el celu. Si falta, el selector lo avisa con el enlace a la página de instalación de esa PC.
- Los dispositivos ya vinculados antes de esta versión se migran solos: su sesión pasa a ser la primera PC de la lista.

### Panel (dashboards personalizables)

La primera pestaña, **Panel**, es un dashboard armable. Arriba se elige cuál mostrar (cada dispositivo recuerda el suyo). El botón **⋯** junto al selector abre las **opciones del dashboard** elegido, sin entrar a editar: cambiarle el nombre, marcarlo como **predeterminado** en celulares y tablets y/o en escritorio (pantallas de 1100 px o más), duplicarlo o eliminarlo. Un dispositivo de ese tipo arranca siempre en su predeterminado, aunque antes haya elegido otro; cada tipo tiene uno solo, así que marcarlo en uno lo quita del otro. El predeterminado se guarda en la PC (campo `defaultFor`), así que vale para todos los celulares o computadoras que se conecten, y se marca con ★ en el selector. **Editar** reemplaza el selector por una barra de una sola fila (**＋ Widget**, **⚙** ajustes, **Cancelar**, **Guardar**). En el celu (menos de 1100 px), mientras se edita esa barra queda fija abajo en lugar de las pestañas, así está a mano aunque se baje por el dashboard. Cada widget muestra su nombre y su tamaño, con ⚙ arriba a la izquierda, ✕ arriba a la derecha y la esquina para redimensionar abajo.

- **＋ Widget** agrega uno de cuatro tipos:
  - **Métrica**: CPU, GPU, memoria, batería, ventiladores, límites de potencia, red, discos o sistema.
  - **Sensor**: cualquier sensor (con buscador) como número, medidor o gráfico con mín/máx.
  - **Aplicaciones**: accesos con el ícono de cada app instalada, de escritorio o de la Store. Tocar uno la abre en la PC.
  - **Control**: volumen, brillo, multimedia, micrófono, modo de energía, modo ASUS, presets de TDP (configurables), límite de carga, Hz de pantalla, bloquear/suspender.
- **Mover**: arrastrar el widget a cualquier celda, también dejando huecos. Con mouse se arrastra directo; en pantallas táctiles, deslizar desplaza la página y para mover hay que **mantener apretado** un momento (vibra al activarse). Mientras se arrastra, el widget sigue al dedo, una sombra marca dónde va a quedar y los widgets que estorban se corren hacia abajo con animación (vuelven a su lugar si el widget sigue de largo). Cerca del borde de la pantalla, la página se desplaza sola.
- **Redimensionar**: arrastrar la esquina inferior derecha, o elegir ancho/alto en ⚙. Si al crecer pisa a otros, esos se corren hacia abajo.
- Cada widget guarda su posición (`x`, `y`) además del tamaño. En una pantalla con menos columnas, los que no entran se corren a la izquierda y hacia abajo hasta que no se pisan. Los widgets nuevos (o de dashboards de versiones anteriores, sin posición) van al primer hueco libre.
- **⚙** del widget lo configura y **✕** lo quita. Al agregar uno nuevo, elegir qué muestra (métrica, control o estilo de sensor) le propone el tamaño que mejor le queda.
- **⚙ de la barra** (ajustes del dashboard): nombre, columnas, tamaño de celda y alto de fila con botones segmentados, más Duplicar y Eliminar.
  - **Columnas Auto** (predeterminado): la celda tiene un tamaño fijo (chica 64, mediana 76, grande 96 o muy grande 120 px) y entran tantas columnas como quepan. Un 1×1 es siempre un cuadradito, del mismo tamaño en el celu y en la PC: con celda mediana, el celu tiene 4 columnas, la tablet 8 y una pantalla de 1600 px 17. Si un widget es más ancho que las columnas disponibles, se recorta a todo el ancho. El tamaño de celda solo se muestra con columnas Auto.
  - **Columnas fijas** (4, 6, 8 o 12): la grilla siempre tiene esa cantidad y las celdas se estiran con la pantalla.
  - **Alto de fila Cuadrado** (predeterminado): iguala el alto de cada fila al ancho de una columna, así un 1×1 es cuadrado en cualquier pantalla. Tiene un mínimo de 84 px, lo justo para el título, el valor y la barra de un 1×1, y un máximo de 160 px. También se puede fijar en 75, 90, 110 o 130 px.
- En el celu, las ventanas de configuración salen desde abajo, de borde a borde, con las acciones en una barra al pie.
- **Guardar** lo guarda en la PC; **Cancelar** descarta los cambios.
- En el selector, **+ Nuevo dashboard…** crea uno vacío.
- Si no hay predeterminado, la primera vez que un dispositivo abre una PC se elige solo el dashboard más ancho que entra completo en su pantalla (en el celu, uno de 4 columnas; en la PC, uno más ancho). Después se recuerda el elegido.

Los widgets adaptan su contenido al tamaño: en una fila, las métricas muestran solo el valor principal y los controles ocultan el título, sin scroll. El título de las métricas (ícono + nombre) es igual en todos los tamaños; si no entra, se corta con "…", y en un 1×1 se muestra solo el nombre para que se lea. El nombre tiene prioridad sobre el dato secundario del título (por ejemplo, el modelo de la GPU), que es el que se achica primero. Las etiquetas de los datos se achican en widgets angostos y nunca se pisan; en un 2×2 (datos en 2 columnas y 2 filas de alto) se muestran solo los dos primeros datos, para que la barra y el valor no se aplasten. CPU, GPU, Memoria, Batería, Límites de potencia, Red y Aplicaciones nunca scrollean: el gráfico se achica y las barras por núcleo se ocultan si el widget es bajo. Red muestra el total de bajada/subida con su gráfico y, en widgets altos con más de un adaptador activo, el detalle de cada uno. Discos siempre muestra cada unidad con su barra de espacio, y la actividad del disco físico cuando hay lugar. En Sistema angosto, cada dato va debajo de su nombre.

Los gráficos de las métricas están integrados a la tarjeta: llegan hasta los bordes de los costados (y hasta el de abajo si son lo último), con un relleno que se desvanece hacia abajo. Para eso el margen interno está en el contenido del widget (`.wbody`) y el gráfico lo cruza con márgenes negativos. En CPU, GPU, Memoria y Batería de 2 filas de alto el gráfico se oculta, porque no hay lugar. Los gráficos muestran el detalle al pasar el cursor (o tocar): el valor en ese momento, hace cuánto fue y el mínimo, promedio y máximo del historial (90 s). Mientras el historial se llena, el gráfico usa todo el ancho. En widgets más grandes, si sobra contenido se puede scrollear, pero la barra solo aparece al pasar el mouse. Con columnas fijas, el mismo dashboard se ve muy distinto en el celu y en la PC; con columnas automáticas, un solo dashboard sirve para los dos. El primer dashboard ("Principal") se crea automáticamente con las tarjetas del antiguo Resumen, y las columnas se eligen según la pantalla del dispositivo que lo crea.

Las pestañas Panel y Control ocupan todo el ancho de la ventana (en escritorio, más columnas). En Control, "Límite de carga" y "Equipo" son tarjetas compactas apiladas en un solo lugar de la grilla. Sensores y Procesos son listas y se limitan a 1100 px para que se lean bien.

Los dashboards se guardan en `dashboards.json` junto al ejecutable (máximo 30 dashboards y 100 widgets por dashboard).

#### Aplicaciones

- La lista sale de la carpeta de aplicaciones de Windows (`shell:AppsFolder`) y omite desinstaladores y enlaces web.
- Solo se pueden abrir apps de esa lista: el servidor rechaza cualquier otro id o ruta.
- Se abren a través del Explorador, así corren **sin permisos de administrador** aunque InfoPanel sí los tenga.
- Los íconos se generan en la PC con transparencia y se guardan en memoria.

### Qué muestra

- **CPU**: carga total y barras por núcleo, temperatura, consumo (W), clock, voltajes.
- **GPU** (AMD / NVIDIA / Intel): carga, temperatura, consumo, clock, VRAM.
- **Memoria** física y virtual.
- **Batería**: nivel, si está enchufada (Cargando / Enchufada) o a batería (según Windows), carga/descarga en W, tiempo restante, desgaste.
- **Ventiladores**: RPM (placa vía LibreHardwareMonitor y ASUS vía ACPI).
- **Red**: subida/bajada total con historial, y por adaptador.
- **Discos**: espacio, temperatura, lectura/escritura.
- **Sistema**: versión de Windows, placa, BIOS, GPU y driver, módulos de RAM, dirección del panel y tiempo encendido.
- **Procesos**: buscador por nombre o PID y los 50 primeros según la columna elegida (tocar la cabecera ordena por nombre, CPU o RAM; tocarla de nuevo invierte el orden), con opción de cerrarlos.
- Suspender, Hibernar, Reiniciar y Apagar piden confirmación en una ventana de la app con el nombre de la PC (no con el diálogo del navegador, que el celu permite desactivar).
- Tocar la pestaña en la que ya se está vuelve arriba de todo. Con una ventana abierta, el fondo no se desplaza.
- Silenciar el audio o el micrófono cambia el botón al instante (sin esperar el próximo dato de la PC) y vuelve atrás si la PC rechaza el cambio.
- Pestaña **Sensores** con todos los sensores crudos (mín/máx) y filtro.

#### CPU sin PawnIO

Sin el driver PawnIO, LibreHardwareMonitor da 0 en temperatura, consumo y clock del CPU. En ese caso se usan fuentes alternativas, que aparecen como sensores extra del CPU:

| Dato | Fuente | Sensor |
|---|---|---|
| Temperatura | ACPI de ASUS (`0x00120094`) | `CPU (ACPI ASUS)` |
| Temperatura (otras marcas, ej. Intel) | Zonas térmicas ACPI de Windows (`Thermal Zone Information\High Precision Temperature`, la más caliente) | `Zona térmica (ACPI)` |
| Consumo del paquete | Contador de Windows `Energy Meter(RAPL_Package0_PKG)\Power` | `Package (RAPL)` |
| Clock efectivo | `% Processor Performance` × frecuencia base | `Efectivo (Windows)` |

El panel toma el primer valor mayor a 0 según el orden: LHM → alternativo. Si se instala PawnIO, se usan los sensores de LHM.

### Qué controla

| Control | Cómo | Equipos |
|---|---|---|
| Volumen / silencio | Core Audio (NAudio) | Todos |
| Silenciar micrófono (todos los micrófonos activos) | Core Audio | Todos |
| Dispositivo de salida de audio predeterminado | `IPolicyConfig` (COM no documentada de Windows) | Todos |
| Resolución y frecuencia (Hz) de cada pantalla | `ChangeDisplaySettingsEx` | Todos |
| Límite de carga de batería (60 / 80 / 100 %) | ACPI ASUS (`0x00120057`) | ROG Ally, laptops ROG/TUF |
| Brillo | WMI `WmiMonitorBrightness` | Pantallas internas (laptops, Ally) |
| Multimedia (play/pausa, siguiente, anterior) | Teclas multimedia | Todos |
| Modo de energía de Windows 11 (eficiencia/equilibrado/rendimiento) y plan de energía | `powrprof` / `powercfg` | Todos |
| Bloquear, suspender, hibernar, reiniciar, apagar | Win32 / `shutdown` | Todos |
| Cerrar procesos | `Process.Kill` | Todos |
| Modo ASUS (Silent / Balanced / Turbo) | ACPI `\\.\ATKACPI` | ROG Ally, laptops ROG/TUF |
| Curvas de ventilador (8 puntos, CPU/GPU/Mid) | ACPI ASUS | ROG Ally, laptops ROG/TUF |
| TDP (SPL / sPPT / fPPT) | ACPI ASUS, o `ryzenadj.exe` | ASUS, o CPUs AMD Ryzen |
| Ventiladores de placa (% fijo o automático) | LibreHardwareMonitor | Placas con Super I/O compatible |
| Presets de pantallas (orden/posición, resolución, Hz, principal, cuáles activas) | API CCD `SetDisplayConfig` | Todos |
| Modo de proyección (solo interna / duplicar / extender / solo externa, como Win+P) | API CCD | Todos |
| Iniciar con Windows | Tarea programada elevada | Todos |

**Modo de energía y plan de energía** son dos ajustes distintos de Windows y la tarjeta "Energía de Windows" explica cada uno. El **modo** (Eficiencia / Equilibrado / Rendimiento) es el selector rápido de Windows 11 y se aplica encima del plan activo. El **plan** es el esquema clásico de Opciones de energía (cuándo suspender, estados del procesador, etc.); en los ASUS, Armoury Crate cambia el plan con cada modo.

### Pantallas: disposición y presets

En Control → Pantallas:

- **Mapa de disposición** (como en Configuración de Windows): cada pantalla prendida es un rectángulo a escala con su número; la principal está resaltada. Se arrastra una pantalla y al soltarla se pega al borde de la más cercana, alineando bordes cercanos y sin pisarse. **Aplicar ubicación** la manda a Windows (la principal queda en 0,0 y las demás se corren igual); **Deshacer** vuelve a la actual. Un toque sin arrastrar no cuenta como cambio, y prender, apagar o cambiar la principal descarta los movimientos sin aplicar, así el mapa siempre muestra las pantallas que están prendidas.
- **Lista de pantallas conectadas**, también las apagadas: el interruptor prende o apaga cada una (apagar pide confirmación y siempre tiene que quedar una prendida). Al prender una, Windows elige su resolución y la ubica a un costado; después se acomoda en el mapa. **Hacer principal** cambia la pantalla principal sin mover nada.

- **Guardar actual**: guarda la configuración completa de Windows (orden y posición, resolución, frecuencia, rotación, pantalla principal y cuáles están activas) con un nombre. Primero se acomoda todo en Configuración de Windows → Pantalla y después se guarda.
- **Aplicar**: valida la configuración con las pantallas conectadas y la aplica. Si falta algún monitor del preset, Windows la rechaza y no cambia nada.
- **Modos rápidos**: solo interna, duplicar, extender, solo externa, con divisiones entre opciones. El modo actual se rellena con el color de acento: se deduce de las pantallas prendidas (una sola interna o externa, todas en el mismo lugar = duplicar, en lugares distintos = extender). Al tocar uno se marca en el momento y, si Windows lo rechaza, vuelve al real.

Se guardan en `display-presets.json` junto al ejecutable. Como los identificadores de las placas de video cambian entre reinicios, al aplicar se reasignan buscando cada monitor por su id, que sí es estable.

### Temas

En Control → App: Oscuro, AMOLED (negro puro), Claro, ROG (rojo), Cyber, Océano y **Personalizado**. La elección se guarda en el navegador de cada dispositivo.

El tema **Personalizado** abre un editor:

- **Base**: uno de los temas anteriores (define fondo, tarjetas y texto).
- **Acento**: botones, barras y selección. Se elige con el selector de color, escribiendo el hexadecimal (`#7c6cff` o `#7cf`) o con los colores sugeridos.
- **Secundario**: gráficos y detalles.
- Los cambios se ven en vivo. El texto sobre el acento se pone negro o blanco según su luminancia, para que siempre se lea. **Restablecer** vuelve a los valores iniciales. Los colores están definidos como variables CSS en `wwwroot/style.css` (`:root[data-theme="..."]`) y la lista en `THEMES` de `app.js`.

### Configuración (`config.json`)

Se crea junto al ejecutable en el primer arranque:

| Clave | Default | Descripción |
|---|---|---|
| `Port` | `5055` | Puerto HTTP |
| `HttpsPort` | `5056` | Puerto HTTPS (app instalable) |
| `DiscoveryPort` | `5057` | Puerto UDP de descubrimiento de PCs |
| `InstanceId` | aleatorio | Identificador estable de esta PC en el selector |
| `Pin` | aleatorio | PIN de emparejamiento (borrarlo genera uno nuevo) |
| `TdpMin` / `TdpMax` | `5` / `30` | Rango permitido de TDP en W. 30 W es el máximo del ROG Ally; en laptops puede subirse |
| `BatteryLimit` | `null` | Último límite de carga elegido; se reaplica al arrancar porque el firmware puede volver a 100 % |
| `Tokens` | `[]` | Dispositivos vinculados. "Desvincular todos" desde la app lo vacía |

### Diseño de la UI

Sistema visual (implementado en `wwwroot/style.css`, bloque "Sistema visual v2"):

- **Colores**: superficies grafito (`--bg`, `--card`, `--card-2`, `--card-3`), bordes `--line` / `--line-soft` y un solo acento. El estado seleccionado usa `--accent-soft` (fondo) y `--accent-line` (borde) en lugar de un relleno sólido; el relleno sólido de acento queda para la acción principal.
- **Tipografía**: fuente del sistema con cifras tabulares. Valor protagonista 34 px/700, título de tarjeta 15 px/600 en color de texto con ícono de línea, etiquetas 12 px/500.
- **Medidas**: tarjeta con radio 16 y padding 16; controles con radio 12 y 44 px de alto (táctil); espaciado en base 4.
- **Componentes**: botones (primario, secundario, contorno, fantasma, peligro, ícono), segmentados, chips, interruptores, búsqueda, ventana inferior con asa en el celu.
- **Navegación**: pestañas con ícono abajo en el celu; desde 1100 px se convierten en barra lateral con el logo. Con el celu en horizontal (menos de 500 px de alto) pasan a una barra lateral angosta de íconos y el encabezado queda en una sola fila baja, para dejarle el alto al contenido.
- **Tarjetas**: los gráficos ocupan el alto que sobra, con área suave bajo la línea (CPU, GPU, Memoria, Batería, potencia), así las tarjetas altas no quedan con huecos.
- **Sensores**: buscador y chips por tipo (Temperatura, Carga, Consumo, Clock…), con los nombres de tipo en español.
- **Procesos**: PID bajo el nombre y una barrita de CPU por fila.
- **Control**: volumen y brillo con ícono, micrófono como interruptor y curva de ventilador que muestra solo el valor del punto que se toca. Arriba tiene un buscador y chips de categoría, como Sensores:
  - Categorías: **Sonido** (sonido y pantalla, multimedia), **Energía** (energía de Windows, TDP, límite de carga, equipo), **Ventiladores** (modo ASUS y curva, ventiladores de placa), **Pantallas** y **App** (tema, inicio con Windows, sesión).
  - Solo aparecen las categorías que existen en esa PC.
  - La búsqueda mira títulos, botones y opciones (por ejemplo, "apagar" deja solo Equipo).
  - Cada dispositivo recuerda la última categoría elegida.
  - Cada tarjeta se marca con `data-cat` en `buildControl` (`app.js`); para sumar una categoría, agregarla a `CONTROL_CATS`.

### Linux

La misma app y la misma web, con el hardware leído de `/sys` y `/proc` y los controles hechos con las herramientas del sistema. Corre como root en el servicio `infopanel` de systemd (lo crea `install.sh`), sin interfaz propia. Funciona en servidores sin escritorio (Proxmox, Debian, Ubuntu Server) y en equipos de escritorio. Solo x86_64.

**Qué muestra**: los mismos widgets que en Windows. Los sensores se arman con los nombres de LibreHardwareMonitor, así la web los usa igual.

| Dato | Fuente |
|---|---|
| CPU: carga total y por núcleo, clocks | `/proc/stat`, cpufreq (o `/proc/cpuinfo` en máquinas virtuales) |
| CPU: temperatura | `coretemp` (Intel), `k10temp`/`zenpower` (AMD); si no, zonas térmicas (`x86_pkg_temp`, ACPI) |
| CPU: consumo | RAPL (`/sys/class/powercap`, requiere root) |
| Memoria y swap | `/proc/meminfo` |
| GPU NVIDIA | `nvidia-smi` (carga, temperatura, consumo, clocks, VRAM, ventilador) |
| GPU AMD | `amdgpu` (carga, VRAM y su hwmon: temperaturas, consumo, clocks, ventilador) |
| GPU Intel | Frecuencia (i915 o xe), carga estimada por el tiempo fuera de reposo (RC6) y consumo del dominio `uncore` de RAPL; no tiene temperatura propia |
| Discos | `/proc/diskstats` (lectura/escritura, actividad) y temperatura (`nvme`, `drivetemp`); espacio de cada sistema de archivos real (en ZFS, la raíz y los pools, no cada dataset de VM o contenedor) |
| Red | `/proc/net/dev` de las placas físicas conectadas (no los puentes `vmbr`, `docker`, `veth`, `tap`: repetirían el tráfico) |
| Placa | El resto de los chips hwmon: ventiladores (aparecen cuando giran alguna vez), temperaturas y voltajes |
| Batería y alimentación | `/sys/class/power_supply` |
| Sistema | `/etc/os-release` y kernel, DMI (BIOS y placa), RAM con `dmidecode`, GPUs con `lspci` |

**Qué controla**:

| Control | Cómo | Cuándo aparece |
|---|---|---|
| Volumen, silencio, micrófono, salida | `pactl` (PulseAudio o PipeWire) o `wpctl`, como el usuario con sesión abierta | Si hay audio y una sesión de usuario |
| Multimedia | `playerctl` | Si está instalado |
| Brillo | `/sys/class/backlight` | Si hay pantalla interna |
| Modo de energía | `powerprofilesctl`; si no, la preferencia de energía del CPU (EPP) o el regulador (`powersave`, `schedutil`, `performance`) | Si alguno está disponible |
| Bloquear, suspender, hibernar, reiniciar, apagar | `loginctl lock-sessions`, `systemctl suspend/hibernate/reboot/poweroff` | Bloquear con sesión gráfica; suspender e hibernar si el kernel los ofrece (`/sys/power/state`) |
| Ventiladores de la placa | PWM de hwmon (`pwmN_enable` = 1 manual, se vuelve al modo original con Automático y al cerrar) | Si el chip lo permite |
| Cerrar procesos | Igual que en Windows | Siempre |
| Iniciar con el sistema | `systemctl enable/disable infopanel` | Siempre (necesita el servicio de `install.sh`) |

No hay control de pantallas, modos ASUS, TDP ni lista de aplicaciones: la web oculta esas tarjetas y widgets (cada PC informa qué ofrece en `controls.caps`).

**Instalación sin descargar**: también se puede bajar `InfoPanel-linux-x64` e `install.sh` del release, ponerlos en la misma carpeta y correr `sudo sh install.sh` (usa el ejecutable que tiene al lado). `sudo sh install.sh --uninstall` lo desinstala.

**Comandos**: `infopanel pin` (direcciones, PIN y QR), `infopanel --help`. Los datos (`config.json`, `certs/`, `dashboards.json`) están en `/opt/infopanel`. Puertos: 5055 (HTTP), 5056 (HTTPS) y 5057 (UDP, descubrimiento); si hay firewall, hay que permitirlos.

**Herramientas opcionales**: `pciutils` (nombres de las GPUs), `dmidecode` (módulos de RAM), `nvidia-smi` (viene con el driver de NVIDIA), `playerctl`, `power-profiles-daemon`.

**Probado en WSL con Debian 13 y systemd**: instalación con `install.sh` (servicio, comando `infopanel`, PIN y QR en la terminal), vinculación por PIN, telemetría por WebSocket (CPU, memoria y swap, discos, red, batería, procesos), página de Control con solo lo disponible, inicio con el sistema (`systemctl enable/disable`), cerrar procesos, actualización desde la app (descarga, verificación, reemplazo y reinicio por systemd conservando la configuración) y `--uninstall`. Sin hardware real: en WSL no hay temperaturas, RAPL, GPU, DMI ni PCI, así que esas fuentes no están verificadas en un equipo físico. Para probar al lado de un InfoPanel de Windows, usar otros puertos en `/opt/infopanel/config.json` (`Port`, `HttpsPort`, `DiscoveryPort`).

## Notas y limitaciones

- **ASUS**: el ACPI no permite leer el modo actual ni los límites de TDP. El modo se deduce del plan de energía que activa Armoury Crate (Silent / Performance / Turbo), pero solo cuando ese plan cambia: un cambio de modo hecho desde InfoPanel no cambia el plan, así que se respeta el último modo aplicado.
- El TDP solo se conoce después de aplicarlo desde InfoPanel, y vuelve a "desconocido" con cada cambio de modo.
- El ACPI siempre devuelve la curva de fábrica; InfoPanel recuerda las curvas aplicadas hasta el próximo cambio de modo.
- **Armoury Crate** puede sobrescribir el TDP y las curvas al cambiar de modo desde su interfaz o el botón del Ally.
- Cambiar el modo ASUS restaura las curvas y los límites de potencia de fábrica de ese modo (así funciona el firmware).
- Al cerrar InfoPanel, los ventiladores de placa vuelven a automático.
- Es HTTP en la red local: el PIN y los tokens protegen el acceso, pero no hay cifrado. No exponer el puerto a Internet.

### Pruebas realizadas (ROG Ally RC71L, como administrador)

| Control | Resultado |
|---|---|
| Volumen, silencio, brillo, modo de energía de Windows | Aplicados y releídos correctamente |
| Modo ASUS Turbo → Silent | Ventiladores de 5400 a 3400 RPM en 20 s |
| Curva de ventilador CPU | CPU de 3100 a 5500 RPM; curvas decrecientes rechazadas |
| TDP 8 W vs Turbo de fábrica | Rendimiento multinúcleo al ~20 %; volver a Turbo restaura el 100 % |
| TDP fuera de rango (50 W) | Recortado a `TdpMax` (30 W) |
| Presets de pantallas | Guardar, aplicar, validar y borrar OK con la pantalla interna (no hubo monitor externo para probar varias) |
| Inicio con Windows | Tarea creada elevada, con batería y sin límite; se quita bien |
| CPU sin PawnIO | 57–60 °C (ACPI), 11–13 W (RAPL), ~3600 MHz efectivos |
| Micrófono | Silenciar y activar OK |
| Frecuencia de pantalla | 120 → 60 → 120 Hz OK; modos no soportados rechazados |
| Límite de carga | 100 % aplicado y guardado; valores fuera de 40–100 rechazados |
| Dashboards | Crear, agregar los 4 tipos de widget, redimensionar, mover, guardar, recargar y borrar OK |
| Apps | 114 apps listadas; íconos con transparencia; Calculadora abierta desde el widget sin admin; ids fuera de la lista rechazados |
| HTTPS | Cadena validada con OpenSSL contra la CA propia; el certificado incluye la IP, `localhost` y el nombre de la PC |
| Descubrimiento | Una PC simulada por UDP aparece en `/api/peers` y en el selector; los paquetes inválidos se ignoran |
| Varias PCs | Sesión anterior migrada; vinculación por dirección entre orígenes (PIN incorrecto rechazado); API y WebSocket hacia la PC elegida |
| App instalada en un celu real | No probada (requiere instalar la CA en el teléfono) |
| Salida de audio | No probada (el Ally tiene un solo dispositivo de salida) |
| Multimedia, suspender, apagar, ventiladores de placa, modos de proyección | No probados (el Ally no tiene ventiladores controlables por LibreHardwareMonitor) |
