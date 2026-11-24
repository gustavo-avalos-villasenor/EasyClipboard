# Sincronización en la nube

EasyClipboard puede sincronizar los **elementos fijados** entre varios equipos Windows a través de **ClipboardSecretServer**, un servidor propio y pequeño que solo guarda un blob cifrado. El servidor nunca ve el contenido.

- Se sincronizan los fijados de **texto** e **imagen** (hasta 2 MB por imagen). Los temporales y los fijados de tipo «archivos» (rutas locales) no salen del equipo.
- Además, **lo último copiado** (texto) se comparte entre todos los equipos, incluido el móvil Android (ver §6).
- El portapapeles funciona igual sin conexión; la sincronización es un añadido.

## 1. Cómo funciona

```
 Equipo A                         ClipboardSecretServer                  Equipo B
 items.json ─► cifrar ─► PUT blob (If-Match) ─► SQLite ─► SSE "changed" ─► GET blob ─► descifrar ─► fusionar
```

1. Al fijar, editar, reordenar o quitar un fijado, el panel espera ~1,5 s y sincroniza.
2. **Descarga** el blob actual, lo **descifra**, lo **fusiona** con los fijados locales y, si cambió algo, lo **sube** con `If-Match: <etag>`.
3. Si otro equipo escribió entre medias, el servidor responde `412` y el cliente vuelve a descargar, fusionar y subir (hasta 5 intentos).
4. Los demás equipos reciben un aviso por **SSE** (`/v1/clipboard/events`) y sincronizan al momento. Como respaldo, cada equipo comprueba cada 10 minutos (`If-None-Match` → `304` si no hay cambios).

### Fusión

- Cada fijado tiene un `id` estable y una marca `modified`. Por elemento **gana el cambio más reciente**; en empate decide una regla determinista, igual en todos los equipos.
- Quitar un fijado deja una **lápida** (`tombstone`) con la hora. En los demás equipos ese elemento deja de estar fijado y queda como temporal.
- Si el mismo contenido se fijó por separado en dos equipos, se unifica en un solo elemento.
- Las lápidas se conservan **60 días**. Un equipo que pase más tiempo desconectado podría recuperar un fijado que se quitó en otro equipo durante ese periodo.

### Sin conexión

- Historial, fijados, Win+V y pegar funcionan igual: todo vive en `panel-data\items.json`.
- Aparece **un único** aviso de error, no uno por reintento. Los cambios se guardan en local.
- El canal de avisos se reconecta con espera creciente (de 5 s a 2 min) y hay una comprobación completa cada 10 minutos. También se puede forzar con **«Sincronizar ahora»** en la bandeja.
- Al volver la conexión, cada equipo sube lo pendiente y se fusiona; aparece el aviso «La sincronización vuelve a funcionar».

## 2. Seguridad

| Qué | Dónde |
| --- | --- |
| URL, token del equipo y clave de cifrado | Administrador de credenciales de Windows, entrada `EasyClipboard/CloudSync` |
| Estado de sincronización (etag, ids sincronizados) | `panel-data\sync-state.json`, sin contenido ni secretos |
| Contenido en el servidor | Solo el blob cifrado, más las 10 revisiones anteriores |

**Cifrado:** clave maestra aleatoria de 64 bytes, de la que se derivan con HMAC-SHA256 una clave de cifrado y otra de autenticación. El contenido se guarda como JSON comprimido con GZip, cifrado con AES-256-CBC (IV aleatorio) y autenticado con HMAC-SHA256 en modo *encrypt-then-MAC*. El MAC se verifica en tiempo constante antes de descifrar.

Formato del blob: `"ECS1"` (4 bytes) · IV (16) · texto cifrado · HMAC (32).

- La **clave** es la misma en todos tus equipos y **no se puede recuperar**. Sin ella, lo sincronizado no se puede leer. Guárdala en tu gestor de contraseñas.
- El **token** identifica a cada equipo ante el servidor. Usa uno por equipo para poder revocarlos por separado.
- «Copiar clave» la copia marcada para que ni EasyClipboard ni el historial de Windows la guarden.

## 3. Configurar un equipo

**Primer equipo**

