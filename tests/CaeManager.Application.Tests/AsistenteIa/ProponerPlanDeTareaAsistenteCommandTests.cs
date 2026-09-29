using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// Cada envío del texto al proveedor —el primero y cada cambio de Tenant destino— pasa
/// por un Command, y por tanto por la autorización de escritura vigente en ese momento.
/// </summary>
public class ProponerPlanDeTareaAsistenteCommandTests
{
    private static readonly Guid TareaId = Guid.NewGuid();

    private sealed class RolCambiante(string rol) : ICurrentUserService
    {
        public string Rol { get; set; } = rol;
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(Rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class SinSesion : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(null);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    /// <summary>El «proveedor»: cuenta cuántas veces se le pide un plan.</summary>
    private sealed class MediadorFalso(EstadoTareaAsistente estado, string? texto = "reclama la documentación") : IMediator
    {
        public List<ProponerPlanAsistenteQuery> Proveedor { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerTareaAsistenteQuery => Result.Exito(new TareaAsistenteDto(
                    TareaId, Guid.NewGuid(), estado, DateTime.UtcNow, DateTime.UtcNow, null,
                    texto is null ? [] : [new TurnoTareaAsistenteDto(1, AutorTurnoTareaAsistente.Persona, texto, DateTime.UtcNow)], [])),
                ProponerPlanAsistenteQuery q => Registrar(q),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private Result<PlanPropuestoDto> Registrar(ProponerPlanAsistenteQuery q)
        {
            Proveedor.Add(q);
            return Result.Exito(new PlanPropuestoDto(SituacionPlan.NoEntendido, null, 0, [], null, false, null));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static Task<Result<PlanPropuestoDto>> ConPipeline(
        RolCambiante usuario, MediadorFalso mediador, ProponerPlanDeTareaAsistenteCommand orden)
    {
        var autorizacion = new AutorizacionEscrituraBehavior<ProponerPlanDeTareaAsistenteCommand, Result<PlanPropuestoDto>>(
            usuario, new SinSesion(), new SinTenant());
        var handler = new ProponerPlanDeTareaAsistenteCommandHandler(mediador);
        return autorizacion.Handle(orden, ct => handler.Handle(orden, ct), CancellationToken.None);
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_pierde_el_permiso_de_escritura_no_vuelve_a_enviar_su_texto_al_proveedor()
    {
        var usuario = new RolCambiante("GestorCae");
        var mediador = new MediadorFalso(EstadoTareaAsistente.PlanListo);

        var primera = await ConPipeline(usuario, mediador, new ProponerPlanDeTareaAsistenteCommand(TareaId));
        primera.EsExitoso.Should().BeTrue();
        mediador.Proveedor.Should().ContainSingle("control positivo: con escritura vigente el proveedor sí se llama");

        usuario.Rol = "Consulta"; // la Asignación de Cartera baja con el circuito abierto
        var repropuesta = await ConPipeline(usuario, mediador, new ProponerPlanDeTareaAsistenteCommand(TareaId, Guid.NewGuid()));

        repropuesta.EsFallido.Should().BeTrue();
        repropuesta.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        mediador.Proveedor.Should().ContainSingle("la repropuesta no llegó al proveedor");
    }

    [Fact]
    public async Task El_texto_sale_de_la_tarea_y_el_Tenant_elegido_viaja_al_motor()
    {
        var mediador = new MediadorFalso(EstadoTareaAsistente.Conversando, "alta de un centro");
        var tenant = Guid.NewGuid();

        await ConPipeline(new RolCambiante("GestorCae"), mediador, new ProponerPlanDeTareaAsistenteCommand(TareaId, tenant));

        mediador.Proveedor.Single().Should().Be(new ProponerPlanAsistenteQuery("alta de un centro", tenant));
    }

    [Theory]
    [InlineData(EstadoTareaAsistente.Confirmada)]
    [InlineData(EstadoTareaAsistente.Terminada)]
    [InlineData(EstadoTareaAsistente.Descartada)]
    public async Task Una_tarea_cerrada_no_llama_al_proveedor(EstadoTareaAsistente estado)
    {
        var mediador = new MediadorFalso(estado);

        var resultado = await ConPipeline(new RolCambiante("GestorCae"), mediador, new ProponerPlanDeTareaAsistenteCommand(TareaId));

        resultado.Error.Codigo.Should().Be("AsistenteIa.TareaNoModificable");
        mediador.Proveedor.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_tarea_sin_texto_no_llama_al_proveedor()
    {
        var mediador = new MediadorFalso(EstadoTareaAsistente.Conversando, texto: null);

        var resultado = await ConPipeline(new RolCambiante("GestorCae"), mediador, new ProponerPlanDeTareaAsistenteCommand(TareaId));

        resultado.Error.Codigo.Should().Be("AsistenteIa.TareaSinTexto");
        mediador.Proveedor.Should().BeEmpty();
    }
}
