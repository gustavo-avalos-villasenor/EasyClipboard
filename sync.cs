using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

// Sincronización en la nube de los elementos fijados contra ClipboardSecretServer.
// El servidor solo guarda un blob cifrado en el cliente (AES-256-CBC + HMAC-SHA256, encrypt-then-MAC).

internal class SyncItem {
  public string id { get; set; }
  public string kind { get; set; }
  public string name { get; set; }
  public string text { get; set; }
  public string html { get; set; }
  public string png { get; set; } // Base64
  public int order { get; set; }
  public long time { get; set; }
  public long modified { get; set; }
}

internal class SyncDoc {
  public int v { get; set; }
  public List<SyncItem> items { get; set; }
  public Dictionary<string, long> tombstones { get; set; }
}

internal class SyncConfig {
  public string url { get; set; }
  public string token { get; set; }
  public string key { get; set; }
}

internal class SyncState {
  public string etag { get; set; }
  public List<string> synced { get; set; }   // ids vivos tras la última sincronización correcta
  public string localSig { get; set; }       // firma de los fijados locales tras esa sincronización
  public string server { get; set; }         // url + huella de la clave a la que pertenece este estado
  public string liveId { get; set; }         // último «copiado» enviado o recibido (/v1/clipboard/live)
  public string liveHash { get; set; }       // SHA-256 de su texto, para no reenviar lo que acaba de llegar
  public string liveEtag { get; set; }
}

internal sealed class SyncException : Exception { public SyncException(string message) : base(message) { } }

internal sealed class SyncOutcome {
  public bool Unchanged;
  public SyncDoc Doc;
  public string ETag;
  public int Dropped; // imágenes fijadas que no caben en el blob y no se sincronizan
}

// ---------- Administrador de credenciales de Windows ----------
internal static class CredStore {
  const string Target = "EasyClipboard/CloudSync";
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct CREDENTIAL {
    public uint Flags, Type;
    public string TargetName, Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist, AttributeCount;
    public IntPtr Attributes;
    public string TargetAlias, UserName;
  }
  [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL cred, uint flags);
  [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, uint type, uint flags, out IntPtr cred);
  [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, uint type, uint flags);
  [DllImport("advapi32.dll")] static extern void CredFree(IntPtr cred);

  public static string Read() {
    IntPtr p;
    if (!CredRead(Target, 1, 0, out p)) return null;
    try {
      var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
      return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
    } finally { CredFree(p); }
  }
  public static void Write(string value) {
    var bytes = Encoding.Unicode.GetBytes(value);
    var c = new CREDENTIAL { Type = 1 /* GENERIC */, TargetName = Target, UserName = "EasyClipboard", Persist = 2 /* LOCAL_MACHINE */, CredentialBlobSize = (uint)bytes.Length };
    c.CredentialBlob = Marshal.AllocHGlobal(bytes.Length);
    try {
      Marshal.Copy(bytes, 0, c.CredentialBlob, bytes.Length);
      if (!CredWrite(ref c, 0)) throw new System.ComponentModel.Win32Exception();
    } finally { Marshal.FreeHGlobal(c.CredentialBlob); }
  }
  public static void Delete() { CredDelete(Target, 1, 0); }
}

// ---------- Cifrado ----------
internal static class SyncCrypto {
  static readonly byte[] Magic = Encoding.ASCII.GetBytes("ECS1");
  const int IvLen = 16, MacLen = 32;

  public static string NewKey() {
    var k = new byte[64];
    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(k);
    return Convert.ToBase64String(k);
  }
  public static byte[] ParseKey(string text) {
    byte[] k;
    try { k = Convert.FromBase64String((text ?? "").Trim()); } catch (FormatException) { throw new SyncException("La clave no es Base64 válido."); }
    if (k.Length != 64) throw new SyncException("La clave debe ser de 64 bytes (88 caracteres en Base64).");
    return k;
  }
  public static string Fingerprint(byte[] key) {
    using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(key)).Substring(0, 12);
  }
  // Claves separadas para cifrar y autenticar, derivadas de la clave maestra.
  static void Split(byte[] key, out byte[] enc, out byte[] mac) {
    using (var h = new HMACSHA256(key)) {
      enc = h.ComputeHash(Encoding.ASCII.GetBytes("EasyClipboard/enc/v1"));
      mac = h.ComputeHash(Encoding.ASCII.GetBytes("EasyClipboard/mac/v1"));
    }
  }

  public static byte[] Encrypt(byte[] key, byte[] plain) {
    byte[] enc, macKey; Split(key, out enc, out macKey);
    byte[] packed;
    using (var ms = new MemoryStream()) {
      using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(plain, 0, plain.Length);
      packed = ms.ToArray();
    }
    using (var aes = Aes.Create()) {
      aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = enc; aes.GenerateIV();
      byte[] ct;
      using (var e = aes.CreateEncryptor()) ct = e.TransformFinalBlock(packed, 0, packed.Length);
      var head = Magic.Concat(aes.IV).Concat(ct).ToArray();
      using (var h = new HMACSHA256(macKey)) return head.Concat(h.ComputeHash(head)).ToArray();
    }
  }

  public static byte[] Decrypt(byte[] key, byte[] blob) {
    byte[] enc, macKey; Split(key, out enc, out macKey);
    if (blob == null || blob.Length < Magic.Length + IvLen + 16 + MacLen) throw new SyncException("El blob remoto está incompleto.");
    for (int i = 0; i < Magic.Length; i++) if (blob[i] != Magic[i]) throw new SyncException("El blob remoto no tiene un formato reconocido.");
    int headLen = blob.Length - MacLen;
    byte[] expected;
    using (var h = new HMACSHA256(macKey)) expected = h.ComputeHash(blob, 0, headLen);
    int diff = 0;
    for (int i = 0; i < MacLen; i++) diff |= expected[i] ^ blob[headLen + i];
    if (diff != 0) throw new SyncException("La clave de cifrado no coincide con la de tus otros equipos.");
    using (var aes = Aes.Create()) {
      aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.Key = enc;
      var iv = new byte[IvLen]; Buffer.BlockCopy(blob, Magic.Length, iv, 0, IvLen); aes.IV = iv;
      int off = Magic.Length + IvLen;
      byte[] packed;
      using (var d = aes.CreateDecryptor()) packed = d.TransformFinalBlock(blob, off, headLen - off);
      using (var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress)) using (var outMs = new MemoryStream()) { gz.CopyTo(outMs); return outMs.ToArray(); }
    }
  }
}

