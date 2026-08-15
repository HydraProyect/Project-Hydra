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
}
