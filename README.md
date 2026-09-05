# jellyfin-plugin-estadisticas

Plugin de **estadísticas de música** para Jellyfin 10.11+. Registra reproducciones de audio en una **base de datos SQLite propia** (sin tocar la BD de Jellyfin) y genera informes Top 25 y Bottom 25 por canción, artista, álbum y género en múltiples ventanas temporales. Permite crear listas de reproducción y programar su publicación de forma diaria, semanal, mensual o anual.

> **Versión:** 0.0.0.1 (alpha inicial)
> **Target ABI:** Jellyfin 10.11.0.0
> **Framework:** .NET 9

## Características (v0.0.0.1)

- **Captura de reproducciones** de audio (umbral 20 segundos) en SQLite propio, totalmente independiente de la BD de Jellyfin.
- **Solo audio**: no se registran reproducciones de vídeo, libros, etc.
- **Metadatos guardados por canción**: nombre, artista del álbum, álbum, género(s), duración, artistas múltiples, géneros múltiples, cliente y dispositivo desde el que se reprodujo.
- **Top 25** por canción / artista / álbum / género en 6 ventanas temporales:
  - Últimas 2 semanas (semana completa, excluye la semana actual — semanas lun-dom)
  - Último mes (excluye el mes actual)
  - Últimos 3 meses (excluye el mes actual)
  - Últimos 6 meses (excluye el mes actual)
  - Últimos 12 meses (excluye el mes actual)
  - Año anterior (año calendario completo, excluye el año actual)
- **Bottom 25** (menos escuchadas) en las mismas 4 dimensiones × 6 ventanas. Incluye canciones/artistas/álbumes/géneros con 0 reproducciones en el periodo. Desempate: menos reproducciones totales históricas, luego alfabético.
- **Creación de listas de reproducción** desde cualquier consulta Top/Bottom.
- **Programación de publicación** de listas:
  - Diaria (hora)
  - Semanal (día de la semana + hora)
  - Mensual (día del mes 1-28 + hora)
  - Anual (MM-DD + hora)
  - La playlist se **reemplaza en su misma ID** (no se crean duplicados).
- **Purga mensual automática**: reproducciones con más de 14 meses se eliminan de la BD principal.
- **Base de datos histórica anual**: resumen ligero por año (por canción/artista/álbum/género), retenido indefinidamente.

## Decisiones de diseño (v0.0.0.1)

- **Solo usuario admin**: el plugin captura y muestra estadísticas del admin. No hay filtrado por usuario en esta versión.
- **"Reproducción válida" = posición ≥ 20s** del item de audio. No requiere completar la canción.
- **Multi-género y multi-artista**: una canción con `Rock; Metal` cuenta en ambos géneros por separado. Una canción con `Artist A feat. Artist B` cuenta en ambos artistas por separado.
- **Semanas = lun-dom** (ISO 8601). La "semana actual" se excluye de las consultas de 2 semanas.
- **"Último año"** = año calendario anterior completo (ej.: en 2025 sería todo 2024).
- **"Últimos 12 meses"** = ventana móvil de 12 meses calendario anteriores, excluyendo el mes actual.
- **Bottom 25**: el desempate para empates en 0 reproducciones es `total_plays ASC, name ASC` para que el ranking sea estable y reproducible.
- **Playlists**: al crear una playlist desde una consulta de dimensión "Artistas/Álbumes/Géneros", el plugin resuelve la canción más representativa de cada entidad (la más reproducida en el periodo para Top, la menos para Bottom) y la añade. No duplica canciones entre entidades.

## Instalación

1. Agrega este repositorio a Jellyfin: `Dashboard > Plugins > Repositories > +`
   - URL: `https://github.com/pepebarrascout/jellyfin-plugin-estadisticas/raw/main/manifest.json`
2. Instala "Estadisticas" desde el catálogo.
3. Reinicia Jellyfin.
4. Ve a `Dashboard > Plugins > Estadisticas` para abrir el panel.

## Compilación manual

Requisitos:
- .NET 9 SDK
- Git

```bash
git clone https://github.com/pepebarrascout/jellyfin-plugin-estadisticas.git
cd jellyfin-plugin-estadisticas
dotnet build -c Release
```

El DLL resultante estará en `bin/Release/net9.0/Jellyfin.Plugin.Estadisticas.dll`. Cópialo a `<JellyfinDataPath>/plugins/Estadisticas/` junto con `meta.json`.

