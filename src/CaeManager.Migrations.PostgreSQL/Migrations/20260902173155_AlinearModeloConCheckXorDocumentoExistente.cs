using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// DCR-19: declara en el modelo EF (<c>DocumentoConfiguration</c>) la
    /// constraint <c>CK_Documentos_PropietarioXor</c> que
    /// <c>RendimientoBusquedasYCheckXorDocumento</c> ya creó en SQL crudo el
    /// 2026-08-01 — hasta ahora el modelo no la conocía, así que
    /// "dotnet ef migrations has-pending-model-changes" no la exigía y una
    /// reconfiguración de la tabla podía perderla sin que nada avisara.
    ///
    /// Esta migración NO crea la constraint por primera vez: en toda base que
    /// haya aplicado la migración de agosto (producción, staging, y la base
    /// de pruebas de integración, que aplica todas las migraciones en orden)
    /// la constraint ya existe, así que un <c>AddCheckConstraint</c> generado
    /// a ciegas fallaría con "ya existe". El <c>Up</c> es idempotente
    /// condicionando la creación a <c>pg_constraint</c> —no un DROP+ADD
    /// incondicional— a propósito: <c>ALTER TABLE ... ADD CONSTRAINT</c>
    /// sobre una CHECK exige un lock fuerte y revalida todas las filas de la
    /// tabla; en toda base real (producción, staging) la constraint ya
    /// existe, así que un DROP+ADD pagaría ese coste de bloqueo/revalidación
    /// en cada despliegue sin necesidad — comprobar primero lo evita salvo en
    /// el caso excepcional (constraint ausente) donde sí hace falta crearla.
    ///
    /// El <c>Down</c> es deliberadamente un no-op: la propietaria de crear y
    /// destruir esta constraint en la base de datos sigue siendo
    /// <c>RendimientoBusquedasYCheckXorDocumento</c> (su <c>Down</c> hace el
    /// DROP). Si este <c>Down</c> también la borrara, revertir solo esta
    /// migración dejaría la base sin la constraint mientras el modelo EF
    /// (fuera de esta migración) seguiría sin conocerla — un estado que no
    /// corresponde a ningún punto real del historial de migraciones.
    /// </summary>
    public partial class AlinearModeloConCheckXorDocumentoExistente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'CK_Documentos_PropietarioXor'
                          AND conrelid = '"Documentos"'::regclass
                    ) THEN
                        ALTER TABLE "Documentos" ADD CONSTRAINT "CK_Documentos_PropietarioXor"
                        CHECK (num_nonnulls("TrabajadorId", "ClienteId", "EmpresaId", "VehiculoId", "ProyectoId") = 1);
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op a propósito: RendimientoBusquedasYCheckXorDocumento
            // (2026-08-01) sigue siendo la propietaria del DROP — ver el
            // comentario de la clase.
        }
    }
}
