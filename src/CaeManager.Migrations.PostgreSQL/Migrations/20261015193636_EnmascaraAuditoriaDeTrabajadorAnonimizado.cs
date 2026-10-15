using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class EnmascaraAuditoriaDeTrabajadorAnonimizado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Decisión de Chris (2026-10-15): al anonimizar un Trabajador, sus datos personales
            // en el historial de auditoría se enmascaran.
            //
            // RegistrosAuditoria sigue siendo de solo inserción para cae_app_runtime (sin UPDATE
            // ni DELETE: AuditoriaSoloInsercionBajoRuntimeTests). La única reescritura posible es
            // esta función SECURITY DEFINER, que EjecucionPurgaService llama en la misma
            // transacción que guarda la anonimización. Se acota sola, sin parámetros que elijan
            // otra cosa:
            //   - Tenant: el de la sesión (app.tenant_id, el mismo que la política
            //     aislamiento_tenant de la tabla). El propietario de la función es el rol de las
            //     migraciones, que no está sujeto a RLS, así que el filtro explícito es el que
            //     acota; sin Tenant en la sesión falla con 42501 en vez de no hacer nada.
            //   - Entidad: solo filas con EntidadTipo 'Trabajador' y EntidadId el recibido, y solo
            //     si ese Trabajador es del Tenant de la sesión y ya está anonimizado
            //     (AnonimizadoEnUtc no nulo); si no, devuelve 0 sin tocar nada.
            //   - Campos: solo los valores de las claves de Trabajador.CamposDeDatosPersonales
            //     dentro de DatosAntes y DatosDespues (si son un objeto JSON) pasan a "***"; el
            //     resto del JSON se copia tal cual, en el mismo orden y con el mismo formato
            //     compacto (ObtenerAuditoriaQuery busca el marcador literal "EstaEliminado":true). Nunca Accion, UsuarioId, ActorRealUsuarioId, TipoActor, ViaAcceso,
            //     ViaAccesoId, FechaUtc ni el resto de columnas.
            // Devuelve cuántas entradas reescribió. EXECUTE solo para cae_app_runtime.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.app_enmascarar_auditoria_de_trabajador_anonimizado(p_trabajador_id uuid) RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_tenant uuid := NULLIF(current_setting('app.tenant_id', true), '')::uuid;
                  v_campos text[] := ARRAY[
                    'Nombre', 'Apellidos', 'Alias', 'Dni', 'FechaNacimiento',
                    'Email', 'Telefono', 'Observaciones', 'Puesto',
                    'NumeroAfiliacionSeguridadSocial', 'Nacionalidad', 'TipoVinculo',
                    'FechaAltaEmpresa', 'FechaBajaEmpresa', 'FiguraEnRnt',
                    'TipoPermisoTrabajo', 'CaducidadPermisoTrabajo', 'MotivoPermisoTrabajo'];
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
                REVOKE ALL ON FUNCTION public.app_enmascarar_auditoria_de_trabajador_anonimizado(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_enmascarar_auditoria_de_trabajador_anonimizado(uuid) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_enmascarar_auditoria_de_trabajador_anonimizado(uuid);
                """);
        }
    }
}
