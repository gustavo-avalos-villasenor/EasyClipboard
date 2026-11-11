# EasyClipboard

Sustituto ligero del panel **Win+V** de Windows 10/11. Es un único archivo C# (WinForms, .NET Framework 4) y no necesita instalar nada más.

- **Temporales arriba**, del más reciente al más antiguo. Se guardan los **últimos 25**, también tras reiniciar el equipo.
- **Fijados abajo**, sin límite, con fondo azulado y en el orden que tú elijas.
- Las copias repetidas no se duplican. Respeta los gestores de contraseñas que piden no registrar lo copiado.
- No modifica el historial ni los fijados nativos de Windows. Si cierras EasyClipboard, Win+V vuelve a abrir el panel de Windows.

---

## 1. Requisitos

| Requisito | Comprobación |
| --- | --- |
| Windows 10 u 11 de 64 bits | — |
| .NET Framework 4.x (viene con Windows) | `dir %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` |
| Git (solo para clonar y actualizar) | `git --version` |

No hacen falta permisos de administrador.

## 2. Instalación

Instálalo en una carpeta **de tu usuario** donde se pueda escribir. El historial se guarda en `panel-data\`, junto al ejecutable, así que **no lo pongas en `C:\Program Files`**.

```cmd
git clone https://github.com/gustavo-avalos-villasenor/EasyClipboard.git "%LOCALAPPDATA%\EasyClipboard"
cd /d "%LOCALAPPDATA%\EasyClipboard"
build.cmd
```

`build.cmd` genera `panel.exe` en la misma carpeta. Si no muestra errores, la compilación es correcta.

## 3. Primer arranque

```cmd
panel.exe
```

- Aparece el panel y un icono azul en el área de notificaciones. Si no lo ves, mira en la flecha `^` de la bandeja.
- Se crea `panel-data\`. La primera vez la lista está vacía y se va llenando a medida que copias.
- Pulsa **Win+V**: debe abrirse este panel, no el de Windows.

## 4. Arranque automático con Windows

Clic derecho en el icono de la bandeja y activa **«Iniciar con Windows»**.

Esto crea la entrada `EasyClipboard` en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Arranca oculto (`--background`) desde la ruta actual de `panel.exe`. Si mueves la carpeta, desactívalo y vuelve a activarlo desde la nueva ubicación.

Para comprobarlo:

```cmd
reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v EasyClipboard
```

> En versiones anteriores la entrada se llamaba `CodexClipboardPanel`. Al activar o desactivar la opción se elimina automáticamente.

## 5. Uso diario

| Acción | Cómo |
| --- | --- |
| Abrir / cerrar | **Win+V**, clic en el icono de la bandeja o `iniciar.cmd`. `Esc` o un clic fuera lo cierran |
| Buscar | Escribe directamente |
| Elegir y pegar | `↑`/`↓` y `Enter`, o clic en la tarjeta. Se pega en la ventana anterior |
| Fijar | `☆`. Para quitar el fijado (`★`) hay que pulsar **dos veces** |
| Modo edición | Botón **✎ Editar** o `Ctrl+E`. Se desactiva al cerrar el panel |
| Editar texto (en modo edición) | `✎` abre el editor. `Ctrl+Enter` guarda, `Esc` cancela |
| Reordenar fijados (en modo edición) | `↑`/`↓` de la tarjeta o `Alt+↑/↓` |
| Borrar (en modo edición) | `×`. En los fijados hay que pulsar **dos veces** |

## 6. Actualizar

1. Clic derecho en el icono de la bandeja → **Salir**. No se puede sobrescribir `panel.exe` mientras está abierto.
2. Ejecuta:

   ```cmd
   cd /d "%LOCALAPPDATA%\EasyClipboard"
   git pull
   build.cmd
   panel.exe --background
   ```

`panel-data\` no está en el repositorio, así que `git pull` no lo toca.

## 7. Datos y copias de seguridad

- `panel-data\items.json` guarda la lista, los fijados y el orden. `items.json.bak` es la versión anterior a cada guardado.
- `panel-data\*.png` son las imágenes.
- `panel-data\errors.log` solo aparece si algo falla.
- Si `items.json` se corrompe, se guarda una copia `items.corrupt-*.json` y se carga el `.bak`. Nunca se sobrescribe sin copia.
- **Se guarda sin cifrar.** No fijes contraseñas ni tokens; si se colaron, bórralos desde el panel en modo edición.
- Para hacer una copia de seguridad, copia la carpeta `panel-data\` completa con el programa cerrado.

## 8. Desactivar o desinstalar

1. Icono de la bandeja → desactiva **«Iniciar con Windows»** → **Salir**. Win+V vuelve a abrir el panel de Windows.
2. Para desinstalarlo, borra la carpeta `%LOCALAPPDATA%\EasyClipboard`, incluido `panel-data\` si no quieres conservar el historial.

No se modifica nada más del sistema: ni servicios, ni `DisabledHotkeys`, ni los datos del portapapeles de Windows.

## 9. Problemas frecuentes

| Síntoma | Causa y solución |
| --- | --- |
| Win+V abre el panel de Windows | EasyClipboard no está en marcha (`tasklist /FI "IMAGENAME eq panel.exe"`); arráncalo. Si al iniciar avisó «No se pudo interceptar Win+V», reinícialo |
| No pega en una ventana | Esa ventana se ejecuta como administrador y Windows bloquea el pegado automático. El contenido ya está en el portapapeles: pulsa `Ctrl+V` |
| Se abre el menú Inicio al soltar Win | Avísalo con la versión de Windows. Mientras tanto, suelta `V` antes que `Win` |
| No aparece algo que copié | Venía de un gestor de contraseñas (se excluye a propósito), era texto de más de 4 MB, o era idéntico a uno existente (en ese caso sube arriba) |
| El antivirus avisa | El programa usa un gancho de teclado de bajo nivel para capturar Win+V. Es el mismo mecanismo que AutoHotkey. Revisa el código en `panel.cs` y añade una exclusión si lo apruebas |
| `build.cmd` falla | Comprueba que existe `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` y que `panel.exe` no está abierto |
| Algo falla sin mensaje | Revisa `panel-data\errors.log` |

## 10. Notas técnicas

- `AddClipboardFormatListener` detecta cada copia. Se procesa 120 ms después del último cambio, para agrupar ráfagas.
- Win+V se intercepta con `WH_KEYBOARD_LL` en un hilo propio, para no frenar el teclado del sistema. Se envía una tecla neutra (`0xE8`) para que soltar `Win` no abra Inicio.
- La lista se dibuja a mano y solo pinta las filas visibles.
- Un mutex (`Local\CodexClipboardPanel`) impide que haya dos instancias. Si ejecutas `panel.exe` otra vez, se abre el panel de la instancia que ya está en marcha.
