# Jellyfin Estadisticas Plugin
<div align="center">
    <p>
        <img alt="Logo" src="https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-estadisticas/main/logo.png" height="180"/><br />
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-estadisticas/releases"><img alt="Total GitHub Downloads" src="https://img.shields.io/github/downloads/pepebarrascout/jellyfin-plugin-estadisticas/total?color=9b59b6&label=descargas"/></a>
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-estadisticas/issues"><img alt="GitHub Issues" src="https://img.shields.io/github/issues/pepebarrascout/jellyfin-plugin-estadisticas?color=9b59b6"/></a>
        <a href="https://jellyfin.org/"><img alt="Jellyfin Version" src="https://img.shields.io/badge/Jellyfin-10.11.x-blue.svg"/></a>
        <a href="https://github.com/pepebarrascout/jellyfin-plugin-estadisticas"><img alt="Music Stats" src="https://img.shields.io/badge/Music-Statistics-orange?logo=last.fm&logoColor=white"/></a>
    </p>
</div>

> **Estadísticas de música para Jellyfin**. Registra reproducciones de audio en una **base de datos SQLite propia** (sin tocar la BD de Jellyfin) y genera informes **Top 50** y **Bottom 50** por canción, artista, álbum y género en múltiples ventanas temporales. Permite crear listas de reproducción y programar su publicación de forma diaria, semanal, mensual o anual.

**Requiere Jellyfin versión `10.11.0` o superior.**

---

## ✨ Características

| Característica | Descripción |
|---|---|
| 🎵 **Solo Audio** | Registra exclusivamente reproducciones de música (no video, libros, etc.) |
| ⏱️ **Scrobbling 20s** | Una reproducción se cuenta tras superar los 20 segundos de escucha |
| 💾 **SQLite Propio** | Base de datos independiente. **No modifica la BD de Jellyfin** |
| 📊 **Top 50** | Canciones, artistas, álbumes y géneros más escuchados en 6 ventanas temporales |
| 📉 **Bottom 50** | Canciones, artistas, álbumes y géneros menos escuchados (incluye 0 reproducciones) |
| 🗓️ **6 Ventanas** | 2 semanas, 1, 3, 6 y 12 meses, y año anterior (todas excluyen el periodo actual) |
| 🎼 **Multi-Género/Artista** | Una canción con `Rock; Metal` cuenta en ambos gééneros por separado |
| 📅 **Semanas Dom-Sáb** | Las semanas van de domingo a sábado; la semana actual (en curso) se excluye |
| 📋 **Playlists desde Consultas** | Crea listas de reproducción desde cualquier Top/Bottom 50 |
| 🔁 **Programación de Publicación** | Diaria, semanal (día de la semana), mensual (día del mes), anual (fecha del año) |
| ♻️ **Reemplazo In-Place** | Las playlists programadas se actualizan en la misma ID (sin duplicados) |
| 🧹 **Purga Automática** | Elimina reproducciones con más de 14 meses una vez al mes |
| 📚 **Archivo Histórico** | Base de datos separada con agregados anuales (retención indefinida) |
| 🔒 **Solo Admin** | Estadísticas del usuario administrador (no multiusuario en esta versión) |

---

## 📊 Ventanas Temporales

Todas las consultas **excluyen el periodo actual** (semana/mes/año en curso):

| Ventana | Etiqueta | Detalle |
|---|---|---|
| `2w` | Últimas 2 semanas | 2 semanas completas (dom-sáb) previas a la semana actual |
| `1m` | Último mes | Mes calendario anterior al actual |
| `3m` | Últimos 3 meses | 3 meses calendario anteriores, excluyendo el actual |
| `6m` | Últimos 6 meses | 6 meses calendario anteriores, excluyendo el actual |
| `12m` | Últimos 12 meses | 12 meses calendario anteriores, excluyendo el actual |
| `last_year` | Año anterior | Año calendario completo anterior al actual |

> **Diferencia clave:** "Últimos 12 meses" es una ventana móvil de 12 meses. "Año anterior" es el año calendario completo (ej.: en 2025 sería todo 2024).

---

## 📋 Clientes Probados

| Cliente | Plataforma | Estado |
|---|---|---|
| 🌐 **Jellyfin Web** | Interfaz web nativa | ✅ Funcional |
| 📱 **Jellyfin para Android** | App oficial de Jellyfin | ✅ Funcional |
| 🎵 **[Finamp]** | Android / iOS | ✅ Funcional |
| 🖥️ **[Feishin]** | Escritorio | 🔄 Por confirmar |

---

## 🚀 Instalación

