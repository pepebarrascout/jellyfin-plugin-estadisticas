# API Pública del Plugin Estadísticas

Este documento describe los endpoints HTTP del plugin **Estadísticas** para Jellyfin.
Todos los endpoints están bajo la ruta base `/Plugins/Estadisticas/`.

> **Autenticación:** Por defecto, Jellyfin requiere un token `X-Emby-Token` para
> acceder a cualquier ruta bajo `/Plugins/*`. Para uso público (publicar en una
> web externa), configura un reverse proxy (nginx/Caddy) que inyecté el token o
> que exima las rutas `/Public/*` de la autenticación.

## Índice

- [Endpoints de consulta (admin)](#endpoints-de-consulta-admin)
- [Endpoints públicos (para publicación externa)](#endpoints-públicos-para-publicación-externa)
- [Endpoints de gestión de listas programadas](#endpoints-de-gestión-de-listas-programadas)
- [Endpoints de histórico anual](#endpoints-de-histórico-anual)
- [Endpoints de logros](#endpoints-de-logros)
- [Ejemplos de uso](#ejemplos-de-uso)
- [Esquemas de datos](#esquemas-de-datos)

---

## Endpoints de consulta (admin)

### GET /Query

Ejecuta una consulta Top 50 o Bottom 50.

**Parámetros (query string):**

| Parámetro | Tipo | Requerido | Descripción |
|---|---|---|---|
| `dimension` | string | Sí | `Songs`, `Artists`, `Albums`, o `Genres` |
| `direction` | string | Sí | `Top` o `Bottom` |
| `window` | string | Sí | `2w`, `1m`, `3m`, `6m`, `12m`, o `last_year` |
| `limit` | int | No | Número de resultados (default 50, máximo 200) |
| `year` | int | No | Filtrar por año de la canción (ej: `1982`). Omite para todos. |

**Respuesta:**

```json
{
  "success": true,
  "dimension": "Songs",
  "direction": "Top",
  "window": "12m",
  "windowLabel": "Últimos 12 meses",
  "rangeStart": "2025-09-01",
  "rangeEnd": "2026-08-31",
  "yearFilter": 1982,
  "rowCount": 50,
  "rows": [
    {
      "rank": 1,
      "itemId": "abc123-def456-...",
      "name": "Canción Ejemplo",
      "subtitle": "Artista - Álbum",
      "playCount": 142,
      "totalPlayCount": 350
    }
  ]
}
```

### GET /Years

Lista de años disponibles (de las canciones reproducidas), descendente.

```json
{ "success": true, "years": [2026, 2025, 2024, 2023, 1982] }
```

### GET /Genres

Lista de géneros disponibles, ordenados alfabéticamente.

```json
{ "success": true, "genres": ["Blues", "Jazz", "Metal", "Rock"] }
```

### GET /ListeningTime

Tiempo total escuchado (aproximado) por género y ventana temporal.
Aproximación: suma la duración completa de cada canción reproducida.

```json
{
  "success": true,
  "data": {
    "Rock": { "2w": 12015000, "1m": 56023000, "3m": 211440000, ... },
    "Metal": { "2w": 4320000, "1m": 28911000, "3m": 98550000, ... }
  }
}
```

Los valores están en **milisegundos**. Para mostrar en horas: `ms / 3600000`.

### POST /CreatePlaylist

Crea una playlist en Jellyfin con los resultados de una consulta.

**Parámetros (query string):**

| Parámetro | Tipo | Requerido | Descripción |
|---|---|---|---|
| `name` | string | Sí | Nombre de la playlist |
| `dimension` | string | Sí | `Songs`, `Artists`, `Albums`, o `Genres` |
| `direction` | string | Sí | `Top` o `Bottom` |
| `window` | string | Sí | `2w`, `1m`, `3m`, `6m`, `12m`, o `last_year` |
| `limit` | int | No | Número máximo de canciones (default 50) |
| `year` | int | No | Filtrar por año |

**Respuesta:**

```json
{ "success": true, "playlistId": "abc-123-def", "itemCount": 50 }
```

---

## Endpoints públicos (para publicación externa)

Estos endpoints devuelven datos agregados listos para publicar en una web externa.
No permiten modificar nada (read-only).

### GET /Public/Stats

Snapshot completo en JSON: totales, top 10 por ventana, tiempo por género, logros.

**Estructura de la respuesta:**

```json
{
  "generatedAt": "2026-09-07T10:30:00Z",
  "serverTimeLocal": "2026-09-07T04:30:00-06:00",
  "totalPlays": 12345,
  "totalTracks": 8765,
  "totalArtists": 432,
  "totalGenres": 18,
  "topByWindow": {
    "2w": {
      "label": "Últimas 2 semanas",
      "topSongs": [...],
      "topArtists": [...],
      "topGenres": [...]
    },
    "1m": { ... },
    ...
  },
  "listeningTimeByGenre": {
    "Rock": { "2w": 12015000, ... },
    ...
  },
  "achievements": {
    "earned": 12,
    "total": 30,
    "earnedList": [
      { "id": "plays_100", "name": "Oyente Casual", "category": "Volumen" },
      ...
    ]
  }
}
```

### GET /Public/Rss

Feed RSS 2.0 con el top 10 de canciones por ventana temporal. Útil para
suscribirse en un lector RSS o incrustar en una web.

**Content-Type:** `application/rss+xml`

**Estructura:**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0">
  <channel>
    <title>Estadísticas de Música - Jellyfin</title>
    <description>Top de canciones más escuchadas por ventana temporal</description>
    <item>
      <title>Top 10 canciones — Últimos 12 meses</title>
      <description><![CDATA[<ol>
        <li>Canción 1 — Artista (142 reproducciones)</li>
        <li>Canción 2 — Artista (98 reproducciones)</li>
        ...
      </ol>]]></description>
      <pubDate>Mon, 01 Sep 2026 00:00:00 GMT</pubDate>
      <guid isPermaLink="false">estadisticas-12m-20260901</guid>
    </item>
  </channel>
</rss>
```

---

## Endpoints de gestión de listas programadas

### GET /ScheduledPlaylists

Lista todas las listas programadas.

### POST /ScheduledPlaylists

Crea una nueva lista programada. Body JSON con los campos del DTO.

### PUT /ScheduledPlaylists/{id}

Actualiza una lista programada existente.

### DELETE /ScheduledPlaylists/{id}

Borra una lista programada (no borra la playlist en Jellyfin).

### POST /ScheduledPlaylists/{id}/Run

Ejecuta inmediatamente una lista programada (sin esperar a su hora).

**DTO (body para POST/PUT):**

```json
{
  "Name": "Top 50 Rock — Último mes",
  "QueryDirection": "Top",
  "QueryDimension": "Genres",
  "QueryWindow": "1m",
  "Genre": "Rock",
  "Year": 1982,
  "Limit": 50,
  "Frequency": "Weekly",
  "TimeOfDay": "08:00",
  "DayOfWeek": 0,
  "DayOfMonth": null,
  "MonthAndDay": null,
  "Enabled": true
}
```

| Campo | Valores |
|---|---|
| `QueryDirection` | `Top` o `Bottom` |
| `QueryDimension` | `Songs` o `Genres` |
| `QueryWindow` | `2w`, `1m`, `3m`, `6m`, `12m`, `last_year` |
| `Genre` | string o null (solo si dimension=Genres) |
| `Year` | int o null |
| `Frequency` | `Daily`, `Weekly`, `Monthly`, `Yearly` |
| `TimeOfDay` | `HH:mm` (24h, ej: `08:00`) |
| `DayOfWeek` | 0=Lun..6=Dom (solo Weekly) |
| `DayOfMonth` | 1-28 (solo Monthly) |
| `MonthAndDay` | `MM-DD` (solo Yearly, ej: `12-25`) |

---

## Endpoints de histórico anual

### GET /Historical/Years

Lista de años disponibles en la BD histórica (descendente).

### GET /Historical/Year/{year}

Resumen completo de un año: top 25 canciones, artistas, géneros, álbumes + totales.

**Respuesta:**

```json
{
  "success": true,
  "year": 2025,
  "data": {
    "totalPlays": 5432,
    "totalDurationMs": 987654321,
    "topSongs": [
      { "itemId": "...", "name": "...", "albumArtist": "...", "albumName": "...", "playCount": 142, "durationMs": 234000 }
    ],
    "topArtists": [ { "name": "...", "playCount": 350 } ],
    "topGenres": [ { "name": "Rock", "playCount": 1234 } ],
    "topAlbums": [ { "albumArtist": "...", "albumName": "...", "playCount": 500 } ]
  }
}
```

### GET /Historical/Compare

Comparativa lado a lado de todos los años disponibles.

```json
{
  "success": true,
  "data": [
    {
      "year": 2025,
      "totalPlays": 5432,
      "topGenre": "Rock",
      "topGenrePlays": 1234,
      "topArtist": "The Beatles",
      "topArtistPlays": 350,
      "topSong": "Yesterday",
      "topSongPlays": 142,
      "totalDurationMs": 987654321
    }
  ]
}
```

---

## Endpoints de logros

### GET /Achievements

Lista todos los logros y niveles con su progreso actual.

```json
{
  "success": true,
  "earnedCount": 12,
  "totalCount": 30,
  "achievements": [
    {
      "id": "plays_100",
      "category": "Volumen",
      "name": "Oyente Casual",
      "description": "100 reproducciones totales",
      "icon": "music_note",
      "current": 12345,
      "target": 100,
      "earned": true,
      "unit": "",
      "progress": 100.0
    },
    {
      "id": "month_gold",
      "category": "Nivel Mensual",
      "name": "Oro Mensual",
      "description": "500 reproducciones este mes",
      "icon": "military_tech",
      "current": 234,
      "target": 500,
      "earned": false,
      "unit": "",
      "progress": 46.8
    }
  ]
}
```

### Categorías de logros

| Categoría | Descripción | Ejemplos |
|---|---|---|
| **Volumen** | Reproducciones totales | Oyente Casual (100), Audiófilo (5000), Melómano (10000) |
| **Diversidad** | Canciones/artistas/géneros/álbumes distintos | Explorador (100 canciones), Sibarita (25 géneros) |
| **Tiempo** | Horas escuchadas (aprox.) | Primeras 10h, Centenario (100h), Mil Horas |
| **Nivel Mensual** | Reproducciones este mes | Bronce (50), Plata (200), Oro (500), Platino (1000), Diamante (2000) |
| **Nivel Semestral** | Reproducciones este semestre (6 meses) | Bronce (300), Plata (1200), Oro (3000), Platino (6000) |
| **Nivel Anual** | Reproducciones este año calendario | Bronce (600), Plata (2400), Oro (6000), Platino (12000), Diamante (24000) |

---

## Endpoints de diagnóstico

### GET /Debug/Status

Estado de la BD: conteos, hora del servidor, reproducción más antigua/reciente, plays por ventana.

### POST /Debug/ClearAll

Borra todas las reproducciones y canciones. Las listas programadas se conservan.

---

## Ejemplos de uso

### curl: obtener Top 50 canciones de 1982

```bash
curl "https://tu-servidor-jellyfin/Plugins/Estadisticas/Query?dimension=Songs&direction=Top&window=last_year&year=1982&limit=50" \
  -H "X-Emby-Token: TU_TOKEN"
```

### curl: crear playlist Top 25 Rock del último mes

```bash
curl -X POST "https://tu-servidor-jellyfin/Plugins/Estadisticas/CreatePlaylist?name=Top%2025%20Rock&dimension=Genres&direction=Top&window=1m&limit=25" \
  -H "X-Emby-Token: TU_TOKEN"
```

### curl: obtener JSON público para web

```bash
curl "https://tu-servidor-jellyfin/Plugins/Estadisticas/Public/Stats" \
  -H "X-Emby-Token: TU_TOKEN"
```

### JavaScript: incrustar top 10 en una web

```html
<div id="top-songs"></div>
<script>
fetch('https://tu-servidor-jellyfin/Plugins/Estadisticas/Public/Stats', {
  headers: { 'X-Emby-Token': 'TU_TOKEN' }
})
.then(r => r.json())
.then(data => {
  const songs = data.topByWindow['12m'].topSongs;
  const html = songs.map((s, i) =>
    `<li>${i+1}. ${s.Name} — ${s.Subtitle || ''} (${s.PlayCount} plays)</li>`
  ).join('');
  document.getElementById('top-songs').innerHTML = '<ol>' + html + '</ol>';
});
</script>
```

### Python: descargar feed RSS

```python
import feedparser
feed = feedparser.parse(
    'https://tu-servidor-jellyfin/Plugins/Estadisticas/Public/Rss',
    request_headers={'X-Emby-Token': 'TU_TOKEN'}
)
for entry in feed.entries:
    print(entry.title)
    print(entry.description)
```

### Cron: backup diario del JSON público

```bash
0 6 * * * curl -s -H "X-Emby-Token: TU_TOKEN" \
  "https://tu-servidor-jellyfin/Plugins/Estadisticas/Public/Stats" \
  > /backups/estadisticas-$(date +\%Y\%m\%d).json
```

---

## Esquemas de datos

### QueryResultRow

| Campo | Tipo | Descripción |
|---|---|---|
| `Rank` | int | Posición (1-based) |
| `ItemId` | string | Jellyfin ItemId (solo para Songs; vacío para agregados) |
| `Name` | string | Nombre (canción, artista, álbum, o género) |
| `Subtitle` | string? | Artista - Álbum (Songs), Artista (Albums), null (Artists/Genres) |
| `PlayCount` | long | Reproducciones en el periodo consultado |
| `TotalPlayCount` | long | Reproducciones totales históricas |

### AchievementInfo

| Campo | Tipo | Descripción |
|---|---|---|
| `Id` | string | Identificador único (ej: `plays_100`) |
| `Category` | string | Categoría (Volumen, Diversidad, Tiempo, Nivel Mensual, etc.) |
| `Name` | string | Nombre corto (ej: "Oyente Casual") |
| `Description` | string | Descripción con el objetivo |
| `Icon` | string | Nombre del icono Material Icons (ej: `music_note`, `military_tech`) |
| `Current` | long | Progreso actual |
| `Target` | long | Objetivo para desbloquear |
| `Earned` | bool | Si ya se desbloqueó |
| `Unit` | string | Unidad opcional (ej: `h` para horas) |
| `Progress` | double | Porcentaje 0-100 |

---

## Notas

- **Zona horaria:** Todas las ventanas temporales se calculan en `America/Guatemala` (UTC-6). Los límites de calendario (inicio de mes, inicio de semana dom-sáb) se calculan en hora local y se convierten a UTC para las consultas SQL.
- **Aproximación de tiempo escuchado:** El plugin NO captura cuántos segundos se escuchó realmente de cada canción. Para consultas de "tiempo total", se suma la duración completa de cada canción reproducida. Esto sobreestima el tiempo real (usuarios pueden saltar antes del final).
- **Retención:** Las reproducciones se purgan después de 14 meses (tarea mensual). Los agregados anuales se conservan indefinidamente en la BD histórica.
