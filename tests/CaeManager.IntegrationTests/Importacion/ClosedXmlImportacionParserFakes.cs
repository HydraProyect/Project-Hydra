using System.Linq.Expressions;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore.Query;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Fakes en memoria de los seis IxxxQueryContext que consume
/// <see cref="CaeManager.Infrastructure.Importacion.ClosedXmlImportacionParser"/>,
/// para probarlo sin PostgreSQL (el parser no escribe nada — la invariante que
/// prueba esta suite es puramente de análisis). Un <c>List.AsQueryable()</c>
/// normal no soporta los operadores asíncronos de EF Core (<c>ToListAsync</c>)
/// que usa el parser porque su proveedor LINQ-to-Objects no implementa
/// <see cref="IAsyncEnumerable{T}"/> — de ahí <see cref="TestAsyncQueryable{T}"/>,
/// el mismo patrón que <c>CaeManager.Application.Tests.Integraciones.TestAsyncQueryable</c>
/// (proyectos distintos, sin referencia entre sí).
/// </summary>
internal sealed class EmpresasQueryContextFalso : IEmpresasQueryContext
{
    public List<Empresa> ListaEmpresas { get; } = [];

    public IQueryable<Empresa> Empresas => new TestAsyncQueryable<Empresa>(ListaEmpresas.AsQueryable());
    public IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa => new TestAsyncQueryable<CredencialAccesoEmpresa>(Enumerable.Empty<CredencialAccesoEmpresa>().AsQueryable());
    public IQueryable<RelacionEmpresarial> RelacionesEmpresariales => new TestAsyncQueryable<RelacionEmpresarial>(Enumerable.Empty<RelacionEmpresarial>().AsQueryable());
}

internal sealed class CentrosQueryContextFalso : ICentrosQueryContext
{
    public List<Centro> ListaCentros { get; } = [];

    public IQueryable<Centro> Centros => new TestAsyncQueryable<Centro>(ListaCentros.AsQueryable());
    public IQueryable<CanalGestionDocumental> CanalesGestionDocumental => new TestAsyncQueryable<CanalGestionDocumental>(Enumerable.Empty<CanalGestionDocumental>().AsQueryable());
}

internal sealed class TrabajadoresQueryContextFalso : ITrabajadoresQueryContext
{
    public List<Trabajador> ListaTrabajadores { get; } = [];

    public IQueryable<Trabajador> Trabajadores => new TestAsyncQueryable<Trabajador>(ListaTrabajadores.AsQueryable());
    public IQueryable<DeteccionTrabajador> DeteccionesTrabajador => new TestAsyncQueryable<DeteccionTrabajador>(Enumerable.Empty<DeteccionTrabajador>().AsQueryable());
}

internal sealed class DocumentosQueryContextFalso : IDocumentosQueryContext
{
    public List<Documento> ListaDocumentos { get; } = [];

    public IQueryable<Documento> Documentos => new TestAsyncQueryable<Documento>(ListaDocumentos.AsQueryable());
    public IQueryable<RevisionIaDocumento> RevisionesIaDocumento => new TestAsyncQueryable<RevisionIaDocumento>(Enumerable.Empty<RevisionIaDocumento>().AsQueryable());
    public IQueryable<AprobacionDocumento> AprobacionesDocumento => new TestAsyncQueryable<AprobacionDocumento>(Enumerable.Empty<AprobacionDocumento>().AsQueryable());
    public IQueryable<VerificacionDocumentoOficial> VerificacionesDocumentoOficial => new TestAsyncQueryable<VerificacionDocumentoOficial>(Enumerable.Empty<VerificacionDocumentoOficial>().AsQueryable());
    public IQueryable<FirmaDigitalDocumento> FirmasDigitalesDocumento => new TestAsyncQueryable<FirmaDigitalDocumento>(Enumerable.Empty<FirmaDigitalDocumento>().AsQueryable());
    public IQueryable<FirmaEnCampoDocumento> FirmasEnCampoDocumento => new TestAsyncQueryable<FirmaEnCampoDocumento>(Enumerable.Empty<FirmaEnCampoDocumento>().AsQueryable());
    public IQueryable<FirmaGuardadaUsuario> FirmasGuardadasUsuario => new TestAsyncQueryable<FirmaGuardadaUsuario>(Enumerable.Empty<FirmaGuardadaUsuario>().AsQueryable());
    public IQueryable<SelloEmpresa> SellosEmpresa => new TestAsyncQueryable<SelloEmpresa>(Enumerable.Empty<SelloEmpresa>().AsQueryable());
    public IQueryable<AcreditacionDocumentoPlataforma> AcreditacionesDocumentoPlataforma => new TestAsyncQueryable<AcreditacionDocumentoPlataforma>(Enumerable.Empty<AcreditacionDocumentoPlataforma>().AsQueryable());
    public IQueryable<RechazoAcreditacionDocumentoPlataforma> RechazosAcreditacionDocumentoPlataforma => new TestAsyncQueryable<RechazoAcreditacionDocumentoPlataforma>(Enumerable.Empty<RechazoAcreditacionDocumentoPlataforma>().AsQueryable());
}

