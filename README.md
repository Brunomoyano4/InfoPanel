# InfoPanel

Telemetría y control de la PC desde el celular (u otro dispositivo), por la red local.
Un servicio corre en la PC, lee los sensores y sirve una web instalable como app que se actualiza en vivo.
Hay versión para **Windows** (con ícono en la bandeja) y para **Linux** (servicio de systemd, pensada también para servidores como Proxmox).

## Qué hace

- **Muestra en vivo**: CPU (carga por núcleo, temperatura, consumo, clock), GPU (AMD, NVIDIA e Intel), memoria, batería, ventiladores, red, discos, datos del sistema, procesos (con opción de cerrarlos) y todos los sensores crudos.
- **Dashboards armables**: widgets de métricas, sensores, accesos a aplicaciones y controles, que se mueven y redimensionan desde el celu.
- **Controla**: volumen, micrófono y salida de audio, brillo, multimedia, modo y plan de energía, bloquear, suspender, reiniciar y apagar, ventiladores de placa, resolución, Hz y presets de pantallas e inicio con el sistema.
- **En equipos ASUS** (ROG Ally, laptops ROG/TUF): modo Silent/Balanced/Turbo, curvas de ventilador, TDP y límite de carga de batería.
- **Varias PCs** desde una sola app, temas de color y actualizaciones automáticas.

Cada PC muestra solo los controles que su sistema ofrece (en Linux no hay pantallas, ASUS, TDP ni aplicaciones).

## Instalar

### Windows

1. Descargar `InfoPanel.exe` de la [última versión](https://github.com/Brunomoyano4/InfoPanel/releases/latest). Es un solo archivo: no hace falta instalar .NET ni nada más.
2. Ponerlo en una carpeta propia (por ejemplo `C:\InfoPanel`), porque al lado se van a crear su configuración y sus certificados.
3. Abrirlo y aceptar el permiso de administrador. Como el ejecutable no está firmado, Windows puede mostrar "Windows protegió su PC": hay que tocar **Más información → Ejecutar de todas formas**.
4. Aceptar el aviso del firewall en **redes privadas**.
5. Aparece el ícono de InfoPanel junto al reloj. Desde su menú, **Conectar un celular (QR)…** muestra un QR para abrirlo en el celu, y ahí se ve también el PIN.
6. Opcional: activar **Iniciar con Windows** desde el mismo menú.

Requisitos: Windows 10/11 de 64 bits. Opcional: el driver [PawnIO](https://pawnio.eu) para leer todos los sensores de CPU y placa (sin él, la temperatura, el consumo y el clock del CPU salen de otras fuentes).

### Linux

Con una sola línea, como root (instala en `/opt/infopanel`, crea el servicio `infopanel` de systemd y lo deja arrancando con el equipo):

```bash
curl -fsSL https://github.com/Brunomoyano4/InfoPanel/releases/latest/download/install.sh | sudo sh
```

Al terminar muestra las direcciones, el PIN y un QR en la terminal. Después, `infopanel pin` los vuelve a mostrar y `journalctl -u infopanel -f` muestra el registro. Para desinstalarlo (borra también la configuración y los dashboards):

```bash
curl -fsSL https://github.com/Brunomoyano4/InfoPanel/releases/latest/download/install.sh | sudo sh -s -- --uninstall
```

Requisitos: x86_64 con systemd. Opcionales: `pciutils` y `dmidecode` (nombres de GPU y módulos de RAM), `nvidia-smi` (viene con el driver de NVIDIA), `playerctl` y `power-profiles-daemon`.

### Puertos

**5055** HTTP, **5056** HTTPS y **5057** UDP (descubrimiento de otras PCs). En Windows alcanza con aceptar el aviso del firewall; en Linux, si hay firewall, hay que abrirlos.

## Conectar el celu

1. En la misma red Wi-Fi, escanear el QR (o abrir la dirección que muestra InfoPanel).
2. Ingresar el PIN. El dispositivo queda vinculado y no vuelve a pedirlo.
3. La primera vez, un tutorial muestra cada parte de la app.

Cada PC tiene su propio PIN, sus certificados y sus dashboards. Para sumar otra PC, tocar el nombre de la PC arriba a la izquierda: aparecen las que tienen InfoPanel en la red, o se agregan por dirección.

### Instalarlo como app

Chrome y Safari solo instalan una web como app si va por HTTPS. InfoPanel genera su propio certificado, y en el celu hay que instalarlo **una vez por PC**:

1. Abrir la página de instalación: **Conectar un celular (QR)** en la bandeja, o el aviso "Instalar como app" de la pestaña Panel.
2. Descargar el certificado e instalarlo como **Certificado de CA** (la página muestra los pasos para Android, iPhone/iPad, Windows o Mac).
3. **Abrir InfoPanel seguro**, vincular con el PIN y elegir **Instalar app** en el menú del navegador.

La app queda atada a la IP de la PC: conviene reservarle una IP fija en el router.

## Actualizaciones

InfoPanel busca versiones nuevas al arrancar y cada 6 horas. Cuando hay una, avisa en la app (Control → App → **Actualizar**) y, en Windows, también en la bandeja. Se descarga, se verifica y se reinicia sola, conservando la configuración. Las novedades de cada versión están en Control → App → **Historial de versiones** y en [CHANGELOG.md](CHANGELOG.md).

## Seguridad

- Es para la red local: el PIN y los tokens protegen el acceso, pero por HTTP no hay cifrado. No exponer los puertos a Internet.
- `certs/ca.pfx` (junto al ejecutable) tiene la clave privada del certificado de esa PC: no compartirla.

## Más información

[DESARROLLO.md](DESARROLLO.md) tiene el detalle de cada función, la configuración (`config.json`), cómo compilar y publicar, la estructura del código, la API, las limitaciones con equipos ASUS y las pruebas realizadas.