1. Pide un token en el servidor: `sudo ./clipboard-secret-server token create --name <equipo>`.
2. Clic derecho en el icono de la bandeja → **«Sincronización en la nube…»**.
3. **URL del servidor:** `https://<servidor>.<tailnet>.ts.net:1445`.
4. **Token de este equipo:** pega el token.
5. Pulsa **«Generar clave nueva»** y luego **«Copiar clave»**, y guárdala en tu gestor de contraseñas.
6. Pulsa **Guardar**.

**Cada equipo nuevo**

Haz lo mismo con su propio token, pero **pega la clave existente**. No generes una nueva.

**Exportar e importar (como un `.conf` de WireGuard)**

- **«Exportar…»** guarda la URL, el token y la clave en un archivo de texto `.ecsync`:

  ```ini
  # EasyClipboard: sincronización en la nube
  [EasyClipboard]
  Server = https://<servidor>.<tailnet>.ts.net:1445
  Token = <token>
  Key = <clave en Base64>
  ```

- **«Importar…»** carga ese archivo en el diálogo. Revisa los datos y pulsa **Guardar**.
- El archivo lleva los secretos **sin cifrar**. Pásalo por un canal seguro, bórralo tras importarlo y nunca lo subas a un repositorio.
- Si importas el archivo de otro equipo, ambos compartirán token. Funciona, pero conviene sustituirlo por uno propio para poder revocarlos por separado.

**Desactivar:** usa el botón **«Desactivar»** del mismo diálogo. Borra la configuración del Administrador de credenciales; los fijados locales se quedan.

## 4. Servidor (ClipboardSecretServer)

Es un servicio Python con SQLite en un host Linux de la tailnet. Escucha solo en `127.0.0.1:18767` y se publica en HTTPS mediante **Tailscale Serve** en el puerto `1445`, así que solo es accesible desde los nodos de la tailnet. Es independiente de cualquier otro servicio del host.

### API (v1)

| Método y ruta | Descripción |
| --- | --- |
| `GET /health` | `200 ok`. No requiere autenticación. |
| `GET /v1/clipboard/blob` | Devuelve el blob (`application/octet-stream`) con `ETag`. `404` si aún no existe; `304` con `If-None-Match`. |
| `PUT /v1/clipboard/blob` | Cuerpo binario. Exige `If-None-Match: *` (crear) o `If-Match: "<etag>"`; sin ninguno responde `428`. `412` si hay conflicto; `413` si supera 5 MiB. |
| `GET /v1/clipboard/live` | Lo último copiado: un único blob cifrado, sin historial. `404` si no existe; `304` con `If-None-Match`. |
| `PUT /v1/clipboard/live` | Cuerpo binario. Gana el último en escribir (`If-Match` opcional; `412` si no coincide). `413` si supera 5 MiB. |
| `GET /v1/clipboard/events` | SSE: `event: changed` (fijados) o `event: live` (lo último copiado) con `data: "<etag>"`, y un latido cada 25 s. Los clientes ignoran los eventos que no conocen. |

Todas las rutas salvo `/health` requieren `Authorization: Bearer <token>` y responden `401` si falta o es inválido. Los tokens se guardan solo como hash SHA-256.

### Gestión de tokens

```bash
cd <directorio del servidor>
sudo ./clipboard-secret-server token create --name <equipo>   # imprime el token UNA vez
sudo ./clipboard-secret-server token list
sudo ./clipboard-secret-server token revoke <equipo>
```

### Arranque automático y reinicios

La unidad systemd `clipboard-secret-server.service`:

- se habilita en `multi-user.target`, así que arranca con el sistema;
- arranca después de `network-online.target`;
- tiene `Restart=always` con `RestartSec=3` y `StartLimitIntervalSec=0`: se reinicia ante cualquier caída sin límite de reintentos;
- tiene endurecimiento: `ProtectSystem=strict`, `ProtectHome=read-only`, `NoNewPrivileges`, `PrivateTmp`, `UMask=0077`, y solo puede escribir en `data/`.

SQLite usa WAL con `synchronous=FULL` para mantenerse coherente ante cortes de energía. `tailscale serve --bg` es persistente: vuelve a publicarse al reiniciar `tailscaled` o el host.

Instalar o actualizar la unidad:

