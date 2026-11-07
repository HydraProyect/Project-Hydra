using Bunit;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Web.Features.Calendario.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La barra de capas se monta mientras /calendario carga el mes, así que su consulta sale con la carga principal aún
/// en vuelo. Las dos comparten el <c>CaeManagerDbContext</c> scoped del circuito, que no admite dos operaciones a la
/// vez. Lo que lo impide es <see cref="SerializacionAccesoDatosBehavior{TRequest,TResponse}"/>, que envuelve todo
/// request de MediatR por fuera de los behaviors que tocan datos (su registro lo fija
/// <c>ApplicationServiceCollectionExtensionsTests</c>): la consulta de capas espera en <see cref="PuertaAccesoDatos"/> a
/// que termine la principal.
///
/// <para>
/// El mediador de estos tests pasa cada request por ese mismo behavior con una puerta real, y retiene la consulta
/// principal con una compuerta. El control, sin el behavior, demuestra que el instrumento ve el solapamiento: sin la
/// puerta, las dos consultas están en vuelo a la vez.
/// </para>
/// </summary>
public class CalendarioCapasSinSolapamientoTests : BunitContext
{
    public CalendarioCapasSinSolapamientoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private sealed class MediadorConCompuerta(PuertaAccesoDatos? puerta) : IMediator
    {
        private int _enVuelo;

        public TaskCompletionSource Principal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Entradas { get; } = [];
        public int MaximoEnVuelo { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            puerta is null
                ? AtenderAsync(request)
                : new SerializacionAccesoDatosBehavior<IRequest<TResponse>, TResponse>(puerta)
                    .Handle(request, _ => AtenderAsync(request), cancellationToken);

        private async Task<TResponse> AtenderAsync<TResponse>(IRequest<TResponse> request)
        {
            MaximoEnVuelo = Math.Max(MaximoEnVuelo, Interlocked.Increment(ref _enVuelo));
            Entradas.Add(request.GetType().Name);
            try
            {
                object respuesta;
                switch (request)
                {
                    case ObtenerVencimientosMesQuery:
                        await Principal.Task;
                        respuesta = (IReadOnlyList<VencimientoCalendarioDto>)[];
                        break;
                    case ObtenerVisitasParaCalendarioQuery:
                        respuesta = (IReadOnlyList<VisitaCalendarioDto>)[];
                        break;
                    case ObtenerCapasCalendarioQuery:
                        respuesta = new CapasCalendarioDto(false, [new PersonaCapasCalendarioDto("Aragón", [], [])]);
                        break;
                    default:
                        throw new NotSupportedException(request.GetType().Name);
                }
                return (TResponse)respuesta;
            }
            finally
            {
                Interlocked.Decrement(ref _enVuelo);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
            Task.CompletedTask;
    }

    private (IRenderedComponent<Calendario> Pagina, MediadorConCompuerta Mediador) Montar(bool conPuerta)
    {
        var mediador = new MediadorConCompuerta(conPuerta ? new PuertaAccesoDatos() : null);
        Services.AddScoped<IMediator>(_ => mediador);
        return (Render<Calendario>(p => p.Add(c => c.Hoy, new DateOnly(2026, 11, 15))), mediador);
    }

    [Fact]
    public void Con_la_puerta_las_capas_esperan_a_que_termine_la_carga_principal()
    {
        var (pagina, mediador) = Montar(conPuerta: true);

        mediador.Entradas.Should().Equal(["ObtenerVencimientosMesQuery"],
            "la consulta de capas ya salió, pero espera en la puerta mientras la principal sigue en vuelo");

        mediador.Principal.SetResult();

        pagina.WaitForAssertion(() => mediador.Entradas.Should().HaveCount(3)
            .And.StartWith("ObtenerVencimientosMesQuery")
            .And.Contain("ObtenerCapasCalendarioQuery")
            .And.Contain("ObtenerVisitasParaCalendarioQuery"));
        pagina.WaitForAssertion(() => pagina.FindAll(".capas-calendario").Should().ContainSingle());
        mediador.MaximoEnVuelo.Should().Be(1, "nunca hay dos consultas a la vez sobre el DbContext del circuito");
    }

    [Fact]
    public void Control_sin_la_puerta_las_dos_consultas_se_solapan()
    {
        var (_, mediador) = Montar(conPuerta: false);

        mediador.Entradas.Should().Equal("ObtenerVencimientosMesQuery", "ObtenerCapasCalendarioQuery");
        mediador.MaximoEnVuelo.Should().Be(2, "la barra de capas consulta con la carga principal aún en vuelo");

        mediador.Principal.SetResult();
    }
}
