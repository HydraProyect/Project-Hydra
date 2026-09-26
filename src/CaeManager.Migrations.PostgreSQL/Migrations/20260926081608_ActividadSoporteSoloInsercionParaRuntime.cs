using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// <b>RegistrosActividadSoporte pasa a ser de solo inserción para
    /// <c>cae_app_runtime</c></b>. Es el rastro de lo que Soporte TALVEG hace en un
    /// Tenant (vía heredada de delegación y Sesiones Privilegiadas), y hasta aquí
    /// heredaba los cuatro verbos del <c>GRANT ... ON ALL TABLES</c> de
    /// <c>HabilitarRlsPostgres</c>: una sesión de runtime comprometida —o un fallo de
    /// código— podía reescribir o borrar la traza de Soporte TALVEG sobre su Tenant.
    /// Mismo cierre que <c>AuditoriaSoloInsercionParaRuntime</c> (#893) aplicó a
    /// <c>RegistrosAuditoria</c>, y por las mismas razones:
    ///
    /// <list type="bullet">
    /// <item><b>REVOKE y no políticas RLS por verbo.</b> El REVOKE rechaza con
    /// <c>42501</c>; una política que no autorizara el UPDATE lo dejaría en cero
    /// filas sin error, y rompería los tests que exigen exactamente
    /// <c>aislamiento_tenant</c>.</item>
    /// <item><b>Ningún camino legítimo del runtime lo necesita</b> (medido al
    /// escribir esto): el repositorio solo expone <c>Agregar</c>; cerrar un acceso
    /// añade un registro nuevo (<c>CerrarAccesoSoporteCommand</c>), no actualiza el
    /// de apertura; la entidad no tiene setters públicos ni mutadores; no hay
    /// <c>ExecuteUpdate</c> ni <c>ExecuteDelete</c> sobre ella, ni purga que la
    /// alcance; y la tabla no tiene claves foráneas, así que ninguna cascada llega a
    /// ella.</item>
    /// </list>
    ///
    /// <para>
    /// Privilegio de tabla, no de clúster: lo aplica la propia migración, así que
    /// <c>deploy/bootstrap/roles-de-cluster.sql</c> no cambia. Sin cambio de modelo
    /// de EF.
    /// </para>
    /// </summary>
    public partial class ActividadSoporteSoloInsercionParaRuntime : Migration
    {
        private const string Tabla = "RegistrosActividadSoporte";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sin guarda de existencia del rol, a propósito: un clúster sin
            // cae_app_runtime es un contrato incumplido y tiene que romper con
            // 42704 — misma convención que AuditoriaSoloInsercionParaRuntime.
            migrationBuilder.Sql($@"
REVOKE UPDATE, DELETE ON ""{Tabla}"" FROM cae_app_runtime;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guardado por la existencia del rol: deshacer no puede exigir la
            // precondición que deshacer existe para abandonar.
            migrationBuilder.Sql($@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_runtime') THEN
        GRANT UPDATE, DELETE ON ""{Tabla}"" TO cae_app_runtime;
    END IF;
END $$;
");
        }
    }
}
