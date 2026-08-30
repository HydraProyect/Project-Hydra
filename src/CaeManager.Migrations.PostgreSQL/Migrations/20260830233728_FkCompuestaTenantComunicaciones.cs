using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Auditoría Módulo 8, hallazgo crítico pendiente: Mensaje/ParticipanteConversacion
    /// (hijos de Conversacion) y AdjuntoMensaje (hijo de Mensaje) solo tenían FK de
    /// una columna, sin TenantId — defensa en profundidad ausente frente al filtro
    /// global de lectura para una fila cargada por una vía que no lo pasa
    /// (IgnoreQueryFilters justificado, o un futuro bug de sellado).
    ///
    /// Componer la FK con TenantId como relación de EF Core (HasForeignKey en
    /// HasMany().WithOne()) se intentó y se revirtió el 2026-08-30: al ser
    /// navegaciones de colección reales, EF fija la relación por fixup en cuanto
    /// la entidad hija entra al ChangeTracker por el grafo de Conversacion/Mensaje
    /// — antes de que TenantSelladoInterceptor selle el TenantId real — y choca
    /// con "The property '...TenantId' is part of a key and so cannot be
    /// modified" (rompía 14 tests de Comunicaciones: ingesta de WhatsApp/correo,
    /// clasificación de ruido, reclamaciones).
    ///
    /// La FK compuesta de esta migración es SQL crudo a propósito, sin
    /// declaración equivalente en el modelo Fluent (mismo patrón que
    /// RendimientoBusquedasYCheckXorDocumento, 2026-08-01, para el CHECK XOR de
    /// Documento): al no ser una relación que EF conozca, no dispara
    /// DetectChanges/fixup sobre las entidades hijas y no interactúa en absoluto
    /// con TenantSelladoInterceptor. Es Postgres, no el ORM, quien impide en cada
    /// INSERT/UPDATE que una fila hija apunte a un padre de otro tenant. El
    /// snapshot del modelo no cambia; un futuro "dotnet ef migrations add" no
    /// "ve" estas constraints — aceptable porque nada en el modelo debería
    /// tocarlas.
    ///
    /// Verificado sin filas existentes que violen el invariante (0 de 6
    /// Mensajes, 0 de 4 Participantes, 0 de 0 Adjuntos con TenantId distinto del
    /// de su padre, base de datos local) antes de escribir esta migración.
    /// </summary>
    public partial class FkCompuestaTenantComunicaciones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Conversaciones" ADD CONSTRAINT "AK_Conversaciones_Id_TenantId"
                UNIQUE ("Id", "TenantId");
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Mensajes" ADD CONSTRAINT "AK_Mensajes_Id_TenantId"
                UNIQUE ("Id", "TenantId");
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Mensajes" ADD CONSTRAINT "FK_Mensajes_Conversaciones_ConversacionId_TenantId"
                FOREIGN KEY ("ConversacionId", "TenantId")
                REFERENCES "Conversaciones" ("Id", "TenantId")
                ON DELETE CASCADE;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "ParticipantesConversacion" ADD CONSTRAINT "FK_ParticipantesConversacion_Conversaciones_ConversacionId_TenantId"
                FOREIGN KEY ("ConversacionId", "TenantId")
                REFERENCES "Conversaciones" ("Id", "TenantId")
                ON DELETE CASCADE;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "AdjuntosMensaje" ADD CONSTRAINT "FK_AdjuntosMensaje_Mensajes_MensajeId_TenantId"
                FOREIGN KEY ("MensajeId", "TenantId")
                REFERENCES "Mensajes" ("Id", "TenantId")
                ON DELETE CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "AdjuntosMensaje" DROP CONSTRAINT "FK_AdjuntosMensaje_Mensajes_MensajeId_TenantId";""");
            migrationBuilder.Sql("""ALTER TABLE "ParticipantesConversacion" DROP CONSTRAINT "FK_ParticipantesConversacion_Conversaciones_ConversacionId_TenantId";""");
            migrationBuilder.Sql("""ALTER TABLE "Mensajes" DROP CONSTRAINT "FK_Mensajes_Conversaciones_ConversacionId_TenantId";""");
            migrationBuilder.Sql("""ALTER TABLE "Mensajes" DROP CONSTRAINT "AK_Mensajes_Id_TenantId";""");
            migrationBuilder.Sql("""ALTER TABLE "Conversaciones" DROP CONSTRAINT "AK_Conversaciones_Id_TenantId";""");
        }
    }
}
