#!/usr/bin/env python3
"""
repair_import.py
================

Script de reparación para corregir problemas detectados tras la importación
inicial del historial de Jellyfin. Se ejecuta UNA SOLA VEZ, manualmente,
después de haber corrido import_jellyfin_history.py.

Problemas que corrige:
1. Artistas y géneros faltantes: re-lee la BD de Jellyfin y popula
   track_artists y track_genres para todas las canciones que tengan plays
   pero no tengan artistas/géneros.
2. (Opcional) Plays duplicadas: si se importó en modo additive y hay
   duplicados, puede reducir las plays importadas al count correcto.

Uso:
    python3 repair_import.py \\
        --jellyfin-db /ruta/a/jellyfin.db \\
        --plugin-db  /ruta/a/estadisticas.db \\
        --dry-run

Requisitos: Python 3 + módulo sqlite3 (incluido por defecto).
"""

import argparse
import os
import shutil
import sqlite3
import sys
import tempfile
from datetime import datetime


def log(msg, level="INFO"):
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] [{level}] {msg}", file=sys.stderr)


def copy_jellyfin_db(jellyfin_db_path):
    if not os.path.exists(jellyfin_db_path):
        raise FileNotFoundError(f"Jellyfin DB not found: {jellyfin_db_path}")
    fd, temp_path = tempfile.mkstemp(suffix=".db", prefix="jellyfin_copy_")
    os.close(fd)
    os.unlink(temp_path)
    log(f"Copiando BD de Jellyfin a: {temp_path}")
    shutil.copy2(jellyfin_db_path, temp_path)
    for suffix in ["-wal", "-shm"]:
        src = jellyfin_db_path + suffix
        if os.path.exists(src):
            shutil.copy2(src, temp_path + suffix)
    return temp_path


def cleanup_temp_db(temp_path):
    for suffix in ["", "-wal", "-shm"]:
        path = temp_path + suffix
        if os.path.exists(path):
            os.unlink(path)


def get_columns(conn, table_name):
    cursor = conn.execute(f"PRAGMA table_info({table_name});")
    return {row[1] for row in cursor.fetchall()}


