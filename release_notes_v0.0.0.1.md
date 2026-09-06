## v0.0.0.1 (Alpha) — Initial Release

Primer release del plugin de estadísticas de música para Jellyfin 10.11+.

### 🎵 Características

- **Captura de reproducciones de audio** (umbral 20s) en **base de datos SQLite propia** — no toca la BD de Jellyfin
- **Top 25** por canción, artista, álbum y género en 6 ventanas temporales (2 sem, 1m, 3m, 6m, 12m, año anterior)
- **Bottom 25** (menos escuchadas, incluye 0 reproducciones) en las mismas dimensiones × ventanas
- **Creación de playlists** desde cualquier consulta Top/Bottom
- **Programación de publicación** de playlists: diaria, semanal, mensual o anual (reemplazo in-place, sin duplicados)
- **Purga mensual automática** de reproducciones >14 meses
- **Archivo histórico anual** en BD separada (retención indefinida)
- **Solo audio**: no registra video, libros, etc.
- **Multi-género y multi-artista** contados por separado
- **Semanas de lunes a domingo** (ISO 8601), excluyendo la semana actual
- **3 pestañas** en el panel de control: Top 25, Bottom 25, Listas programadas
- **2 tareas programadas** en `Dashboard > Scheduled Tasks > Estadisticas`

### 📦 Instalación

1. En Jellyfin: **Panel de Control > Plugins > Repositorios > +**
2. URL del manifest:
   ```
   https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-estadisticas/main/manifest.json
   ```
3. Guardar → ir a **Catálogo** → buscar **Estadisticas** → Instalar
4. Reiniciar Jellyfin

### ⚠️ Notas

- Requiere **Jellyfin 10.11.0** o superior
- Solo registra estadísticas del **usuario administrador** (multiusuario se agregará en v0.0.2.0)
- Versión **Alpha**: no se ha probado ampliamente. Reportes de uso bienvenidos en [Issues](https://github.com/pepebarrascout/jellyfin-plugin-estadisticas/issues)

### 📋 Decisiones de diseño

- "Reproducción válida" = posición ≥ 20s
- "Último año" = año calendario anterior completo
- "Últimos 12 meses" = ventana móvil de 12 meses (excluye mes actual)
- Bottom 25 con empates en 0: desempate por menos reproducciones totales históricas, luego alfabético
