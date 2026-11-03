import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Archive,
  ArrowDownToLine,
  ArrowUpRight,
  Box,
  Check,
  ChevronRight,
  Clock,
  Database,
  Files,
  Folder,
  HardDrive,
  History,
  KeyRound,
  Layers,
  Lock,
  LogOut,
  Monitor,
  Plus,
  Search,
  ShieldCheck,
  X,
} from "lucide-react";
import "./style.css";
type Device = {
  id: string;
  name: string;
  head: string | null;
  revoked: boolean;
};
type Snapshot = {
  id: string;
  deviceId: string;
  parent: string | null;
  files: number;
  bytes: number;
  createdTicks: number;
};
type Entry = { path: string; bytes: number; hash: string; chunks: string[] };
type Manifest = { id: string; parent: string | null; files: Entry[] };
type Overview = {
  devices: Device[];
  snapshots: number;
  logicalBytes: number;
  physicalBytes: number;
  chunks: number;
  audit: { id: number; action: string; detail: string; at: number }[];
};
class Fault extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}
async function api<T>(
  path: string,
  method = "GET",
  body?: unknown,
): Promise<T> {
  const r = await fetch("/api" + path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Fault(r.status, (await r.json()).error);
  return r.status === 204 ? (undefined as T) : r.json();
}
const size = (n: number) =>
  n < 1024
    ? `${n} B`
    : n < 1048576
      ? `${(n / 1024).toFixed(1)} KB`
      : `${(n / 1048576).toFixed(1)} MB`;
const time = (ticks: number) =>
  new Date((ticks - 621355968000000000) / 10000).toLocaleString("tr-TR", {
    day: "2-digit",
    month: "short",
    hour: "2-digit",
    minute: "2-digit",
  });
function Brand() {
  return (
    <a className="brand" href="/">
      <span>
        <Box size={24} />
      </span>
      vaultsync<i>®</i>
    </a>
  );
}
function App() {
  const [data, setData] = useState<Overview | null>(null),
    [snapshots, setSnapshots] = useState<Snapshot[]>([]),
    [login, setLogin] = useState(false),
    [view, setView] = useState("overview"),
    [error, setError] = useState(""),
    [modal, setModal] = useState(false),
    [token, setToken] = useState(""),
    [name, setName] = useState(""),
    [busy, setBusy] = useState(false),
    [selected, setSelected] = useState<Manifest | null>(null),
    [query, setQuery] = useState(""),
    [filter, setFilter] = useState("all"),
    [retention, setRetention] = useState<{
      candidates: unknown[];
      message: string;
    } | null>(null);
  function fail(e: unknown) {
    if (e instanceof Fault && e.status === 401) {
      setLogin(true);
      setData(null);
    } else setError((e as Error).message);
  }
  async function refresh() {
    try {
      const [d, s] = await Promise.all([
        api<Overview>("/overview"),
        api<Snapshot[]>("/snapshots"),
      ]);
      setData(d);
      setSnapshots(s);
      setLogin(false);
    } catch (e) {
      fail(e);
    }
  }
  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 15000);
    return () => clearInterval(t);
  }, []);
  const device = (id: string) =>
    data?.devices.find((d) => d.id === id)?.name || id.slice(0, 8);
  const [access, setAccess] = useState("");
  const [diff, setDiff] = useState<{
    added: string[];
    changed: string[];
    removed: string[];
  } | null>(null);
  const [verified, setVerified] = useState(false);
  useEffect(() => {
    setDiff(null);
    setVerified(false);
  }, [selected?.id]);
  if (login)
    return (
      <div className="login">
        <div>
          <Brand />
          <p className="eyebrow">DOSYALARININ ZAMAN ÇİZGİSİ</p>
          <h1>
            Bir kopyadan
            <br />
            daha fazlası.
          </h1>
          <p>
            Her değişikliğin bir geçmişi.
            <br />
            Her dosyanın dönüş yolu.
          </p>
          <div className="login-note">
            <ShieldCheck /> Şifreli parça deposu · Doğrulanmış geri yükleme
          </div>
        </div>
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            try {
              await api("/login", "POST", { key: access });
              setError("");
              await refresh();
            } catch (e) {
              setError((e as Error).message);
            }
          }}
        >
          <span className="eyebrow">ÇALIŞMA ALANI GİRİŞİ</span>
          <h2>Kasayı aç.</h2>
          <p>Bu sunucunun yönetici anahtarını kullan.</p>
          <label>
            Erişim anahtarı
            <input
              type="password"
              required
              value={access}
              onChange={(e) => setAccess(e.target.value)}
              autoComplete="current-password"
            />
          </label>
          {error && <p role="alert">{error}</p>}
          <button className="primary">
            Çalışma alanına gir <ArrowUpRight size={17} />
          </button>
          <small>Yerel Development anahtarı: vaultsync-local-dev</small>
        </form>
      </div>
    );
  if (!data) return <div className="loading">VaultSync hazırlanıyor…</div>;
  const visible = snapshots.filter(
    (s) => filter === "all" || s.deviceId === filter,
  );
  const savings = data.logicalBytes
    ? Math.max(
        0,
        Math.round((1 - data.physicalBytes / data.logicalBytes) * 100),
      )
    : 0;
  return (
    <div className="shell">
      <aside>
        <Brand />
        <p className="workspace">
          KİŞİSEL ÇALIŞMA ALANI
          <br />
          <b>
            Dosya kasası <Lock size={12} />
          </b>
        </p>
        <span className="eyebrow">ÇALIŞMA ALANI</span>
        <nav>
          {[
            ["overview", "Genel bakış", Layers],
            ["versions", "Sürümler", History],
            ["devices", "Cihazlar", Monitor],
            ["storage", "Depolama", Database],
            ["activity", "Etkinlik", Clock],
          ].map(([id, label, Icon]) => {
            const I = Icon as typeof Layers;
            return (
              <button
                key={id as string}
                className={view === id ? "active" : ""}
                onClick={() => {
                  setView(id as string);
                  setSelected(null);
                }}
              >
                <I size={18} />
                {label as string}
                {id === "versions" && <em>{data.snapshots}</em>}
              </button>
            );
          })}
        </nav>
        <div className="sidebar-bottom">
          <ShieldCheck size={20} />
          <b>İçerik doğrulamalı.</b>
          <p>
            Her geri yükleme SHA-256
            <br />
            ve AES-GCM ile denetlenir.
          </p>
          <button
            onClick={async () => {
              await api("/logout", "POST");
              setLogin(true);
              setData(null);
            }}
          >
            <LogOut size={15} /> Oturumu kapat
          </button>
        </div>
      </aside>
      <div className="body">
        <header>
          <span>
            Çalışma alanı <ChevronRight size={13} />{" "}
            {
              (
                {
                  overview: "Genel bakış",
                  versions: "Sürümler",
                  devices: "Cihazlar",
                  storage: "Depolama",
                  activity: "Etkinlik",
                } as Record<string, string>
              )[view]
            }
          </span>
          <span>
            <i className="dot" /> Sunucu bağlantısı açık{" "}
            <span className="avatar">BÖ</span>
          </span>
        </header>
        <main>
          {error && (
            <div role="alert" className="error">
              {error}
              <button onClick={() => setError("")}>
                <X size={15} />
              </button>
            </div>
          )}
          <div className="title">
            <div>
              <p className="eyebrow">
                VAULTSYNC /{" "}
                {view === "devices" ? "BAĞLANTILAR" : "ÇALIŞMA ALANI"}
              </p>
              <h1>
                {
                  (
                    {
                      overview: "Dosyaların, güvende.",
                      versions: "Her değişikliğin bir izi.",
                      devices: "Her cihaz, aynı kasa.",
                      storage: "Daha az yer. Aynı dosyalar.",
                      activity: "Kasada neler oldu?",
                    } as Record<string, string>
                  )[view]
                }
              </h1>
              <p>
                Kaynaklarını takip et. Sürümleri karşılaştır. İhtiyacın
                olduğunda geri dön.
              </p>
            </div>
            <button
              className="primary"
              onClick={() => {
                setModal(true);
                setToken("");
              }}
            >
              <Plus size={17} /> Cihaz bağla
            </button>
          </div>
          {(view === "overview" || view === "storage") && (
            <>
              <div className="stats">
                <article>
                  <span>
                    SAKLANAN VERİ <HardDrive size={16} />
                  </span>
                  <h2>{size(data.physicalBytes)}</h2>
                  <p>{data.chunks} benzersiz parça</p>
                </article>
                <article>
                  <span>
                    SÜRÜM GEÇMİŞİ <Archive size={16} />
                  </span>
                  <h2>
                    {data.snapshots}
                    <small> sürüm</small>
                  </h2>
                  <p>{size(data.logicalBytes)} toplam sürüm içeriği</p>
                </article>
                <article>
                  <span>
                    TEKRAR KULLANIM <Layers size={16} />
                  </span>
                  <h2>%{savings}</h2>
                  <p>Mantıksal veriye göre parça tasarrufu</p>
                </article>
                <article>
                  <span>
                    BAĞLI KAYNAKLAR <Monitor size={16} />
                  </span>
                  <h2>
                    {data.devices.filter((d) => !d.revoked).length}
                    <small> cihaz</small>
                  </h2>
                  <p>Yönetici tarafından yetkilendirilmiş</p>
                </article>
              </div>
              <section className="feature">
                <div>
                  <span className="eyebrow">
                    KÜÇÜK PARÇALAR. SAĞLAM BİR GEÇMİŞ.
                  </span>
                  <h2>
                    Değişeni aktar.
                    <br />
                    Geçmişi koru.
                  </h2>
                  <p>
                    Aynı içerik tekrar gönderilmez. Sürüm ancak tüm parçalar ve
                    dosya hash’leri doğrulandığında kaydedilir.
                  </p>
                  <span className="feature-pill">
                    <Check size={14} /> AES-256-GCM şifreli nesneler
                  </span>
                </div>
                <div className="block-art" aria-label="Dedup parça deposu">
                  <div className="block-label">
                    İÇERİK ADRESLİ DEPO <span>SHA-256</span>
                  </div>
                  <div className="blocks">
                    {Array.from({ length: 35 }, (_, i) => (
                      <div
                        key={i}
                        className={
                          i % 7 === 0 ? "reused" : i % 4 === 0 ? "muted" : ""
                        }
                      >
                        {i % 7 === 0 ? <Check size={14} /> : null}
                      </div>
                    ))}
                  </div>
                  <p>
                    <i /> Benzersiz parça <i className="light" /> Tekrar
                    kullanılan içerik
                  </p>
                  <small>
                    Şematik gösterim · canlı aktarım grafiği değildir
                  </small>
                </div>
              </section>
            </>
          )}
          {(view === "overview" || view === "versions") && (
            <section className="panel">
              <div className="panel-heading">
                <div>
                  <h2>
                    Son sürümler <span>{visible.length}</span>
                  </h2>
                  <p>
                    Her kaynak için bağımsız, değiştirilemeyen zaman çizgisi.
                  </p>
                </div>
                <div className="filters">
                  <Search size={16} />
                  <input
                    placeholder="Dosya veya sürüm ara"
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                  />
                  <select
                    aria-label="Kaynak cihaz"
                    value={filter}
                    onChange={(e) => setFilter(e.target.value)}
                  >
                    <option value="all">Tüm cihazlar</option>
                    {data.devices.map((d) => (
                      <option key={d.id} value={d.id}>
                        {d.name}
                      </option>
                    ))}
                  </select>
                </div>
              </div>
              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>SÜRÜM / KAYNAK</th>
                      <th>TARİH</th>
                      <th>DOSYA</th>
                      <th>BOYUT</th>
                      <th>DURUM</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {visible
                      .filter((s) =>
                        (device(s.deviceId) + s.id)
                          .toLowerCase()
                          .includes(query.toLowerCase()),
                      )
                      .map((s) => (
                        <tr key={s.id}>
                          <td>
                            <div className="file-name">
                              <span>
                                <Folder size={19} />
                              </span>
                              <div>
                                <b>{device(s.deviceId)}</b>
                                <small>
                                  {s.id.slice(0, 8)} ·{" "}
                                  {s.parent ? "Artımlı sürüm" : "İlk sürüm"}
                                </small>
                              </div>
                            </div>
                          </td>
                          <td>{time(s.createdTicks)}</td>
                          <td>{s.files}</td>
                          <td>{size(s.bytes)}</td>
                          <td>
                            <span className="badge">
                              <Check size={12} /> Doğrulandı
                            </span>
                          </td>
                          <td>
                            <button
                              className="text-button"
                              onClick={async () => {
                                try {
                                  setSelected(
                                    await api<Manifest>("/snapshots/" + s.id),
                                  );
                                } catch (e) {
                                  fail(e);
                                }
                              }}
                            >
                              İncele <ChevronRight size={14} />
                            </button>
                          </td>
                        </tr>
                      ))}
                  </tbody>
                </table>
              </div>
              {visible.length === 0 && (
                <div className="empty">
                  <Files size={32} />
                  <h3>İlk sürümün burada başlayacak.</h3>
                  <p>Bir cihaz bağla, ajanı kaynak klasörünle çalıştır.</p>
                  <button onClick={() => setModal(true)}>
                    Cihaz bağla <ArrowUpRight size={14} />
                  </button>
                </div>
              )}
            </section>
          )}
          {view === "devices" && (
            <div className="devices">
              {data.devices.map((d) => (
                <article key={d.id}>
                  <Monitor size={28} />
                  <span className={d.revoked ? "revoked" : "badge"}>
                    {d.revoked ? "Erişim kapalı" : "Yetkili kaynak"}
                  </span>
                  <h2>{d.name}</h2>
                  <code>{d.id.slice(0, 16)}</code>
                  <p>
                    {d.head
                      ? "Son sürüm: " + d.head.slice(0, 8)
                      : "İlk yedek bekleniyor"}
                  </p>
                  {!d.revoked && (
                    <button
                      onClick={async () => {
                        if (
                          !confirm(
                            "Cihazın API erişimi iptal edilsin mi? Mevcut yedekler korunur.",
                          )
                        )
                          return;
                        try {
                          await api("/devices/" + d.id, "DELETE");
                          await refresh();
                        } catch (e) {
                          fail(e);
                        }
                      }}
                    >
                      Erişimi iptal et
                    </button>
                  )}
                </article>
              ))}
              {data.devices.length === 0 && (
                <div className="empty">Henüz bağlı cihaz yok.</div>
              )}
            </div>
          )}
          {view === "storage" && (
            <section className="panel retention">
              <div>
                <h2>Saklama politikası önizlemesi</h2>
                <p>
                  Her kaynakta son 5 sürüm korunsaydı hangi kayıtlar aday
                  olurdu?
                </p>
                <small>
                  Bu işlem veri silmez. Paylaşılan parçalar nedeniyle fiziksel
                  alan kazanımı ayrıca hesaplanmalıdır.
                </small>
              </div>
              <button
                onClick={async () => {
                  try {
                    setRetention(await api("/retention/preview?keep=5"));
                  } catch (e) {
                    fail(e);
                  }
                }}
              >
                Önizlemeyi hesapla
              </button>
              {retention && (
                <p>
                  {retention.candidates.length} aday sürüm · {retention.message}
                </p>
              )}
            </section>
          )}
          {view === "activity" && (
            <section className="panel audit">
              <h2>İşlem günlüğü</h2>
              {data.audit.map((a) => (
                <article key={a.id}>
                  <span>
                    <Check size={15} />
                  </span>
                  <div>
                    <b>{a.detail}</b>
                    <small>{a.action}</small>
                  </div>
                  <time>{time(a.at)}</time>
                </article>
              ))}
              {data.audit.length === 0 && <p>Henüz işlem yok.</p>}
            </section>
          )}
          <footer>
            <span>
              VAULTSYNC <b>01</b> / SÜRÜMLÜ DOSYA KASASI
            </span>
            <span>Yerel çalışma alanı · 15 saniyede bir güncellenir</span>
          </footer>
        </main>
      </div>
      {modal && (
        <div className="overlay">
          <section className="dialog">
            <button
              className="close"
              aria-label="Kapat"
              onClick={() => setModal(false)}
            >
              <X />
            </button>
            <Monitor size={30} />
            <h2>Yeni bir kaynak bağla.</h2>
            {token ? (
              <>
                <p>
                  Bu anahtar yalnızca şimdi gösterilir. Ajanın ortam değişkenine
                  ekle; kaynak koduna yazma.
                </p>
                <code className="secret">{token}</code>
                <pre>
                  VAULTSYNC_DEVICE_KEY=&lt;bu anahtar&gt;{`\n`}
                  VAULTSYNC_API=http://127.0.0.1:9251{`\n`}dotnet run --project
                  agent -- &lt;klasör&gt;
                </pre>
                <button className="primary" onClick={() => setModal(false)}>
                  Anahtarı kaydettim <Check size={15} />
                </button>
              </>
            ) : (
              <form
                onSubmit={async (e) => {
                  e.preventDefault();
                  setBusy(true);
                  try {
                    const d = await api<{ key: string }>("/devices", "POST", {
                      name,
                    });
                    setToken(d.key);
                    await refresh();
                  } catch (e) {
                    fail(e);
                  } finally {
                    setBusy(false);
                  }
                }}
              >
                <p>Her cihaz ayrı anahtar ve sürüm geçmişi kullanır.</p>
                <label>
                  Cihaz adı
                  <input
                    required
                    maxLength={80}
                    placeholder="Örn. Tasarım bilgisayarı"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                  />
                </label>
                <button className="primary" disabled={busy}>
                  Anahtar oluştur <KeyRound size={16} />
                </button>
              </form>
            )}
          </section>
        </div>
      )}
      {selected && (
        <div className="overlay">
          <section className="dialog wide">
            <button
              className="close"
              aria-label="Sürümü kapat"
              onClick={() => setSelected(null)}
            >
              <X />
            </button>
            <span className="eyebrow">SÜRÜM / {selected.id.slice(0, 8)}</span>
            <h2>Dosyaların o anki hali.</h2>
            <p>
              İndirme sırasında şifreli parçalar ve dosya hash değeri yeniden
              doğrulanır.
            </p>
            <a
              className="primary"
              href={"/api/snapshots/" + selected.id + "/archive"}
            >
              <ArrowDownToLine size={16} /> Sürümü ZIP olarak indir
            </a>
            <div className="version-actions">
              <button
                onClick={async () => {
                  try {
                    setDiff(await api("/snapshots/" + selected.id + "/diff"));
                  } catch (e) {
                    fail(e);
                  }
                }}
              >
                Önceki sürümle karşılaştır
              </button>
              <button
                onClick={async () => {
                  try {
                    await api("/snapshots/" + selected.id + "/verify", "POST");
                    setVerified(true);
                    await refresh();
                  } catch (e) {
                    fail(e);
                  }
                }}
              >
                {verified ? "Bütünlük doğrulandı" : "Bütünlüğü doğrula"}
              </button>
            </div>
            {diff && (
              <div className="diff">
                <p>
                  <b>{diff.added.length}</b> yeni · <b>{diff.changed.length}</b>{" "}
                  değişen · <b>{diff.removed.length}</b> kaldırılan dosya
                </p>
                {diff.added.map((p) => (
                  <small key={"a" + p}>+ {p}</small>
                ))}
                {diff.changed.map((p) => (
                  <small key={"c" + p}>↻ {p}</small>
                ))}
                {diff.removed.map((p) => (
                  <small key={"d" + p}>− {p}</small>
                ))}
              </div>
            )}
            <div className="manifest">
              {selected.files.map((f) => (
                <a
                  key={f.path}
                  href={`/api/snapshots/${selected.id}/file?path=${encodeURIComponent(f.path)}`}
                >
                  <Files size={16} />
                  <span>
                    {f.path}
                    <small>
                      {f.hash.slice(0, 16)} · {f.chunks.length} parça
                    </small>
                  </span>
                  <b>{size(f.bytes)}</b>
                  <ArrowDownToLine size={15} />
                </a>
              ))}
              {selected.files.length === 0 && (
                <p>Bu sürüm boş klasör durumunu temsil eder.</p>
              )}
            </div>
          </section>
        </div>
      )}
    </div>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