def read_jellyfin_metadata(conn):
    """
    Read ALL audio items from Jellyfin DB and extract:
    - item_id (guid)
    - artists (from ItemValues where type indicates artist, OR from the
      Artists field in TypedBaseItems if it's a JSON/pipe-separated string)
    - genres (from ItemValues where type indicates genre, OR from Genres field)
    - album_artist (from TypedBaseItems)

    Jellyfin 10.11 stores artists and genres in the ItemValues table, but the
    Type column uses integer values:
      0 = Genre
      1 = Artist
      2 = AlbumArtist
      3 = Studio
      ...
    (This varies by version, so we try multiple heuristics.)

    Also, in some Jellyfin versions, the TypedBaseItems table has a "Data"
    column with serialized JSON that includes Artists and Genres arrays.
    We try that as a fallback.
    """
    # Find items table
    cursor = conn.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;")
    tables = {row[0] for row in cursor.fetchall()}

    items_table = None
    for candidate in ["TypedBaseItems", "BaseItems"]:
        if candidate in tables:
            items_table = candidate
            break
    if not items_table:
        raise RuntimeError("No se encontró tabla de items en Jellyfin DB")

    items_cols = get_columns(conn, items_table)
    log(f"Tabla de items: {items_table}")
    log(f"Columnas disponibles: {sorted(items_cols)}")

    # Map guid column
    guid_col = None
    for c in ["Guid", "guid", "Id"]:
        if c in items_cols:
            guid_col = c
            break
    if not guid_col:
        raise RuntimeError("No se encontró columna GUID")

    # Check for Data column (serialized JSON with Artists/Genres)
    has_data = "Data" in items_cols
    if has_data:
        log("Encontrada columna 'Data' (metadata serializada)")

    # Build query to get all audio items with their GUID
    type_col = None
    for c in ["Type", "type", "item_type"]:
        if c in items_cols:
            type_col = c
            break

    type_filter = ""
    if type_col:
        type_filter = f'WHERE "{type_col}" LIKE "%Audio%"'

    query = f'SELECT "{guid_col}" FROM {items_table} {type_filter};'
    log(f"Obteniendo GUIDs de items de audio...")

    cursor = conn.execute(query)
    audio_guids = set()
    for row in cursor.fetchall():
        if row[0]:
            audio_guids.add(str(row[0]).strip())

    log(f"Encontrados {len(audio_guids)} items de audio")

    # Now try to read artists and genres from ItemValues
    iv_table = None
    for candidate in ["ItemValues", "ItemValuesExtra"]:
        if candidate in tables:
            iv_table = candidate
            break

    metadata = {}  # item_id -> {artists: [], genres: [], album_artist: None}

    if iv_table:
        iv_cols = get_columns(conn, iv_table)
        log(f"Tabla ItemValues: {iv_table}, columnas: {sorted(iv_cols)}")

        # Map columns
        iv_type_col = None
        for c in ["Type", "type"]:
            if c in iv_cols:
                iv_type_col = c
                break
        iv_value_col = None
        for c in ["Value", "value"]:
            if c in iv_cols:
                iv_value_col = c
                break
        iv_item_col = None
        for c in ["ItemId", "item_id"]:
            if c in iv_cols:
                iv_item_col = c
                break
        iv_ivid_col = None
        for c in ["ItemValueId", "item_value_id"]:
            if c in iv_cols:
                iv_ivid_col = c
                break

        # Check if ItemValuesMap junction table exists (Jellyfin 10.11)
        cursor = conn.execute("SELECT name FROM sqlite_master WHERE type='table' AND name = 'ItemValuesMap';")
        has_iv_map = cursor.fetchone() is not None

        if has_iv_map and iv_ivid_col:
            # Jellyfin 10.11 normalized schema: JOIN ItemValuesMap + ItemValues
            log("Usando ItemValuesMap (esquema normalizado de Jellyfin 10.11)")
            iv_map_cols = get_columns(conn, "ItemValuesMap")
            ivm_item_col = None
            for c in ["ItemId", "item_id"]:
                if c in iv_map_cols:
                    ivm_item_col = c
                    break
            ivm_ivid_col = None
            for c in ["ItemValueId", "item_value_id"]:
                if c in iv_map_cols:
                    ivm_ivid_col = c
                    break

            if iv_value_col and iv_type_col and ivm_item_col and ivm_ivid_col:
                # Jellyfin 10.11 ItemValueType: Artist=0, AlbumArtist=1, Genre=2
                log("Leyendo géneros (Type=2) vía ItemValuesMap...")
                cursor = conn.execute(f"""
                    SELECT iv."{iv_value_col}" AS value, ivm."{ivm_item_col}" AS item_id
                    FROM ItemValuesMap ivm
                    JOIN {iv_table} iv ON iv."{iv_ivid_col}" = ivm."{ivm_ivid_col}"
                    WHERE iv."{iv_type_col}" = 2;
                """)
                genre_count = 0
                for row in cursor.fetchall():
                    genre = row[0]
                    item_id = str(row[1]).strip() if row[1] else ""
                    if genre and item_id in audio_guids:
                        if item_id not in metadata:
                            metadata[item_id] = {"artists": [], "genres": [], "album_artist": None}
                        metadata[item_id]["genres"].append(genre)
                        genre_count += 1
                log(f"Encontradas {genre_count} asignaciones de género")

                log("Leyendo artistas (Type=0,1) vía ItemValuesMap...")
                cursor = conn.execute(f"""
                    SELECT iv."{iv_value_col}" AS value, ivm."{ivm_item_col}" AS item_id, iv."{iv_type_col}" AS vtype
                    FROM ItemValuesMap ivm
                    JOIN {iv_table} iv ON iv."{iv_ivid_col}" = ivm."{ivm_ivid_col}"
                    WHERE iv."{iv_type_col}" IN (0, 1);
                """)
                artist_count = 0
                for row in cursor.fetchall():
                    artist = row[0]
                    item_id = str(row[1]).strip() if row[1] else ""
                    type_val = row[2]
                    if artist and item_id in audio_guids:
                        if item_id not in metadata:
                            metadata[item_id] = {"artists": [], "genres": [], "album_artist": None}
                        metadata[item_id]["artists"].append(artist)
                        if type_val in (1, "1"):
                            metadata[item_id]["album_artist"] = artist
                        artist_count += 1
                log(f"Encontradas {artist_count} asignaciones de artista")

        elif iv_value_col and iv_item_col:
            # Older Jellyfin: ItemValues has ItemId directly
            log("ItemValuesMap no existe. Usando ItemValues con ItemId directo (esquema antiguo)")
            log("Leyendo ItemValues (todos los tipos)...")
            cursor = conn.execute(f"""
                SELECT "{iv_item_col}", "{iv_value_col}", "{iv_type_col}"
                FROM {iv_table}
                WHERE "{iv_item_col}" IS NOT NULL AND "{iv_value_col}" IS NOT NULL;
            """)
            iv_rows = cursor.fetchall()
            log(f"ItemValues: {len(iv_rows)} filas totales")

            # Jellyfin 10.11 mapping: Artist=0, AlbumArtist=1, Genre=2
            for row in iv_rows:
                item_id = str(row[0]).strip() if row[0] else ""
                value = row[1]
                type_val = row[2]

                if not item_id or not value or item_id not in audio_guids:
                    continue

                if item_id not in metadata:
                    metadata[item_id] = {"artists": [], "genres": [], "album_artist": None}

                if type_val in (2, "2", "Genre", "genre"):
                    metadata[item_id]["genres"].append(value)
                elif type_val in (0, "0", "Artist", "artist"):
                    metadata[item_id]["artists"].append(value)
                elif type_val in (1, "1", "AlbumArtist", "album_artist"):
                    metadata[item_id]["artists"].append(value)
                    metadata[item_id]["album_artist"] = value

            log(f"Metadata extraída para {len(metadata)} items")

    # Approach 3: try reading from Data column (serialized JSON)
    if has_data and len(metadata) < len(audio_guids) * 0.5:
        log("Intentando leer metadata desde columna 'Data' (JSON)...", "WARN")
        import json

        query = f'SELECT "{guid_col}", "Data" FROM {items_table} {type_filter};'
        cursor = conn.execute(query)
        data_count = 0
        for row in cursor.fetchall():
            item_id = str(row[0]).strip() if row[0] else ""
            data = row[1]
            if not item_id or not data or item_id not in audio_guids:
                continue

            try:
                # Data is a JSON blob with fields like "Artists", "Genres", "AlbumArtist"
                obj = json.loads(data)
                if item_id not in metadata:
                    metadata[item_id] = {"artists": [], "genres": [], "album_artist": None}

                if "Artists" in obj and isinstance(obj["Artists"], list):
                    metadata[item_id]["artists"].extend(obj["Artists"])
                if "ArtistItems" in obj and isinstance(obj["ArtistItems"], list):
                    for a in obj["ArtistItems"]:
                        if isinstance(a, dict) and "Name" in a:
                            metadata[item_id]["artists"].append(a["Name"])
                if "Genres" in obj and isinstance(obj["Genres"], list):
                    metadata[item_id]["genres"].extend(obj["Genres"])
                if "AlbumArtist" in obj and obj["AlbumArtist"]:
                    metadata[item_id]["album_artist"] = obj["AlbumArtist"]
                if "AlbumArtists" in obj and isinstance(obj["AlbumArtists"], list):
                    for a in obj["AlbumArtists"]:
                        if isinstance(a, dict) and "Name" in a:
                            metadata[item_id]["artists"].append(a["Name"])
                            if not metadata[item_id]["album_artist"]:
                                metadata[item_id]["album_artist"] = a["Name"]

                if metadata[item_id]["artists"] or metadata[item_id]["genres"]:
                    data_count += 1
            except (json.JSONDecodeError, TypeError):
                pass

        log(f"Approach 3 (Data JSON): enriquecida metadata para {data_count} items adicionales")

    # Deduplicate artists and genres per item
    for item_id, m in metadata.items():
        m["artists"] = list(dict.fromkeys(m["artists"]))  # preserve order, remove dups
        m["genres"] = list(dict.fromkeys(m["genres"]))

    log(f"Metadata final: {len(metadata)} items con datos")
    return metadata


