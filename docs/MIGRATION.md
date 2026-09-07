# Migración: Importar historial desde Jellyfin

Este documento explica cómo importar el historial de reproducciones existente
en la base de datos de Jellyfin hacia el plugin Estadísticas. **Es opcional**
y se hace una sola vez.

## ¿Cuándo usar esto?

Si ya tenías música escuchada en Jellyfin **antes** de instalar el plugin
Estadísticas, ese historial no está en la BD del plugin (el plugin sólo
captura reproducciones nuevas desde que se instaló).

Este script lee el `PlayCount` y `LastPlayedDate` que Jellyfin guarda por
canción, y los importa a la BD del plugin para que tus Top 50 / Bottom 50
tengan datos históricos desde el principio.

## Requisitos

- **Python 3** (preinstalado en Ubuntu Server)
- **Módulo `sqlite3`** (incluido en Python 3 por defecto, no hay que instalar nada)
- **Acceso SSH** al servidor donde corre Jellyfin
- **El plugin Estadísticas ya instalado** y Jellyfin reiniciado al menos una vez
  (para que la BD `estadisticas.db` exista)

## Importante

- **NO se detiene Jellyfin** — el script copia la BD de Jellyfin a un archivo
  temporal y trabaja con la copia. Jellyfin sigue corriendo normalmente.
- **NO se modifica la BD de Jellyfin** — el script sólo lee (de una copia).
- **NO se pierden datos del plugin** — las reproducciones que el plugin ya
  capturó se conservan intactas. Las importadas se añaden con timestamps
  anteriores a la instalación del plugin.
- **Se ejecuta UNA SOLA VEZ** — después de la importación, no necesitas volver
  a correrlo.

## Instrucciones para Docker

### 1. Conectarse por SSH al servidor

```bash
ssh usuario@tu-servidor
```

### 2. Descargar el script

Si tienes el repositorio clonado:

```bash
cd /tmp
wget https://raw.githubusercontent.com/pepebarrascout/jellyfin-plugin-estadisticas/main/scripts/import_jellyfin_history.py
chmod +x import_jellyfin_history.py
```

O copia el contenido del script manualmente a `/tmp/import_jellyfin_history.py`.

### 3. Localizar las bases de datos

Las rutas dependen de tu configuración de Docker. Típicamente:

```bash
# BD de Jellyfin (dentro del volumen montado en el host)
# Busca jellyfin.db o library.db:
find /home/usuario/jellyfin -name "jellyfin.db" -o -name "library.db" 2>/dev/null

# BD del plugin (la crea el plugin dentro del volumen de Jellyfin)
find /home/usuario/jellyfin -name "estadisticas.db" 2>/dev/null
```

Rutas típicas (ajusta según tu `docker-compose.yml`):

```
/home/usuario/jellyfin/config/data/jellyfin.db          ← BD de Jellyfin
/home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db  ← BD del plugin
```

### 4. (Recomendado) Hacer un dry run primero

Esto muestra qué se importaría **sin tocar** la BD del plugin:

```bash
cd /tmp
python3 import_jellyfin_history.py \
  --jellyfin-db /home/usuario/jellyfin/config/data/jellyfin.db \
  --plugin-db  /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db \
  --dry-run
```

Verás algo como:

```
[10:30:15] [INFO] Copiando BD de Jellyfin a archivo temporal: /tmp/jellyfin_copy_xxxxx.db
[10:30:15] [INFO] Abriendo copia de BD de Jellyfin (read-only)...
[10:30:16] [INFO] Encontrados 8432 items de audio en la BD de Jellyfin
[10:30:16] [INFO] Encontrados play counts para 5234 items
[10:30:16] [INFO] Items con play count > 0: 5234
[10:30:16] [INFO] Total items a importar: 5234
[10:30:16] [INFO] Total reproducciones a importar (estimado): 45678
[10:30:16] [INFO] DRY RUN — no se modificó la BD del plugin.
```

### 5. Hacer la importación real

```bash
python3 import_jellyfin_history.py \
  --jellyfin-db /home/usuario/jellyfin/config/data/jellyfin.db \
  --plugin-db  /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db
```

El script pedirá confirmación:

```
¿Importar 5234 canciones con ~45678 reproducciones?
Modo: additive
BD destino: /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db
Escribe 'si' para continuar: si
```

Escribe `si` y presiona Enter. La importación puede tardar unos minutos
dependiendo del tamaño de tu biblioteca.

### 6. Verificar

Abre el panel del plugin Estadísticas en Jellyfin y consulta Top 50 — deberías
ver resultados inmediatamente (con datos históricos).

### 7. Borrar el script (opcional)

```bash
rm /tmp/import_jellyfin_history.py
```

## Modos de importación

### `--mode additive` (default, seguro)

Importa TODAS las reproducciones que dice Jellyfin (`PlayCount`), distribuidas
en fechas anteriores a la instalación del plugin. Las plays reales del plugin
se conservan.

**Resultado:** el total será `PlayCount_de_Jellyfin + plays_reales_del_plugin`.

