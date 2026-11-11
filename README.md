# EasyClipboard

Sustituto ligero del panel **Win+V** de Windows 10/11, en un único archivo C# (WinForms, .NET Framework 4), sin dependencias.

## Qué hace

- **Win+V** abre el panel junto al cursor. Al salir desde el icono de la bandeja, Win+V vuelve al panel original de Windows.
- **Temporales arriba**, del más reciente al más antiguo. Se conservan los **últimos 25**, también tras reiniciar.
- **Fijados abajo**, sin límite, con fondo azulado y en el orden que tú elijas.
- Las copias repetidas no se duplican: el elemento existente sube arriba. Las imágenes se comparan por contenido.
- Guarda texto (con su HTML), imágenes PNG y rutas de archivos copiados.
- Respeta las marcas de exclusión de los gestores de contraseñas (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`).
- No toca el historial ni los fijados de Windows.

## Uso

- Escribe para buscar. `↑`/`↓` eligen, `Enter` pega y `Esc` cierra. Un clic fuera también lo cierra.
- Clic en una tarjeta: se copia y se pega en la ventana anterior. Si esa ventana es de administrador, pulsa `Ctrl+V` a mano.
- `☆` fija. Para quitar el fijado (`★`) hay que pulsar dos veces.
- **✎ Editar** (o `Ctrl+E`) muestra el lápiz, las flechas y el borrado. Así no se pulsan por accidente.
  - `✎` edita el texto: `Ctrl+Enter` guarda, `Esc` cancela.
  - `↑`/`↓` o `Alt+↑/↓` reordenan los fijados.
  - `×` borra; en los fijados hay que pulsarlo dos veces.
- Menú del icono de la bandeja: abrir, «Iniciar con Windows», abrir la carpeta de datos y salir.

## Compilar y ejecutar

```cmd
build.cmd
panel.exe              :: abre el panel
panel.exe --background :: arranca oculto (así lo usa «Iniciar con Windows»)
```

El historial se guarda **sin cifrar** en `panel-data/`, junto al ejecutable. Está excluido del repositorio.