```bash
sudo bash <<'EOF'
set -eu
cd <directorio del servidor>
install -m 644 clipboard-secret-server.service /etc/systemd/system/clipboard-secret-server.service
systemctl daemon-reload
systemctl enable clipboard-secret-server
systemctl restart clipboard-secret-server
curl -fsS --retry 20 --retry-connrefused --retry-delay 1 http://127.0.0.1:18767/health; echo
systemctl is-enabled clipboard-secret-server
systemctl is-active clipboard-secret-server
systemctl is-enabled tailscaled
tailscale serve status
EOF
```

Publicar en Tailscale (solo la primera vez):

```bash
sudo tailscale serve --bg --https=1445 http://127.0.0.1:18767
```

## 5. Problemas frecuentes

| Síntoma | Causa y solución |
| --- | --- |
| «El servidor rechazó el token del dispositivo» | El token se revocó o se copió mal. Crea otro con `token create` y vuelve a pegarlo en el diálogo. |
| «La clave de cifrado no coincide con la de tus otros equipos» | Este equipo tiene otra clave. Pega la clave original; no generes una nueva. |
| «Sin conexión con el servidor» | El equipo no está en la tailnet, o el servicio está caído. Comprueba `https://<servidor>:1445/health` y, en el host, `systemctl status clipboard-secret-server` y `journalctl -u clipboard-secret-server -n 40`. |
| «N imagen(es) no caben y no se sincronizan» | Una imagen de más de 2 MB no se sincroniza, y si el total supera 5 MiB cifrados se dejan fuera las imágenes más antiguas. El texto se sigue sincronizando. Quita imágenes fijadas grandes si las necesitas en otros equipos. |
| No sé si sincroniza | Clic derecho en la bandeja: la línea «Sincronización: …» muestra el último resultado. «Sincronizar ahora» siempre avisa del resultado. Los errores se anotan en `panel-data\errors.log`. |
| `curl` a `127.0.0.1:18767` falla justo después de `systemctl restart` | El servicio aún está arrancando. Usa `--retry 20 --retry-connrefused`. |
| `token create` (o cualquier comando) no muestra nada en la terminal | La salida estándar de esa sesión quedó redirigida (por ejemplo, por un heredoc pegado a medias). Comprueba con `echo hola`; se arregla con `exec 1>/dev/tty 2>/dev/tty` o abriendo una sesión nueva. |
| Los cambios tardan en llegar a otro equipo | El canal SSE se está reconectando. Como mucho llegan en 10 minutos; usa «Sincronizar ahora» para forzarlo. |

## 6. Lo último copiado y el móvil

Cada texto que copias en un equipo (o eliges en el panel) se sube cifrado con la misma clave a `/v1/clipboard/live`, con el formato `{"v":1,"id","kind":"text","text","time","from"}`. Los demás equipos lo reciben por SSE (`event: live`) y lo ponen en su portapapeles, donde entra en el historial como cualquier copia. Se avisa con «Copiado desde <equipo>».

- No se envía lo que excluyen los gestores de contraseñas, ni lo que acaba de llegar de otro equipo (se compara la huella SHA-256 del texto).
- Al configurar un equipo nuevo no se aplica lo que ya hubiera en el servidor; solo lo que se copie después.
- Se desactiva desde la bandeja: **«Compartir lo copiado con el móvil»** (crea `panel-data\live-off`).
- Por ahora solo texto; las imágenes siguen sincronizándose como fijados.

**App Android** (en el host: `/home/Pixel/easyclipboard/`, se compila e instala por ADB con `build.sh --install`):

- **PC → móvil:** automático. Un servicio en primer plano mantiene el canal SSE y se reconecta al cambiar de red.
- **Móvil → PC:** con un toque. Android (10+) solo deja leer el portapapeles a la app que tiene el foco: botón «Enviar al PC» de Ajustes rápidos, la acción de la notificación, el botón de la app o Compartir → «Enviar al PC». El contenido marcado como sensible no se envía.
- Muestra los fijados de texto; tocar uno lo copia.
- Configuración: URL, un token propio (`token create --name pixel`) y la **misma clave**; admite importar un `.ecsync`. Se guarda cifrada con el Android Keystore.