## Tareas programadas

El plugin registra dos tareas en `Dashboard > Scheduled Tasks > Estadisticas`:

| Tarea | Default | Descripción |
|---|---|---|
| Purgar reproducciones antiguas (>14 meses) | 1º de cada mes, 03:00 | Elimina plays >14 meses y actualiza el archivo histórico anual. |
| Publicar listas programadas | Cada 15 min | Revisa las listas programadas vencidas y las republica. |

Ambas se pueden reconfigurar desde el dashboard de Jellyfin.

## Estructura del proyecto

```
jellyfin-plugin-estadisticas/
├── EstadisticasPlugin.cs                    # Entry point + IHasWebPages
├── EstadisticasPluginServiceRegistrator.cs  # DI registration
├── Configuration/
│   ├── PluginConfiguration.cs               # XML config (scrobble threshold, retention)
│   └── config.html                          # Dashboard page (3 tabs)
├── Data/
│   ├── SqliteDb.cs                          # Connection management
│   └── Schema.cs                            # Schema creation (main + historical)
├── Models/
│   ├── PlayRecord.cs                        # One play event
│   ├── TrackInfo.cs                         # Cached track metadata
│   ├── QueryResult.cs                       # Enums + result rows
│   ├── TimeWindow.cs                        # Date arithmetic for windows
│   └── ScheduledPlaylist.cs                 # Scheduled playlist entity
├── Services/
│   ├── PlaybackTrackerService.cs            # IHostedService, playback events
│   ├── StatisticsService.cs                 # Top/Bottom SQL queries
│   ├── PlaylistPublisherService.cs          # Create/update Jellyfin playlists
│   ├── PlaylistSchedulerService.cs          # CRUD + due-time eval
│   └── HistoricalArchiveService.cs          # 14-month purge + yearly aggregates
├── Tasks/
│   ├── PurgeOldPlaysTask.cs                 # Monthly IScheduledTask
│   └── PublishScheduledPlaylistsTask.cs     # 15-min IScheduledTask
├── Api/
│   └── EstadisticasApiController.cs         # REST endpoints
├── manifest.json                            # Jellyfin repo manifest
├── meta.json                                # Build metadata
├── global.json                              # .NET SDK version
├── nuget.config                             # NuGet sources
└── Jellyfin.Plugin.Estadisticas.csproj      # Project file
```

## Base de datos

El plugin usa **dos bases SQLite independientes** en `<JellyfinDataPath>/plugins/estadisticas/`:

- `estadisticas.db` — BD principal. Tablas: `tracks`, `track_artists`, `track_genres`, `plays`, `scheduled_playlists`. Retención: 14 meses en `plays`.
- `estadisticas_historical.db` — BD histórica. Tablas: `yearly_songs`, `yearly_artists`, `yearly_genres`, `yearly_albums`. Retención: indefinida.

**Jellyfin's own database is NEVER modified by this plugin.**

## Limitaciones conocidas (v0.0.0.1)

- No hay consultas combinadas (Top artistas por género, etc.) — se agregará en versión futura.
- No hay pestaña de consultas complejas con selectores y gráfico de barras — se agregará en versión futura.
- No hay selector de usuario; solo admin.
- La API no expone todavía los datos históricos anuales.
- No hay tests automatizados todavía.

## Hoja de ruta

- v0.0.0.2: tests unitarios para ventanas temporales y consultas.
- v0.0.0.3: pestaña de consultas complejas con selectores + Chart.js.
- v0.0.0.4: consultas combinadas (Top X por Y).
- v0.0.1.0: API pública para consumir estadísticas desde otros clientes.

## Licencia

MIT — ver [LICENSE](LICENSE).

## Repositorios de referencia

Este plugin se basa en patrones aprendidos de:
- [jellyfin-plugin-podcast](https://github.com/pepebarrascout/jellyfin-plugin-podcast)
- [jellyfin-smartlists-plugin](https://github.com/jyourstone/jellyfin-smartlists-plugin)
- [jellyfin-plugin-lastfm](https://github.com/pepebarrascout/jellyfin-plugin-lastfm)
- [jellyfin-plugin-listenbrainz](https://github.com/pepebarrascout/jellyfin-plugin-listenbrainz)
- [jellyfin-plugin-radio-online](https://github.com/pepebarrascout/jellyfin-plugin-radio-online)