// ---------- Fusión y red (sin estado de UI; se ejecuta en segundo plano) ----------
internal static class SyncEngine {
  public const string BlobPath = "/v1/clipboard/blob", EventsPath = "/v1/clipboard/events", LivePath = "/v1/clipboard/live";
  public const int MaxBlob = 5 * 1024 * 1024, MaxImage = 2 * 1024 * 1024;
  const long TombstoneTtl = 60L * 24 * 3600 * 1000;

  static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }

  static string Fingerprint(SyncItem x) { return (x.kind ?? "") + "\u0001" + (x.name ?? "") + "\u0001" + (x.text ?? "") + "\u0001" + x.order + "\u0001" + (x.png == null ? 0 : x.png.Length); }
  // Último escritor gana; en empate decide una regla determinista igual en todos los equipos.
  public static bool Newer(SyncItem a, SyncItem b) {
    if (a.modified != b.modified) return a.modified > b.modified;
    return String.CompareOrdinal(Fingerprint(a), Fingerprint(b)) > 0;
  }

  public static SyncDoc Merge(List<SyncItem> local, SyncDoc remote, ICollection<string> baseIds, long now) {
    var all = new Dictionary<string, SyncItem>();
    foreach (var r in remote.items ?? new List<SyncItem>()) if (r != null && !String.IsNullOrEmpty(r.id)) all[r.id] = r;
    foreach (var l in local) {
      SyncItem r;
      if (all.TryGetValue(l.id, out r)) {
        if (Newer(l, r)) { if (l.png == null) l.png = r.png; all[l.id] = l; }
        else if (r.png == null && l.png != null && r.kind == "image") r.png = l.png;
      } else all[l.id] = l;
    }
    var tombs = new Dictionary<string, long>(remote.tombstones ?? new Dictionary<string, long>());
    var localIds = new HashSet<string>(local.Select(x => x.id));
    // Lo que estaba sincronizado y ya no está fijado aquí se quitó en este equipo.
    foreach (var id in baseIds) if (!localIds.Contains(id)) { long t; tombs[id] = tombs.TryGetValue(id, out t) ? Math.Max(t, now) : now; }
    foreach (var kv in tombs) { SyncItem it; if (all.TryGetValue(kv.Key, out it) && it.modified <= kv.Value) all.Remove(kv.Key); }
    foreach (var id in tombs.Where(kv => now - kv.Value > TombstoneTtl).Select(kv => kv.Key).ToList()) tombs.Remove(id);
    return new SyncDoc { v = 1, items = all.Values.OrderBy(x => x.order).ThenBy(x => x.id, StringComparer.Ordinal).ToList(), tombstones = tombs };
  }

  public static string Signature(SyncDoc d) {
    var sb = new StringBuilder();
    foreach (var x in (d.items ?? new List<SyncItem>()).OrderBy(x => x.id, StringComparer.Ordinal)) sb.Append(x.id).Append(':').Append(x.modified).Append(':').Append(x.png == null ? 0 : 1).Append(';');
    sb.Append('|');
    foreach (var kv in (d.tombstones ?? new Dictionary<string, long>()).OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(';');
    return sb.ToString();
  }

  public static byte[] Seal(byte[] key, SyncDoc doc) { return SyncCrypto.Encrypt(key, Encoding.UTF8.GetBytes(Json().Serialize(doc))); }
  public static SyncDoc Open(byte[] key, byte[] blob) {
    var doc = Json().Deserialize<SyncDoc>(Encoding.UTF8.GetString(SyncCrypto.Decrypt(key, blob)));
    if (doc == null) doc = new SyncDoc();
    if (doc.items == null) doc.items = new List<SyncItem>();
    if (doc.tombstones == null) doc.tombstones = new Dictionary<string, long>();
    return doc;
  }

  internal sealed class Resp { public int Status; public string ETag; public byte[] Body; }

  public static HttpWebRequest Request(SyncConfig cfg, string method, string path) {
    var r = (HttpWebRequest)WebRequest.Create(cfg.url.TrimEnd('/') + path);
    r.Method = method; r.UserAgent = "EasyClipboard"; r.Timeout = 30000; r.ReadWriteTimeout = 60000;
    r.Headers[HttpRequestHeader.Authorization] = "Bearer " + cfg.token;
    return r;
  }

  static Resp Send(SyncConfig cfg, string method, byte[] body, string ifMatch, string ifNoneMatch) { return Send(cfg, method, BlobPath, body, ifMatch, ifNoneMatch); }
  internal static Resp Send(SyncConfig cfg, string method, string path, byte[] body, string ifMatch, string ifNoneMatch) {
    var r = Request(cfg, method, path);
    if (ifMatch != null) r.Headers[HttpRequestHeader.IfMatch] = ifMatch;
    if (ifNoneMatch != null) r.Headers[HttpRequestHeader.IfNoneMatch] = ifNoneMatch;
    if (body != null) {
      r.ContentType = "application/octet-stream"; r.ContentLength = body.Length;
      using (var s = r.GetRequestStream()) s.Write(body, 0, body.Length);
    }
    HttpWebResponse resp;
    try { resp = (HttpWebResponse)r.GetResponse(); }
    catch (WebException ex) {
      resp = ex.Response as HttpWebResponse;
      if (resp == null) throw new SyncException("Sin conexión con el servidor (" + ex.Status + ").");
    }
    using (resp) {
      var res = new Resp { Status = (int)resp.StatusCode, ETag = resp.Headers[HttpResponseHeader.ETag] };
      using (var s = resp.GetResponseStream()) using (var ms = new MemoryStream()) { if (s != null) s.CopyTo(ms); res.Body = ms.ToArray(); }
      return res;
    }
  }

  internal static SyncException Unexpected(Resp r, string what) {
    if (r.Status == 401 || r.Status == 403) return new SyncException("El servidor rechazó el token del dispositivo.");
    if (r.Status == 413) return new SyncException("Los fijados ocupan más de lo que admite el servidor (5 MiB cifrados).");
    if (r.Status == 429) return new SyncException("El servidor pidió esperar (demasiadas peticiones).");
    return new SyncException(what + ": respuesta HTTP " + r.Status + ".");
  }

  public static SyncOutcome Run(SyncConfig cfg, byte[] key, List<SyncItem> local, SyncState st, bool localChanged, long now) {
    var baseIds = st.synced ?? new List<string>();
    for (int attempt = 0; attempt < 5; attempt++) {
      bool probe = attempt == 0 && !localChanged && st.etag != null;
      var get = Send(cfg, "GET", null, null, probe ? st.etag : null);
      if (get.Status == 304) return new SyncOutcome { Unchanged = true, ETag = st.etag };
      SyncDoc remote; string etag;
      if (get.Status == 404) { remote = new SyncDoc { v = 1, items = new List<SyncItem>(), tombstones = new Dictionary<string, long>() }; etag = null; }
      else if (get.Status == 200) { remote = Open(key, get.Body); etag = get.ETag; }
      else throw Unexpected(get, "Al descargar");

      var merged = Merge(local.Select(Clone).ToList(), remote, baseIds, now);
      int dropped; var body = Fit(key, merged, out dropped);
      if (etag != null && Signature(merged) == Signature(remote)) return new SyncOutcome { Doc = merged, ETag = etag, Dropped = dropped };

      var put = Send(cfg, "PUT", body, etag, etag == null ? "*" : null);
      if (put.Status == 200 || put.Status == 201 || put.Status == 204) return new SyncOutcome { Doc = merged, ETag = put.ETag, Dropped = dropped };
      if (put.Status == 412) { Thread.Sleep(200 * (attempt + 1)); continue; } // otro equipo escribió antes: volver a fusionar
      throw Unexpected(put, "Al subir");
    }
    throw new SyncException("Otro equipo está escribiendo a la vez; se reintentará.");
  }

  // Si el blob no cabe en el servidor, se dejan fuera las imágenes más antiguas en vez de bloquear
  // toda la sincronización. El orden es determinista para que todos los equipos descarten las mismas.
  static byte[] Fit(byte[] key, SyncDoc doc, out int dropped) {
    dropped = 0;
    var body = Seal(key, doc);
    if (body.Length <= MaxBlob) return body;
    var images = doc.items.Where(x => x.kind == "image").OrderBy(x => x.modified).ThenBy(x => x.id, StringComparer.Ordinal).ToList();
    foreach (var img in images) {
      doc.items.Remove(img); dropped++;
      body = Seal(key, doc);
      if (body.Length <= MaxBlob) return body;
    }
    throw new SyncException("Los fijados de texto ocupan más de 5 MiB cifrados; quita alguno.");
  }

  static SyncItem Clone(SyncItem x) {
    return new SyncItem { id = x.id, kind = x.kind, name = x.name, text = x.text, html = x.html, png = x.png, order = x.order, time = x.time, modified = x.modified };
  }
}

