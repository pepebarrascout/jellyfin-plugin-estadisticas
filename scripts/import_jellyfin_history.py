#!/usr/bin/env python3
"""
import_jellyfin_history.py
==========================

Importa el historial de reproducciones de la base de datos de Jellyfin hacia
la base de datos del plugin Estadísticas. Se ejecuta UNA SOLA VEZ, manualmente,
desde la terminal del servidor (host), sin necesidad de entrar al contenedor
Docker ni de detener Jellyfin.

Cómo funciona
-------------
1. Copia la BD de Jellyfin (jellyfin.db + su -wal si existe) a /tmp/ para no
   tocar el original. Jellyfin sigue corriendo normalmente.
2. Lee la copia: extrae canciones de audio, su metadata (nombre, álbum, artista,
   géneros, año, duración) y el PlayCount + LastPlayedDate de cada una.
3. Inserta la metadata en la BD del plugin (tracks, track_artists, track_genres)
   usando INSERT OR IGNORE para no duplicar.
4. Distribuye las N reproducciones (PlayCount) en fechas escalonadas hacia atrás
   desde LastPlayedDate, SIN pisar las plays reales que el plugin ya capturó.
5. Borra la copia temporal.

Uso
---
    # Ver qué se importaría (sin tocar la BD del plugin)
    python3 import_jellyfin_history.py \\
        --jellyfin-db /ruta/a/jellyfin/config/data/jellyfin.db \\
        --plugin-db  /ruta/a/jellyfin/config/data/plugins/estadisticas/estadisticas.db \\
        --dry-run

    # Importación real
    python3 import_jellyfin_history.py \\
        --jellyfin-db /ruta/a/jellyfin/config/data/jellyfin.db \\
        --plugin-db  /ruta/a/jellyfin/config/data/plugins/estadisticas/estadisticas.db

Modos
-----
    --mode additive  (default)
        Importa TODAS las reproducciones que dice Jellyfin (PlayCount), distribuidas
        en fechas anteriores a la instalación del plugin. Las plays reales del plugin
        se conservan. El total será PlayCount_de_Jellyfin + plays_reales_del_plugin.
        Sobreestima ligeramente, pero es seguro y no pierde datos.

    --mode adjusted
        Resta las reproducciones que el plugin ya capturó. Si Jellyfin dice PlayCount=50
        y el plugin ya tiene 5 plays reales, importa 45. El total coincide con
        PlayCount_de_Jellyfin. Más preciso.

Requisitos
----------
- Python 3 (preinstalado en Ubuntu Server)
- Módulo sqlite3 (incluido en Python 3 por defecto, no hay que instalar nada)
- Acceso de lectura a la BD de Jellyfin (vía volumen Docker montado en el host)
- Acceso de escritura a la BD del plugin

Notas
-----
- NO se modifica la BD de Jellyfin. El script sólo la lee (de una copia).
- NO se detiene Jellyfin. La copia se hace con el servidor en marcha.
- Si una canción ya existe en la BD del plugin, se actualiza su metadata
  (nombre, álbum, géneros, año, duración) pero NO se modifican sus plays existentes.
- Las plays importadas tienen timestamps anteriores a la primera play real del
  plugin (o a la fecha de instalación del plugin), para no solaparse.
"""

import argparse
import os
import shutil
import sqlite3
import sys
import tempfile
from datetime import datetime, timedelta, timezone


# ============================================================================
# Helper functions
# ============================================================================

def log(msg, level="INFO"):
    """Print a timestamped log message to stderr."""
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] [{level}] {msg}", file=sys.stderr)


def copy_jellyfin_db(jellyfin_db_path):
    """
    Copy the Jellyfin database (+ WAL file if it exists) to a temp file.
    Returns the path to the temp copy.

    SQLite in WAL mode allows copying the .db file while the server is running.
    The copy may miss the very last committed transactions, but it will be a
    valid, consistent database.
    """
    if not os.path.exists(jellyfin_db_path):
        raise FileNotFoundError(f"Jellyfin DB not found: {jellyfin_db_path}")

    fd, temp_path = tempfile.mkstemp(suffix=".db", prefix="jellyfin_copy_")
    os.close(fd)
    os.unlink(temp_path)  # Remove empty file so shutil.copy2 can copy over it

    log(f"Copiando BD de Jellyfin a archivo temporal: {temp_path}")
    shutil.copy2(jellyfin_db_path, temp_path)

    # Also copy the WAL file if it exists (for consistency)
    wal_path = jellyfin_db_path + "-wal"
    if os.path.exists(wal_path):
        log(f"Copiando archivo WAL: {wal_path}")
        shutil.copy2(wal_path, temp_path + "-wal")

    # Also copy the SHM file if it exists (shared memory index for WAL)
    shm_path = jellyfin_db_path + "-shm"
    if os.path.exists(shm_path):
        shutil.copy2(shm_path, temp_path + "-shm")

    return temp_path


