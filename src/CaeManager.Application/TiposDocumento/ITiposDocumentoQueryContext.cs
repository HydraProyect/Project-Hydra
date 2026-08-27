using CaeManager.Domain.Documentos;

namespace CaeManager.Application.TiposDocumento;

public interface ITiposDocumentoQueryContext
{
    IQueryable<TipoDocumento> TiposDocumento { get; }
    IQueryable<TipoDocumentoCentro> TiposDocumentoCentros { get; }
    IQueryable<TipoDocumentoAlias> TiposDocumentoAlias { get; }
    IQueryable<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente { get; }
}