// ---------- Integración con el panel ----------
internal sealed partial class ClipPanel {
  static readonly string SyncStatePath = Path.Combine(Store, "sync-state.json");
  SyncConfig syncCfg;
  byte[] syncKey;
  SyncState syncState = new SyncState();
  bool syncRunning, syncPending, syncFailing, syncManual;
  int syncDroppedShown = -1;
  string syncStatus = "Desactivada";
  readonly WinTimer syncDebounce = new WinTimer { Interval = 1500 };
  readonly WinTimer syncPoll = new WinTimer { Interval = 10 * 60 * 1000 };
  volatile int syncGeneration;
  volatile bool closing;
  volatile HttpWebRequest eventsRequest;

  static long NowMs() { return DateTimeOffset.Now.ToUnixTimeMilliseconds(); }

  void InitSync() {
    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
    ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8); // el canal SSE ocupa una conexión fija
    syncDebounce.Tick += (s, e) => { syncDebounce.Stop(); StartSync(); };
    syncPoll.Tick += (s, e) => StartSync();
    liveDebounce.Tick += (s, e) => { liveDebounce.Stop(); StartLivePush(); };
    try {
      if (File.Exists(SyncStatePath)) syncState = new JavaScriptSerializer().Deserialize<SyncState>(File.ReadAllText(SyncStatePath, Encoding.UTF8)) ?? new SyncState();
    } catch (Exception ex) { Log("sync state", ex); syncState = new SyncState(); }
    try {
      var raw = CredStore.Read();
      if (!String.IsNullOrEmpty(raw)) UseSyncConfig(new JavaScriptSerializer().Deserialize<SyncConfig>(raw));
    } catch (Exception ex) { Log("sync config", ex); syncStatus = "Error: configuración ilegible"; }
  }

  void AddSyncMenu(ContextMenuStrip menu) {
    menu.Items.Add("Sincronización en la nube…", null, (s, e) => ShowSyncDialog());
    var live = new ToolStripMenuItem("Compartir lo copiado con el móvil", null, (s, e) => SetLiveEnabled(!LiveEnabled));
    menu.Items.Add(live);
    menu.Opening += (s, e) => { live.Enabled = syncCfg != null; live.Checked = LiveEnabled; };
    var now = new ToolStripMenuItem("Sincronizar ahora", null, (s, e) => { syncManual = true; StartSync(); });
    menu.Items.Add(now);
    var status = new ToolStripMenuItem { Enabled = false };
    menu.Items.Add(status);
    menu.Opening += (s, e) => { now.Enabled = syncCfg != null; status.Text = "Sincronización: " + syncStatus; };
  }

  void SaveSyncState() {
    try { File.WriteAllText(SyncStatePath, new JavaScriptSerializer().Serialize(syncState), new UTF8Encoding(false)); } catch (Exception ex) { Log("sync state save", ex); }
  }

  // Aplica (o quita, con null) la configuración sin guardarla.
  void UseSyncConfig(SyncConfig cfg) {
    syncGeneration++;
    var req = eventsRequest; if (req != null) try { req.Abort(); } catch { }
    syncDebounce.Stop(); syncPoll.Stop();
    syncCfg = null; syncKey = null; syncFailing = false;
    if (cfg == null) { syncStatus = "Desactivada"; return; }
    syncKey = SyncCrypto.ParseKey(cfg.key);
    syncCfg = cfg;
    var server = cfg.url.TrimEnd('/') + "#" + SyncCrypto.Fingerprint(syncKey);
    if (syncState.server != server) { syncState = new SyncState { server = server }; SaveSyncState(); } // otro servidor o clave: empezar de cero
    syncStatus = "Pendiente";
    syncPoll.Start();
    int gen = syncGeneration;
    new Thread(() => EventsLoop(cfg, gen)) { IsBackground = true, Name = "sync-events" }.Start();
    SyncSoon(500);
  }

  void SyncSoon(int delay) {
    if (syncCfg == null) return;
    syncDebounce.Stop(); syncDebounce.Interval = delay; syncDebounce.Start();
  }

  // Llamado tras guardar items.json: si cambiaron los fijados, subirlos.
  void AfterSave() { if (syncCfg != null && PinnedSignature() != syncState.localSig) SyncSoon(1500); }

  bool Syncable(ClipItem it) {
    if (!it.pinned || String.IsNullOrEmpty(it.id)) return false;
    if (it.kind == "text") return !String.IsNullOrEmpty(it.text);
    if (it.kind != "image") return false; // las rutas de archivos solo tienen sentido en este equipo
    try { var fi = new FileInfo(Path.Combine(Store, it.imageFile ?? "")); return !fi.Exists || fi.Length <= SyncEngine.MaxImage; } catch { return false; }
  }

  string PinnedSignature() {
    var sb = new StringBuilder();
    foreach (var it in items.Where(Syncable).OrderBy(x => x.id, StringComparer.Ordinal)) sb.Append(it.id).Append(':').Append(it.modified).Append(';');
    return sb.ToString();
  }

  List<SyncItem> SnapshotPinned() {
    var list = new List<SyncItem>();
    foreach (var it in items.Where(Syncable)) {
      string png = null;
      if (it.kind == "image") {
        try { var path = Path.Combine(Store, it.imageFile ?? ""); if (File.Exists(path)) png = Convert.ToBase64String(File.ReadAllBytes(path)); } catch (Exception ex) { Log("sync png", ex); }
      }
      list.Add(new SyncItem { id = it.id, kind = it.kind, name = it.name, text = it.kind == "text" ? it.text : null, html = it.html, png = png, order = it.order, time = it.time, modified = it.modified });
    }
    return list;
  }

  void StartSync() {
    if (syncCfg == null) return;
    if (syncRunning) { syncPending = true; return; }
    syncRunning = true; syncPending = false;
    var cfg = syncCfg; var key = syncKey; int gen = syncGeneration;
    List<SyncItem> local;
    try { local = SnapshotPinned(); } catch (Exception ex) { syncRunning = false; Log("sync snapshot", ex); return; }
    bool changed = PinnedSignature() != syncState.localSig;
    var st = new SyncState { etag = syncState.etag, synced = new List<string>(syncState.synced ?? new List<string>()) };
    long now = NowMs();
    syncStatus = "Sincronizando…";
    ThreadPool.QueueUserWorkItem(_ => {
      SyncOutcome res = null; Exception err = null;
      try { res = SyncEngine.Run(cfg, key, local, st, changed, now); } catch (Exception ex) { err = ex; }
      PostToUi(() => FinishSync(gen, res, err));
    });
  }

  void FinishSync(int gen, SyncOutcome res, Exception err) {
    syncRunning = false;
    if (gen != syncGeneration || closing) { if (syncPending && syncCfg != null) SyncSoon(300); return; }
    bool manual = syncManual; syncManual = false;
    if (err != null) {
      if (err is SyncException) LogLine("sync", err.Message); else Log("sync", err);
      syncStatus = "Error: " + err.Message;
      if (!syncFailing || manual) Notify("Sincronización: " + err.Message);
      syncFailing = true;
    } else {
      if (!res.Unchanged) ApplySync(res.Doc, res.ETag);
      int dropped = res.Unchanged ? Math.Max(syncDroppedShown, 0) : res.Dropped;
      int big = items.Count(x => x.pinned && x.kind == "image" && !Syncable(x));
      string note = dropped + big == 0 ? "" : " (" + (dropped + big) + " imagen(es) no caben y no se sincronizan)";
      syncStatus = "Sincronizado a las " + DateTime.Now.ToString("HH:mm") + note;
      if (syncFailing) Notify("La sincronización vuelve a funcionar." + note);
      else if (manual) Info(syncStatus + ".");
      else if (dropped + big != syncDroppedShown && dropped + big > 0) Notify("Sincronización:" + note + ". Límite: 2 MB por imagen y 5 MiB en total.");
      syncDroppedShown = dropped + big;
      syncFailing = false;
    }
    if (syncPending) SyncSoon(300);
  }

  void ApplySync(SyncDoc doc, string etag) {
    bool changed = false;
    var mergedIds = new HashSet<string>(doc.items.Select(x => x.id));
    foreach (var m in doc.items) {
      var local = items.FirstOrDefault(x => x.id == m.id);
      byte[] png = null;
      if (m.kind == "image" && m.png != null) try { png = Convert.FromBase64String(m.png); } catch { png = null; }
      if (local == null) {
        // El mismo contenido ya existe aquí con otro id (fijado en dos equipos por separado): se adopta el id remoto.
        var probe = new ClipItem { kind = m.kind, text = m.text, hash = png == null ? null : Hash(png) };
        var key = Key(probe);
        local = key == null ? null : items.FirstOrDefault(x => !mergedIds.Contains(x.id) && Key(x) == key);
        if (local != null) {
          Image th; if (thumbs.TryGetValue(local.id, out th)) { thumbs.Remove(local.id); if (th != null) th.Dispose(); }
          local.id = m.id;
        }
      } else if (local.pinned && local.modified > m.modified) continue; // cambiado aquí después: se sube en la próxima
      if (local == null) {
        if (m.kind == "image" && png == null) continue;
        local = new ClipItem { id = m.id, kind = m.kind, time = m.time != 0 ? m.time : NowMs() };
        items.Add(local);
      }
      local.pinned = true; local.name = m.name; local.order = m.order; local.modified = m.modified;
      if (m.kind == "text") { if (local.text != m.text) { local.text = m.text; local.html = m.html; } else if (m.html != null) local.html = m.html; }
      if (m.kind == "image" && png != null) {
        if (String.IsNullOrEmpty(local.imageFile)) local.imageFile = local.id + ".png";
        var path = Path.Combine(Store, local.imageFile);
        try { if (!File.Exists(path)) File.WriteAllBytes(path, png); local.hash = Hash(png); } catch (Exception ex) { Log("sync image", ex); }
      }
      changed = true;
    }
    // Quitado en otro equipo: aquí deja de estar fijado (queda como temporal).
    foreach (var kv in doc.tombstones) {
      var local = items.FirstOrDefault(x => x.id == kv.Key && x.pinned);
      if (local != null && local.modified <= kv.Value && !mergedIds.Contains(local.id)) { local.pinned = false; changed = true; }
    }
    syncState.etag = etag;
    syncState.synced = doc.items.Select(x => x.id).ToList();
    if (changed) { Prune(); MarkDirty(); RefreshList(null); }
    syncState.localSig = PinnedSignature();
    SaveSyncState();
  }

  // Aviso del servidor (SSE): si otro equipo cambió el blob, sincronizar. Reconecta con espera creciente.
  void EventsLoop(SyncConfig cfg, int gen) {
    int delay = 5000;
    while (!closing && gen == syncGeneration) {
      try {
        var r = SyncEngine.Request(cfg, "GET", SyncEngine.EventsPath);
        r.Accept = "text/event-stream"; r.ReadWriteTimeout = 90000; r.AllowReadStreamBuffering = false;
        eventsRequest = r;
        using (var resp = (HttpWebResponse)r.GetResponse())
        using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) {
          delay = 5000;
          PostToUi(() => { if (gen == syncGeneration) { StartSync(); StartLivePull(gen); } }); // reconciliar tras (re)conectar
          string evt = null, line;
          while (gen == syncGeneration && (line = reader.ReadLine()) != null) {
            if (line.StartsWith("event:")) evt = line.Substring(6).Trim();
            else if (line.StartsWith("data:") && evt == "changed") { var tag = line.Substring(5).Trim(); PostToUi(() => OnRemoteChanged(gen, tag)); }
            else if (line.StartsWith("data:") && evt == "live") PostToUi(() => StartLivePull(gen));
            else if (line.Length == 0) evt = null;
          }
        }
      } catch { }
      finally { eventsRequest = null; }
      for (int waited = 0; waited < delay && !closing && gen == syncGeneration; waited += 500) Thread.Sleep(500);
      delay = Math.Min(delay * 2, 120000);
    }
  }

  static string BareTag(string tag) { return (tag ?? "").Trim().Replace("W/", "").Trim('"'); }
  void OnRemoteChanged(int gen, string tag) {
    if (gen != syncGeneration) return;
    if (BareTag(tag) != BareTag(syncState.etag)) SyncSoon(300);
  }

  void StopSync() {
    closing = true; syncGeneration++;
    var req = eventsRequest; if (req != null) try { req.Abort(); } catch { }
  }

  void ShowSyncDialog() {
    using (var dlg = new SyncDialog(syncCfg, syncStatus)) {
      if (dlg.ShowDialog() != DialogResult.OK) return;
      syncManual = true; // mostrar el resultado de la primera sincronización
      try {
        if (dlg.Result == null) { CredStore.Delete(); UseSyncConfig(null); return; }
        SyncCrypto.ParseKey(dlg.Result.key);
        CredStore.Write(new JavaScriptSerializer().Serialize(dlg.Result));
        UseSyncConfig(dlg.Result);
      } catch (Exception ex) { Log("sync setup", ex); Notify("No se pudo guardar la configuración: " + ex.Message); }
    }
  }
}

