using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// <b>Particionado mensual de los dos registros de auditoría del Tenant
    /// propietario</b> (P1-M2): <c>RegistrosAuditoria</c> por <c>FechaUtc</c> y
    /// <c>RegistrosAccesoDocumentoSensible</c> por <c>OcurridoEnUtc</c>, con la PK
    /// ampliada a <c>(Id, fecha)</c> porque PostgreSQL exige la columna de
    /// partición en toda clave única. El cambio de PK que EF generó
    /// (DropPrimaryKey/AddPrimaryKey) se sustituye por la conversión completa de
    /// <see cref="ParticionadoMensualEventos"/>: una tabla ordinaria no se puede
    /// convertir en particionada con ALTER, hay que recrearla y copiar.
    ///
    /// <para>
    /// Contrato que conserva (y que comprueban <c>ParticionadoAuditoriaBajoRuntimeTests</c>
    /// y <c>MigracionParticionarAuditoriaPorMesTests</c>): ningún evento se
    /// pierde ni cambia (la propia migración aborta si el recuento o la suma de
    /// hashes por fila no cuadran); <c>cae_app_runtime</c> sigue con solo SELECT
    /// e INSERT sobre la madre (AuditoriaSoloInsercionParaRuntime) y sin ningún
    /// privilegio sobre las particiones; las políticas RLS son las mismas en la
    /// madre y en cada partición. Sin purga: nada aquí borra una partición.
    /// </para>
    ///
    /// <para>
    /// Privilegios de tabla y funciones de la propia base: no toca
    /// <c>deploy/bootstrap/roles-de-cluster.sql</c>.
    /// </para>
    /// </summary>
    public partial class ParticionarAuditoriaPorMes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ParticionadoMensualEventos.CrearFuncionesSql);
            foreach (var (tabla, columna) in ParticionadoMensualEventos.Tablas)
                migrationBuilder.Sql(ParticionadoMensualEventos.ParticionarSql(tabla, columna));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (tabla, columna) in ParticionadoMensualEventos.Tablas)
                migrationBuilder.Sql(ParticionadoMensualEventos.DesparticionarSql(tabla, columna));
            migrationBuilder.Sql(ParticionadoMensualEventos.EliminarFuncionesSql);
        }
    }
}
