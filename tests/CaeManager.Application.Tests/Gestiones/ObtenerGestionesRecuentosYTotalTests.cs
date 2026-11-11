using System.Collections;
using System.Linq.Expressions;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>
/// El total y los recuentos por estado del listado de Gestiones: con la franja de estado, el total sale del mismo
/// agrupado que los recuentos (una ida menos) y tiene que coincidir con el que daba el <c>count(*)</c> aparte.
/// </summary>
public class ObtenerGestionesRecuentosYTotalTests
{
    private readonly Centro _visible = new(Guid.NewGuid(), Guid.NewGuid(), "Centro visible");
    private readonly Centro _oculto = new(Guid.NewGuid(), Guid.NewGuid(), "Centro oculto");
    private readonly TipoDocumento _tipo = new("Formación", null, false, 0, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
    private readonly Trabajador _ana = Trabajador.DeSubcontrata(Guid.NewGuid(), "Ana", "Alba", "60005301A");
    private readonly Trabajador _bea = Trabajador.DeSubcontrata(Guid.NewGuid(), "Bea", "Bueno", "60005302G");
    private readonly List<Gestion> _gestiones = [];
    private readonly ContadorDeIdas _idas = new();

    public ObtenerGestionesRecuentosYTotalTests()
    {
        for (var i = 0; i < 3; i++) _gestiones.Add(new Gestion(_ana.Id, _visible.Id, _tipo.Id));
        for (var i = 0; i < 2; i++)
        {
            var completada = new Gestion(_bea.Id, _visible.Id, _tipo.Id);
            completada.Completar(DateTime.UtcNow);
            _gestiones.Add(completada);
        }
        // Fuera del alcance de quien mira: no cuenta ni en el total ni en los recuentos.
        _gestiones.Add(new Gestion(_ana.Id, _oculto.Id, _tipo.Id));
    }

    private ObtenerGestionesQueryHandler Handler()
    {
        var trabajadores = new TrabajadoresQueryContextFalso();
        trabajadores.ListaTrabajadores.AddRange([_ana, _bea]);
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.AddRange([_visible, _oculto]);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(_tipo);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_visible.Id]);
        return new(new GestionesContadas(_gestiones, _idas), trabajadores, centros, tipos, alcance);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(EstadoGestion.Pendiente, 3)]
    [InlineData(EstadoGestion.Completada, 2)]
    public async Task Con_la_franja_el_total_y_los_recuentos_salen_de_una_sola_ida(EstadoGestion? estado, int totalEsperado)
    {
        var resultado = await Handler().Handle(
            new ObtenerGestionesQuery(null, estado, null, Pagina: 1, TamanoPagina: 2, ConRecuentosPorEstado: true), CancellationToken.None);

        resultado.TotalElementos.Should().Be(totalEsperado);
        resultado.Elementos.Should().HaveCount(Math.Min(2, totalEsperado));
        resultado.Elementos.Should().OnlyContain(g => g.CentroId == _visible.Id && (estado == null || g.Estado == estado));
        resultado.RecuentosPorEstado.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [nameof(EstadoGestion.Pendiente)] = 3,
            [nameof(EstadoGestion.Completada)] = 2,
        });
        // El agrupado por estado y la página: el count(*) aparte ya no se lanza.
        _idas.Idas.Should().Be(2);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(EstadoGestion.Pendiente, 3)]
    [InlineData(EstadoGestion.Completada, 2)]
    public async Task Sin_la_franja_el_total_sigue_siendo_el_count_de_lo_filtrado(EstadoGestion? estado, int totalEsperado)
    {
        var resultado = await Handler().Handle(
            new ObtenerGestionesQuery(null, estado, null, Pagina: 2, TamanoPagina: 2), CancellationToken.None);

        resultado.TotalElementos.Should().Be(totalEsperado);
        resultado.Elementos.Should().HaveCount(Math.Clamp(totalEsperado - 2, 0, 2));
        resultado.RecuentosPorEstado.Should().BeNull();
        _idas.Idas.Should().Be(2);
    }

    [Fact]
    public async Task Un_estado_sin_ninguna_gestion_da_total_cero_y_su_recuento_a_cero()
    {
        _gestiones.RemoveAll(g => g.Estado == EstadoGestion.Completada);

        var resultado = await Handler().Handle(
            new ObtenerGestionesQuery(null, EstadoGestion.Completada, null, ConRecuentosPorEstado: true), CancellationToken.None);

        resultado.TotalElementos.Should().Be(0);
        resultado.Elementos.Should().BeEmpty();
        resultado.RecuentosPorEstado![nameof(EstadoGestion.Completada)].Should().Be(0);
        resultado.RecuentosPorEstado![nameof(EstadoGestion.Pendiente)].Should().Be(3);
    }

    private sealed class GestionesContadas(List<Gestion> gestiones, ContadorDeIdas idas) : IGestionesQueryContext
    {
        public IQueryable<Gestion> Gestiones => new QueryableContado<Gestion>(gestiones.AsQueryable(), idas);
        public IQueryable<GestionAutomatica> GestionesAutomaticas =>
            new TestAsyncQueryable<GestionAutomatica>(Array.Empty<GestionAutomatica>().AsQueryable());
        public IQueryable<PasoGestionAutomatica> PasosGestionAutomatica =>
            new TestAsyncQueryable<PasoGestionAutomatica>(Array.Empty<PasoGestionAutomatica>().AsQueryable());
    }
}

/// <summary>Cuenta las idas a la «base de datos» en memoria: cada enumeración asíncrona y cada escalar asíncrono es una.</summary>
internal sealed class ContadorDeIdas
{
    public int Idas { get; set; }
}

internal sealed class QueryableContado<T>(IQueryable<T> interna, ContadorDeIdas contador) : IOrderedQueryable<T>, IAsyncEnumerable<T>
{
    public Type ElementType => interna.ElementType;
    public Expression Expression => interna.Expression;
    public IQueryProvider Provider => new ProveedorContado(interna.Provider, contador);
    public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => interna.GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        contador.Idas++;
        return new TestAsyncEnumerator<T>(interna.GetEnumerator());
    }
}

internal sealed class ProveedorContado(IQueryProvider interno, ContadorDeIdas contador) : IAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression)
    {
        var consulta = interno.CreateQuery(expression);
        return (IQueryable)Activator.CreateInstance(
            typeof(QueryableContado<>).MakeGenericType(consulta.ElementType), consulta, contador)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new QueryableContado<TElement>(interno.CreateQuery<TElement>(expression), contador);

    public object? Execute(Expression expression) => interno.Execute(expression);
    public TResult Execute<TResult>(Expression expression) => interno.Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        contador.Idas++;
        var tipoResultado = typeof(TResult).GetGenericArguments().FirstOrDefault() ?? typeof(TResult);
        var resultado = interno.Execute(expression);
        var desdeResultado = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado);
        return (TResult)desdeResultado.Invoke(null, [resultado])!;
    }
}
