using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// P6, fase «contraer»: ninguna política RLS ni función de <c>public</c> vuelve a
    /// leer los GUC sueltos <c>app.tenant_id</c>, <c>app.tenant_origen_id</c> y
    /// <c>app.usuario_id</c>, que puede fijar cualquiera que ejecute SQL en la
    /// sesión. Pasan a <c>app_ctx_*()</c>, que solo devuelve valor con un token
    /// firmado por la aplicación en <c>app.contexto</c> para el backend de esa
    /// conexión. Sin token, el contexto es nulo y las políticas no casan (fallo
    /// cerrado); con un token no válido, 42501.
    ///
    /// <para>
    /// <b>Precondición de despliegue.</b> Toda instancia que sirva tráfico tiene que
    /// firmar ya (fase «expandir», en producción desde #887) y tener una clave
    /// descifrable: <c>/salud</c>, componente <c>contexto-rls-clave</c>, en Healthy.
    /// Sin clave, todas las lecturas y escrituras bajo RLS se quedan sin filas.
    /// </para>
    ///
    /// <para>
    /// Toda migración posterior escribe <c>(SELECT app_ctx_tenant_id())</c> y nunca
    /// <c>current_setting('app.…')</c> (ratchet
    /// <c>MigracionesPosterioresAContraerNoLeenGucSueltosTests</c>).
    /// </para>
    /// </summary>
    public partial class ContraeContextoRlsFirmado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReescrituraPoliticasContextoRls.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReescrituraPoliticasContextoRls.SqlInversa);
        }
    }
}