def cleanup_temp_db(temp_path):
    """Delete the temp copy and its WAL/SHM files."""
    for suffix in ["", "-wal", "-shm"]:
        path = temp_path + suffix
        if os.path.exists(path):
            os.unlink(path)
            log(f"Archivo temporal borrado: {path}")


# ============================================================================
# Jellyfin DB reading
# ============================================================================

def find_jellyfin_tables(conn):
    """
    Detect which tables exist in the Jellyfin DB. The schema changed between
    Jellyfin versions, so we need to be flexible.

    Returns a dict with the table names we found, or None for tables that
    don't exist.
    """
    cursor = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
    )
    tables = {row[0] for row in cursor.fetchall()}

    result = {
        "items": None,
        "user_data": None,
        "item_values": None,
        "users": None,
    }

    for candidate in ["TypedBaseItems", "BaseItems"]:
        if candidate in tables:
            result["items"] = candidate
            break

    for candidate in ["ItemUserData", "UserDatas", "UserData"]:
        if candidate in tables:
            result["user_data"] = candidate
            break

    for candidate in ["ItemValues", "ItemValuesExtra"]:
        if candidate in tables:
            result["item_values"] = candidate
            break

    for candidate in ["Users", "JellyfinUsers", "UsersDefault"]:
        if candidate in tables:
            result["users"] = candidate
            break

    return result


def get_columns(conn, table_name):
    """Return the set of column names for a table."""
    cursor = conn.execute(f"PRAGMA table_info({table_name});")
    return {row[1] for row in cursor.fetchall()}