// ---------- «Lo último copiado» entre equipos (/v1/clipboard/live) ----------
// Cada texto copiado aquí se sube cifrado; lo que otro equipo (p. ej. el móvil) sube llega por SSE y se pone en el portapapeles.
internal sealed partial class ClipPanel {
  static readonly string LiveOffPath = Path.Combine(Store, "live-off");
  readonly WinTimer liveDebounce = new WinTimer { Interval = 400 };
  string livePendingText;
  bool livePushing, livePulling, livePullAgain;

  bool LiveEnabled { get { return !File.Exists(LiveOffPath); } }

  void SetLiveEnabled(bool on) {
    try { if (on) File.Delete(LiveOffPath); else File.WriteAllText(LiveOffPath, ""); } catch (Exception ex) { Log("live toggle", ex); }
  }

  static string TextHash(string text) {
    using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
  }

  // Llamado al copiar texto (o elegirlo en el panel).
  void LiveCopied(string text) {
    if (syncCfg == null || String.IsNullOrEmpty(text) || !LiveEnabled) return;
    if (TextHash(text) == syncState.liveHash) return; // es lo que acaba de llegar o ya se envió
    livePendingText = text;
    liveDebounce.Stop(); liveDebounce.Start();
  }

  void StartLivePush() {
    if (livePushing || livePendingText == null || syncCfg == null) return;
    var text = livePendingText; livePendingText = null;
    var cfg = syncCfg; var key = syncKey; int gen = syncGeneration;
    var id = Guid.NewGuid().ToString("N");
    livePushing = true;
    ThreadPool.QueueUserWorkItem(_ => {
      string etag = null; Exception err = null;
      try {
        var doc = new Dictionary<string, object> { { "v", 1 }, { "id", id }, { "kind", "text" }, { "text", text }, { "time", NowMs() }, { "from", Environment.MachineName } };
        var body = SyncCrypto.Encrypt(key, Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(doc)));
        if (body.Length > SyncEngine.MaxBlob) throw new SyncException("El texto copiado es demasiado grande para enviarlo.");
        var r = SyncEngine.Send(cfg, "PUT", SyncEngine.LivePath, body, null, null);
        if (r.Status != 200 && r.Status != 201) throw SyncEngine.Unexpected(r, "Al enviar lo copiado");
        etag = r.ETag;
      } catch (Exception ex) { err = ex; }
      PostToUi(() => {
        livePushing = false;
        if (gen != syncGeneration) return;
        if (err != null) { if (err is SyncException) LogLine("live", err.Message); else Log("live", err); }
        else { syncState.liveId = id; syncState.liveHash = TextHash(text); syncState.liveEtag = etag; SaveSyncState(); }
        if (livePendingText != null) StartLivePush();
      });
    });
  }

  void StartLivePull(int gen) {
    if (syncCfg == null || gen != syncGeneration) return;
    if (livePulling) { livePullAgain = true; return; }
    livePulling = true; livePullAgain = false;
    var cfg = syncCfg; var key = syncKey; var etag = syncState.liveEtag;
    ThreadPool.QueueUserWorkItem(_ => {
      Dictionary<string, object> doc = null; string newTag = null; Exception err = null;
      try {
        var r = SyncEngine.Send(cfg, "GET", SyncEngine.LivePath, null, null, etag);
        if (r.Status == 200) {
          newTag = r.ETag;
          doc = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(SyncCrypto.Decrypt(key, r.Body)));
        } else if (r.Status != 304 && r.Status != 404) throw SyncEngine.Unexpected(r, "Al recibir lo copiado");
      } catch (Exception ex) { err = ex; }
      PostToUi(() => {
        livePulling = false;
        if (gen != syncGeneration) return;
        if (err != null) { if (err is SyncException) LogLine("live", err.Message); else Log("live", err); }
        else if (doc != null) ApplyLive(doc, newTag);
        if (livePullAgain) StartLivePull(gen);
      });
    });
  }

  void ApplyLive(Dictionary<string, object> doc, string etag) {
    object o;
    string id = doc.TryGetValue("id", out o) ? o as string : null;
    string from = doc.TryGetValue("from", out o) ? o as string : null;
    string kind = doc.TryGetValue("kind", out o) ? o as string : null;
    string text = doc.TryGetValue("text", out o) ? o as string : null;
    bool first = syncState.liveId == null && syncState.liveEtag == null; // recién configurado: no pisar el portapapeles con algo antiguo
    syncState.liveEtag = etag;
    if (id == null || id == syncState.liveId || from == Environment.MachineName || first || kind != "text" || String.IsNullOrEmpty(text) || !LiveEnabled) {
      if (id != null) syncState.liveId = id;
      SaveSyncState();
      return;
    }
    syncState.liveId = id; syncState.liveHash = TextHash(text); SaveSyncState();
    try {
      Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 10, 50); // entra en el historial como cualquier copia
      Info("Copiado desde " + (from ?? "otro equipo") + ".");
    } catch (Exception ex) { Log("live apply", ex); }
  }
}

