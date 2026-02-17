# 🛡️ S2AW — Source 2 Anti-Wallhack

> **Counter-Strike 2 için sunucu taraflı anti-wallhack eklentisi**  
> CounterStrikeSharp + Ray-Trace bridge ile, oyuncu görünürlüğünü viewer bazlı yönetir.

---

## ✨ Ne Yapar?

S2AW, her viewer için her target pawn’ı değerlendirir:

- 👀 **Görünürse:** Pawn normal şekilde transmit edilir.
- 🧱 **LOS dışındaysa:** Sadece o viewer için target pawn index’i transmit listesinden çıkarılır.
- 🧩 **Yan entity’ler (controller/scoreboard/weapon):** Doğrudan filtrelenmez.

Bu sayede wallhack avantajı azalır; sunucu stabilitesi için fail-open güvenliği korunur.

---

## 🔧 Zorunlu Bileşenler

S2AW yalnızca resmi Ray-Trace bridge yolunu kullanır.  
**CS2TraceRay fallback yoktur.**

- Metamod:Source
- CounterStrikeSharp
- Ray-Trace (native)
- RayTraceImpl (CS# plugin)
- RayTraceApi (shared assembly)

### Beklenen Sunucu Dizilimi

```text
csgo/addons/
├── metamod/
│   └── RayTrace.vdf
├── RayTrace/
│   ├── bin/win64/RayTrace.dll
│   └── gamedata.json
└── counterstrikesharp/
    ├── plugins/
    │   ├── RayTraceImpl/
    │   └── S2AW/S2AW.dll
    └── shared/
        └── RayTraceApi/
```

---

## 🏗️ Build

```bash
dotnet build S2AW/S2AW.csproj -c Release -warnaserror
```

---

## 🧠 Çalışma Akışı

1. `OnTick` aktif oyuncuları ve target expanded AABB snapshot’larını oluşturur.
2. Viewer’lar motion-priority + adaptif batch ile sıralanır.
3. Her viewer-target ilişkisinde önce ucuz gate’ler (cache, distance, FOV, stagger/carry) çalışır.
4. Gerekirse LOS trace yapılır.
5. Hidden pawn listesi viewer slotu için commit edilir.
6. `CheckTransmit`, ilgili viewer için sadece hidden pawn index’lerini çıkarır.

---

## ⚡ Performans Modeli (Özet)

S2AW trace maliyetini agresif biçimde düşürmek için:

- 🎯 Tick başına global trace budget (`max_traces_per_tick`)
- 📉 Yüke göre adaptif budget/distance
  - mid load: ~%75 budget, ~%85 distance
  - heavy load: ~%60 budget, ~%70 distance
- 🧮 Viewer başına fair-share trace bölüşümü
- 🗂️ Relation cache (`hidden`, `grace`, `next-evaluate`)
- 🧊 Static carry + deterministic far/mid stagger (no-trace tekrar kullanım)
- 📏 Motion-priority etkisini mesafe ile sınırlama
- 📌 Küçük ve sabit sample set (`base <= 3`)
- 👁️ PeekAssist kısıtları
  - sadece low-load
  - sadece hidden + priority relation
  - viewer başına tick’te en fazla 1 assist denemesi
  - global assist budget limiti
- ⏱️ Round başlangıcında kısa fail-open warmup (`round_start_fail_open_ms`)

---

## ⚙️ Config Anahtarları

| Anahtar | Varsayılan | Açıklama |
|---|---:|---|
| `enabled` | `true` | Eklenti aktif/pasif |
| `ignore_bots` | `false` | Botları hedef olarak yok say |
| `process_bot_viewers` | `true` | Botlar viewer olarak işlensin mi |
| `hide_teammates` | `true` | Takım arkadaşları da LOS dışındaysa gizlenebilir |
| `tick_divider` | `1` | Kaç tickte bir değerlendirme yapılır |
| `max_viewers_per_tick` | `64` | Tick başına işlenecek max viewer |
| `max_distance` | `5000` | Bu mesafenin ötesi fail-open visible |
| `visibility_grace_ticks` | `4` | Flicker azaltmak için görünürlük grace |
| `reveal_sync_ticks` | `12` | Hidden→visible geçişte sync hold |
| `enforce_fov_check` | `true` | FOV gate aktif/pasif |
| `fov_dot_threshold` | `-0.20` | FOV dot eşiği |
| `max_traces_per_tick` | `3500` | Tick trace bütçesi |
| `raytrace_retry_ticks` | `128` | RayTrace reconnect deneme aralığı |
| `expanded_box_scale_xy` | `3.0` | Expanded AABB XY ölçeği |
| `expanded_box_scale_z` | `1.5` | Expanded AABB Z ölçeği |
| `sample_budget` | `2` | Relation başına base sample bütçesi |
| `first_pass_budget` | `1` | İlk hızlı örnekleme bütçesi |
| `peek_eye_offset` | `28.0` | PeekAssist omuz offseti |
| `round_start_fail_open_ms` | `500` | Round başı warmup fail-open süresi |
| `debug_draw_traces` | `false` | Debug trace çizimi |
| `debug_draw_expanded_aabb` | `false` | Debug AABB çizimi |
| `debug_draw_interval_ms` | `1000` | Debug çizim throttle |
| `debug_draw_max_beams` | `256` | Debug beam güvenlik limiti |

---

## 🖥️ Komutlar

- `css_s2aw_status` → Durum ve aktif runtime bilgisi
- `css_s2aw_stats` → Son tick ortalamaları ve yük göstergeleri
- `css_s2aw_stats_reset` → Stats geçmişini sıfırlar

---

## 🚑 Notlar

- Ray-Trace backend yoksa veya trace budget dolarsa sistem **fail-open** davranır.
- Bu tasarım, gameplay stabilitesini korumak için bilinçli tercihtir.
- 32-slot yoğun sunucularda ilk tuning sırası:
  1. `ignore_bots` / `process_bot_viewers`
  2. `max_traces_per_tick`
  3. `sample_budget` + `first_pass_budget`
  4. `peek_eye_offset`

