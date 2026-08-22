-- =====================================================================
-- Principales de seguridad del clúster — ESPECIFICACIÓN NORMATIVA
-- =====================================================================
--
-- Este fichero es la ÚNICA definición de qué roles debe tener un clúster
-- de Hydra y con qué atributos. CI, desarrollo, VPS, ensayo de
-- restauración y el arnés de tests son adaptadores que lo EJECUTAN; no
-- redefinen nada. Si los atributos se duplicaran en un workflow, en un
-- compose o en un runbook, acabarían divergiendo.
--
-- ---------------------------------------------------------------------
-- POR QUÉ EXISTE ESTE FICHERO
-- ---------------------------------------------------------------------
-- Los roles son objetos de CLÚSTER: viven en pg_authid, que es un
-- catálogo compartido por todas las bases. Crearlos desde una migración
-- de una base es un error de nivel, y costó caro:
--
--   El 42704 "role cae_app_soporte does not exist" apareció tres veces
--   en CI. La traza instrumentada (PR #251) lo dejó reconstruido: seis
--   migradores entran en RolSoporteSoloLectura en 9 ms; 125 ms después
--   tres fallan DENTRO de su propio bloque DO, en la sentencia posterior
--   al CREATE ROLE protegido, y tres pasan.
--
-- Es decir: tragarse el duplicate_object NO garantiza que el rol exista
-- y sea utilizable en la sentencia siguiente del mismo bloque. La
-- reparación no es proteger mejor esa sentencia — es que nadie compita
-- por crear un objeto que debería existir antes de que arranque el
-- primer migrador.
--
-- ---------------------------------------------------------------------
-- CONTRATO OPERACIONAL — leer antes de tocar nada
-- ---------------------------------------------------------------------
-- La idempotencia que este script garantiza es sobre REEJECUCIÓN:
--
--     ejecución 1  ->  crea o converge
--     ejecución 2  ->  ya correcto, no cambia nada
--
-- y NO sobre concurrencia:
--
--     ejecución 1 + ejecución 2 SIMULTÁNEAS  ->  fuera de contrato
--
-- La seguridad de este script depende de que sea un paso de
-- inicialización EXCLUSIVO, ejecutado por un único proceso antes de que
-- ninguna migración arranque. No depende de proteger cada sentencia
-- contra carreras — ese era precisamente el patrón que falló.
--
-- En concreto, el ALTER ROLE de más abajo reventaría con 42704 si otro
-- proceso estuviera creando el rol al mismo tiempo. No se blinda a
-- propósito: blindarlo daría la falsa impresión de que el script es
-- seguro bajo concurrencia, y no lo es ni tiene que serlo.
--
-- ---------------------------------------------------------------------
-- QUÉ NO VA AQUÍ
-- ---------------------------------------------------------------------
-- Los privilegios sobre objetos de una base —GRANT sobre esquemas,
-- tablas y secuencias, y ALTER DEFAULT PRIVILEGES— siguen siendo
-- responsabilidad de las migraciones de cada base, porque esos objetos
-- sí son por base. Aquí solo van los principales y sus atributos.
--
-- Tampoco es un cajón de "roles varios": un test de arquitectura fija
-- que este fichero contiene exactamente los dos principales del contrato
-- y ningún tercero.
-- =====================================================================


-- cae_app_runtime — el rol con el que la aplicación conectará cuando se
-- complete la rotación pendiente. NOBYPASSRLS es lo que mantiene la
-- garantía de aislamiento por encima de cualquier otra: un rol que
-- pudiera saltarse RLS haría inútiles las políticas.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_runtime') THEN
        CREATE ROLE cae_app_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END $$;

-- Converge los atributos si el rol ya existía mal configurado. Sin esto,
-- un clúster con un cae_app_runtime heredado y con LOGIN o BYPASSRLS
-- quedaría fuera de contrato sin que nadie lo notara.
ALTER ROLE cae_app_runtime WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;


-- cae_app_soporte — solo lectura, para las sesiones privilegiadas del
-- plano 3 (ADR-011). El interceptor hace SET ROLE hacia él; la
-- restricción de escritura la impone la base, no la aplicación.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
        CREATE ROLE cae_app_soporte NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END $$;

ALTER ROLE cae_app_soporte WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;


-- ---------------------------------------------------------------------
-- VERIFICACIÓN POST-BOOTSTRAP
-- ---------------------------------------------------------------------
-- Va dentro del propio script para que TODOS los adaptadores la hereden
-- sin repetirla. Si el estado final no es el contratado, esto revienta
-- aquí —de forma ruidosa y determinista— en vez de dejar que arranque
-- una aplicación que fallará más tarde y peor.
DO $$
DECLARE
    incumplen text;
BEGIN
    SELECT string_agg(esperado.rol, ', ' ORDER BY esperado.rol)
    INTO incumplen
    FROM (VALUES ('cae_app_runtime'), ('cae_app_soporte')) AS esperado(rol)
    WHERE NOT EXISTS (
        SELECT 1
        FROM pg_roles r
        WHERE r.rolname = esperado.rol
          AND NOT r.rolcanlogin
          AND NOT r.rolsuper
          AND NOT r.rolcreatedb
          AND NOT r.rolcreaterole
          AND NOT r.rolbypassrls);

    IF incumplen IS NOT NULL THEN
        RAISE EXCEPTION
            'Bootstrap de clúster incompleto: % no existe o no tiene los atributos requeridos', incumplen;
    END IF;
END $$;
