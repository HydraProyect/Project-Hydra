using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeNotaInternaATrabajadorYCentro : Migration
    {
        // Los campos que enmascaraba la función hasta esta migración (EnmascaraAuditoriaDeTrabajadorAnonimizado).
        private const string CamposAnteriores =
            """
            'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                                'Email', 'Telefono', 'Observaciones', 'Puesto',
                                'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                                'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                                'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo'
            """;

        // Los mismos más 'Notas': la «Nota interna» de Trabajador 360 es texto libre sobre una persona física, así que
        // es dato personal (Trabajador.CamposDeDatosPersonales) y la anonimización la enmascara también en el historial.
        private const string CamposConNota =
            """
            'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                                'Email', 'Telefono', 'Observaciones', 'Notas', 'Puesto',
                                'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                                'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                                'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo'
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // La «Nota interna» de Trabajador 360 y de Centro 360, con el mismo límite que la de Empresa 360
            // (Empresa.Notas, character varying(2000)). Columnas opcionales y sin valor por defecto: no tocan las
            // políticas RLS de las dos tablas, que siguen filtrando por TenantId.
            migrationBuilder.AddColumn<string>(
                name: "Notas",
                table: "Trabajadores",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notas",
                table: "Centros",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            // Trabajador.Notas entra en Trabajador.CamposDeDatosPersonales: la función que enmascara el historial de un
            // Trabajador anonimizado repite esa lista en SQL y se redefine con 'Notas'. CREATE OR REPLACE conserva el
            // propietario, SECURITY DEFINER se repite en la definición, y los permisos (REVOKE de PUBLIC, EXECUTE solo
            // para cae_app_runtime) no cambian: PostgreSQL no los toca al reemplazar el cuerpo. Mismo cuerpo que en
            // EnmascaraAuditoriaDeTrabajadorAnonimizado salvo la lista de campos.
            migrationBuilder.Sql(DefinicionDeLaFuncion(CamposConNota));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DefinicionDeLaFuncion(CamposAnteriores));

            migrationBuilder.DropColumn(
                name: "Notas",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "Notas",
                table: "Centros");
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
