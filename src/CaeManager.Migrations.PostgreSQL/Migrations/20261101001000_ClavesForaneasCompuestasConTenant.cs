using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ClavesForaneasCompuestasConTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // M-27 (ASVS 4.0.3 V4.1.3, V4.2.1). Primero la comprobación de datos: si alguna fila hija ya cuelga de un
            // padre de otro Tenant, aborta nombrando relación y recuento, sin corregir nada.
            migrationBuilder.Sql(ComprobacionTenantClavesForaneas.SqlComprobacionPrevia);

            // Las FKs compuestas que existían solo en SQL crudo, invisibles al modelo, pasan a ser las del modelo. Cada una
            // duplicaría a la nueva, y las dos claves alternativas (Id, TenantId) se quedan sin nadie que las use.
            // AK_Conversaciones_Id_TenantId se conserva: NotasInternasConversacion y PropuestasRecordatorioConversacion
            // siguen apuntando a ella.
            migrationBuilder.Sql("""
                ALTER TABLE public."Mensajes" DROP CONSTRAINT "FK_Mensajes_Conversaciones_ConversacionId_TenantId";
                ALTER TABLE public."AdjuntosMensaje" DROP CONSTRAINT "FK_AdjuntosMensaje_Mensajes_MensajeId_TenantId";
                ALTER TABLE public."ParticipantesConversacion" DROP CONSTRAINT "FK_ParticipantesConversacion_Conversaciones_ConversacionId_Tena";
                ALTER TABLE public."PasosTareaAsistente" DROP CONSTRAINT "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId_TenantI";
                ALTER TABLE public."TurnosTareaAsistente" DROP CONSTRAINT "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId_Tenant";
                ALTER TABLE public."CambiosEstadoConversacion" DROP CONSTRAINT "FK_CambiosEstadoConversacion_Conversaciones_ConversacionId_Tena";
                ALTER TABLE public."AlertasCriticasConversacion" DROP CONSTRAINT "FK_AlertasCriticas_Conversaciones_Tenant";
                ALTER TABLE public."AlertasCriticasConversacion" DROP CONSTRAINT "FK_AlertasCriticas_Mensajes_Tenant";
                ALTER TABLE public."Mensajes" DROP CONSTRAINT "AK_Mensajes_Id_TenantId";
                ALTER TABLE public."TareasAsistente" DROP CONSTRAINT "AK_TareasAsistente_Id_TenantId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AdjuntosMensaje_Mensajes_MensajeId",
                table: "AdjuntosMensaje");

            migrationBuilder.DropForeignKey(
                name: "FK_AlertasCriticasConversacion_Conversaciones_ConversacionId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_AlertasCriticasConversacion_Mensajes_MensajeId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_CambiosEstadoAcreditacionPlataforma_AcreditacionesDocumento~",
                table: "CambiosEstadoAcreditacionPlataforma");

            migrationBuilder.DropForeignKey(
                name: "FK_CambiosEstadoConversacion_Conversaciones_ConversacionId",
                table: "CambiosEstadoConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Centros_CentroId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_ClienteId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_EmpresaId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_SubcontrataId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgendaRoles_ContactosAgenda_ContactoAgendaId",
                table: "ContactosAgendaRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_ContactosAgenda_ContactoAgend~",
                table: "ContactosAgendaTiposDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_TiposDocumento_TipoDocumentoId",
                table: "ContactosAgendaTiposDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_DetallesSugerenciaGestionCorreo_SugerenciasGestionCorreo_Su~",
                table: "DetallesSugerenciaGestionCorreo");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentosGenerados_Documentos_DocumentoId",
                table: "DocumentosGenerados");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentosGenerados_PlantillasDocumentoVersion_PlantillaDoc~",
                table: "DocumentosGenerados");

            migrationBuilder.DropForeignKey(
                name: "FK_FirmasDigitalesDocumento_Documentos_DocumentoId",
                table: "FirmasDigitalesDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_FirmasEnCampoDocumento_Documentos_DocumentoId",
                table: "FirmasEnCampoDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_IntencionesConversacion_Conversaciones_ConversacionId",
                table: "IntencionesConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_IntencionesConversacion_Mensajes_MensajeId",
                table: "IntencionesConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_LotesGeneracionDocumento_PlantillasDocumentoVersion_Plantil~",
                table: "LotesGeneracionDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_Mensajes_Conversaciones_ConversacionId",
                table: "Mensajes");

            migrationBuilder.DropForeignKey(
                name: "FK_MiembrosPoolLinea_LineasWhatsApp_LineaWhatsAppId",
                table: "MiembrosPoolLinea");

            migrationBuilder.DropForeignKey(
                name: "FK_ParticipantesConversacion_Conversaciones_ConversacionId",
                table: "ParticipantesConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId",
                table: "PasosTareaAsistente");

            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasDocumento_PlantillasDocumentoVersion_VersionActua~",
                table: "PlantillasDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasDocumentoVersion_PlantillasDocumento_PlantillaDoc~",
                table: "PlantillasDocumentoVersion");

            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasElemento_PlantillasDocumentoVersion_PlantillaDocu~",
                table: "PlantillasElemento");

            migrationBuilder.DropForeignKey(
                name: "FK_RechazosAcreditacionDocumentoPlataforma_AcreditacionesDocum~",
                table: "RechazosAcreditacionDocumentoPlataforma");

            migrationBuilder.DropForeignKey(
                name: "FK_ReclamacionesDocumentales_Conversaciones_ConversacionId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropForeignKey(
                name: "FK_ReclamacionesDocumentalesDocumentos_ReclamacionesDocumental~",
                table: "ReclamacionesDocumentalesDocumentos");

            migrationBuilder.DropForeignKey(
                name: "FK_SellosEmpresa_Empresas_EmpresaId",
                table: "SellosEmpresa");

            migrationBuilder.DropForeignKey(
                name: "FK_TiposDocumentoAlias_TiposDocumento_TipoDocumentoId",
                table: "TiposDocumentoAlias");

            migrationBuilder.DropForeignKey(
                name: "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId",
                table: "TurnosTareaAsistente");

            migrationBuilder.DropForeignKey(
                name: "FK_VerificacionesDocumentoOficial_Documentos_DocumentoId",
                table: "VerificacionesDocumentoOficial");

            migrationBuilder.DropIndex(
                name: "IX_VerificacionesDocumentoOficial_DocumentoId",
                table: "VerificacionesDocumentoOficial");

            migrationBuilder.DropIndex(
                name: "IX_SellosEmpresa_EmpresaId",
                table: "SellosEmpresa");

            migrationBuilder.DropIndex(
                name: "IX_ReclamacionesDocumentales_ConversacionId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropIndex(
                name: "IX_RechazosAcreditacionDocumentoPlataforma_AcreditacionId",
                table: "RechazosAcreditacionDocumentoPlataforma");

            migrationBuilder.DropIndex(
                name: "IX_PlantillasDocumentoVersion_PlantillaDocumentoId",
                table: "PlantillasDocumentoVersion");

            migrationBuilder.DropIndex(
                name: "IX_PlantillasDocumento_VersionActualId",
                table: "PlantillasDocumento");

            migrationBuilder.DropIndex(
                name: "IX_LotesGeneracionDocumento_PlantillaDocumentoVersionId",
                table: "LotesGeneracionDocumento");

            migrationBuilder.DropIndex(
                name: "IX_IntencionesConversacion_ConversacionId",
                table: "IntencionesConversacion");

            migrationBuilder.DropIndex(
                name: "IX_IntencionesConversacion_MensajeId",
                table: "IntencionesConversacion");

            migrationBuilder.DropIndex(
                name: "IX_FirmasEnCampoDocumento_DocumentoId",
                table: "FirmasEnCampoDocumento");

            migrationBuilder.DropIndex(
                name: "IX_FirmasDigitalesDocumento_DocumentoId",
                table: "FirmasDigitalesDocumento");

            migrationBuilder.DropIndex(
                name: "IX_DocumentosGenerados_DocumentoId",
                table: "DocumentosGenerados");

            migrationBuilder.DropIndex(
                name: "IX_DocumentosGenerados_PlantillaDocumentoVersionId",
                table: "DocumentosGenerados");

            migrationBuilder.DropIndex(
                name: "IX_ContactosAgendaTiposDocumento_TipoDocumentoId",
                table: "ContactosAgendaTiposDocumento");

            migrationBuilder.DropIndex(
                name: "IX_ContactosAgenda_CentroId",
                table: "ContactosAgenda");

            migrationBuilder.DropIndex(
                name: "IX_ContactosAgenda_ClienteId",
                table: "ContactosAgenda");

            migrationBuilder.DropIndex(
                name: "IX_ContactosAgenda_EmpresaId",
                table: "ContactosAgenda");

            migrationBuilder.DropIndex(
                name: "IX_ContactosAgenda_SubcontrataId",
                table: "ContactosAgenda");

            migrationBuilder.DropIndex(
                name: "IX_CambiosEstadoConversacion_ConversacionId",
                table: "CambiosEstadoConversacion");

            migrationBuilder.DropIndex(
                name: "IX_CambiosEstadoAcreditacionPlataforma_AcreditacionId",
                table: "CambiosEstadoAcreditacionPlataforma");

            migrationBuilder.DropIndex(
                name: "IX_AlertasCriticasConversacion_ConversacionId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.DropIndex(
                name: "IX_AlertasCriticasConversacion_MensajeId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TareasAsistente_TenantId_Id",
                table: "TareasAsistente",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SugerenciasGestionCorreo_TenantId_Id",
                table: "SugerenciasGestionCorreo",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ReclamacionesDocumentales_TenantId_Id",
                table: "ReclamacionesDocumentales",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PlantillasDocumentoVersion_TenantId_Id",
                table: "PlantillasDocumentoVersion",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PlantillasDocumento_TenantId_Id",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Mensajes_TenantId_Id",
                table: "Mensajes",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_LineasWhatsApp_TenantId_Id",
                table: "LineasWhatsApp",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Conversaciones_TenantId_Id",
                table: "Conversaciones",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ContactosAgenda_TenantId_Id",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AcreditacionesDocumentoPlataforma_TenantId_Id",
                table: "AcreditacionesDocumentoPlataforma",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TurnosTareaAsistente_TenantId_TareaAsistenteId",
                table: "TurnosTareaAsistente",
                columns: new[] { "TenantId", "TareaAsistenteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentalesDocumentos_TenantId_ReclamacionDoc~",
                table: "ReclamacionesDocumentalesDocumentos",
                columns: new[] { "TenantId", "ReclamacionDocumentalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentales_TenantId_ConversacionId",
                table: "ReclamacionesDocumentales",
                columns: new[] { "TenantId", "ConversacionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasElemento_TenantId_PlantillaDocumentoVersionId",
                table: "PlantillasElemento",
                columns: new[] { "TenantId", "PlantillaDocumentoVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumento_TenantId_VersionActualId",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "VersionActualId" });

            migrationBuilder.CreateIndex(
                name: "IX_PasosTareaAsistente_TenantId_TareaAsistenteId",
                table: "PasosTareaAsistente",
                columns: new[] { "TenantId", "TareaAsistenteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantesConversacion_TenantId_ConversacionId",
                table: "ParticipantesConversacion",
                columns: new[] { "TenantId", "ConversacionId" });

            migrationBuilder.CreateIndex(
                name: "IX_MiembrosPoolLinea_TenantId_LineaWhatsAppId",
                table: "MiembrosPoolLinea",
                columns: new[] { "TenantId", "LineaWhatsAppId" });

            migrationBuilder.CreateIndex(
                name: "IX_Mensajes_TenantId_ConversacionId",
                table: "Mensajes",
                columns: new[] { "TenantId", "ConversacionId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntencionesConversacion_TenantId_MensajeId",
                table: "IntencionesConversacion",
                columns: new[] { "TenantId", "MensajeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_TenantId_DocumentoId",
                table: "DocumentosGenerados",
                columns: new[] { "TenantId", "DocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_DetallesSugerenciaGestionCorreo_TenantId_SugerenciaGestionC~",
                table: "DetallesSugerenciaGestionCorreo",
                columns: new[] { "TenantId", "SugerenciaGestionCorreoId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgendaTiposDocumento_TenantId_TipoDocumentoId",
                table: "ContactosAgendaTiposDocumento",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertasCriticasConversacion_TenantId_MensajeId",
                table: "AlertasCriticasConversacion",
                columns: new[] { "TenantId", "MensajeId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdjuntosMensaje_TenantId_MensajeId",
                table: "AdjuntosMensaje",
                columns: new[] { "TenantId", "MensajeId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AdjuntosMensaje_Mensajes_TenantId_MensajeId",
                table: "AdjuntosMensaje",
                columns: new[] { "TenantId", "MensajeId" },
                principalTable: "Mensajes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertasCriticasConversacion_Conversaciones_TenantId_Convers~",
                table: "AlertasCriticasConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                principalTable: "Conversaciones",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertasCriticasConversacion_Mensajes_TenantId_MensajeId",
                table: "AlertasCriticasConversacion",
                columns: new[] { "TenantId", "MensajeId" },
                principalTable: "Mensajes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CambiosEstadoAcreditacionPlataforma_AcreditacionesDocumento~",
                table: "CambiosEstadoAcreditacionPlataforma",
                columns: new[] { "TenantId", "AcreditacionId" },
                principalTable: "AcreditacionesDocumentoPlataforma",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CambiosEstadoConversacion_Conversaciones_TenantId_Conversac~",
                table: "CambiosEstadoConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                principalTable: "Conversaciones",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Centros_TenantId_CentroId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "CentroId" },
                principalTable: "Centros",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_TenantId_ClienteId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "ClienteId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_TenantId_EmpresaId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_TenantId_SubcontrataId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "SubcontrataId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgendaRoles_ContactosAgenda_TenantId_ContactoAgend~",
                table: "ContactosAgendaRoles",
                columns: new[] { "TenantId", "ContactoAgendaId" },
                principalTable: "ContactosAgenda",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_ContactosAgenda_TenantId_Cont~",
                table: "ContactosAgendaTiposDocumento",
                columns: new[] { "TenantId", "ContactoAgendaId" },
                principalTable: "ContactosAgenda",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_TiposDocumento_TenantId_TipoD~",
                table: "ContactosAgendaTiposDocumento",
                columns: new[] { "TenantId", "TipoDocumentoId" },
                principalTable: "TiposDocumento",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DetallesSugerenciaGestionCorreo_SugerenciasGestionCorreo_Te~",
                table: "DetallesSugerenciaGestionCorreo",
                columns: new[] { "TenantId", "SugerenciaGestionCorreoId" },
                principalTable: "SugerenciasGestionCorreo",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentosGenerados_Documentos_TenantId_DocumentoId",
                table: "DocumentosGenerados",
                columns: new[] { "TenantId", "DocumentoId" },
                principalTable: "Documentos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentosGenerados_PlantillasDocumentoVersion_TenantId_Pla~",
                table: "DocumentosGenerados",
                columns: new[] { "TenantId", "PlantillaDocumentoVersionId" },
                principalTable: "PlantillasDocumentoVersion",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FirmasDigitalesDocumento_Documentos_TenantId_DocumentoId",
                table: "FirmasDigitalesDocumento",
                columns: new[] { "TenantId", "DocumentoId" },
                principalTable: "Documentos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FirmasEnCampoDocumento_Documentos_TenantId_DocumentoId",
                table: "FirmasEnCampoDocumento",
                columns: new[] { "TenantId", "DocumentoId" },
                principalTable: "Documentos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_IntencionesConversacion_Conversaciones_TenantId_Conversacio~",
                table: "IntencionesConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                principalTable: "Conversaciones",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_IntencionesConversacion_Mensajes_TenantId_MensajeId",
                table: "IntencionesConversacion",
                columns: new[] { "TenantId", "MensajeId" },
                principalTable: "Mensajes",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LotesGeneracionDocumento_PlantillasDocumentoVersion_TenantI~",
                table: "LotesGeneracionDocumento",
                columns: new[] { "TenantId", "PlantillaDocumentoVersionId" },
                principalTable: "PlantillasDocumentoVersion",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Mensajes_Conversaciones_TenantId_ConversacionId",
                table: "Mensajes",
                columns: new[] { "TenantId", "ConversacionId" },
                principalTable: "Conversaciones",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MiembrosPoolLinea_LineasWhatsApp_TenantId_LineaWhatsAppId",
                table: "MiembrosPoolLinea",
                columns: new[] { "TenantId", "LineaWhatsAppId" },
                principalTable: "LineasWhatsApp",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ParticipantesConversacion_Conversaciones_TenantId_Conversac~",
                table: "ParticipantesConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                principalTable: "Conversaciones",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PasosTareaAsistente_TareasAsistente_TenantId_TareaAsistente~",
                table: "PasosTareaAsistente",
                columns: new[] { "TenantId", "TareaAsistenteId" },
                principalTable: "TareasAsistente",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasDocumento_PlantillasDocumentoVersion_TenantId_Ver~",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "VersionActualId" },
                principalTable: "PlantillasDocumentoVersion",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasDocumentoVersion_PlantillasDocumento_TenantId_Pla~",
                table: "PlantillasDocumentoVersion",
                columns: new[] { "TenantId", "PlantillaDocumentoId" },
                principalTable: "PlantillasDocumento",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasElemento_PlantillasDocumentoVersion_TenantId_Plan~",
                table: "PlantillasElemento",
                columns: new[] { "TenantId", "PlantillaDocumentoVersionId" },
                principalTable: "PlantillasDocumentoVersion",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RechazosAcreditacionDocumentoPlataforma_AcreditacionesDocum~",
                table: "RechazosAcreditacionDocumentoPlataforma",
                columns: new[] { "TenantId", "AcreditacionId" },
                principalTable: "AcreditacionesDocumentoPlataforma",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReclamacionesDocumentales_Conversaciones_TenantId_Conversac~",
                table: "ReclamacionesDocumentales",
                columns: new[] { "TenantId", "ConversacionId" },
                principalTable: "Conversaciones",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReclamacionesDocumentalesDocumentos_ReclamacionesDocumental~",
                table: "ReclamacionesDocumentalesDocumentos",
                columns: new[] { "TenantId", "ReclamacionDocumentalId" },
                principalTable: "ReclamacionesDocumentales",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SellosEmpresa_Empresas_TenantId_EmpresaId",
                table: "SellosEmpresa",
                columns: new[] { "TenantId", "EmpresaId" },
                principalTable: "Empresas",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TiposDocumentoAlias_TiposDocumento_TenantId_TipoDocumentoId",
                table: "TiposDocumentoAlias",
                columns: new[] { "TenantId", "TipoDocumentoId" },
                principalTable: "TiposDocumento",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TurnosTareaAsistente_TareasAsistente_TenantId_TareaAsistent~",
                table: "TurnosTareaAsistente",
                columns: new[] { "TenantId", "TareaAsistenteId" },
                principalTable: "TareasAsistente",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VerificacionesDocumentoOficial_Documentos_TenantId_Document~",
                table: "VerificacionesDocumentoOficial",
                columns: new[] { "TenantId", "DocumentoId" },
                principalTable: "Documentos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdjuntosMensaje_Mensajes_TenantId_MensajeId",
                table: "AdjuntosMensaje");

            migrationBuilder.DropForeignKey(
                name: "FK_AlertasCriticasConversacion_Conversaciones_TenantId_Convers~",
                table: "AlertasCriticasConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_AlertasCriticasConversacion_Mensajes_TenantId_MensajeId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_CambiosEstadoAcreditacionPlataforma_AcreditacionesDocumento~",
                table: "CambiosEstadoAcreditacionPlataforma");

            migrationBuilder.DropForeignKey(
                name: "FK_CambiosEstadoConversacion_Conversaciones_TenantId_Conversac~",
                table: "CambiosEstadoConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Centros_TenantId_CentroId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_TenantId_ClienteId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_TenantId_EmpresaId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgenda_Empresas_TenantId_SubcontrataId",
                table: "ContactosAgenda");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgendaRoles_ContactosAgenda_TenantId_ContactoAgend~",
                table: "ContactosAgendaRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_ContactosAgenda_TenantId_Cont~",
                table: "ContactosAgendaTiposDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_TiposDocumento_TenantId_TipoD~",
                table: "ContactosAgendaTiposDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_DetallesSugerenciaGestionCorreo_SugerenciasGestionCorreo_Te~",
                table: "DetallesSugerenciaGestionCorreo");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentosGenerados_Documentos_TenantId_DocumentoId",
                table: "DocumentosGenerados");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentosGenerados_PlantillasDocumentoVersion_TenantId_Pla~",
                table: "DocumentosGenerados");

            migrationBuilder.DropForeignKey(
                name: "FK_FirmasDigitalesDocumento_Documentos_TenantId_DocumentoId",
                table: "FirmasDigitalesDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_FirmasEnCampoDocumento_Documentos_TenantId_DocumentoId",
                table: "FirmasEnCampoDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_IntencionesConversacion_Conversaciones_TenantId_Conversacio~",
                table: "IntencionesConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_IntencionesConversacion_Mensajes_TenantId_MensajeId",
                table: "IntencionesConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_LotesGeneracionDocumento_PlantillasDocumentoVersion_TenantI~",
                table: "LotesGeneracionDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_Mensajes_Conversaciones_TenantId_ConversacionId",
                table: "Mensajes");

            migrationBuilder.DropForeignKey(
                name: "FK_MiembrosPoolLinea_LineasWhatsApp_TenantId_LineaWhatsAppId",
                table: "MiembrosPoolLinea");

            migrationBuilder.DropForeignKey(
                name: "FK_ParticipantesConversacion_Conversaciones_TenantId_Conversac~",
                table: "ParticipantesConversacion");

            migrationBuilder.DropForeignKey(
                name: "FK_PasosTareaAsistente_TareasAsistente_TenantId_TareaAsistente~",
                table: "PasosTareaAsistente");

            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasDocumento_PlantillasDocumentoVersion_TenantId_Ver~",
                table: "PlantillasDocumento");

            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasDocumentoVersion_PlantillasDocumento_TenantId_Pla~",
                table: "PlantillasDocumentoVersion");

            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasElemento_PlantillasDocumentoVersion_TenantId_Plan~",
                table: "PlantillasElemento");

            migrationBuilder.DropForeignKey(
                name: "FK_RechazosAcreditacionDocumentoPlataforma_AcreditacionesDocum~",
                table: "RechazosAcreditacionDocumentoPlataforma");

            migrationBuilder.DropForeignKey(
                name: "FK_ReclamacionesDocumentales_Conversaciones_TenantId_Conversac~",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropForeignKey(
                name: "FK_ReclamacionesDocumentalesDocumentos_ReclamacionesDocumental~",
                table: "ReclamacionesDocumentalesDocumentos");

            migrationBuilder.DropForeignKey(
                name: "FK_SellosEmpresa_Empresas_TenantId_EmpresaId",
                table: "SellosEmpresa");

            migrationBuilder.DropForeignKey(
                name: "FK_TiposDocumentoAlias_TiposDocumento_TenantId_TipoDocumentoId",
                table: "TiposDocumentoAlias");

            migrationBuilder.DropForeignKey(
                name: "FK_TurnosTareaAsistente_TareasAsistente_TenantId_TareaAsistent~",
                table: "TurnosTareaAsistente");

            migrationBuilder.DropForeignKey(
                name: "FK_VerificacionesDocumentoOficial_Documentos_TenantId_Document~",
                table: "VerificacionesDocumentoOficial");

            migrationBuilder.DropIndex(
                name: "IX_TurnosTareaAsistente_TenantId_TareaAsistenteId",
                table: "TurnosTareaAsistente");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TareasAsistente_TenantId_Id",
                table: "TareasAsistente");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SugerenciasGestionCorreo_TenantId_Id",
                table: "SugerenciasGestionCorreo");

            migrationBuilder.DropIndex(
                name: "IX_ReclamacionesDocumentalesDocumentos_TenantId_ReclamacionDoc~",
                table: "ReclamacionesDocumentalesDocumentos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ReclamacionesDocumentales_TenantId_Id",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropIndex(
                name: "IX_ReclamacionesDocumentales_TenantId_ConversacionId",
                table: "ReclamacionesDocumentales");

            migrationBuilder.DropIndex(
                name: "IX_PlantillasElemento_TenantId_PlantillaDocumentoVersionId",
                table: "PlantillasElemento");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PlantillasDocumentoVersion_TenantId_Id",
                table: "PlantillasDocumentoVersion");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PlantillasDocumento_TenantId_Id",
                table: "PlantillasDocumento");

            migrationBuilder.DropIndex(
                name: "IX_PlantillasDocumento_TenantId_VersionActualId",
                table: "PlantillasDocumento");

            migrationBuilder.DropIndex(
                name: "IX_PasosTareaAsistente_TenantId_TareaAsistenteId",
                table: "PasosTareaAsistente");

            migrationBuilder.DropIndex(
                name: "IX_ParticipantesConversacion_TenantId_ConversacionId",
                table: "ParticipantesConversacion");

            migrationBuilder.DropIndex(
                name: "IX_MiembrosPoolLinea_TenantId_LineaWhatsAppId",
                table: "MiembrosPoolLinea");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Mensajes_TenantId_Id",
                table: "Mensajes");

            migrationBuilder.DropIndex(
                name: "IX_Mensajes_TenantId_ConversacionId",
                table: "Mensajes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_LineasWhatsApp_TenantId_Id",
                table: "LineasWhatsApp");

            migrationBuilder.DropIndex(
                name: "IX_IntencionesConversacion_TenantId_MensajeId",
                table: "IntencionesConversacion");

            migrationBuilder.DropIndex(
                name: "IX_DocumentosGenerados_TenantId_DocumentoId",
                table: "DocumentosGenerados");

            migrationBuilder.DropIndex(
                name: "IX_DetallesSugerenciaGestionCorreo_TenantId_SugerenciaGestionC~",
                table: "DetallesSugerenciaGestionCorreo");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Conversaciones_TenantId_Id",
                table: "Conversaciones");

            migrationBuilder.DropIndex(
                name: "IX_ContactosAgendaTiposDocumento_TenantId_TipoDocumentoId",
                table: "ContactosAgendaTiposDocumento");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ContactosAgenda_TenantId_Id",
                table: "ContactosAgenda");

            migrationBuilder.DropIndex(
                name: "IX_AlertasCriticasConversacion_TenantId_MensajeId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.DropIndex(
                name: "IX_AdjuntosMensaje_TenantId_MensajeId",
                table: "AdjuntosMensaje");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AcreditacionesDocumentoPlataforma_TenantId_Id",
                table: "AcreditacionesDocumentoPlataforma");

            migrationBuilder.CreateIndex(
                name: "IX_VerificacionesDocumentoOficial_DocumentoId",
                table: "VerificacionesDocumentoOficial",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_SellosEmpresa_EmpresaId",
                table: "SellosEmpresa",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentales_ConversacionId",
                table: "ReclamacionesDocumentales",
                column: "ConversacionId");

            migrationBuilder.CreateIndex(
                name: "IX_RechazosAcreditacionDocumentoPlataforma_AcreditacionId",
                table: "RechazosAcreditacionDocumentoPlataforma",
                column: "AcreditacionId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumentoVersion_PlantillaDocumentoId",
                table: "PlantillasDocumentoVersion",
                column: "PlantillaDocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumento_VersionActualId",
                table: "PlantillasDocumento",
                column: "VersionActualId");

            migrationBuilder.CreateIndex(
                name: "IX_LotesGeneracionDocumento_PlantillaDocumentoVersionId",
                table: "LotesGeneracionDocumento",
                column: "PlantillaDocumentoVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_IntencionesConversacion_ConversacionId",
                table: "IntencionesConversacion",
                column: "ConversacionId");

            migrationBuilder.CreateIndex(
                name: "IX_IntencionesConversacion_MensajeId",
                table: "IntencionesConversacion",
                column: "MensajeId");

            migrationBuilder.CreateIndex(
                name: "IX_FirmasEnCampoDocumento_DocumentoId",
                table: "FirmasEnCampoDocumento",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_FirmasDigitalesDocumento_DocumentoId",
                table: "FirmasDigitalesDocumento",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_DocumentoId",
                table: "DocumentosGenerados",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_PlantillaDocumentoVersionId",
                table: "DocumentosGenerados",
                column: "PlantillaDocumentoVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgendaTiposDocumento_TipoDocumentoId",
                table: "ContactosAgendaTiposDocumento",
                column: "TipoDocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_CentroId",
                table: "ContactosAgenda",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_ClienteId",
                table: "ContactosAgenda",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_EmpresaId",
                table: "ContactosAgenda",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_SubcontrataId",
                table: "ContactosAgenda",
                column: "SubcontrataId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoConversacion_ConversacionId",
                table: "CambiosEstadoConversacion",
                column: "ConversacionId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoAcreditacionPlataforma_AcreditacionId",
                table: "CambiosEstadoAcreditacionPlataforma",
                column: "AcreditacionId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertasCriticasConversacion_ConversacionId",
                table: "AlertasCriticasConversacion",
                column: "ConversacionId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertasCriticasConversacion_MensajeId",
                table: "AlertasCriticasConversacion",
                column: "MensajeId");

            migrationBuilder.AddForeignKey(
                name: "FK_AdjuntosMensaje_Mensajes_MensajeId",
                table: "AdjuntosMensaje",
                column: "MensajeId",
                principalTable: "Mensajes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertasCriticasConversacion_Conversaciones_ConversacionId",
                table: "AlertasCriticasConversacion",
                column: "ConversacionId",
                principalTable: "Conversaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertasCriticasConversacion_Mensajes_MensajeId",
                table: "AlertasCriticasConversacion",
                column: "MensajeId",
                principalTable: "Mensajes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CambiosEstadoAcreditacionPlataforma_AcreditacionesDocumento~",
                table: "CambiosEstadoAcreditacionPlataforma",
                column: "AcreditacionId",
                principalTable: "AcreditacionesDocumentoPlataforma",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CambiosEstadoConversacion_Conversaciones_ConversacionId",
                table: "CambiosEstadoConversacion",
                column: "ConversacionId",
                principalTable: "Conversaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Centros_CentroId",
                table: "ContactosAgenda",
                column: "CentroId",
                principalTable: "Centros",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_ClienteId",
                table: "ContactosAgenda",
                column: "ClienteId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_EmpresaId",
                table: "ContactosAgenda",
                column: "EmpresaId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgenda_Empresas_SubcontrataId",
                table: "ContactosAgenda",
                column: "SubcontrataId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgendaRoles_ContactosAgenda_ContactoAgendaId",
                table: "ContactosAgendaRoles",
                column: "ContactoAgendaId",
                principalTable: "ContactosAgenda",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_ContactosAgenda_ContactoAgend~",
                table: "ContactosAgendaTiposDocumento",
                column: "ContactoAgendaId",
                principalTable: "ContactosAgenda",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ContactosAgendaTiposDocumento_TiposDocumento_TipoDocumentoId",
                table: "ContactosAgendaTiposDocumento",
                column: "TipoDocumentoId",
                principalTable: "TiposDocumento",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DetallesSugerenciaGestionCorreo_SugerenciasGestionCorreo_Su~",
                table: "DetallesSugerenciaGestionCorreo",
                column: "SugerenciaGestionCorreoId",
                principalTable: "SugerenciasGestionCorreo",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentosGenerados_Documentos_DocumentoId",
                table: "DocumentosGenerados",
                column: "DocumentoId",
                principalTable: "Documentos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentosGenerados_PlantillasDocumentoVersion_PlantillaDoc~",
                table: "DocumentosGenerados",
                column: "PlantillaDocumentoVersionId",
                principalTable: "PlantillasDocumentoVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FirmasDigitalesDocumento_Documentos_DocumentoId",
                table: "FirmasDigitalesDocumento",
                column: "DocumentoId",
                principalTable: "Documentos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FirmasEnCampoDocumento_Documentos_DocumentoId",
                table: "FirmasEnCampoDocumento",
                column: "DocumentoId",
                principalTable: "Documentos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_IntencionesConversacion_Conversaciones_ConversacionId",
                table: "IntencionesConversacion",
                column: "ConversacionId",
                principalTable: "Conversaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_IntencionesConversacion_Mensajes_MensajeId",
                table: "IntencionesConversacion",
                column: "MensajeId",
                principalTable: "Mensajes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LotesGeneracionDocumento_PlantillasDocumentoVersion_Plantil~",
                table: "LotesGeneracionDocumento",
                column: "PlantillaDocumentoVersionId",
                principalTable: "PlantillasDocumentoVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Mensajes_Conversaciones_ConversacionId",
                table: "Mensajes",
                column: "ConversacionId",
                principalTable: "Conversaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MiembrosPoolLinea_LineasWhatsApp_LineaWhatsAppId",
                table: "MiembrosPoolLinea",
                column: "LineaWhatsAppId",
                principalTable: "LineasWhatsApp",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ParticipantesConversacion_Conversaciones_ConversacionId",
                table: "ParticipantesConversacion",
                column: "ConversacionId",
                principalTable: "Conversaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId",
                table: "PasosTareaAsistente",
                column: "TareaAsistenteId",
                principalTable: "TareasAsistente",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasDocumento_PlantillasDocumentoVersion_VersionActua~",
                table: "PlantillasDocumento",
                column: "VersionActualId",
                principalTable: "PlantillasDocumentoVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasDocumentoVersion_PlantillasDocumento_PlantillaDoc~",
                table: "PlantillasDocumentoVersion",
                column: "PlantillaDocumentoId",
                principalTable: "PlantillasDocumento",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasElemento_PlantillasDocumentoVersion_PlantillaDocu~",
                table: "PlantillasElemento",
                column: "PlantillaDocumentoVersionId",
                principalTable: "PlantillasDocumentoVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RechazosAcreditacionDocumentoPlataforma_AcreditacionesDocum~",
                table: "RechazosAcreditacionDocumentoPlataforma",
                column: "AcreditacionId",
                principalTable: "AcreditacionesDocumentoPlataforma",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReclamacionesDocumentales_Conversaciones_ConversacionId",
                table: "ReclamacionesDocumentales",
                column: "ConversacionId",
                principalTable: "Conversaciones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReclamacionesDocumentalesDocumentos_ReclamacionesDocumental~",
                table: "ReclamacionesDocumentalesDocumentos",
                column: "ReclamacionDocumentalId",
                principalTable: "ReclamacionesDocumentales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SellosEmpresa_Empresas_EmpresaId",
                table: "SellosEmpresa",
                column: "EmpresaId",
                principalTable: "Empresas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TiposDocumentoAlias_TiposDocumento_TipoDocumentoId",
                table: "TiposDocumentoAlias",
                column: "TipoDocumentoId",
                principalTable: "TiposDocumento",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId",
                table: "TurnosTareaAsistente",
                column: "TareaAsistenteId",
                principalTable: "TareasAsistente",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VerificacionesDocumentoOficial_Documentos_DocumentoId",
                table: "VerificacionesDocumentoOficial",
                column: "DocumentoId",
                principalTable: "Documentos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("""
                ALTER TABLE public."TareasAsistente" ADD CONSTRAINT "AK_TareasAsistente_Id_TenantId" UNIQUE ("Id", "TenantId");
                ALTER TABLE public."Mensajes" ADD CONSTRAINT "AK_Mensajes_Id_TenantId" UNIQUE ("Id", "TenantId");
                ALTER TABLE public."AlertasCriticasConversacion" ADD CONSTRAINT "FK_AlertasCriticas_Mensajes_Tenant" FOREIGN KEY ("MensajeId", "TenantId") REFERENCES public."Mensajes"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."AlertasCriticasConversacion" ADD CONSTRAINT "FK_AlertasCriticas_Conversaciones_Tenant" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."CambiosEstadoConversacion" ADD CONSTRAINT "FK_CambiosEstadoConversacion_Conversaciones_ConversacionId_Tena" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."TurnosTareaAsistente" ADD CONSTRAINT "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId_Tenant" FOREIGN KEY ("TareaAsistenteId", "TenantId") REFERENCES public."TareasAsistente"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."PasosTareaAsistente" ADD CONSTRAINT "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId_TenantI" FOREIGN KEY ("TareaAsistenteId", "TenantId") REFERENCES public."TareasAsistente"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."ParticipantesConversacion" ADD CONSTRAINT "FK_ParticipantesConversacion_Conversaciones_ConversacionId_Tena" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."AdjuntosMensaje" ADD CONSTRAINT "FK_AdjuntosMensaje_Mensajes_MensajeId_TenantId" FOREIGN KEY ("MensajeId", "TenantId") REFERENCES public."Mensajes"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."Mensajes" ADD CONSTRAINT "FK_Mensajes_Conversaciones_ConversacionId_TenantId" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;
                """);
        }
    }
}