def write_metadata_to_plugin(plugin_conn, metadata, dry_run=False):
    """
    Populate track_artists and track_genres for items that are in the plugin DB
    but don't have artists/genres yet. Also update album_artist on tracks.
    """
    # Find which items in the plugin DB need metadata
    cursor = plugin_conn.execute("""
        SELECT t.item_id, t.name, t.album_artist
        FROM tracks t
        WHERE NOT EXISTS (SELECT 1 FROM track_artists ta WHERE ta.item_id = t.item_id)
    """)
    items_needing_metadata = cursor.fetchall()
    log(f"Items en plugin DB sin artistas: {len(items_needing_metadata)}")

    cursor = plugin_conn.execute("""
        SELECT t.item_id
        FROM tracks t
        WHERE NOT EXISTS (SELECT 1 FROM track_genres tg WHERE tg.item_id = t.item_id)
    """)
    items_needing_genres = [row[0] for row in cursor.fetchall()]
    log(f"Items en plugin DB sin géneros: {len(items_needing_genres)}")

    artists_inserted = 0
    genres_inserted = 0
    album_artists_updated = 0
    items_not_found_in_jellyfin = 0

    all_items_needing = set()
    for row in items_needing_metadata:
        all_items_needing.add(row[0])
    for item_id in items_needing_genres:
        all_items_needing.add(item_id)

    for item_id in all_items_needing:
        if item_id not in metadata:
            items_not_found_in_jellyfin += 1
            continue

        m = metadata[item_id]
        artists = m["artists"]
        genres = m["genres"]
        album_artist = m["album_artist"]

        # Insert artists
        for artist in artists:
            if artist and artist.strip():
                if not dry_run:
                    plugin_conn.execute(
                        "INSERT OR IGNORE INTO track_artists (item_id, artist) VALUES (?, ?)",
                        (item_id, artist.strip())
                    )
                artists_inserted += 1

        # Insert genres
        for genre in genres:
            if genre and genre.strip():
                if not dry_run:
                    plugin_conn.execute(
                        "INSERT OR IGNORE INTO track_genres (item_id, genre) VALUES (?, ?)",
                        (item_id, genre.strip())
                    )
                genres_inserted += 1

        # Update album_artist on tracks if missing
        if album_artist and not dry_run:
            plugin_conn.execute("""
                UPDATE tracks SET album_artist = ? WHERE item_id = ? AND (album_artist IS NULL OR album_artist = '')
            """, (album_artist, item_id))
            album_artists_updated += 1

    log(f"Artistas insertados: {artists_inserted}")
    log(f"Géneros insertados: {genres_inserted}")
    log(f"Album artists actualizados: {album_artists_updated}")
    log(f"Items no encontrados en Jellyfin: {items_not_found_in_jellyfin}")

    return {
        "artists_inserted": artists_inserted,
        "genres_inserted": genres_inserted,
        "album_artists_updated": album_artists_updated,
        "items_not_found": items_not_found_in_jellyfin,
    }