def read_jellyfin_data(conn, user_id=None):
    """
    Read audio items + their play counts from the Jellyfin DB copy.

    Returns a list of dicts, each with:
        item_id, name, album_artist, album_name, duration_ms, year,
        genres (list), artists (list), play_count, last_played (ISO or None)
    """
    tables = find_jellyfin_tables(conn)
    if not tables["items"]:
        raise RuntimeError("No se encontró la tabla de items (TypedBaseItems/BaseItems) en la BD de Jellyfin")
    if not tables["user_data"]:
        log("Aviso: no se encontró tabla de user data. Todos los play counts serán 0.", "WARN")

    items_table = tables["items"]
    user_data_table = tables["user_data"]
    item_values_table = tables["item_values"]

    items_cols = get_columns(conn, items_table)
    log(f"Tabla de items: {items_table}, columnas: {sorted(items_cols)}")

    # Map our needed fields to actual column names
    col_map = {}
    for field, candidates in [
        ("guid", ["Guid", "guid", "Id"]),
        ("name", ["Name", "name"]),
        ("album_artist", ["AlbumArtist", "album_artist"]),
        ("album_name", ["Album", "album"]),
        ("runtime_ticks", ["RunTimeTicks", "run_time_ticks", "RunTimeTicks"]),
        ("production_year", ["ProductionYear", "production_year"]),
        ("path", ["Path", "path"]),
        ("type", ["Type", "type", "item_type"]),
    ]:
        for c in candidates:
            if c in items_cols:
                col_map[field] = c
                break

    if "guid" not in col_map:
        raise RuntimeError(f"No se encontró columna GUID en {items_table}")
    if "name" not in col_map:
        raise RuntimeError(f"No se encontró columna Name en {items_table}")

    # Build SELECT for audio items
    select_cols = [
        f'"{col_map["guid"]}" AS guid',
        f'"{col_map["name"]}" AS name',
    ]
    if "album_artist" in col_map:
        select_cols.append(f'"{col_map["album_artist"]}" AS album_artist')
    else:
        select_cols.append("NULL AS album_artist")
    if "album_name" in col_map:
        select_cols.append(f'"{col_map["album_name"]}" AS album_name')
    else:
        select_cols.append("NULL AS album_name")
    if "runtime_ticks" in col_map:
        select_cols.append(f'"{col_map["runtime_ticks"]}" AS runtime_ticks')
    else:
        select_cols.append("NULL AS runtime_ticks")
    if "production_year" in col_map:
        select_cols.append(f'"{col_map["production_year"]}" AS production_year')
    else:
        select_cols.append("NULL AS production_year")
    if "path" in col_map:
        select_cols.append(f'"{col_map["path"]}" AS path')
    else:
        select_cols.append("NULL AS path")

    type_filter = ""
    if "type" in col_map:
        select_cols.append(f'"{col_map["type"]}" AS item_type')
        type_filter = f"WHERE \"{col_map['type']}\" LIKE '%Audio%'"

    query = f"SELECT {', '.join(select_cols)} FROM {items_table} {type_filter};"
    log(f"Leyendo items de audio...")

    cursor = conn.execute(query)
    items = {}
    for row in cursor.fetchall():
        guid = row[0]
        if not guid:
            continue
        item_id = str(guid).strip()
        items[item_id] = {
            "item_id": item_id,
            "name": row[1] or "(unknown)",
            "album_artist": row[2],
            "album_name": row[3],
            "runtime_ticks": row[4],
            "production_year": row[5],
            "path": row[6],
            "play_count": 0,
            "last_played": None,
            "genres": [],
            "artists": [],
        }

    log(f"Encontrados {len(items)} items de audio en la BD de Jellyfin")

    # Read user data (play counts + last played)
    if user_data_table:
        ud_cols = get_columns(conn, user_data_table)
        log(f"Tabla de user data: {user_data_table}, columnas: {sorted(ud_cols)}")

        ud_col_map = {}
        for field, candidates in [
            ("item_id", ["ItemId", "item_id", "Key", "key"]),
            ("user_id", ["UserId", "user_id"]),
            ("play_count", ["PlayCount", "play_count"]),
            ("last_played", ["LastPlayedDate", "last_played", "LastPlayedDate"]),
        ]:
            for c in candidates:
                if c in ud_cols:
                    ud_col_map[field] = c
                    break

        if "item_id" in ud_col_map and "play_count" in ud_col_map:
            user_filter = ""
            params = ()
            if user_id and "user_id" in ud_col_map:
                user_filter = f'WHERE "{ud_col_map["user_id"]}" = ?'
                params = (user_id,)

            agg_cols = [
                f'"{ud_col_map["item_id"]}" AS item_id',
                f'SUM(CAST("{ud_col_map["play_count"]}" AS INTEGER)) AS total_plays',
            ]
            if "last_played" in ud_col_map:
                agg_cols.append(f'MAX("{ud_col_map["last_played"]}") AS last_played')
            else:
                agg_cols.append("NULL AS last_played")

            ud_query = f"""
                SELECT {', '.join(agg_cols)}
                FROM {user_data_table}
                {user_filter}
                GROUP BY "{ud_col_map["item_id"]}"
                HAVING total_plays > 0;
            """
            log(f"Leyendo play counts...")

            cursor = conn.execute(ud_query, params)
            play_data_found = 0
            for row in cursor.fetchall():
                item_id_raw = row[0]
                if not item_id_raw:
                    continue
                item_id = str(item_id_raw).strip()
                play_count = row[1] or 0
                last_played = row[2]

                if item_id in items:
                    items[item_id]["play_count"] = play_count
                    items[item_id]["last_played"] = last_played
                    play_data_found += 1

            log(f"Encontrados play counts para {play_data_found} items")

    # Read genres and artists from ItemValues table (if it exists)
    if item_values_table:
        iv_cols = get_columns(conn, item_values_table)
        log(f"Tabla de item values: {item_values_table}, columnas: {sorted(iv_cols)}")

        iv_col_map = {}
        for field, candidates in [
            ("type", ["Type", "type", "ItemValueId"]),
            ("value", ["Value", "value"]),
            ("item_id", ["ItemId", "item_id"]),
        ]:
            for c in candidates:
                if c in iv_cols:
                    iv_col_map[field] = c
                    break

        if "value" in iv_col_map and "item_id" in iv_col_map:
            type_col = iv_col_map.get("type")
            if type_col:
                # Jellyfin 10.11 ItemValueType mapping (from ItemValueType.cs):
                #   Artist = 0, AlbumArtist = 1, Genre = 2
                # Read genres (Type = 2)
                genre_query = f"""
                    SELECT "{iv_col_map["value"]}", "{iv_col_map["item_id"]}"
                    FROM {item_values_table}
                    WHERE "{type_col}" = 2 OR "{type_col}" = 'Genre' OR "{type_col}" = 'genre';
                """
                try:
                    cursor = conn.execute(genre_query)
                    genre_count = 0
                    for row in cursor.fetchall():
                        genre = row[0]
                        item_id = str(row[1]).strip() if row[1] else ""
                        if genre and item_id in items:
                            items[item_id]["genres"].append(genre)
                            genre_count += 1
                    log(f"Encontradas {genre_count} asignaciones de género")
                except Exception as e:
                    log(f"No se pudieron leer géneros con filtro de tipo: {e}", "WARN")

                # Read artists (Type = 0 = Artist, Type = 1 = AlbumArtist)
                artist_query = f"""
                    SELECT "{iv_col_map["value"]}", "{iv_col_map["item_id"]}", "{type_col}"
                    FROM {item_values_table}
                    WHERE "{type_col}" IN (0, 1) OR "{type_col}" IN ('Artist', 'artist', 'AlbumArtist', 'album_artist');
                """
                try:
                    cursor = conn.execute(artist_query)
                    artist_count = 0
                    for row in cursor.fetchall():
                        artist = row[0]
                        item_id = str(row[1]).strip() if row[1] else ""
                        if artist and item_id in items:
                            items[item_id]["artists"].append(artist)
                            artist_count += 1
                    log(f"Encontradas {artist_count} asignaciones de artista")
                except Exception as e:
                    log(f"No se pudieron leer artistas con filtro de tipo: {e}", "WARN")

    # Filter out items with no play count
    items_with_plays = {k: v for k, v in items.items() if v["play_count"] > 0}
    log(f"Items con play count > 0: {len(items_with_plays)}")

    return list(items_with_plays.values())