internal sealed class SyncDialog : Form {
  public SyncConfig Result;
  readonly TextBox url = new TextBox(), token = new TextBox(), key = new TextBox();

  public SyncDialog(SyncConfig current, string status) {
    Text = "Sincronización en la nube"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
    StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
    BackColor = Theme.Bg; ForeColor = Theme.Fore; Font = Theme.Body; ClientSize = new Size(520, 372);
    int y = 16;
    Controls.Add(new Label { Text = "Se sincronizan los elementos fijados de texto e imagen, cifrados en este equipo antes de enviarlos.", Location = new Point(16, y), Size = new Size(488, 40), ForeColor = Theme.Muted });
    y += 48;
    y = Field("URL del servidor", url, y, false);
    y = Field("Token de este equipo", token, y, true);
    y = Field("Clave de cifrado (la misma en todos tus equipos)", key, y, true);
    if (current != null) { url.Text = current.url; token.Text = current.token; key.Text = current.key; }
    else url.Text = "https://";
    var gen = Button("Generar clave nueva", 16, y, 160); gen.Click += (s, e) => {
      if (key.Text.Trim().Length > 0 && MessageBox.Show(this, "Ya hay una clave. Si generas otra, este equipo dejará de poder leer lo que suban los demás. ¿Continuar?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
      key.Text = SyncCrypto.NewKey();
    };
    var copy = Button("Copiar clave", 184, y, 120); copy.Click += (s, e) => CopySecret(key.Text.Trim());
    var show = Button("Mostrar", 312, y, 80); show.Click += (s, e) => { bool hide = !key.UseSystemPasswordChar; key.UseSystemPasswordChar = hide; token.UseSystemPasswordChar = hide; show.Text = hide ? "Mostrar" : "Ocultar"; };
    var import = Button("Importar…", 400, y, 104); import.Click += (s, e) => ImportFile();
    y += 44;
    Controls.Add(new Label { Text = "Estado: " + status, Location = new Point(16, y), Size = new Size(488, 40), ForeColor = Theme.Muted });
    var save = Button("Guardar", 404, 324, 100); save.BackColor = Theme.Accent; save.ForeColor = Color.White;
    save.Click += (s, e) => { var cfg = ReadFields(); if (cfg == null) return; Result = cfg; DialogResult = DialogResult.OK; };
    var export = Button("Exportar…", 134, 324, 100); export.Click += (s, e) => ExportFile();
    var cancel = Button("Cancelar", 296, 324, 100); cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; };
    if (current != null) {
      var off = Button("Desactivar", 16, 324, 110); off.ForeColor = Theme.Danger;
      off.Click += (s, e) => { Result = null; DialogResult = DialogResult.OK; };
    }
    CancelButton = null;
  }

  int Field(string label, TextBox box, int y, bool secret) {
    Controls.Add(new Label { Text = label, Location = new Point(16, y), Size = new Size(488, 20), ForeColor = Theme.Muted, Font = Theme.Small });
    box.Location = new Point(16, y + 22); box.Width = 488; box.BackColor = Theme.Card; box.ForeColor = Theme.Fore; box.BorderStyle = BorderStyle.FixedSingle;
    box.UseSystemPasswordChar = secret;
    Controls.Add(box);
    return y + 58;
  }

  Label Button(string text, int x, int y, int w) {
    var b = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, 32), BackColor = Theme.Card, ForeColor = Theme.Fore, Font = Theme.Small, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
    Controls.Add(b);
    return b;
  }

