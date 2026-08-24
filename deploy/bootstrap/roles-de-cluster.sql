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
-- sí son por base. Aquí van los principales, sus atributos y la
-- MEMBRESÍA entre ellos.
--
-- La membresía entra por el mismo criterio que todo lo demás, no por
-- comodidad: pg_auth_members es un catálogo de clúster igual que
-- pg_authid. El criterio no es "¿es un GRANT?" sino "¿dónde vive el
-- objeto que se toca?".
--
-- Tampoco es un cajón de "roles varios": un test de arquitectura fija
-- que este fichero contiene exactamente los dos principales del contrato
-- y ningún tercero.
-- =====================================================================


-- cae_app_runtime — la identidad de CONEXIÓN de la aplicación. NOBYPASSRLS es
-- lo que mantiene la garantía de aislamiento por encima de cualquier otra: un
-- rol que pudiera saltarse RLS haría inútiles las políticas.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_runtime') THEN
        -- Nace NOLOGIN a propósito: un rol recién creado no tiene contraseña,
        -- así que LOGIN no le serviría para conectar y solo anunciaría una
        -- capacidad que no tiene. Habilitarlo es cosa del despliegue, que es
        -- quien posee el secreto (ver RUNBOOK-RLS.md).
        CREATE ROLE cae_app_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END $$;

-- Converge SOLO los invariantes de seguridad, y deliberadamente NO toca LOGIN.
--
-- Esta línea llegó a decir NOLOGIN, y era un defecto real con consecuencia
-- operativa: producción llevaba desde el 2026-08-14 con
-- `ALTER ROLE cae_app_runtime LOGIN PASSWORD '…'` (RUNBOOK-RLS.md), que es
-- justamente lo que hace que RLS restrinja de verdad allí. Este guion se
-- escribió ocho días después codificando el estado ANTERIOR a esa activación,
-- así que ejecutarlo contra producción habría retirado el LOGIN y dejado a la
-- aplicación sin poder abrir su conexión restringida.
--
-- La distinción que lo evita: LOGIN es configuración de DESPLIEGUE —depende de
-- si ese entorno provisiona una contraseña— y este guion no puede
-- provisionarla sin contener un secreto. Lo que no puede otorgar, tampoco debe
-- destruir. Los cuatro atributos de abajo sí son innegociables y por eso
-- siguen convergiendo.
ALTER ROLE cae_app_runtime WITH NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;


-- cae_app_soporte — solo lectura, para las sesiones privilegiadas del
-- plano 3 (ADR-011). El interceptor hace SET ROLE hacia él; la
-- restricción de escritura la impone la base, no la aplicación.
--
-- Aquí NOLOGIN SÍ es un atributo de seguridad, al contrario que en
-- cae_app_runtime: este rol no debe ser nunca una identidad de conexión, solo
-- se adopta desde una sesión ya autenticada. Por eso se fuerza, y por eso la
-- verificación de abajo lo comprueba solo para él.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
        CREATE ROLE cae_app_soporte NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
    END IF;
END $$;

ALTER ROLE cae_app_soporte WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;


-- La MEMBRESÍA que hace utilizable todo lo anterior.
--
-- Sin ella, el SET ROLE del interceptor falla: cae_app_runtime no puede adoptar
-- un rol del que no es miembro. Faltaba, y no lo delataba ningún test porque
-- todos ejercitaban el SET ROLE desde la conexión PROPIETARIA —que puede
-- adoptar cualquier rol sin ser miembro de nada—. El enforcement de solo
-- lectura del plano 3 estaba probado por un camino que producción no recorre.
--
-- Va aquí y no en una migración porque pg_auth_members es un catálogo de
-- CLÚSTER, igual que pg_authid: es exactamente el criterio que separa lo de
-- este fichero de lo de las migraciones por base.
--
-- WITH INHERIT FALSE (PostgreSQL 16+; CI usa 17 y el despliegue 18) es
-- deliberado: cae_app_runtime podrá ADOPTAR el rol de soporte, pero no hereda
-- sus privilegios de forma pasiva. La diferencia importa hacia el futuro — si
-- algún día cae_app_soporte recibiera un privilegio que runtime no debe tener,
-- la herencia se lo daría en silencio. Adoptar es un acto; heredar, un efecto.
--
-- Idempotente: repetir el GRANT no falla, solo reafirma la opción.
GRANT cae_app_soporte TO cae_app_runtime WITH INHERIT FALSE;


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
    soporte_conecta boolean;
BEGIN
    -- Invariantes de SEGURIDAD, exigidos a los dos por igual. rolcanlogin ya
    -- no está aquí: dejó de ser simétrico el día que producción habilitó
    -- cae_app_runtime para conectar, que es el estado correcto y no una
    -- desviación que haya que corregir.
    SELECT string_agg(esperado.rol, ', ' ORDER BY esperado.rol)
    INTO incumplen
    FROM (VALUES ('cae_app_runtime'), ('cae_app_soporte')) AS esperado(rol)
    WHERE NOT EXISTS (
        SELECT 1
        FROM pg_roles r
        WHERE r.rolname = esperado.rol
          AND NOT r.rolsuper
          AND NOT r.rolcreatedb
          AND NOT r.rolcreaterole
          AND NOT r.rolbypassrls);

    IF incumplen IS NOT NULL THEN
        RAISE EXCEPTION
            'Bootstrap de clúster incompleto: % no existe o no tiene los atributos de seguridad requeridos', incumplen;
    END IF;

    -- Y el invariante que NO es simétrico. cae_app_runtime queda fuera de esta
    -- comprobación a propósito: su LOGIN lo decide el despliegue.
    SELECT r.rolcanlogin INTO soporte_conecta
    FROM pg_roles r WHERE r.rolname = 'cae_app_soporte';

    IF soporte_conecta THEN
        RAISE EXCEPTION
            'cae_app_soporte tiene LOGIN: solo debe adoptarse con SET ROLE desde una sesión ya autenticada, nunca conectarse';
    END IF;

    -- La membresía. Sin ella los dos roles existen, cumplen todos sus atributos
    -- de seguridad, y el soporte no funciona: cae_app_runtime no puede adoptar
    -- un rol del que no es miembro. Es el caso exacto en el que un bootstrap
    -- "correcto" deja el sistema roto, así que se comprueba en vez de suponerse.
    IF NOT EXISTS (
        SELECT 1
        FROM pg_auth_members m
        JOIN pg_roles concedido ON concedido.oid = m.roleid
        JOIN pg_roles miembro   ON miembro.oid = m.member
        WHERE concedido.rolname = 'cae_app_soporte'
          AND miembro.rolname = 'cae_app_runtime')
    THEN
        RAISE EXCEPTION
            'cae_app_runtime no es miembro de cae_app_soporte: el SET ROLE del interceptor fallaría y las sesiones de soporte no podrían abrirse';
    END IF;
END $$;
