using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeContactoEmergenciaATrabajador : Migration
    {
        // Los campos que enmascaraba la función hasta esta migración (AnadeNotaInternaATrabajadorYCentro).
        private const string CamposAnteriores =
            """
            'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                                'Email', 'Telefono', 'Observaciones', 'Notas', 'Puesto',
                                'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                                'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                                'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo'
            """;

        // Los mismos más el contacto de emergencia: nombre y teléfono de una persona física a la que avisar, así que son
        // dato personal (Trabajador.CamposDeDatosPersonales) y la anonimización los enmascara también en el historial.
        private const string CamposConContactoEmergencia =
            """
            'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                                'Email', 'Telefono', 'Observaciones', 'Notas',
                                'ContactoEmergenciaNombre', 'ContactoEmergenciaTelefono', 'Puesto',
                                'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                                'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                                'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo'
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El contacto de emergencia del Trabajador (decisión de Chris, 2026-10-18). Columnas opcionales y sin valor
            // por defecto: no tocan la política RLS de Trabajadores, que sigue filtrando por TenantId.
            migrationBuilder.AddColumn<string>(
                name: "ContactoEmergenciaNombre",
                table: "Trabajadores",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactoEmergenciaTelefono",
                table: "Trabajadores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Los dos campos entran en Trabajador.CamposDeDatosPersonales: la función que enmascara el historial de un
            // Trabajador anonimizado repite esa lista en SQL y se redefine con ellos. CREATE OR REPLACE conserva el
            // propietario, SECURITY DEFINER se repite en la definición, y los permisos (REVOKE de PUBLIC, EXECUTE solo
            // para cae_app_runtime) no cambian: PostgreSQL no los toca al reemplazar el cuerpo. Mismo cuerpo que en
            // AnadeNotaInternaATrabajadorYCentro salvo la lista de campos.
            migrationBuilder.Sql(DefinicionDeLaFuncion(CamposConContactoEmergencia));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DefinicionDeLaFuncion(CamposAnteriores));

            migrationBuilder.DropColumn(
                name: "ContactoEmergenciaNombre",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "ContactoEmergenciaTelefono",
                table: "Trabajadores");
        }

        private static string DefinicionDeLaFuncion(string campos) =>
            $$"""
            CREATE OR REPLACE FUNCTION public.app_enmascarar_auditoria_de_trabajador_anonimizado(p_trabajador_id uuid) RETURNS integer
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path TO 'pg_catalog', 'pg_temp'
                AS $$
            DECLARE
              v_tenant uuid := NULLIF(current_setting('app.tenant_id', true), '')::uuid;
              v_campos text[] := ARRAY[
                {{campos}}];
              v_filas integer;
            BEGIN
              IF v_tenant IS NULL THEN
                RAISE EXCEPTION 'Sin Tenant en la sesión: no se enmascara el historial de ningún Trabajador.'
                  USING ERRCODE = '42501';
              END IF;

              IF p_trabajador_id IS NULL OR NOT EXISTS (
                  SELECT 1 FROM public."Trabajadores" t
                  WHERE t."Id" = p_trabajador_id
                    AND t."TenantId" = v_tenant
                    AND t."AnonimizadoEnUtc" IS NOT NULL) THEN
                RETURN 0;
              END IF;

              UPDATE public."RegistrosAuditoria" r
                 SET "DatosAntes" = CASE WHEN jsonb_typeof(r."DatosAntes"::jsonb) = 'object' THEN (
                       SELECT COALESCE('{' || string_agg(to_json(e.clave)::text || ':' ||
                                CASE WHEN e.clave = ANY (v_campos) THEN '"***"' ELSE e.valor::text END,
                                ',' ORDER BY e.orden) || '}', r."DatosAntes")
                       FROM json_each(r."DatosAntes"::json) WITH ORDINALITY AS e(clave, valor, orden))
                       ELSE r."DatosAntes" END,
                     "DatosDespues" = CASE WHEN jsonb_typeof(r."DatosDespues"::jsonb) = 'object' THEN (
                       SELECT COALESCE('{' || string_agg(to_json(e.clave)::text || ':' ||
                                CASE WHEN e.clave = ANY (v_campos) THEN '"***"' ELSE e.valor::text END,
                                ',' ORDER BY e.orden) || '}', r."DatosDespues")
                       FROM json_each(r."DatosDespues"::json) WITH ORDINALITY AS e(clave, valor, orden))
                       ELSE r."DatosDespues" END
               WHERE r."TenantId" = v_tenant
                 AND r."EntidadTipo" = 'Trabajador'
                 AND r."EntidadId" = p_trabajador_id
                 AND (COALESCE(jsonb_typeof(r."DatosAntes"::jsonb) = 'object' AND r."DatosAntes"::jsonb ?| v_campos, false)
                      OR COALESCE(jsonb_typeof(r."DatosDespues"::jsonb) = 'object' AND r."DatosDespues"::jsonb ?| v_campos, false));

              GET DIAGNOSTICS v_filas = ROW_COUNT;
              RETURN v_filas;
            END;
            $$;
            """;
    }
}
