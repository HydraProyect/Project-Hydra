using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.TiposDocumento;

/// <summary>
/// Envuelve las listas en <see cref="TestAsyncQueryable{T}"/> (ver
/// IntegracionesQueryContextFalso) porque un <c>List.AsQueryable()</c> a
/// secas no soporta los operadores asíncronos de EF Core que usa
/// FirmarDocumentoEnCampoCommandHandler (FirstOrDefaultAsync).
/// </summary>
public class TiposDocumentoQueryContextFalso : ITiposDocumentoQueryContext
{
    public List<CaeManager.Domain.Documentos.TipoDocumento> ListaTiposDocumento { get; } = [];
    public List<TipoDocumentoCentro> ListaTiposDocumentoCentros { get; } = [];
    public List<ConfiguracionIaDocumentoCliente> ListaConfiguracionesIaDocumentoCliente { get; } = [];

    public IQueryable<CaeManager.Domain.Documentos.TipoDocumento> TiposDocumento =>
        new TestAsyncQueryable<CaeManager.Domain.Documentos.TipoDocumento>(ListaTiposDocumento.AsQueryable());
    public IQueryable<TipoDocumentoCentro> TiposDocumentoCentros =>
        new TestAsyncQueryable<TipoDocumentoCentro>(ListaTiposDocumentoCentros.AsQueryable());
    public IQueryable<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente =>
        new TestAsyncQueryable<ConfiguracionIaDocumentoCliente>(ListaConfiguracionesIaDocumentoCliente.AsQueryable());
}