# ============================================================================
# Plugin DB writing
# ============================================================================

def get_plugin_first_play_timestamp(conn):
    """
    Returns the timestamp of the earliest play in the plugin DB (ISO 8601),
    or None if the plugin DB has no plays yet.

    This is used as the upper bound for imported plays: all imported plays will
    have timestamps BEFORE this date, so they don't overlap with real plays
    captured by the plugin.
    """
    cursor = conn.execute("SELECT MIN(played_at) FROM plays;")
    row = cursor.fetchone()
    if row and row[0]:
        return row[0]
    return None


def write_to_plugin_db(plugin_conn, items, mode="additive"):
    """
    Write the imported items and their plays to the plugin DB.
    """
    plugin_first_play = get_plugin_first_play_timestamp(plugin_conn)
    if plugin_first_play:
        log(f"Primer play real del plugin: {plugin_first_play}")
        try:
            upper_bound = datetime.fromisoformat(plugin_first_play.replace("Z", "+00:00"))
        except Exception:
            upper_bound = datetime.now(timezone.utc)
    else:
        log("El plugin no tiene plays reales todavía. Usando 'ahora' como límite superior.")
        upper_bound = datetime.now(timezone.utc)

    total_tracks_inserted = 0
    total_tracks_updated = 0
    total_artists = 0
    total_genres = 0
    total_plays_inserted = 0
    total_plays_skipped = 0

    now_utc = datetime.now(timezone.utc).isoformat()

    for idx, item in enumerate(items):
        item_id = item["item_id"]
        name = item["name"] or "(unknown)"
        album_artist = item["album_artist"]
        album_name = item["album_name"]
        duration_ms = None
        if item["runtime_ticks"]:
            duration_ms = int(item["runtime_ticks"] / 10000)
        year = item["production_year"]
        file_path = item["path"]

        # 1. UPSERT track
        cursor = plugin_conn.execute(
            "SELECT item_id FROM tracks WHERE item_id = ?",
            (item_id,)
        )
        exists = cursor.fetchone() is not None

        plugin_conn.execute("""
            INSERT INTO tracks (item_id, name, album_artist, album_name, duration_ms, file_path, year, first_seen, last_updated)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(item_id) DO UPDATE SET
                name = excluded.name,
                album_artist = excluded.album_artist,
                album_name = excluded.album_name,
                duration_ms = excluded.duration_ms,
                file_path = excluded.file_path,
                year = excluded.year,
                last_updated = excluded.last_updated
        """, (item_id, name, album_artist, album_name, duration_ms, file_path, year, now_utc, now_utc))

        if exists:
            total_tracks_updated += 1
        else:
            total_tracks_inserted += 1

        # 2. Refresh artists
        plugin_conn.execute("DELETE FROM track_artists WHERE item_id = ?", (item_id,))
        artists = item["artists"] or ([album_artist] if album_artist else [])
        for artist in artists:
            if artist and artist.strip():
                plugin_conn.execute(
                    "INSERT OR IGNORE INTO track_artists (item_id, artist) VALUES (?, ?)",
                    (item_id, artist.strip())
                )
                total_artists += 1

        # 3. Refresh genres
        plugin_conn.execute("DELETE FROM track_genres WHERE item_id = ?", (item_id,))
        for genre in item["genres"]:
            if genre and genre.strip():
                plugin_conn.execute(
                    "INSERT OR IGNORE INTO track_genres (item_id, genre) VALUES (?, ?)",
                    (item_id, genre.strip())
                )
                total_genres += 1

        # 4. Calculate how many plays to import
        play_count_jellyfin = item["play_count"]
        play_count_plugin = 0
        if mode == "adjusted":
            cursor = plugin_conn.execute(
                "SELECT COUNT(*) FROM plays WHERE item_id = ?",
                (item_id,)
            )
            play_count_plugin = cursor.fetchone()[0]

        plays_to_import = play_count_jellyfin - play_count_plugin if mode == "adjusted" else play_count_jellyfin
        if plays_to_import <= 0:
            total_plays_skipped += play_count_jellyfin
            continue

        # 5. Distribute plays in dates going backwards from LastPlayedDate
        if item["last_played"]:
            try:
                last_played_str = str(item["last_played"])
                if "T" in last_played_str:
                    last_played = datetime.fromisoformat(last_played_str.replace("Z", "+00:00"))
                else:
                    last_played = datetime.fromisoformat(last_played_str).replace(tzinfo=timezone.utc)
            except Exception:
                last_played = None
        else:
            last_played = None

        if last_played is None:
            last_played = upper_bound - timedelta(days=1)

        if last_played > upper_bound:
            last_played = upper_bound - timedelta(seconds=1)

        # Distribute: 1 play every 3 days going backwards
        interval_days = 3

        for i in range(plays_to_import):
            play_date = last_played - timedelta(days=interval_days * i)
            if play_date >= upper_bound:
                play_date = upper_bound - timedelta(seconds=i + 1)
            # Don't go beyond 14 months (would be purged anyway)
            if play_date < upper_bound - timedelta(days=14 * 30):
                break

            play_date_iso = play_date.isoformat()
            plugin_conn.execute("""
                INSERT INTO plays (item_id, played_at, user_id, client, device)
                VALUES (?, ?, ?, ?, ?)
            """, (item_id, play_date_iso, "imported-from-jellyfin", "Jellyfin (import)", None))
            total_plays_inserted += 1

        # Progress log every 1000 items
        if (idx + 1) % 1000 == 0:
            log(f"Procesados {idx + 1}/{len(items)} items...")

    return {
        "tracks_inserted": total_tracks_inserted,
        "tracks_updated": total_tracks_updated,
        "artists": total_artists,
        "genres": total_genres,
        "plays_inserted": total_plays_inserted,
        "plays_skipped": total_plays_skipped,
    }


