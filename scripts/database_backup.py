#!/usr/bin/env python3
"""Native logical backup/restore. Credentials come from PG* env or a protected MySQL option file."""
import argparse
import os
from pathlib import Path
import re
import subprocess


def run(args, **kwargs):
    result = subprocess.run(args, stderr=subprocess.PIPE, **kwargs)
    if result.returncode:
        raise RuntimeError('Database command failed; check connectivity, privileges and client version (details withheld to protect secrets)')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['backup', 'restore'])
    parser.add_argument('--provider', required=True, choices=['PostgreSQL', 'MySQL', 'MariaDB'])
    parser.add_argument('--database', required=True)
    parser.add_argument('--file', required=True, type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]{0,62}', args.database):
        parser.error('Use a simple database identifier')
    path = args.file.expanduser().absolute()
    if path.is_symlink():
        parser.error('Backup path must not be a symlink')
    if args.provider == 'PostgreSQL':
        client = [os.environ.get('REMVORA_PSQL', 'psql'), '-X', '--no-password', '--set=ON_ERROR_STOP=1', '--dbname', args.database]
        dump = [os.environ.get('REMVORA_PG_DUMP', 'pg_dump'), '--no-password', '--format=custom', '--no-owner', '--no-acl', '--dbname', args.database]
        query = client + ['-Atc', "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema')"]
        restore = [os.environ.get('REMVORA_PG_RESTORE', 'pg_restore'), '--no-password', '--exit-on-error', '--single-transaction', '--no-owner', '--no-acl', '--dbname', args.database, str(path)]
    else:
        defaults = Path(os.environ['REMVORA_DB_DEFAULTS_FILE']).resolve(strict=True)
        if os.name != 'nt' and defaults.stat().st_mode & 0o077:
            parser.error('Database credentials file must be mode 0600')
        name = 'mariadb' if args.provider == 'MariaDB' else 'mysql'
        client = [os.environ.get('REMVORA_MYSQL', name), '--defaults-extra-file='+str(defaults), '--batch', '--skip-column-names', args.database]
        dump = [os.environ.get('REMVORA_MYSQL_DUMP', name+'dump' if name == 'mysql' else 'mariadb-dump'), '--defaults-extra-file='+str(defaults), '--single-transaction', '--skip-lock-tables', '--no-tablespaces', args.database]
        query = client + ['-e', 'SELECT count(*) FROM information_schema.tables WHERE table_schema=DATABASE()']
        restore = client
    if args.action == 'backup':
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        # Exclusive creation protects prior backups, including against a concurrent invocation.
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        try:
            with os.fdopen(descriptor, 'wb') as output:
                run(dump, stdout=output)
                output.flush(); os.fsync(output.fileno())
        except BaseException:
            path.unlink(missing_ok=True)
            raise
        print('Backup created. Encrypt and copy it to your service-specific backup storage.')
    else:
        if not path.is_file():
            parser.error('Backup file does not exist')
        count = run(query, capture_output=False, stdout=subprocess.PIPE).stdout.decode().strip()
        if count != '0':
            parser.error('Restore requires an existing EMPTY database; refusing to overwrite data')
        with path.open('rb') as source:
            run(restore, stdin=source if args.provider != 'PostgreSQL' else subprocess.DEVNULL, stdout=subprocess.DEVNULL)
        print('Restore completed. Verify migrations, row counts and application acceptance before cutover.')


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, KeyError, OSError) as error:
        raise SystemExit(str(error)) from None