def dedup_imported_plays(plugin_conn, dry_run=False):
    """
    Remove duplicate imported plays. Keeps only 1 play per (item_id, date) pair
    for imported plays. This helps when the import ran twice or when additive
    mode over-counted.

    Strategy: for plays with user_id='imported-from-jellyfin', keep only the
    first occurrence of each (item_id, DATE(played_at)) pair.
    """
    log("Verificando plays importadas duplicadas...")

    # Count imported plays
    cursor = plugin_conn.execute(
        "SELECT COUNT(*) FROM plays WHERE user_id = 'imported-from-jellyfin'"
    )
    total_imported = cursor.fetchone()[0]
    log(f"Plays importadas totales: {total_imported}")

    # Find duplicates: same item_id + same date (YYYY-MM-DD)
    cursor = plugin_conn.execute("""
        SELECT item_id, substr(played_at, 1, 10) as play_date, COUNT(*) as cnt
        FROM plays
        WHERE user_id = 'imported-from-jellyfin'
        GROUP BY item_id, play_date
        HAVING cnt > 1
    """)
    dup_groups = cursor.fetchall()
    log(f"Grupos de duplicados (mismo item + misma fecha): {len(dup_groups)}")

    if not dup_groups:
        log("No hay duplicados que corregir.")
        return {"duplicates_removed": 0}

    total_to_remove = sum(g[2] - 1 for g in dup_groups)
    log(f"Plays duplicadas a eliminar: {total_to_remove}")

    if dry_run:
        return {"duplicates_removed": total_to_remove}

    # For each duplicate group, keep the MIN(id) and delete the rest
    removed = 0
    for item_id, play_date, cnt in dup_groups:
        cursor = plugin_conn.execute("""
            SELECT id FROM plays
            WHERE user_id = 'imported-from-jellyfin'
              AND item_id = ?
              AND substr(played_at, 1, 10) = ?
            ORDER BY id ASC
        """, (item_id, play_date))
        ids = [row[0] for row in cursor.fetchall()]
        # Keep first, delete rest
        ids_to_delete = ids[1:]
        if ids_to_delete:
            placeholders = ",".join("?" * len(ids_to_delete))
            plugin_conn.execute(
                f"DELETE FROM plays WHERE id IN ({placeholders})",
                ids_to_delete
            )
            removed += len(ids_to_delete)

    log(f"Plays duplicadas eliminadas: {removed}")
    return {"duplicates_removed": removed}