### Método 1: Desde el Catálogo de Plugins de Jellyfin (vía Manifest) ⭐ Recomendado

1. En tu servidor Jellyfin, navega a **Panel de Control > Plugins > Repositorios**
2. Haz clic en el botón **+** (agregar repositorio)
3. Ingresa los siguientes datos:
   - **Nombre**: `Estadisticas Plugin`
   - **URL del Manifest**:
     ```
     https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-estadisticas/main/manifest.json
     ```
4. Haz clic en **Guardar**
5. Navega a la pestaña **Catálogo**
6. Busca **Estadisticas** en la lista de plugins disponibles
7. Haz clic en **Instalar**
8. Reinicia Jellyfin cuando se te solicite

### Método 2: Instalación Manual

1. Descarga la última versión desde [Releases](https://github.com/pepebarrascout/jellyfin-plugin-estadisticas/releases)
2. Descomprime el archivo ZIP
3. Copia todos los archivos a la carpeta de plugins de tu servidor Jellyfin:
   - **Linux**: `~/.config/jellyfin/plugins/Estadisticas/`
   - **Windows**: `%LocalAppData%\Jellyfin\plugins\Estadisticas\`
   - **macOS**: `~/.local/share/jellyfin/plugins/Estadisticas/`
   - **Docker**: Monta un volumen en `/config/plugins/Estadisticas/` dentro del contenedor
4. Reinicia Jellyfin

---

## ⚙️ Configuración

### Pestaña 1: Top 50 (Más Escuchadas)

1. Navega a **Panel de Control > Plugins > Estadisticas**
2. Selecciona la dimensión (Canciones, Artistas, Álbumes o Géneros)
3. Selecciona la ventana temporal (2 semanas, 1, 3, 6, 12 meses o año anterior)
4. Haz clic en **Consultar**
5. Para crear una playlist con los resultados:
   - Escribe un nombre en el campo "Nombre de la playlist a crear"
   - Haz clic en **Crear playlist con estos resultados**

### Pestaña 2: Bottom 50 (Menos Escuchadas)

- Misma mecánica que Top 50 pero en orden inverso
- **Incluye canciones/artistas/álbumes/géneros con 0 reproducciones** en el periodo
- Desempate: menos reproducciones totales históricas y luego orden cronológico ascendente (primer registro en la base de datos); las más antiguas primero
- También permite crear playlists desde los resultados

### Pestaña 3: Listas Programadas

1. Haz clic en **+ Nueva lista programada**
2. Completa el formulario:
   - **Nombre**: Nombre de la playlist en Jellyfin
   - **Orden**: Descendente (de la más escuchada a la menos escuchada) o Ascendente (de la menos escuchada a la más escuchada)
   - **Dimensión**: Canciones o Géneros
   - **Género musical** (solo Géneros): El género de las canciones (ej.: Rock)
   - **Ventana temporal**: Una de las 6 disponibles
   - **Límite**: Número máximo de canciones (default 50)
   - **Frecuencia**: Diaria, Semanal, Mensual o Anual
   - **Hora**: Hora local del servidor (formato 24h HH:mm)
   - **Día de la semana** (solo Semanal): Lunes a Domingo
   - **Día del mes** (solo Mensual): 1-28 (los días 29-31 se ajustan en meses cortos)
   - **Fecha del año** (solo Anual): Formato MM-DD (ej.: `12-25` para 25 de diciembre)
3. Haz clic en **Guardar**
4. Puedes **Ejecutar ahora** para forzar la publicación, o esperar al próximo horario programado

> **Importante:** Las playlists programadas se **reemplazan en la misma ID**. No se crean duplicados ("Estadisticas1", "Estadisticas11", etc.). El patrón es el mismo que usa `jellyfin-smartlists-plugin`.

---

## ⏰ Programación de Tareas

El plugin registra dos tareas en **Panel de Control > Tareas Programadas > Estadisticas**:

| Default | Tarea | Descripción |
|---|---|---|
| 🕒 **Cada 15 min** | Publicar listas programadas | Revisa las listas vencidas y las republica (reemplazando contenido) |
| 🕒 **Diario a las 03:00** | Purgar reproducciones antiguas | Elimina plays >14 meses y actualiza el archivo histórico. Solo ejecuta el día 1 del mes. |

Ambas tareas se pueden reconfigurar desde el dashboard de Jellyfin.

---

## 📁 Estructura de Archivos

```
{Datos de Jellyfin}/plugins/estadisticas/
├── estadisticas.db                       ← BD principal (SQLite, WAL mode)
│   ├── tracks                            ← Metadata de canciones (150K+ soportadas)
│   ├── track_artists                     ← Artistas multi-valor por canción
│   ├── track_genres                      ← Géneros multi-valor por canción
│   ├── plays                             ← Una fila por reproducción (retención 14 meses)
│   └── scheduled_playlists               ← Listas programadas
└── estadisticas_historical.db            ← BD histórica (SQLite, retención indefinida)
    ├── yearly_songs                      ← Agregado por canción por año
    ├── yearly_artists                    ← Agregado por artista por año
    ├── yearly_genres                     ← Agregado por género por año
    └── yearly_albums                     ← Agregado por álbum por año
```

> **⚠️ IMPORTANTE:** Este plugin **NUNCA escribe en la base de datos de Jellyfin**. Todas las estadísticas se guardan exclusivamente en los dos archivos SQLite anteriores.

---

## 🔧 Solución de Problemas

### El plugin no aparece en el panel de control
- Verifica que el archivo `.dll` esté en la carpeta correcta de plugins
- Asegúrate de reiniciar Jellyfin después de copiar los archivos
- Revisa los logs de Jellyfin para errores de carga del plugin

### No se registran reproducciones
- Verifica que el item sea de tipo **Audio** (no video, no audiobook, etc.)
- La reproducción debe superar los **20 segundos** para contar como válida
- Revisa los logs de Jellyfin: busca mensajes `Recorded play:` del plugin
- Solo se registran reproducciones del **usuario administrador** en esta versión

### Las consultas Top 50 devuelven vacío
- Es normal si todavía no hay suficientes reproducciones en el periodo consultado
- Las consultas **excluyen el periodo actual** (semana/mes/año en curso)
- Prueba con una ventana más amplia (12 meses o año anterior) para validar

### Las Bottom 50 muestran canciones que sí he escuchado
- Es el comportamiento esperado. Una canción puede tener muchas reproducciones totales pero 0 en el periodo consultado
- El desempate es: menos reproducciones totales históricas → luego orden cronológico ascendente (primer registro en la base de datos)

### Las playlists programadas no se publican
- Verifica que la tarea **"Publicar listas de reproducción programadas"** esté habilitada
- Revisa la columna `next_run` en la pestaña **Listas programadas** del plugin
- La tarea se ejecuta cada 15 minutos; si la próxima ejecución está en el futuro, espera

### La purga de reproducciones antiguas no se ejecuta
- Solo se ejecuta el **día 1 de cada mes a las 03:00** (hora local del servidor)
- Si cambias la programación desde el dashboard, mantén la lógica de "día 1 del mes" o ajusta el código

---

## 🛠️ Compilación

### Requisitos Previos
- [.NET SDK 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git

### Pasos para Compilar

```bash
# Clonar el repositorio
git clone https://github.com/pepebarrascout/jellyfin-plugin-estadisticas.git
cd jellyfin-plugin-estadisticas

# Compilar en modo Release
dotnet build -c Release

# Los archivos compilados estarán en:
# bin/Release/net9.0/Jellyfin.Plugin.Estadisticas.dll
```

Para generar el ZIP distribuible, usa el script `scripts/build_final.py` (requiere Python 3):

```bash
python3 scripts/build_final.py
# Genera: jellyfin-plugin-estadisticas_0.0.0.1.zip
```

El archivo `.dll` resultante se copia a la carpeta de plugins de Jellyfin junto con `meta.json` y `logo.png`.

---

## 🏗️ Arquitectura

| Archivo | Responsabilidad |
|---|---|
| `EstadisticasPlugin.cs` | Entry point del plugin. Configuración y página web del dashboard |
| `EstadisticasPluginServiceRegistrator.cs` | Registro de servicios en el contenedor DI de Jellyfin |
| `Data/SqliteDb.cs` | Gestión de conexiones a las dos BDs SQLite (main + histórica) |
| `Data/Schema.cs` | Creación de esquemas (tablas e índices) |
| `Services/PlaybackTrackerService.cs` | Servicio en segundo plano que captura reproducciones (≥20s, audio only) |
| `Services/StatisticsService.cs` | Consultas Top 50 / Bottom 50 SQL por dimensión y ventana |
| `Services/PlaylistPublisherService.cs` | Creación/actualización de playlists en Jellyfin (in-place) |
| `Services/PlaylistSchedulerService.cs` | CRUD + cómputo de next_run para listas programadas |
| `Services/HistoricalArchiveService.cs` | Purga mensual (>14 meses) + agregados anuales |
| `Tasks/PurgeOldPlaysTask.cs` | IScheduledTask: purga mensual |
| `Tasks/PublishScheduledPlaylistsTask.cs` | IScheduledTask: publicación cada 15 min |
| `Api/EstadisticasApiController.cs` | API REST para consultas y gestión de listas |
| `Configuration/PluginConfiguration.cs` | Modelo de configuración (persistencia XML automática) |
| `Configuration/config.html` | Página de configuración del dashboard (3 pestañas) |
| `Models/TimeWindow.cs` | Aritmética de ventanas temporales (excluye periodo actual) |
| `Models/PlayRecord.cs` | Modelo de una reproducción |
| `Models/TrackInfo.cs` | Modelo de metadata de canción |
| `Models/QueryResult.cs` | Enums y filas de resultado de consultas |
| `Models/ScheduledPlaylist.cs` | Modelo de lista programada |

---

## 📊 Decisiones de Diseño (v0.0.0.1)

- **Solo usuario admin**: el plugin captura y muestra estadísticas del admin. No hay filtrado por usuario en esta versión.
- **"Reproducción válida" = posición ≥ 20s** del item de audio. No requiere completar la canción.
- **Multi-género y multi-artista**: una canción con `Rock; Metal` cuenta en ambos géneros por separado. Una canción con `Artist A feat. Artist B` cuenta en ambos artistas por separado.
- **Semanas = dom-sáb**. La "semana actual" (aunque esté en curso desde el domingo) se excluye de las consultas de 2 semanas; se muestran las dos últimas semanas completas.
- **"Último año"** = año calendario anterior completo (ej.: en 2025 sería todo 2024).
- **"Últimos 12 meses"** = ventana móvil de 12 meses calendario anteriores, excluyendo el mes actual.
- **Bottom 50**: el desempate es `total_plays ASC, first_seen ASC` (menos reproducciones históricas y luego orden cronológico ascendente por el primer registro en la base de datos) para que el ranking sea estable y reproducible.
- **Playlists desde Artistas/Álbumes/Géneros**: al crear una playlist desde una consulta de dimensión agregada, el plugin resuelve la canción más representativa de cada entidad (la más reproducida en el periodo para Top, la menos para Bottom) y la añade. No duplica canciones entre entidades.

---

## 💬 Soporte y Contribuciones

- **Reportes de bugs y sugerencias**: Usa la sección de [Issues](https://github.com/pepebarrascout/jellyfin-plugin-estadisticas/issues) para reportar problemas o proponer nuevas funciones
- **Contribuciones**: Las contribuciones son bienvenidas. No dudes en enviar un Pull Request
- **Reportes de uso**: Si has probado el plugin en un cliente de Jellyfin que no aparece en la lista de [Clientes Probados](#-clientes-probados), por favor compártelo.
- **Versión Alpha**: El plugin aún se encuentra todavía en versión Alpha y no se ha probado ampliamente. Agradecemos los **reportes de uso** para ir depurando el código. ¡Ayúdanos a probar el plugin!.

---

## 🗺️ Hoja de Ruta

| Versión | Característica |
|---|---|
| `v0.0.0.2` | Tests unitarios para ventanas temporales y consultas SQL |
| `v0.0.0.3` | Pestaña de consultas complejas con selectores en cascada + gráfico de barras (Chart.js) |
| `v0.0.0.4` | Consultas combinadas (Top X por Y) |
| `v0.0.1.0` | API pública para consumir estadísticas desde otros clientes |
| `v0.0.2.0` | Soporte multiusuario con selector |

---

## 📚 Repositorios de Referencia

Este plugin se basa en patrones aprendidos de:
- [jellyfin-plugin-podcast](https://github.com/pepebarrascout/jellyfin-plugin-podcast)
- [jellyfin-smartlists-plugin](https://github.com/jyourstone/jellyfin-smartlists-plugin)
- [jellyfin-plugin-lastfm](https://github.com/pepebarrascout/jellyfin-plugin-lastfm)
- [jellyfin-plugin-listenbrainz](https://github.com/pepebarrascout/jellyfin-plugin-listenbrainz)
- [jellyfin-plugin-radio-online](https://github.com/pepebarrascout/jellyfin-plugin-radio-online)

---

## ⚠️ Disclaimer

Este plugin es un proyecto independiente y no está afiliado, respaldado ni patrocinado por Jellyfin. Jellyfin es una marca registrada de [The Jellyfin Project](https://jellyfin.org/).

---

## 📄 Licencia

Este proyecto está bajo la licencia [MIT](LICENSE).

[Feishin]: https://github.com/jeffvli/feishin
[Finamp]: https://github.com/Finamp/Finamp