  void Warn(string message) { MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }

  SyncConfig ReadFields() {
    var u = url.Text.Trim();
    Uri parsed;
    if (!Uri.TryCreate(u, UriKind.Absolute, out parsed) || parsed.Scheme != "https") { Warn("La URL debe empezar por https://"); return null; }
    if (token.Text.Trim().Length == 0) { Warn("Falta el token del dispositivo."); return null; }
    try { SyncCrypto.ParseKey(key.Text); } catch (SyncException ex) { Warn(ex.Message); return null; }
    return new SyncConfig { url = u.TrimEnd('/'), token = token.Text.Trim(), key = key.Text.Trim() };
  }

  const string FileFilter = "Configuración de EasyClipboard (*.ecsync)|*.ecsync|Todos los archivos (*.*)|*.*";

  void ExportFile() {
    var cfg = ReadFields(); if (cfg == null) return;
    if (MessageBox.Show(this, "El archivo contiene el token y la clave sin cifrar: quien lo tenga puede leer y modificar tus fijados.\n\nGuárdalo en un lugar seguro y bórralo tras importarlo en el otro equipo. ¿Continuar?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
    using (var dlg = new SaveFileDialog { Filter = FileFilter, FileName = "easyclipboard-" + Environment.MachineName.ToLowerInvariant() + ".ecsync", OverwritePrompt = true }) {
      if (dlg.ShowDialog(this) != DialogResult.OK) return;
      try { File.WriteAllText(dlg.FileName, SyncFile.Format(cfg), new UTF8Encoding(false)); }
      catch (Exception ex) { Warn("No se pudo guardar: " + ex.Message); }
    }
  }

  void ImportFile() {
    using (var dlg = new OpenFileDialog { Filter = FileFilter }) {
      if (dlg.ShowDialog(this) != DialogResult.OK) return;
      try {
        var cfg = SyncFile.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8));
        url.Text = cfg.url; token.Text = cfg.token; key.Text = cfg.key;
        MessageBox.Show(this, "Configuración cargada. Revisa los datos y pulsa Guardar.\n\nSi el token es de otro equipo, conviene pedir uno propio para poder revocarlos por separado.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
      } catch (Exception ex) { Warn("No se pudo importar: " + ex.Message); }
    }
  }


  // Copia la clave pidiendo a los historiales (este panel y el de Windows) que no la guarden.
  void CopySecret(string value) {
    if (value.Length == 0) return;
    try {
      var data = new DataObject();
      data.SetText(value, TextDataFormat.UnicodeText);
      data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
      data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
      data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
      Clipboard.SetDataObject(data, true, 10, 50);
    } catch (Exception ex) { Warn("No se pudo copiar: " + ex.Message); }
  }
}

// Archivo de configuración legible, al estilo de un .conf de WireGuard.
internal static class SyncFile {
  public static string Format(SyncConfig cfg) {
    return "# EasyClipboard: sincronización en la nube\n"
      + "# Contiene secretos sin cifrar. No lo compartas ni lo subas a ningún repositorio.\n"
      + "[EasyClipboard]\n"
      + "Server = " + cfg.url + "\n"
      + "Token = " + cfg.token + "\n"
      + "Key = " + cfg.key + "\n";
  }

  public static SyncConfig Parse(string text) {
    var cfg = new SyncConfig();
    foreach (var raw in (text ?? "").Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
      int eq = line.IndexOf('=');
      if (eq <= 0) continue;
      var name = line.Substring(0, eq).Trim().ToLowerInvariant(); var value = line.Substring(eq + 1).Trim();
      if (name == "server" || name == "url") cfg.url = value;
      else if (name == "token") cfg.token = value;
      else if (name == "key") cfg.key = value;
    }
    if (String.IsNullOrEmpty(cfg.url) || String.IsNullOrEmpty(cfg.token) || String.IsNullOrEmpty(cfg.key)) throw new SyncException("Al archivo le falta Server, Token o Key.");
    SyncCrypto.ParseKey(cfg.key);
    return cfg;
  }
}
