using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

public interface IDocumentosQueryContext
{
    IQueryable<Documento> Documentos { get; }
    IQueryable<RevisionIaDocumento> RevisionesIaDocumento { get; }
    IQueryable<AprobacionDocumento> AprobacionesDocumento { get; }
    IQueryable<VerificacionDocumentoOficial> VerificacionesDocumentoOficial { get; }
    IQueryable<FirmaDigitalDocumento> FirmasDigitalesDocumento { get; }
    IQueryable<FirmaEnCampoDocumento> FirmasEnCampoDocumento { get; }
    IQueryable<FirmaGuardadaUsuario> FirmasGuardadasUsuario { get; }
    IQueryable<SelloEmpresa> SellosEmpresa { get; }
    IQueryable<AcreditacionDocumentoPlataforma> AcreditacionesDocumentoPlataforma { get; }
    IQueryable<RechazoAcreditacionDocumentoPlataforma> RechazosAcreditacionDocumentoPlataforma { get; }

    /// <summary>Historial de presentaciones de un Documento a un Centro: el ancla de la periodicidad especial (<see cref="VigenciaEnCentro.CargarUltimasPresentacionesAsync"/>).</summary>
    IQueryable<PresentacionDocumentoEnCentro> PresentacionesDocumentoEnCentro { get; }
}
