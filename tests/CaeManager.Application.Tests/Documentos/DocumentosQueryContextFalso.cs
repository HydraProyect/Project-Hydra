using CaeManager.Application.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>Envuelve las listas en TestAsyncQueryable (ver IntegracionesQueryContextFalso) para soportar ToListAsync/FirstOrDefaultAsync.</summary>
public class DocumentosQueryContextFalso : IDocumentosQueryContext
{
    public List<Documento> ListaDocumentos { get; } = [];
    public List<RevisionIaDocumento> ListaRevisionesIaDocumento { get; } = [];
    public List<AprobacionDocumento> ListaAprobacionesDocumento { get; } = [];
    public List<VerificacionDocumentoOficial> ListaVerificacionesDocumentoOficial { get; } = [];
    public List<FirmaDigitalDocumento> ListaFirmasDigitalesDocumento { get; } = [];
    public List<FirmaEnCampoDocumento> ListaFirmasEnCampoDocumento { get; } = [];
    public List<AcreditacionDocumentoPlataforma> ListaAcreditacionesDocumentoPlataforma { get; } = [];
    public List<RechazoAcreditacionDocumentoPlataforma> ListaRechazosAcreditacionDocumentoPlataforma { get; } = [];

    public IQueryable<Documento> Documentos => new TestAsyncQueryable<Documento>(ListaDocumentos.AsQueryable());
    public IQueryable<RevisionIaDocumento> RevisionesIaDocumento => new TestAsyncQueryable<RevisionIaDocumento>(ListaRevisionesIaDocumento.AsQueryable());
    public IQueryable<AprobacionDocumento> AprobacionesDocumento => new TestAsyncQueryable<AprobacionDocumento>(ListaAprobacionesDocumento.AsQueryable());
    public IQueryable<VerificacionDocumentoOficial> VerificacionesDocumentoOficial => new TestAsyncQueryable<VerificacionDocumentoOficial>(ListaVerificacionesDocumentoOficial.AsQueryable());
    public IQueryable<FirmaDigitalDocumento> FirmasDigitalesDocumento => new TestAsyncQueryable<FirmaDigitalDocumento>(ListaFirmasDigitalesDocumento.AsQueryable());
    public IQueryable<FirmaEnCampoDocumento> FirmasEnCampoDocumento => new TestAsyncQueryable<FirmaEnCampoDocumento>(ListaFirmasEnCampoDocumento.AsQueryable());
    public IQueryable<AcreditacionDocumentoPlataforma> AcreditacionesDocumentoPlataforma => new TestAsyncQueryable<AcreditacionDocumentoPlataforma>(ListaAcreditacionesDocumentoPlataforma.AsQueryable());
    public IQueryable<RechazoAcreditacionDocumentoPlataforma> RechazosAcreditacionDocumentoPlataforma => new TestAsyncQueryable<RechazoAcreditacionDocumentoPlataforma>(ListaRechazosAcreditacionDocumentoPlataforma.AsQueryable());
}