# ============================================================================
# Main
# ============================================================================

def main():
    parser = argparse.ArgumentParser(
        description="Importa el historial de reproducciones de Jellyfin al plugin Estadísticas.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Ejemplos:

  # Ver qué se importaría (sin tocar la BD del plugin):
  python3 import_jellyfin_history.py \\
    --jellyfin-db /home/usuario/jellyfin/config/data/jellyfin.db \\
    --plugin-db  /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db \\
    --dry-run

  # Importación real (modo additive, seguro):
  python3 import_jellyfin_history.py \\
    --jellyfin-db /home/usuario/jellyfin/config/data/jellyfin.db \\
    --plugin-db  /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db

  # Importación precisa (modo adjusted, resta plays ya capturados por el plugin):
  python3 import_jellyfin_history.py \\
    --jellyfin-db /home/usuario/jellyfin/config/data/jellyfin.db \\
    --plugin-db  /home/usuario/jellyfin/config/data/plugins/estadisticas/estadisticas.db \\
    --mode adjusted
        """
    )
    parser.add_argument(
        "--jellyfin-db",
        required=True,
        help="Ruta a la BD de Jellyfin (jellyfin.db o library.db)"
    )
    parser.add_argument(
        "--plugin-db",
        required=True,
        help="Ruta a la BD del plugin Estadísticas (estadisticas.db)"
    )
    parser.add_argument(
        "--user-id",
        default=None,
        help="(Opcional) ID del usuario a importar. Si se omite, importa para todos los usuarios."
    )
    parser.add_argument(
        "--mode",
        choices=["additive", "adjusted"],
        default="additive",
        help="Modo de importación: 'additive' (default, seguro) o 'adjusted' (preciso)"
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Mostrar qué se importaría sin tocar la BD del plugin"
    )
    parser.add_argument(
        "--no-confirm",
        action="store_true",
        help="No pedir confirmación antes de importar (útil para scripts)"
    )

    args = parser.parse_args()

    log("=" * 60)
    log("Importador de historial de Jellyfin -> Plugin Estadísticas")
    log("=" * 60)
    log(f"BD Jellyfin: {args.jellyfin_db}")
    log(f"BD Plugin:   {args.plugin_db}")
    log(f"Modo:        {args.mode}")
    log(f"Dry run:     {args.dry_run}")
    if args.user_id:
        log(f"Usuario:     {args.user_id}")

    # Step 1: Copy Jellyfin DB to temp file
    try:
        temp_db = copy_jellyfin_db(args.jellyfin_db)
    except Exception as e:
        log(f"Error copiando BD de Jellyfin: {e}", "ERROR")
        return 1

    try:
        # Step 2: Read from the copy
        log("Abriendo copia de BD de Jellyfin (read-only)...")
        jf_conn = sqlite3.connect(f"file:{temp_db}?mode=ro", uri=True)
        jf_conn.row_factory = sqlite3.Row

        items = read_jellyfin_data(jf_conn, args.user_id)

        log(f"Total items a importar: {len(items)}")
        total_plays_to_import = sum(item["play_count"] for item in items)
        log(f"Total reproducciones a importar (estimado): {total_plays_to_import}")

        jf_conn.close()

        if args.dry_run:
            log("=" * 60)
            log("DRY RUN — no se modificó la BD del plugin.")
            log(f"Se importarían {len(items)} canciones con ~{total_plays_to_import} reproducciones.")
            log("Ejecuta sin --dry-run para hacer la importación real.")
            return 0

        # Confirm before proceeding
        if not args.no_confirm:
            print()
            print(f"¿Importar {len(items)} canciones con ~{total_plays_to_import} reproducciones?")
            print(f"Modo: {args.mode}")
            print(f"BD destino: {args.plugin_db}")
            response = input("Escribe 'si' para continuar: ")
            if response.lower() not in ["si", "sí", "y", "yes"]:
                log("Importación cancelada por el usuario.")
                return 0

        # Step 3: Write to plugin DB
        if not os.path.exists(args.plugin_db):
            log(f"La BD del plugin no existe: {args.plugin_db}", "ERROR")
            log("Asegúrate de que el plugin esté instalado y Jellyfin haya reiniciado al menos una vez.")
            return 1

        log("Abriendo BD del plugin (read-write)...")
        plugin_conn = sqlite3.connect(args.plugin_db)
        plugin_conn.execute("PRAGMA journal_mode=WAL;")
        plugin_conn.execute("PRAGMA foreign_keys=ON;")

        log("Iniciando transacción...")
        plugin_conn.execute("BEGIN TRANSACTION;")

        try:
            stats = write_to_plugin_db(plugin_conn, items, mode=args.mode)
            plugin_conn.commit()
            log("Transacción completada (commit).")
        except Exception as e:
            plugin_conn.rollback()
            log(f"Error durante la escritura. Se hizo rollback. Error: {e}", "ERROR")
            plugin_conn.close()
            return 1

        plugin_conn.close()

        # Summary
        log("=" * 60)
        log("Importación completada:")
        log(f"  Canciones nuevas:          {stats['tracks_inserted']}")
        log(f"  Canciones actualizadas:    {stats['tracks_updated']}")
        log(f"  Asignaciones de artista:   {stats['artists']}")
        log(f"  Asignaciones de género:    {stats['genres']}")
        log(f"  Reproducciones insertadas: {stats['plays_inserted']}")
        log(f"  Reproducciones omitidas:   {stats['plays_skipped']}")
        log("=" * 60)
        log("Puedes borrar este script si ya no lo necesitas.")
        return 0

    finally:
        cleanup_temp_db(temp_db)


if __name__ == "__main__":
    sys.exit(main())
