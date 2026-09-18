#!/usr/bin/env bash
set -euo pipefail
password=$(openssl rand -hex 32)
echo "::add-mask::$password"
case "$PROVIDER" in
  PostgreSQL)
    docker run -d --name remvora-db -p 127.0.0.1:5432:5432 -e POSTGRES_USER=remvora -e POSTGRES_DB=remvora -e POSTGRES_PASSWORD="$password" postgres:17 >/dev/null
    connection="Host=127.0.0.1;Database=remvora;Username=remvora;Password=$password"
    ready='pg_isready -U remvora'
    ;;
  MySQL|MariaDB)
    image=mysql:8.4
    if [ "$PROVIDER" = MariaDB ]; then image=mariadb:11.4; fi
    docker run -d --name remvora-db -p 127.0.0.1:3306:3306 -e MYSQL_DATABASE=remvora -e MYSQL_USER=remvora -e MYSQL_PASSWORD="$password" -e MYSQL_ROOT_PASSWORD="$password" "$image" >/dev/null
    connection="Server=127.0.0.1;Database=remvora;User ID=remvora;Password=$password"
    ready='mysqladmin ping --silent'
    if [ "$PROVIDER" = MariaDB ]; then ready='mariadb-admin ping --silent'; fi
    ;;
  *) exit 1 ;;
esac
for attempt in $(seq 1 60); do
  if docker exec remvora-db sh -c "$ready" >/dev/null 2>&1; then break; fi
  if [ "$attempt" = 60 ]; then echo 'Database readiness timeout'; exit 1; fi
  sleep 2
done
echo "REMVORA_TEST_PROVIDER=$PROVIDER" >> "$GITHUB_ENV"
echo "REMVORA_TEST_DATABASE=$connection" >> "$GITHUB_ENV"
