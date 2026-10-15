using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeEncargoAdministracionPleno : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClausulaContrato",
                table: "InstruccionesTratamientoIaTenantPropietario",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EncargoAdministracionId",
                table: "InstruccionesTratamientoIaTenantPropietario",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_InstruccionesTratamientoIa_EncargoSoloPorEncargo",
                table: "InstruccionesTratamientoIaTenantPropietario",
                sql: "(\"OrigenInstruccion\" = 'EncargoAdministracion') = (\"EncargoAdministracionId\" IS NOT NULL AND \"ClausulaContrato\" IS NOT NULL)");

            // Encargo de administración pleno (decisión de Chris, 2026-10-15).
            //
            // app_tenant_tiene_administrador_propio(tenant): ¿tiene el Tenant una cuenta miembro con rol
            // Administrador no desactivada (LockoutEnd a más de un año, mismo umbral que
            // ApplicationUser.EstaDesactivada)? Decide si el encargo es pleno. Una cuenta Administrador
            // pendiente de activar cuenta como Administrador propio (falla cerrado): si no contara, el
            // encargo saldría pleno y el Operador CAE podría corregir su correo, generar su activación o
            // eliminarla y quedarse con el puesto del Administrador que el Tenant propietario invitó.
            // SECURITY DEFINER porque quien pregunta es una persona del Operador CAE externo y lo que ve de
            // "AspNetUsers" depende del Tenant activo de su conexión (cuentas_lectura): si no viera las
            // cuentas del Tenant propietario contaría cero y el encargo saldría pleno, fallando abierto.
            // Solo devuelve un booleano sobre el Tenant que recibe; mismo patrón que app_tenant_de_cuenta.
            // EXECUTE solo para cae_app_runtime y cae_app_aprovisionamiento.
            //
            // posicion_en_el_encargo: el WITH CHECK deja ahora al Operador CAE del encargo escribir la
            // fila solo si queda retirada. Con los privilegios de cae_app_runtime (UPDATE únicamente de
            // las columnas de retirada) y TR_EncargosAdministracion_RetiradaInmutable (la retirada se
            // escribe entera y una sola vez), lo único que gana es renunciar a su propio encargo. Nunca
            // registrarlo: un INSERT suyo de una fila vigente sigue rechazado. Application exige además
            // que el encargo sea pleno y que quien renuncia sea su Administrador (AutoridadSobreElEncargo).
            //
            // TR_EncargosAdministracion_NaceVigente (BEFORE INSERT): un encargo nace vigente; la retirada
            // solo se escribe después, por UPDATE. Sin él, la disyunción nueva del WITH CHECK dejaría al
            // Operador CAE INSERTAR una fila ya retirada a su nombre. Vale para cualquier rol, como los
            // otros dos disparadores de la tabla.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.app_tenant_tiene_administrador_propio(p_tenant_id uuid) RETURNS boolean
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT EXISTS (
                    SELECT 1
                    FROM public."AspNetUsers" u
                    JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
                    JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
                    WHERE u."TenantId" = p_tenant_id
                      AND r."NormalizedName" = 'ADMINISTRADOR'
                      AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days'));
                $$;
                REVOKE ALL ON FUNCTION public.app_tenant_tiene_administrador_propio(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_tenant_tiene_administrador_propio(uuid) TO cae_app_runtime, cae_app_aprovisionamiento;

                CREATE FUNCTION public.encargo_administracion_nace_vigente() RETURNS trigger
                    LANGUAGE plpgsql
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NEW."RetiradoEnUtc" IS NOT NULL OR NEW."RetiradoPorUsuarioId" IS NOT NULL THEN
                        RAISE EXCEPTION 'El encargo % nace vigente: la retirada se escribe después, no al registrarlo.', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER "TR_EncargosAdministracion_NaceVigente" BEFORE INSERT ON public."EncargosAdministracion" FOR EACH ROW EXECUTE FUNCTION public.encargo_administracion_nace_vigente();

                ALTER POLICY posicion_en_el_encargo ON public."EncargosAdministracion" WITH CHECK ((("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (("OperadorTenantId" IS DISTINCT FROM (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid) OR (("RetiradoEnUtc" IS NOT NULL) AND (NOT public.app_tenant_tiene_administrador_propio("PropietarioTenantId"))))));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER POLICY posicion_en_el_encargo ON public."EncargosAdministracion" WITH CHECK ((("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND ("OperadorTenantId" IS DISTINCT FROM (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid)));
                DROP TRIGGER "TR_EncargosAdministracion_NaceVigente" ON public."EncargosAdministracion";
                DROP FUNCTION public.encargo_administracion_nace_vigente();
                DROP FUNCTION public.app_tenant_tiene_administrador_propio(uuid);
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_InstruccionesTratamientoIa_EncargoSoloPorEncargo",
                table: "InstruccionesTratamientoIaTenantPropietario");

            migrationBuilder.DropColumn(
                name: "ClausulaContrato",
                table: "InstruccionesTratamientoIaTenantPropietario");

            migrationBuilder.DropColumn(
                name: "EncargoAdministracionId",
                table: "InstruccionesTratamientoIaTenantPropietario");
        }
    }
}
