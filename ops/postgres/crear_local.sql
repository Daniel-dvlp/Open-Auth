-- Prepara un PostgreSQL local (Windows) para correr Escudo y Billetera fuera de Docker.
-- Ejecutar como superusuario:  psql -h localhost -U postgres -d postgres -f ops/postgres/crear_local.sql
-- Idempotente: se puede correr varias veces. Credenciales SOLO de desarrollo (appsettings.Development.json).
\set ON_ERROR_STOP on

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'escudo') THEN
    CREATE ROLE escudo LOGIN PASSWORD 'escudo_dev';
  ELSE
    ALTER ROLE escudo LOGIN PASSWORD 'escudo_dev';
  END IF;
END $$;

SELECT 'CREATE DATABASE escudo_yuma OWNER escudo ENCODING ''UTF8'' TEMPLATE template0'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'escudo_yuma') \gexec

SELECT 'CREATE DATABASE yuma_billetera OWNER escudo ENCODING ''UTF8'' TEMPLATE template0'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'yuma_billetera') \gexec

SELECT datname, pg_get_userbyid(datdba) AS dueno FROM pg_database WHERE datname IN ('escudo_yuma', 'yuma_billetera');
