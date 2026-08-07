using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Paso 0 del rediseño Communication Workspace (docs/COMUNICACIONES.md
    /// § 16.2): salda la deuda nominal de la Fase 84 renombrando
    /// ConversacionCorreo/MensajeCorreo/AdjuntoMensajeCorreo a
    /// Conversacion/Mensaje/AdjuntoMensaje. El scaffolding de EF generó
    /// Drop/Create (no detecta renombrados); se reescribió a mano como
    /// renames puros — sin pérdida de datos. Las políticas RLS
    /// (aislamiento_tenant) están adheridas a la tabla y sobreviven al
    /// RENAME; los PK/FK se renombran con SQL porque MigrationBuilder no
    /// tiene RenameConstraint.
    /// </summary>
    public partial class RenombrarConversacionMensaje : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Columnas en tablas satélite que referencian a Mensaje/Conversacion ---
            migrationBuilder.RenameColumn(
                name: "MensajeCorreoId",
                table: "SugerenciasVisitaCorreo",
                newName: "MensajeId");

            migrationBuilder.RenameIndex(
                name: "IX_SugerenciasVisitaCorreo_MensajeCorreoId",
                table: "SugerenciasVisitaCorreo",
                newName: "IX_SugerenciasVisitaCorreo_MensajeId");

            migrationBuilder.RenameColumn(
                name: "MensajeCorreoId",
                table: "SugerenciasGestionCorreo",
                newName: "MensajeId");

            migrationBuilder.RenameIndex(
                name: "IX_SugerenciasGestionCorreo_MensajeCorreoId",
                table: "SugerenciasGestionCorreo",
                newName: "IX_SugerenciasGestionCorreo_MensajeId");

            migrationBuilder.RenameColumn(
                name: "ConversacionCorreoId",
                table: "ParticipantesConversacion",
                newName: "ConversacionId");

            migrationBuilder.RenameIndex(
                name: "IX_ParticipantesConversacion_ConversacionCorreoId",
                table: "ParticipantesConversacion",
                newName: "IX_ParticipantesConversacion_ConversacionId");

            migrationBuilder.RenameColumn(
                name: "MensajeCorreoOrigenId",
                table: "Gestiones",
                newName: "MensajeOrigenId");

            // --- Tablas del agregado ---
            migrationBuilder.RenameTable(
                name: "ConversacionesCorreo",
                newName: "Conversaciones");

            migrationBuilder.RenameTable(
                name: "MensajesCorreo",
                newName: "Mensajes");

            migrationBuilder.RenameTable(
                name: "AdjuntosMensajeCorreo",
                newName: "AdjuntosMensaje");

            // --- Columnas del agregado ---
            migrationBuilder.RenameColumn(
                name: "ConversacionCorreoId",
                table: "Mensajes",
                newName: "ConversacionId");

            migrationBuilder.RenameColumn(
                name: "RemitenteEmail",
                table: "Mensajes",
                newName: "Remitente");

            migrationBuilder.RenameColumn(
                name: "MensajeCorreoId",
                table: "AdjuntosMensaje",
                newName: "MensajeId");

            // --- Índices (no se renombran solos al renombrar la tabla) ---
            migrationBuilder.RenameIndex(
                name: "IX_ConversacionesCorreo_FechaUltimoMensajeUtc",
                table: "Conversaciones",
                newName: "IX_Conversaciones_FechaUltimoMensajeUtc");

            migrationBuilder.RenameIndex(
                name: "IX_ConversacionesCorreo_TenantId_Canal_Estado",
                table: "Conversaciones",
                newName: "IX_Conversaciones_TenantId_Canal_Estado");

            migrationBuilder.RenameIndex(
                name: "IX_ConversacionesCorreo_TenantId_ClienteId",
                table: "Conversaciones",
                newName: "IX_Conversaciones_TenantId_ClienteId");

            migrationBuilder.RenameIndex(
                name: "IX_ConversacionesCorreo_TenantId_ConexionIntegracionId_Telefon~",
                table: "Conversaciones",
                newName: "IX_Conversaciones_TenantId_ConexionIntegracionId_TelefonoConta~");

            migrationBuilder.RenameIndex(
                name: "IX_ConversacionesCorreo_TenantId_Estado",
                table: "Conversaciones",
                newName: "IX_Conversaciones_TenantId_Estado");

            migrationBuilder.RenameIndex(
                name: "IX_ConversacionesCorreo_TenantId_HiloExternoId",
                table: "Conversaciones",
                newName: "IX_Conversaciones_TenantId_HiloExternoId");

            migrationBuilder.RenameIndex(
                name: "IX_MensajesCorreo_ConversacionCorreoId",
                table: "Mensajes",
                newName: "IX_Mensajes_ConversacionId");

            migrationBuilder.RenameIndex(
                name: "IX_MensajesCorreo_FechaUtc",
                table: "Mensajes",
                newName: "IX_Mensajes_FechaUtc");

            migrationBuilder.RenameIndex(
                name: "IX_MensajesCorreo_TenantId_MensajeExternoId",
                table: "Mensajes",
                newName: "IX_Mensajes_TenantId_MensajeExternoId");

            migrationBuilder.RenameIndex(
                name: "IX_AdjuntosMensajeCorreo_MensajeCorreoId",
                table: "AdjuntosMensaje",
                newName: "IX_AdjuntosMensaje_MensajeId");

            // Índice trigram creado por SQL crudo en RendimientoBusquedasYCheckXorDocumento
            // — no vive en el modelo EF, así que el diff no lo ve. IF EXISTS por si el
            // entorno no tiene pg_trgm (la migración original lo crea condicionalmente).
            migrationBuilder.Sql("""ALTER INDEX IF EXISTS "IX_ConversacionesCorreo_Asunto_Trgm" RENAME TO "IX_Conversaciones_Asunto_Trgm";""");

            // --- PK/FK: MigrationBuilder no tiene RenameConstraint ---
            migrationBuilder.Sql("""ALTER TABLE "Conversaciones" RENAME CONSTRAINT "PK_ConversacionesCorreo" TO "PK_Conversaciones";""");
            migrationBuilder.Sql("""ALTER TABLE "Mensajes" RENAME CONSTRAINT "PK_MensajesCorreo" TO "PK_Mensajes";""");
            migrationBuilder.Sql("""ALTER TABLE "AdjuntosMensaje" RENAME CONSTRAINT "PK_AdjuntosMensajeCorreo" TO "PK_AdjuntosMensaje";""");
            migrationBuilder.Sql("""ALTER TABLE "Mensajes" RENAME CONSTRAINT "FK_MensajesCorreo_ConversacionesCorreo_ConversacionCorreoId" TO "FK_Mensajes_Conversaciones_ConversacionId";""");
            migrationBuilder.Sql("""ALTER TABLE "AdjuntosMensaje" RENAME CONSTRAINT "FK_AdjuntosMensajeCorreo_MensajesCorreo_MensajeCorreoId" TO "FK_AdjuntosMensaje_Mensajes_MensajeId";""");
            migrationBuilder.Sql("""ALTER TABLE "ParticipantesConversacion" RENAME CONSTRAINT "FK_ParticipantesConversacion_ConversacionesCorreo_Conversacion~" TO "FK_ParticipantesConversacion_Conversaciones_ConversacionId";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER INDEX IF EXISTS "IX_Conversaciones_Asunto_Trgm" RENAME TO "IX_ConversacionesCorreo_Asunto_Trgm";""");
            migrationBuilder.Sql("""ALTER TABLE "ParticipantesConversacion" RENAME CONSTRAINT "FK_ParticipantesConversacion_Conversaciones_ConversacionId" TO "FK_ParticipantesConversacion_ConversacionesCorreo_Conversacion~";""");
            migrationBuilder.Sql("""ALTER TABLE "AdjuntosMensaje" RENAME CONSTRAINT "FK_AdjuntosMensaje_Mensajes_MensajeId" TO "FK_AdjuntosMensajeCorreo_MensajesCorreo_MensajeCorreoId";""");
            migrationBuilder.Sql("""ALTER TABLE "Mensajes" RENAME CONSTRAINT "FK_Mensajes_Conversaciones_ConversacionId" TO "FK_MensajesCorreo_ConversacionesCorreo_ConversacionCorreoId";""");
            migrationBuilder.Sql("""ALTER TABLE "AdjuntosMensaje" RENAME CONSTRAINT "PK_AdjuntosMensaje" TO "PK_AdjuntosMensajeCorreo";""");
            migrationBuilder.Sql("""ALTER TABLE "Mensajes" RENAME CONSTRAINT "PK_Mensajes" TO "PK_MensajesCorreo";""");
            migrationBuilder.Sql("""ALTER TABLE "Conversaciones" RENAME CONSTRAINT "PK_Conversaciones" TO "PK_ConversacionesCorreo";""");

            migrationBuilder.RenameIndex(
                name: "IX_AdjuntosMensaje_MensajeId",
                table: "AdjuntosMensaje",
                newName: "IX_AdjuntosMensajeCorreo_MensajeCorreoId");

            migrationBuilder.RenameIndex(
                name: "IX_Mensajes_TenantId_MensajeExternoId",
                table: "Mensajes",
                newName: "IX_MensajesCorreo_TenantId_MensajeExternoId");

            migrationBuilder.RenameIndex(
                name: "IX_Mensajes_FechaUtc",
                table: "Mensajes",
                newName: "IX_MensajesCorreo_FechaUtc");

            migrationBuilder.RenameIndex(
                name: "IX_Mensajes_ConversacionId",
                table: "Mensajes",
                newName: "IX_MensajesCorreo_ConversacionCorreoId");

            migrationBuilder.RenameIndex(
                name: "IX_Conversaciones_TenantId_HiloExternoId",
                table: "Conversaciones",
                newName: "IX_ConversacionesCorreo_TenantId_HiloExternoId");

            migrationBuilder.RenameIndex(
                name: "IX_Conversaciones_TenantId_Estado",
                table: "Conversaciones",
                newName: "IX_ConversacionesCorreo_TenantId_Estado");

            migrationBuilder.RenameIndex(
                name: "IX_Conversaciones_TenantId_ConexionIntegracionId_TelefonoConta~",
                table: "Conversaciones",
                newName: "IX_ConversacionesCorreo_TenantId_ConexionIntegracionId_Telefon~");

            migrationBuilder.RenameIndex(
                name: "IX_Conversaciones_TenantId_ClienteId",
                table: "Conversaciones",
                newName: "IX_ConversacionesCorreo_TenantId_ClienteId");

            migrationBuilder.RenameIndex(
                name: "IX_Conversaciones_TenantId_Canal_Estado",
                table: "Conversaciones",
                newName: "IX_ConversacionesCorreo_TenantId_Canal_Estado");

            migrationBuilder.RenameIndex(
                name: "IX_Conversaciones_FechaUltimoMensajeUtc",
                table: "Conversaciones",
                newName: "IX_ConversacionesCorreo_FechaUltimoMensajeUtc");

            migrationBuilder.RenameColumn(
                name: "MensajeId",
                table: "AdjuntosMensaje",
                newName: "MensajeCorreoId");

            migrationBuilder.RenameColumn(
                name: "Remitente",
                table: "Mensajes",
                newName: "RemitenteEmail");

            migrationBuilder.RenameColumn(
                name: "ConversacionId",
                table: "Mensajes",
                newName: "ConversacionCorreoId");

            migrationBuilder.RenameTable(
                name: "AdjuntosMensaje",
                newName: "AdjuntosMensajeCorreo");

            migrationBuilder.RenameTable(
                name: "Mensajes",
                newName: "MensajesCorreo");

            migrationBuilder.RenameTable(
                name: "Conversaciones",
                newName: "ConversacionesCorreo");

            migrationBuilder.RenameColumn(
                name: "MensajeOrigenId",
                table: "Gestiones",
                newName: "MensajeCorreoOrigenId");

            migrationBuilder.RenameIndex(
                name: "IX_ParticipantesConversacion_ConversacionId",
                table: "ParticipantesConversacion",
                newName: "IX_ParticipantesConversacion_ConversacionCorreoId");

            migrationBuilder.RenameColumn(
                name: "ConversacionId",
                table: "ParticipantesConversacion",
                newName: "ConversacionCorreoId");

            migrationBuilder.RenameIndex(
                name: "IX_SugerenciasGestionCorreo_MensajeId",
                table: "SugerenciasGestionCorreo",
                newName: "IX_SugerenciasGestionCorreo_MensajeCorreoId");

            migrationBuilder.RenameColumn(
                name: "MensajeId",
                table: "SugerenciasGestionCorreo",
                newName: "MensajeCorreoId");

            migrationBuilder.RenameIndex(
                name: "IX_SugerenciasVisitaCorreo_MensajeId",
                table: "SugerenciasVisitaCorreo",
                newName: "IX_SugerenciasVisitaCorreo_MensajeCorreoId");

            migrationBuilder.RenameColumn(
                name: "MensajeId",
                table: "SugerenciasVisitaCorreo",
                newName: "MensajeCorreoId");
        }
    }
}
