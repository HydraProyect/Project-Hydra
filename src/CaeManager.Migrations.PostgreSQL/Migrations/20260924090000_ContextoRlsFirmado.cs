using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// P6, fase «expandir»: infraestructura del contexto de sesión RLS firmado
    /// (diseño <c>tecnico/DISENO-CONTEXTO-RLS-FIRMADO-P6-2026-09-23.md</c>).
    ///
    /// <para>
    /// Hasta ahora las políticas leen <c>app.tenant_id</c>,
    /// <c>app.tenant_origen_id</c> y <c>app.usuario_id</c>, que cualquier
    /// sesión puede fijar con <c>set_config</c>: quien tenga la credencial de
    /// <c>cae_app_runtime</c> elige de qué Tenant lee. Esta migración crea la
    /// alternativa: <c>app.contexto</c> lleva un token HMAC-SHA256 que firma la
    /// aplicación con una clave que <c>cae_app_runtime</c> no puede leer ni
    /// escribir, y <c>app_contexto_validado()</c> lo valida.
    /// </para>
    ///
    /// <para>
    /// <b>No cambia ninguna política.</b> La instancia anterior de la
    /// aplicación sigue sirviendo mientras corre el migrador (el servicio
    /// <c>migrador</c> termina antes de que arranque la nueva), y no envía
    /// token: si esta migración pasara las políticas al contexto firmado, esa
    /// instancia se quedaría sin datos hasta el relevo. Las políticas se
    /// reescriben en la fase «contraer», cuando ya solo sirve código que firma
    /// (<c>ReescrituraPoliticasContextoRls</c>).
    /// </para>
    ///
    /// <para>
    /// <b>HMAC sin pgcrypto.</b> <c>sha256(opad || sha256(ipad || carga))</c> es
    /// HMAC-SHA256 (RFC 2104) con la clave ya combinada con los rellenos; la
    /// tabla guarda esos rellenos, que la aplicación calcula
    /// (<c>TokenContextoRls.Rellenos</c>). <c>sha256()</c> es nativa desde
    /// PostgreSQL 11.
    /// </para>
    ///
    /// <para>
    /// <b>Sin token, contexto nulo; token inválido, error.</b> Una conexión sin
    /// <c>app.contexto</c> (propietario, arranque, una herramienta) obtiene
    /// NULL en todo, igual que hoy con los GUC vacíos: las políticas no
    /// encuentran ninguna fila. Un token presente pero manipulado, de otra
    /// conexión, con clave desconocida o caducado lanza 42501: es una señal de
    /// ataque o de fallo del firmante, no un caso de negocio, y callarlo lo
    /// escondería.
    /// </para>
    /// </summary>
    public partial class ContextoRlsFirmado : Migration
    {
        private const string PatronGuid = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

        /// <summary>
        /// <c>v1|clave|tenant|tenant_origen|usuario|origen|pid|caduca|nonce.hmac</c>;
        /// grupo 1 = la carga firmada, 2..9 sus campos, 10 = la firma.
        /// </summary>
        private const string Formato =
            "^(v1\\|(" + PatronGuid + ")\\|(" + PatronGuid + ")?\\|(" + PatronGuid + ")?\\|(" + PatronGuid + ")?\\|([a-z]{1,16})\\|([0-9]{1,10})\\|([0-9]{1,12})\\|([0-9a-f]{32}))\\.([0-9a-f]{64})$";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
CREATE SCHEMA app_privado;
REVOKE ALL ON SCHEMA app_privado FROM PUBLIC;

CREATE TABLE app_privado.claves_contexto (
    id uuid PRIMARY KEY,
    ipad bytea NOT NULL CHECK (octet_length(ipad) = 64),
    opad bytea NOT NULL CHECK (octet_length(opad) = 64),
    valida_hasta timestamptz NOT NULL,
    registrada timestamptz NOT NULL DEFAULT now()
);
REVOKE ALL ON app_privado.claves_contexto FROM PUBLIC;

CREATE FUNCTION public.app_contexto_validado(
    OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean)
  LANGUAGE plpgsql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    token text := current_setting('app.contexto', true);
    partes text[];
    clave record;
BEGIN
    valido := false;
    IF token IS NULL OR token = '' THEN
        RETURN;
    END IF;

    partes := regexp_match(token, '{Formato}');
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
$$;

CREATE FUNCTION public.app_ctx_tenant_id() RETURNS uuid
  LANGUAGE sql STABLE AS $$ SELECT tenant_id FROM public.app_contexto_validado() $$;
CREATE FUNCTION public.app_ctx_tenant_origen_id() RETURNS uuid
  LANGUAGE sql STABLE AS $$ SELECT tenant_origen_id FROM public.app_contexto_validado() $$;
CREATE FUNCTION public.app_ctx_usuario_id() RETURNS uuid
  LANGUAGE sql STABLE AS $$ SELECT usuario_id FROM public.app_contexto_validado() $$;
CREATE FUNCTION public.app_ctx_valido() RETURNS boolean
  LANGUAGE sql STABLE AS $$ SELECT valido FROM public.app_contexto_validado() $$;
");

            // Los tres roles sometidos a RLS: el de tráfico y los dos que
            // adopta con SET ROLE en una sesión privilegiada (cae_app_runtime
            // no los hereda, INHERIT FALSE, así que no le basta con el suyo).
            foreach (var funcion in new[]
                     {
                         "app_contexto_validado()", "app_ctx_tenant_id()", "app_ctx_tenant_origen_id()",
                         "app_ctx_usuario_id()", "app_ctx_valido()",
                     })
            {
                migrationBuilder.Sql($@"
REVOKE ALL ON FUNCTION public.{funcion} FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.{funcion} TO cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP FUNCTION IF EXISTS public.app_ctx_valido();
DROP FUNCTION IF EXISTS public.app_ctx_usuario_id();
DROP FUNCTION IF EXISTS public.app_ctx_tenant_origen_id();
DROP FUNCTION IF EXISTS public.app_ctx_tenant_id();
DROP FUNCTION IF EXISTS public.app_contexto_validado();
DROP SCHEMA IF EXISTS app_privado CASCADE;
");
        }
    }
}
