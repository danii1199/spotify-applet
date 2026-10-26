# Spotify Mini para Windows

La misma idea que la miniaplicación de Cinnamon, pero como icono junto al reloj de Windows 10/11.
Al pulsarlo sale una ventanita con:

- Portada, canción, artista y álbum.
- Aleatorio, anterior, reproducir/pausar, siguiente, repetir y barra de progreso.
- Con tu cuenta conectada: volumen de Spotify, corazón de **Me gusta**, **Reproducir lista** (tus listas y Canciones que te gustan; con aleatorio empiezan por una canción al azar), **Añadir a listas** y buscador de canciones, álbumes, artistas y listas.
- Clic derecho en el icono: reproducir/pausar, siguiente, anterior y salir.

## Instalar

1. Descarga `SpotifyMini.exe` (no hace falta instalar nada más) y guárdalo en una carpeta fija, por ejemplo `Documentos\SpotifyMini`.
2. Ábrelo. Windows puede avisar con «Windows protegió su PC» porque el programa no está firmado: *Más información* → *Ejecutar de todas formas*.
3. Si el icono queda escondido en la flecha `^` de la barra de tareas, arrástralo junto al reloj.
4. En ⚙ (abajo a la derecha de la ventanita) puedes marcar **Iniciar con Windows**.

## Conectar la cuenta

Sirve la misma app de desarrollador que en Linux:

1. En ⚙ pega el **Client ID** (<https://developer.spotify.com/dashboard>, Redirect URI `http://127.0.0.1:8898/callback`).
2. **Conectar cuenta de Spotify…** y acepta en el navegador.

La sesión se guarda en `%APPDATA%\SpotifyMini\token.json`.

## Spotify en segundo plano

En Windows es una opción normal de Spotify: *Configuración → Inicio y comportamiento de la ventana → El botón Cerrar debería minimizar la ventana de Spotify*.
Así, al cerrar la ventana Spotify sigue sonando y lo controlas desde Spotify Mini. Cuando Spotify Mini tiene que abrir Spotify, lo abre minimizado.

## Compilar

Lo compila GitHub Actions (`.github/workflows/windows.yml`) en cada cambio de `windows/`. A mano, en Windows con .NET 8:

```
dotnet publish windows/SpotifyMini/SpotifyMini.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
