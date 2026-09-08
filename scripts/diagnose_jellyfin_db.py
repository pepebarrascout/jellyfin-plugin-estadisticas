#!/usr/bin/env python3
"""
diagnose_jellyfin_db.py
=======================

Script de diagnóstico: lee la BD de Jellyfin y muestra TODA la información
sobre el esquema (tablas, columnas, tipos) y samples de datos, para que
podamos entender dónde están almacenados los artistas y géneros.

NO modifica nada. Sólo lee y muestra información.

Uso:
    python3 diagnose_jellyfin_db.py --jellyfin-db /ruta/a/jellyfin.db
"""

import argparse
import os
import shutil
import sqlite3
import sys
import tempfile
from datetime import datetime


def log(msg):
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", file=sys.stderr)


def copy_jellyfin_db(jellyfin_db_path):
    if not os.path.exists(jellyfin_db_path):
        raise FileNotFoundError(f"Jellyfin DB not found: {jellyfin_db_path}")
    fd, temp_path = tempfile.mkstemp(suffix=".db", prefix="jellyfin_diag_")
    os.close(fd)
    os.unlink(temp_path)
    log(f"Copiando BD a: {temp_path}")
    shutil.copy2(jellyfin_db_path, temp_path)
    for suffix in ["-wal", "-shm"]:
        src = jellyfin_db_path + suffix
        if os.path.exists(src):
            shutil.copy2(src, temp_path + suffix)
    return temp_path