Sobreestima ligeramente (sólo las plays que ocurrieron entre la instalación
del plugin y la importación), pero es seguro y no pierde datos.

### `--mode adjusted` (preciso)

Resta las reproducciones que el plugin ya capturó. Si Jellyfin dice
`PlayCount=50` y el plugin ya tiene 5 plays reales, importa 45.

**Resultado:** el total coincide con `PlayCount_de_Jellyfin`.

Más preciso, pero requiere que el script consulte cuántas plays tiene el
plugin por canción (puede tardar un poco más).

## Parámetros opcionales

| Parámetro | Descripción |
|---|---|
| `--user-id ID` | Importar sólo las reproducciones de un usuario específico. Si se omite, importa para todos los usuarios (sumando play counts). |
| `--no-confirm` | No pedir confirmación interactiva (útil para scripts automatizados). |
| `--dry-run` | Mostrar qué se importaría sin tocar la BD del plugin. |

## ¿Qué datos se importan?

Por cada canción con `PlayCount > 0` en Jellyfin:

| Tabla del plugin | Qué se importa |
|---|---|
| `tracks` | Metadata: nombre, álbum, artista del álbum, duración, año, ruta del archivo |
| `track_artists` | Artistas de la canción (multi-valor) |
| `track_genres` | Géneros de la canción (multi-valor) |
| `plays` | N reproducciones (según PlayCount), distribuidas en fechas anteriores a la instalación del plugin |

## ¿Qué NO se importa?

- **Fechas exactas de reproducción:** Jellyfin no guarda un log de cada
  reproducción con su timestamp. Sólo guarda `PlayCount` (contador) y
  `LastPlayedDate`. El script distribuye las N reproducciones en fechas
  escalonadas hacia atrás desde `LastPlayedDate` (1 play cada 3 días por
  defecto). No es exacto, pero da una distribución usable para las ventanas
  temporales.
- **Tiempo real escuchado:** como se acordó, el plugin no captura cuántos
  segundos se escuchó de cada canción. El script tampoco.
- **Reproducciones de video/libros/etc.:** sólo se importan items de audio.

## Preguntas frecuentes

### ¿Puedo correr el script con Jellyfin en marcha?

**Sí.** El script copia la BD de Jellyfin a un archivo temporal y trabaja con
la copia. Jellyfin sigue corriendo normalmente. Puedes perder 1-2
reproducciones que estén ocurriendo en el instante exacto de la copia, pero el
plugin ya las está capturando por su cuenta.

### ¿Se modifican los datos del plugin?

**No se pierden datos.** Si una canción ya existe en la BD del plugin (porque
se reprodujo después de instalar el plugin), el script actualiza su metadata
(nombre, álbum, géneros, año) pero NO modifica sus plays existentes. Las plays
importadas tienen timestamps anteriores a la primera play real del plugin.

### ¿Puedo correrlo varias veces?

**Sí, pero no es necesario.** Si lo corres de nuevo, duplicará las plays
importadas (en modo `additive`) o las recalculará (en modo `adjusted`). Lo
recomendado es correrlo una sola vez.

### ¿Qué pasa si borro el script después?

**No pasa nada.** El script es una herramienta de un solo uso. Una vez que la
importación se completa, los datos están en la BD del plugin y no dependen del
script.

### ¿Funciona con Jellyfin instalado directamente (sin Docker)?

**Sí.** Las únicas diferencias son las rutas a las BDs:

```bash
# BD de Jellyfin (instalación directa):
~/.config/jellyfin/data/jellyfin.db
# o
/var/lib/jellyfin/data/jellyfin.db

# BD del plugin:
~/.config/jellyfin/data/plugins/estadisticas/estadisticas.db
```

El script funciona igual en ambos casos.

## Solución de problemas

### "Jellyfin DB not found"

Verifica la ruta con `find`:

```bash
find / -name "jellyfin.db" -o -name "library.db" 2>/dev/null
```

### "No se encontró la tabla de items"

Tu versión de Jellyfin puede usar un esquema diferente. El script busca
`TypedBaseItems` y `BaseItems`. Si tu Jellyfin usa otra tabla, revisa la BD
con `sqlite3` y reporta el issue en GitHub.

### "La BD del plugin no existe"

Asegúrate de que:
1. El plugin esté instalado en Jellyfin
2. Jellyfin se haya reiniciado al menos una vez después de instalar el plugin
3. La ruta a `estadisticas.db` sea correcta

### El dry run muestra "0 items"

Puede ser que:
- Tu Jellyfin no tenga reproducciones registradas (`PlayCount=0` en todo)
- El usuario que especificaste no tenga reproducciones
- El esquema de la BD sea diferente (revisa el log del script para ver qué
  tablas y columnas encontró)

## Backup

Antes de correr la importación, puedes hacer un backup de la BD del plugin:

```bash
cp /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db \
   /home/usuario/estadisticas.db.backup
```

Si algo sale mal, puedes restaurar:

```bash
cp /home/usuario/estadisticas.db.backup \
   /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db
```
