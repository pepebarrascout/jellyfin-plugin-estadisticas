#!/usr/bin/env python3
"""
resolve_item_ids.py
===================

Resuelve y guarda los ItemIds de álbum y artista en la base de datos del plugin
Estadísticas, para las canciones que ya están registradas pero no tienen esos IDs.

Esto es necesario porque a partir de v0.0.0.16 el plugin captura album_item_id
y artist_item_id en cada reproducción, pero las canciones importadas o
reproducidas antes de esa versión no los tienen.

El script lee la BD de Jellyfin (de una copia temporal) para resolver los IDs
de los álbumes y artistas basándose en el ItemId de cada canción.

Uso:
    python3 resolve_item_ids.py \\
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


def main():
    parser = argparse.ArgumentParser(
        description="Resuelve album_item_id y artist_item_id en la BD del plugin.",
    )
    parser.add_argument("--jellyfin-db", required=True, help="Ruta a la BD de Jellyfin")
    parser.add_argument("--plugin-db", required=True, help="Ruta a la BD del plugin")
    parser.add_argument("--dry-run", action="store_true", help="No modificar la BD del plugin")
    parser.add_argument("--no-confirm", action="store_true", help="No pedir confirmación")

    args = parser.parse_args()

    log("=" * 60)
    log("Resolutor de album_item_id y artist_item_id")
    log("=" * 60)
    log(f"BD Jellyfin: {args.jellyfin_db}")
    log(f"BD Plugin:   {args.plugin_db}")
    log(f"Dry run:     {args.dry_run}")

    # Step 1: Copy Jellyfin DB
    temp_db = copy_jellyfin_db(args.jellyfin_db)

    try:
        # Step 2: Build a map of item_id -> (album_item_id, artist_item_id) from Jellyfin
        log("Abriendo copia de BD de Jellyfin (read-only)...")
        jf_conn = sqlite3.connect(f"file:{temp_db}?mode=ro", uri=True)
        jf_conn.row_factory = sqlite3.Row

        # In Jellyfin 10.11, BaseItems has ParentId column.
        # For Audio items:
        #   - ParentId points to the MusicAlbum (the album)
        #   - The album's ParentId points to the MusicArtist (or we can look up
        #     the artist by finding the MusicArtist item with matching name)
        #
        # Strategy:
        # 1. For each Audio item in BaseItems, get its ParentId (= album ItemId)
        # 2. For the album, look up ITS ParentId (= artist folder ItemId)
        #    OR look up the album's AlbumArtists field
        # 3. If that fails, try finding a MusicArtist item whose name matches
        #    the album_artist

        # Build map: audio_item_id -> parent_id (album)
        log("Leyendo items de audio de Jellyfin...")
        cursor = jf_conn.execute("""
            SELECT Id, ParentId, Album, AlbumArtists, Type
            FROM BaseItems
            WHERE Type = 'MediaBrowser.Controller.Entities.Audio.Audio';
        """)
        audio_items = {}
        for row in cursor.fetchall():
            item_id = row["Id"]
            parent_id = row["ParentId"]
            album_name = row["Album"]
            album_artists_raw = row["AlbumArtists"]
            audio_items[item_id] = {
                "parent_id": parent_id,
                "album_name": album_name,
                "album_artists_raw": album_artists_raw,
            }

        log(f"Encontrados {len(audio_items)} items de audio en Jellyfin")

        # Build map: album_item_id -> artist_item_id (by looking up the album's ParentId)
        # First, get all MusicAlbum items with their ParentIds
        # NOTE: BaseItems has 'AlbumArtists' (plural, JSON) not 'AlbumArtist' (singular)
        cursor = jf_conn.execute("""
            SELECT Id, ParentId, Name, AlbumArtists
            FROM BaseItems
            WHERE Type = 'MediaBrowser.Controller.Entities.Audio.MusicAlbum';
        """)
        album_to_artist = {}
        album_info = {}
        for row in cursor.fetchall():
            album_id = row["Id"]
            parent_id = row["ParentId"]
            name = row["Name"]
            album_artists_raw = row["AlbumArtists"]
            album_info[album_id] = {"parent_id": parent_id, "name": name, "album_artists_raw": album_artists_raw}
            if parent_id:
                album_to_artist[album_id] = parent_id

        log(f"Encontrados {len(album_info)} álbumes en Jellyfin")

        # Also build a map of MusicArtist items: name -> Id
        cursor = jf_conn.execute("""
            SELECT Id, Name FROM BaseItems
            WHERE Type = 'MediaBrowser.Controller.Entities.Audio.MusicArtist';
        """)
        artist_by_name = {}
        for row in cursor.fetchall():
            artist_by_name[row["Name"]] = row["Id"]

        log(f"Encontrados {len(artist_by_name)} artistas en Jellyfin")

        # Now resolve: for each audio item, find album_item_id and artist_item_id
        resolved = {}
        for item_id, info in audio_items.items():
            album_id = info["parent_id"]
            artist_id = None

            if album_id and album_id in album_to_artist:
                artist_id = album_to_artist[album_id]

            # Fallback: if artist_id not found via album parent, try by name
            if not artist_id and info["album_artists_raw"]:
                # AlbumArtists in Jellyfin DB is often a JSON array like:
                # [{"Name":"Artist Name","Id":"guid"},...]
                import json
                try:
                    artists = json.loads(info["album_artists_raw"])
                    if isinstance(artists, list) and len(artists) > 0:
                        first_artist_name = artists[0].get("Name", "")
                        if first_artist_name in artist_by_name:
                            artist_id = artist_by_name[first_artist_name]
                except (json.JSONDecodeError, TypeError):
                    pass

            # Another fallback: try matching AlbumArtists JSON field from the album
            if not artist_id and album_id and album_id in album_info:
                aa_raw = album_info[album_id].get("album_artists_raw")
                if aa_raw:
                    import json
                    try:
                        artists_list = json.loads(aa_raw)
                        if isinstance(artists_list, list) and len(artists_list) > 0:
                            first_name = artists_list[0].get("Name", "")
                            if first_name and first_name in artist_by_name:
                                artist_id = artist_by_name[first_name]
                    except (json.JSONDecodeError, TypeError):
                        pass

            resolved[item_id] = {
                "album_item_id": album_id,
                "artist_item_id": artist_id,
            }

        log(f"Resueltos {len(resolved)} items (album_item_id y/o artist_item_id)")

        jf_conn.close()

        # Step 3: Update plugin DB
        if not os.path.exists(args.plugin_db):
            log(f"La BD del plugin no existe: {args.plugin_db}", "ERROR")
            return 1

        log("Abriendo BD del plugin (read-write)...")
        plugin_conn = sqlite3.connect(args.plugin_db)
        plugin_conn.execute("PRAGMA journal_mode=WAL;")

        # Find tracks that need updating
        cursor = plugin_conn.execute("""
            SELECT item_id FROM tracks
            WHERE album_item_id IS NULL OR artist_item_id IS NULL
        """)
        tracks_to_update = [row[0] for row in cursor.fetchall()]
        log(f"Tracks en plugin DB sin album_item_id o artist_item_id: {len(tracks_to_update)}")

        if len(tracks_to_update) == 0:
            log("No hay tracks que actualizar. Todo está al día.")
            plugin_conn.close()
            return 0

        if not args.no_confirm and not args.dry_run:
            print()
            print(f"¿Actualizar {len(tracks_to_update)} tracks con album_item_id y artist_item_id?")
            response = input("Escribe 'si' para continuar: ")
            if response.lower() not in ["si", "sí", "y", "yes"]:
                log("Cancelado.")
                plugin_conn.close()
                return 0

        updated_album = 0
        updated_artist = 0
        not_found = 0

        plugin_conn.execute("BEGIN TRANSACTION;")

        try:
            for item_id in tracks_to_update:
                if item_id not in resolved:
                    not_found += 1
                    continue

                r = resolved[item_id]
                album_id = r["album_item_id"]
                artist_id = r["artist_item_id"]

                if album_id:
                    plugin_conn.execute(
                        "UPDATE tracks SET album_item_id = ? WHERE item_id = ? AND album_item_id IS NULL",
                        (album_id, item_id)
                    )
                    updated_album += 1

                if artist_id:
                    plugin_conn.execute(
                        "UPDATE tracks SET artist_item_id = ? WHERE item_id = ? AND artist_item_id IS NULL",
                        (artist_id, item_id)
                    )
                    updated_artist += 1

            if not args.dry_run:
                plugin_conn.commit()
                log("Commit exitoso.")
            else:
                plugin_conn.rollback()
                log("Dry run — no se guardaron cambios.")
        except Exception as e:
            plugin_conn.rollback()
            log(f"Error: {e}. Rollback.", "ERROR")
            plugin_conn.close()
            return 1

        plugin_conn.close()

        log("=" * 60)
        log("Resolución completada:")
        log(f"  Tracks procesados:          {len(tracks_to_update)}")
        log(f"  album_item_id actualizados: {updated_album}")
        log(f"  artist_item_id actualizados: {updated_artist}")
        log(f"  No encontrados en Jellyfin:  {not_found}")
        if args.dry_run:
            log("  (Dry run — no se guardaron cambios)")
        log("=" * 60)
        return 0

    finally:
        cleanup_temp_db(temp_db)


if __name__ == "__main__":
    sys.exit(main())