internal sealed class TiposDocumentoQueryContextFalso : ITiposDocumentoQueryContext
{
    public List<TipoDocumento> ListaTiposDocumento { get; } = [];

    public IQueryable<TipoDocumento> TiposDocumento => new TestAsyncQueryable<TipoDocumento>(ListaTiposDocumento.AsQueryable());
    public IQueryable<TipoDocumentoCentro> TiposDocumentoCentros => new TestAsyncQueryable<TipoDocumentoCentro>(Enumerable.Empty<TipoDocumentoCentro>().AsQueryable());
    public IQueryable<TipoDocumentoAlias> TiposDocumentoAlias => new TestAsyncQueryable<TipoDocumentoAlias>(Enumerable.Empty<TipoDocumentoAlias>().AsQueryable());
    public IQueryable<ConfiguracionIaDocumentoCliente> ConfiguracionesIaDocumentoCliente => new TestAsyncQueryable<ConfiguracionIaDocumentoCliente>(Enumerable.Empty<ConfiguracionIaDocumentoCliente>().AsQueryable());
}

internal sealed class AsignacionesQueryContextFalso : IAsignacionesQueryContext
{
    public List<Asignacion> ListaAsignaciones { get; } = [];

    public IQueryable<Asignacion> Asignaciones => new TestAsyncQueryable<Asignacion>(ListaAsignaciones.AsQueryable());
}

internal sealed class TestAsyncQueryable<T>(IQueryable<T> interna) : IOrderedQueryable<T>, IAsyncEnumerable<T>
{
    public Type ElementType => interna.ElementType;
    public Expression Expression => interna.Expression;
    public IQueryProvider Provider { get; } = new TestAsyncQueryProvider<T>(interna.Provider);

    public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => interna.GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new TestAsyncEnumerator<T>(interna.GetEnumerator());
}

internal sealed class TestAsyncQueryProvider<T>(IQueryProvider interno) : IAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression) => new TestAsyncQueryable<T>(interno.CreateQuery<T>(expression));

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new TestAsyncQueryable<TElement>(interno.CreateQuery<TElement>(expression));

    public object? Execute(Expression expression) => interno.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => interno.Execute<TResult>(expression);

    // Desenvuelve TResult de Task<TResult> — es lo que Async LINQ (ToListAsync…) espera del proveedor.
    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var tipoResultado = typeof(TResult).GetGenericArguments().FirstOrDefault() ?? typeof(TResult);
        var resultado = interno.Execute(expression);
        var metodoFromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado);
        return (TResult)metodoFromResult.Invoke(null, [resultado])!;
    }
}

internal sealed class TestAsyncEnumerator<T>(IEnumerator<T> interno) : IAsyncEnumerator<T>
{
    public T Current => interno.Current;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(interno.MoveNext());

    public ValueTask DisposeAsync()
    {
        interno.Dispose();
        return ValueTask.CompletedTask;
    }
}
