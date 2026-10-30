using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Rendimiento de la RLS firmada, sin debilitarla (M-13). <c>app_contexto_validado()</c> corre una vez por tabla
    /// de cada consulta (cada política hace <c>SELECT app_ctx_tenant_id()</c> en su propio InitPlan), y su
    /// formato, comprobado con una expresión regular de repeticiones acotadas, costaba 1,2 ms por llamada: con una cartera de 10 Tenant, Mi
    /// trabajo pasaba ahí unos 3 de sus 5 segundos, casi sin depender del volumen de datos.
    ///
    /// <para>
    /// Las comprobaciones son las mismas, en el mismo orden y con los mismos errores: formato, clave vigente, firma,
    /// conexión y caducidad. Solo cambia cómo se comprueba el formato: partir por <c>|</c> y comparar cada campo con
    /// <c>translate</c>, con la misma gramática que la expresión anterior. El contexto sigue sin memorizarse: cada
    /// llamada recalcula el HMAC.
    /// </para>
    /// </summary>
    public partial class ValidacionContextoRlsSinExpresionRegular : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION public.app_contexto_validado(OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean)
    RETURNS record
    LANGUAGE plpgsql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $_$
DECLARE
    token text := current_setting('app.contexto', true);
    forma_uuid CONSTANT text := '00000000-0000-0000-0000-000000000000';
    hex CONSTANT text := '0123456789abcdef';
    ceros CONSTANT text := '0000000000000000';
    carga text;
    firma text;
    partes text[];
    clave record;
BEGIN
    valido := false;
    IF token IS NULL OR token = '' THEN
        RETURN;
    END IF;

    -- v1|clave|tenant|tenant_origen|usuario|origen|pid|caduca|nonce.hmac_hex, sin expresión regular: esta función
    -- corre una vez por tabla de cada consulta, y una expresión con repeticiones acotadas costaba más de 1 ms por llamada.
    carga := left(token, -65);
    firma := right(token, 64);
    partes := string_to_array(carga, '|');
    IF substr(token, length(token) - 64, 1) IS DISTINCT FROM '.'
       OR translate(firma, hex, '') <> ''
       OR cardinality(partes) <> 9
       OR partes[1] <> 'v1'
       OR translate(partes[2], hex, ceros) <> forma_uuid
       OR (partes[3] <> '' AND translate(partes[3], hex, ceros) <> forma_uuid)
       OR (partes[4] <> '' AND translate(partes[4], hex, ceros) <> forma_uuid)
       OR (partes[5] <> '' AND translate(partes[5], hex, ceros) <> forma_uuid)
       OR length(partes[6]) NOT BETWEEN 1 AND 16
       OR translate(partes[6], 'abcdefghijklmnopqrstuvwxyz', '') <> ''
       OR length(partes[7]) NOT BETWEEN 1 AND 10
       OR translate(partes[7], '0123456789', '') <> ''
       OR length(partes[8]) NOT BETWEEN 1 AND 12
       OR translate(partes[8], '0123456789', '') <> ''
       OR length(partes[9]) <> 32
       OR translate(partes[9], hex, '') <> '' THEN
        RAISE EXCEPTION 'contexto RLS con formato inválido' USING ERRCODE = '42501';
    END IF;

    SELECT c.ipad, c.opad INTO clave
      FROM app_privado.claves_contexto c
     WHERE c.id = partes[2]::uuid AND c.valida_hasta > now();
    IF NOT FOUND THEN
        RAISE EXCEPTION 'contexto RLS con clave desconocida o retirada' USING ERRCODE = '42501';
    END IF;

    -- Se comparan los hashes de las dos firmas, no las firmas: el tiempo de
    -- la comparación no dice cuántos bytes coinciden.
    IF sha256(sha256(clave.opad || sha256(clave.ipad || convert_to(carga, 'UTF8'))))
       <> sha256(decode(firma, 'hex')) THEN
        RAISE EXCEPTION 'contexto RLS con firma inválida' USING ERRCODE = '42501';
    END IF;

    IF partes[7]::bigint <> pg_backend_pid() THEN
        RAISE EXCEPTION 'contexto RLS firmado para otra conexión' USING ERRCODE = '42501';
    END IF;

    -- now() es el inicio de la transacción: el token tiene que estar vigente
    -- cuando empieza; dentro de ella no caduca (diseño § 7).
    IF to_timestamp(partes[8]::bigint) <= now() THEN
        RAISE EXCEPTION 'contexto RLS caducado' USING ERRCODE = '42501';
    END IF;

    tenant_id := nullif(partes[3], '')::uuid;
    tenant_origen_id := nullif(partes[4], '')::uuid;
    usuario_id := nullif(partes[5], '')::uuid;
    valido := true;
END;
$_$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION public.app_contexto_validado(OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean) RETURNS record
    LANGUAGE plpgsql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $_$
DECLARE
    token text := current_setting('app.contexto', true);
    partes text[];
    clave record;
BEGIN
    valido := false;
    IF token IS NULL OR token = '' THEN
        RETURN;
    END IF;

    partes := regexp_match(token, '^(v1\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\|([a-z]{1,16})\|([0-9]{1,10})\|([0-9]{1,12})\|([0-9a-f]{32}))\.([0-9a-f]{64})$');
    IF partes IS NULL THEN
        RAISE EXCEPTION 'contexto RLS con formato inválido' USING ERRCODE = '42501';
    END IF;

    SELECT c.ipad, c.opad INTO clave
      FROM app_privado.claves_contexto c
     WHERE c.id = partes[2]::uuid AND c.valida_hasta > now();
    IF NOT FOUND THEN
        RAISE EXCEPTION 'contexto RLS con clave desconocida o retirada' USING ERRCODE = '42501';
    END IF;

    -- Se comparan los hashes de las dos firmas, no las firmas: el tiempo de
    -- la comparación no dice cuántos bytes coinciden.
    IF sha256(sha256(clave.opad || sha256(clave.ipad || convert_to(partes[1], 'UTF8'))))
       <> sha256(decode(partes[10], 'hex')) THEN
        RAISE EXCEPTION 'contexto RLS con firma inválida' USING ERRCODE = '42501';
    END IF;

    IF partes[7]::bigint <> pg_backend_pid() THEN
        RAISE EXCEPTION 'contexto RLS firmado para otra conexión' USING ERRCODE = '42501';
    END IF;

    -- now() es el inicio de la transacción: el token tiene que estar vigente
    -- cuando empieza; dentro de ella no caduca (diseño § 7).
    IF to_timestamp(partes[8]::bigint) <= now() THEN
        RAISE EXCEPTION 'contexto RLS caducado' USING ERRCODE = '42501';
    END IF;

    tenant_id := partes[3]::uuid;
    tenant_origen_id := partes[4]::uuid;
    usuario_id := partes[5]::uuid;
    valido := true;
END;
$_$;
");
        }
    }
}
