using System.Text;

namespace CaeManager.Migrations.PostgreSQL;

/// <summary>
/// Comprobación previa de la migración <c>ClavesForaneasCompuestasConTenant</c> (ASVS 4.0.3 V4.1.3, V4.2.1):
/// antes de componer con <c>TenantId</c> las FKs de <see cref="Relaciones"/>, cuenta por cada una las filas hijas cuyo
/// padre es de otro Tenant y, si hay alguna, aborta nombrando relación y recuento. No corrige nada: esas filas son datos
/// de un Tenant colgados de otro, y decidir a quién pertenecen no es trabajo de una migración. La migración corre en
/// transacción, así que abortar deja el esquema como estaba.
///
/// <para>
/// El recuento tiene que ver todas las filas. Las tablas llevan FORCE ROW LEVEL SECURITY, y un rol sujeto a RLS vería
/// cero filas sin ningún error: un «no hay filas cruzadas» falso. Por eso, como en <see cref="ParticionadoMensualEventos"/>,
/// la comprobación exige un rol que evita la RLS y aborta si no lo es, sin debilitar ninguna política.
/// </para>
/// </summary>
public static class ComprobacionTenantClavesForaneas
{
    /// <summary>Las relaciones que la migración compone: tabla hija, columna FK y tabla padre.</summary>
    public static readonly IReadOnlyList<(string Hija, string Columna, string Padre)> Relaciones =
    [
        ("PasosTareaAsistente", "TareaAsistenteId", "TareasAsistente"),
        ("TurnosTareaAsistente", "TareaAsistenteId", "TareasAsistente"),
        ("AdjuntosMensaje", "MensajeId", "Mensajes"),
        ("AlertasCriticasConversacion", "ConversacionId", "Conversaciones"),
        ("AlertasCriticasConversacion", "MensajeId", "Mensajes"),
        ("CambiosEstadoConversacion", "ConversacionId", "Conversaciones"),
        ("DetallesSugerenciaGestionCorreo", "SugerenciaGestionCorreoId", "SugerenciasGestionCorreo"),
        ("IntencionesConversacion", "ConversacionId", "Conversaciones"),
        ("IntencionesConversacion", "MensajeId", "Mensajes"),
        ("Mensajes", "ConversacionId", "Conversaciones"),
        ("ParticipantesConversacion", "ConversacionId", "Conversaciones"),
        ("ContactosAgenda", "CentroId", "Centros"),
        ("ContactosAgenda", "ClienteId", "Empresas"),
        ("ContactosAgenda", "EmpresaId", "Empresas"),
        ("ContactosAgenda", "SubcontrataId", "Empresas"),
        ("ContactosAgendaRoles", "ContactoAgendaId", "ContactosAgenda"),
        ("ContactosAgendaTiposDocumento", "ContactoAgendaId", "ContactosAgenda"),
        ("ContactosAgendaTiposDocumento", "TipoDocumentoId", "TiposDocumento"),
        ("CambiosEstadoAcreditacionPlataforma", "AcreditacionId", "AcreditacionesDocumentoPlataforma"),
        ("RechazosAcreditacionDocumentoPlataforma", "AcreditacionId", "AcreditacionesDocumentoPlataforma"),
        ("FirmasDigitalesDocumento", "DocumentoId", "Documentos"),
        ("FirmasEnCampoDocumento", "DocumentoId", "Documentos"),
        ("VerificacionesDocumentoOficial", "DocumentoId", "Documentos"),
        ("SellosEmpresa", "EmpresaId", "Empresas"),
        ("TiposDocumentoAlias", "TipoDocumentoId", "TiposDocumento"),
        ("MiembrosPoolLinea", "LineaWhatsAppId", "LineasWhatsApp"),
        ("DocumentosGenerados", "DocumentoId", "Documentos"),
        ("DocumentosGenerados", "PlantillaDocumentoVersionId", "PlantillasDocumentoVersion"),
        ("LotesGeneracionDocumento", "PlantillaDocumentoVersionId", "PlantillasDocumentoVersion"),
        ("PlantillasDocumento", "VersionActualId", "PlantillasDocumentoVersion"),
        ("PlantillasDocumentoVersion", "PlantillaDocumentoId", "PlantillasDocumento"),
        ("PlantillasElemento", "PlantillaDocumentoVersionId", "PlantillasDocumentoVersion"),
        ("ReclamacionesDocumentales", "ConversacionId", "Conversaciones"),
        ("ReclamacionesDocumentalesDocumentos", "ReclamacionDocumentalId", "ReclamacionesDocumentales"),
    ];

    /// <summary>
    /// El bloque SQL de la comprobación. Público para que el test de integración lo ejecute contra una fila cruzada
    /// fabricada, sin tener que rebobinar migraciones.
    /// </summary>
    public static string SqlComprobacionPrevia { get; } = Construir();

    private static string Construir()
    {
        var recuentos = new StringBuilder();
        foreach (var (hija, columna, padre) in Relaciones)
        {
            recuentos.Append($$"""

                SELECT count(*) INTO v_recuento
                  FROM public."{{hija}}" h
                  JOIN public."{{padre}}" p ON p."Id" = h."{{columna}}"
                 WHERE p."TenantId" <> h."TenantId";
                IF v_recuento > 0 THEN
                    v_detalle := v_detalle || format(E'\n  %s.%s -> %s: %s filas', '{{hija}}', '{{columna}}', '{{padre}}', v_recuento);
                END IF;

            """);
        }

        return $$"""
            DO $comprobacion$
            DECLARE
                v_recuento bigint;
                v_detalle text := '';
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'M-27: el rol % no evita la RLS; bajo FORCE ROW LEVEL SECURITY el recuento de filas cruzadas saldría a cero sin error', current_user;
                END IF;
            {{recuentos}}
                IF v_detalle <> '' THEN
                    RAISE EXCEPTION 'M-27: hay filas hijas cuyo padre es de otro Tenant; no se corrige nada y la migración se aborta:%', v_detalle;
                END IF;
            END
            $comprobacion$;
            """;
    }
}
