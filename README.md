# Miniaplicación de Spotify para Cinnamon

Controla Spotify desde el panel de Cinnamon (Linux Mint).

- Icono en el panel; al pasar el ratón, vista previa con portada, canción y **todos** los artistas.
- Menú con portada, aleatorio, anterior/pausa/siguiente, repetir (no / lista / canción).
- Barra de progreso animada con 6 diseños a elegir en *Configurar*: líquida, clásica de Spotify, onda de sonido, neón, cuentas y ecualizador. Clic o arrastrar para saltar, rueda = ±5 s.
- Volumen propio de Spotify (independiente del del sistema). Rueda del ratón sobre el icono = volumen, clic central = pausa.
- Buscar dentro del propio menú (canciones, álbumes, artistas, listas) o pegar un enlace `open.spotify.com` para reproducirlo.
- Copiar enlace de la canción, mostrar / cerrar Spotify.
- Textos largos se desplazan solos.
- Con tu cuenta conectada: corazón de **Me gusta** junto al título y **añadir / quitar la canción de tus listas**.
- **Reproducir lista**: tus listas y Canciones que te gustan. Con el aleatorio activado empiezan por una canción al azar. El menú no se cierra al elegir.
- Deja Spotify **en segundo plano, sin ventana**, al abrirse (con la ventana visible gasta ~50 % de CPU, de fondo ~4 %); se desactiva en *Configurar*. *Mostrar Spotify* abre la ventana y cerrarla la vuelve a dejar de fondo.

## Windows

Hay una versión para Windows (icono junto al reloj) en [`windows/`](windows/README.md).

## Instalar

```bash
git clone https://github.com/danii1199/spotify-applet.git
cd spotify-applet
./install.sh
```

Necesita `python3`, `curl`, `wget` y `xdg-utils` (vienen en Mint). Para quitarla: `./install.sh --uninstall`.
Con `./install.sh --link` la miniaplicación apunta al repo, así que basta con `git pull` y recargar.

## Conectar la cuenta (listas y Me gusta)

Spotify no permite tocar listas desde el escritorio (MPRIS), así que esto usa la Web API con tu propia app de desarrollador:

1. En <https://developer.spotify.com/dashboard> → **Create app**.
2. Redirect URI: `http://127.0.0.1:8898/callback` y marcar **Web API**.
3. Copiar el **Client ID** → clic derecho en el icono → **Configurar** → pegarlo.
4. Menú → **Conectar cuenta de Spotify…** y aceptar en el navegador.

Desde febrero de 2026 Spotify exige que el dueño de la app tenga **Premium** (y máximo 5 usuarios por app).
La sesión se guarda en `~/.config/spotify-applet/token.json` (no está en el repo; en un sistema nuevo hay que volver a conectar).

## Spotify en segundo plano

En Linux, cerrar la ventana de Spotify cierra la app. Spotify tiene una opción oculta para que siga sonando de fondo; con Spotify cerrado, añade esta línea a `~/.config/spotify/Users/<tu-usuario>-user/prefs`:

```
ui.minimize_to_tray=true
```

Sin ella, la miniaplicación solo minimiza la ventana.

## Archivos

| Archivo | Qué es |
|---|---|
| `spotify-applet/applet.js` | La miniaplicación (control por MPRIS/D-Bus, vista previa, menú) |
| `spotify-applet/spotify-lists.py` | Ayudante de la Web API: login PKCE, listas, Me gusta |
| `spotify-applet/settings-schema.json` | Opciones (tamaños de portada, estilo de barra, Client ID) |
| `spotify-applet/stylesheet.css` | Estilos |
| `install.sh` | Instalar / actualizar / desinstalar |

Para recargarla tras cambiar algo:
`gdbus call --session --dest org.Cinnamon --object-path /org/Cinnamon --method org.Cinnamon.ReloadXlet spotify-applet APPLET`