def main():
    parser = argparse.ArgumentParser(description="Diagnóstico de la BD de Jellyfin")
    parser.add_argument("--jellyfin-db", required=True, help="Ruta a la BD de Jellyfin")
    args = parser.parse_args()

    temp_db = copy_jellyfin_db(args.jellyfin_db)

    try:
        conn = sqlite3.connect(f"file:{temp_db}?mode=ro", uri=True)
        conn.row_factory = sqlite3.Row

        print("=" * 70)
        print("DIAGNÓSTICO DE BD DE JELLYFIN")
        print("=" * 70)

        # 1. Listar todas las tablas
        print("\n1. TABLAS EN LA BD:")
        cursor = conn.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;")
        tables = [row[0] for row in cursor.fetchall()]
        for t in tables:
            print(f"   - {t}")

        # 2. Para cada tabla relevante, mostrar columnas + sample
        relevant_tables = [t for t in tables if any(k in t.lower() for k in
            ["item", "baseitem", "typedbase", "itemvalue", "userdata", "user_data"])]

        print(f"\n2. TABLAS RELEVANTES ({len(relevant_tables)}):")
        for t in relevant_tables:
            print(f"\n   --- {t} ---")
            # Columnas
            cursor = conn.execute(f"PRAGMA table_info({t});")
            cols = cursor.fetchall()
            print(f"   Columnas ({len(cols)}):")
            for c in cols:
                print(f"     {c['name']} ({c['type']})")

            # Row count
            try:
                cursor = conn.execute(f"SELECT COUNT(*) FROM {t};")
                count = cursor.fetchone()[0]
                print(f"   Filas totales: {count}")
            except:
                pass

            # Sample (first 3 rows, all columns)
            try:
                cursor = conn.execute(f"SELECT * FROM {t} LIMIT 3;")
                rows = cursor.fetchall()
                if rows:
                    print(f"   Sample (primeras 3 filas):")
                    for i, row in enumerate(rows):
                        print(f"     Fila {i+1}:")
                        for key in row.keys():
                            val = row[key]
                            if val is not None and len(str(val)) > 200:
                                val = str(val)[:200] + "..."
                            print(f"       {key} = {repr(val)}")
                else:
                    print(f"   (tabla vacía)")
            except Exception as e:
                print(f"   Error leyendo sample: {e}")

        # 3. Buscar específicamente la tabla de items (TypedBaseItems/BaseItems)
        items_table = None
        for t in ["TypedBaseItems", "BaseItems"]:
            if t in tables:
                items_table = t
                break

        if items_table:
            print(f"\n3. ANÁLISIS DE {items_table}:")

            # Buscar items de tipo Audio
            cursor = conn.execute(f"PRAGMA table_info({items_table});")
            cols = {c['name'] for c in cursor.fetchall()}

            type_col = None
            for c in ["Type", "type"]:
                if c in cols:
                    type_col = c
                    break

            if type_col:
                # Contar items por tipo
                print(f"\n   Items por tipo (columna {type_col}):")
                cursor = conn.execute(f"""
                    SELECT "{type_col}", COUNT(*) as cnt
                    FROM {items_table}
                    GROUP BY "{type_col}"
                    ORDER BY cnt DESC
                    LIMIT 20;
                """)
                for row in cursor.fetchall():
                    print(f"     {row[0]}: {row[1]}")

                # Sample de un item de Audio
                print(f"\n   Sample de item de Audio (primer item):")
                cursor = conn.execute(f"""
                    SELECT * FROM {items_table}
                    WHERE "{type_col}" LIKE '%Audio%'
                    LIMIT 1;
                """)
                row = cursor.fetchone()
                if row:
                    for key in row.keys():
                        val = row[key]
                        if val is not None and len(str(val)) > 500:
                            val = str(val)[:500] + "..."
                        print(f"     {key} = {repr(val)}")

        # 4. Buscar tabla de ItemValues
        iv_table = None
        for t in ["ItemValues", "ItemValuesExtra"]:
            if t in tables:
                iv_table = t
                break

        if iv_table:
            print(f"\n4. ANÁLISIS DE {iv_table}:")

            cursor = conn.execute(f"PRAGMA table_info({iv_table});")
            cols = {c['name'] for c in cursor.fetchall()}
            print(f"   Columnas: {sorted(cols)}")

            # Contar por tipo
            type_col = None
            for c in ["Type", "type"]:
                if c in cols:
                    type_col = c
                    break

            if type_col:
                print(f"\n   Distribución por {type_col}:")
                cursor = conn.execute(f"""
                    SELECT "{type_col}", COUNT(*) as cnt
                    FROM {iv_table}
                    GROUP BY "{type_col}"
                    ORDER BY "{type_col}";
                """)
                for row in cursor.fetchall():
                    print(f"     Type {row[0]}: {row[1]} valores")

                # Sample de cada tipo
                print(f"\n   Samples por tipo (5 valores de cada uno):")
                cursor = conn.execute(f"""
                    SELECT DISTINCT "{type_col}" FROM {iv_table} ORDER BY "{type_col}";
                """)
                type_values = [row[0] for row in cursor.fetchall()]

                value_col = None
                for c in ["Value", "value"]:
                    if c in cols:
                        value_col = c
                        break

                if value_col:
                    for tv in type_values:
                        cursor = conn.execute(f"""
                            SELECT "{value_col}" FROM {iv_table}
                            WHERE "{type_col}" = ?
                            LIMIT 5;
                        """, (tv,))
                        samples = [row[0] for row in cursor.fetchall()]
                        print(f"     Type {tv}: {samples}")

        # 5. Buscar si hay columna "Data" en items table (JSON serializado)
        if items_table and "Data" in cols:
            print(f"\n5. ANÁLISIS DE COLUMNA 'Data' en {items_table}:")
            cursor = conn.execute(f"""
                SELECT "Data" FROM {items_table}
                WHERE "Data" IS NOT NULL AND "Data" != ''
                LIMIT 1;
            """)
            row = cursor.fetchone()
            if row and row[0]:
                data = row[0]
                if len(data) > 2000:
                    print(f"   Sample (primeros 2000 chars):")
                    print(f"   {data[:2000]}...")
                else:
                    print(f"   Contenido completo:")
                    print(f"   {data}")

                # Intentar parsear como JSON
                import json
                try:
                    obj = json.loads(data)
                    print(f"\n   Claves del JSON: {list(obj.keys())[:30]}")
                    # Buscar campos de artistas/géneros
                    for key in obj:
                        if any(k in key.lower() for k in ["artist", "genre", "album"]):
                            val = obj[key]
                            if len(str(val)) > 200:
                                val = str(val)[:200] + "..."
                            print(f"     {key} = {repr(val)}")
                except:
                    print(f"   (no es JSON válido)")
            else:
                print("   (columna Data vacía o NULL)")

        # 6. Buscar UserData / ItemUserData
        ud_table = None
        for t in ["ItemUserData", "UserDatas", "UserData"]:
            if t in tables:
                ud_table = t
                break

        if ud_table:
            print(f"\n6. ANÁLISIS DE {ud_table} (user data):")
            cursor = conn.execute(f"PRAGMA table_info({ud_table});")
            cols = {c['name'] for c in cursor.fetchall()}
            print(f"   Columnas: {sorted(cols)}")

            # Contar filas con PlayCount > 0
            pc_col = None
            for c in ["PlayCount", "play_count"]:
                if c in cols:
                    pc_col = c
                    break
            if pc_col:
                cursor = conn.execute(f"""
                    SELECT COUNT(*) FROM {ud_table}
                    WHERE CAST("{pc_col}" AS INTEGER) > 0;
                """)
                print(f"   Filas con PlayCount > 0: {cursor.fetchone()[0]}")

                # Sample
                cursor = conn.execute(f"""
                    SELECT * FROM {ud_table}
                    WHERE CAST("{pc_col}" AS INTEGER) > 0
                    LIMIT 2;
                """)
                for row in cursor.fetchall():
                    print(f"   Sample: {dict(row)}")

        print("\n" + "=" * 70)
        print("DIAGNÓSTICO COMPLETADO")
        print("=" * 70)
        print("\nCopia toda esta salida y pégala en el chat para que pueda ver")
        print("exactamente cómo está estructurada tu BD de Jellyfin.")

        conn.close()
        return 0

    finally:
        # Cleanup
        for suffix in ["", "-wal", "-shm"]:
            path = temp_db + suffix
            if os.path.exists(path):
                os.unlink(path)


if __name__ == "__main__":
    sys.exit(main())