def main():
    parser = argparse.ArgumentParser(
        description="Repara la importación: re-lee metadata de Jellyfin y corrige duplicados.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--jellyfin-db", required=True, help="Ruta a la BD de Jellyfin")
    parser.add_argument("--plugin-db", required=True, help="Ruta a la BD del plugin")
    parser.add_argument("--dry-run", action="store_true", help="No modificar la BD del plugin")
    parser.add_argument("--no-confirm", action="store_true", help="No pedir confirmación")
    parser.add_argument("--skip-dedup", action="store_true", help="No corregir duplicados de plays")
    parser.add_argument("--skip-metadata", action="store_true", help="No re-importar metadata de artistas/géneros")

    args = parser.parse_args()

    log("=" * 60)
    log("Script de reparación de importación")
    log("=" * 60)
    log(f"BD Jellyfin: {args.jellyfin_db}")
    log(f"BD Plugin:   {args.plugin_db}")
    log(f"Dry run:     {args.dry_run}")

    temp_db = copy_jellyfin_db(args.jellyfin_db)

    try:
        # Read metadata from Jellyfin
        if not args.skip_metadata:
            log("Abriendo copia de BD de Jellyfin (read-only)...")
            jf_conn = sqlite3.connect(f"file:{temp_db}?mode=ro", uri=True)
            metadata = read_jellyfin_metadata(jf_conn)
            jf_conn.close()
        else:
            metadata = {}

        # Open plugin DB
        if not os.path.exists(args.plugin_db):
            log(f"La BD del plugin no existe: {args.plugin_db}", "ERROR")
            return 1

        log("Abriendo BD del plugin (read-write)...")
        plugin_conn = sqlite3.connect(args.plugin_db)
        plugin_conn.execute("PRAGMA journal_mode=WAL;")
        plugin_conn.execute("PRAGMA foreign_keys=ON;")

        if not args.no_confirm and not args.dry_run:
            print()
            print("¿Proceder con la reparación?")
            response = input("Escribe 'si' para continuar: ")
            if response.lower() not in ["si", "sí", "y", "yes"]:
                log("Cancelado.")
                return 0

        plugin_conn.execute("BEGIN TRANSACTION;")

        try:
            stats = {}
            if not args.skip_metadata and metadata:
                log("Escribiendo metadata (artistas/géneros) en la BD del plugin...")
                stats["metadata"] = write_metadata_to_plugin(plugin_conn, metadata, args.dry_run)

            if not args.skip_dedup:
                log("Corrigiendo plays duplicadas...")
                stats["dedup"] = dedup_imported_plays(plugin_conn, args.dry_run)

            if not args.dry_run:
                plugin_conn.commit()
                log("Commit exitoso.")
            else:
                plugin_conn.rollback()
                log("Dry run — no se guardaron cambios.")
        except Exception as e:
            plugin_conn.rollback()
            log(f"Error: {e}. Rollback.", "ERROR")
            import traceback
            traceback.print_exc()
            plugin_conn.close()
            return 1

        plugin_conn.close()

        log("=" * 60)
        log("Reparación completada:")
        if "metadata" in stats:
            m = stats["metadata"]
            log(f"  Artistas insertados:        {m['artists_inserted']}")
            log(f"  Géneros insertados:         {m['genres_inserted']}")
            log(f"  Album artists actualizados: {m['album_artists_updated']}")
            log(f"  Items no encontrados:       {m['items_not_found']}")
        if "dedup" in stats:
            log(f"  Plays duplicadas eliminadas: {stats['dedup']['duplicates_removed']}")
        log("=" * 60)
        return 0

    finally:
        cleanup_temp_db(temp_db)


if __name__ == "__main__":
    sys.exit(main())
