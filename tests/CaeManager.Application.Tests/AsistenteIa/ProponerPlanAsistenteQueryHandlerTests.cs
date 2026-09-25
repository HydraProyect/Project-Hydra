using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Decisiones;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Q = CaeManager.Application.AsistenteIa.Candidatos.ObtenerCandidatosAsistenteQueryHandler;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// Orquestación del plan propuesto: Nivel 0 sobre toda la cartera antes de enviar el texto,
/// candidatos solo de la cartera, Tenant destino y datos pendientes. La regla del
/// Tenant y el sellado tienen su propia suite (<see cref="CandidatosAsistenteTests"/>).
/// </summary>
public class ProponerPlanAsistenteQueryHandlerTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid CentroA = Guid.NewGuid();
    private static readonly Guid CentroB = Guid.NewGuid();
    private static readonly Guid TrabajadorA = Guid.NewGuid();
    private static readonly Guid TrabajadorB = Guid.NewGuid();

    private const string Orden = "Visita de Ana Ruiz a Nave Norte el 5 de octubre";

    [Fact]
    public async Task Sin_instruccion_en_el_Tenant_de_la_pantalla_no_se_llama_al_proveedor()
    {
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro);

        var resultado = await Handler(decisiones, habilitados: [TenantB]).Handle(new(Orden), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AsistenteIa.SinInstruccion");
        decisiones.Clasificaciones.Should().Be(0);
        decisiones.Solicitadas.Should().BeNull();
    }

    [Fact]
    public async Task Si_el_modelo_se_abstiene_no_hay_plan_ni_se_leen_candidatos()
    {
        var decisiones = new DecisionesFalsas(ordenId: null);
        var mediator = new MediatorFalso(DosTenants());

        var resultado = await Handler(decisiones, mediator: mediator).Handle(new(Orden), default);

        resultado.Valor.Situacion.Should().Be(SituacionPlan.NoEntendido);
        resultado.Valor.Confirmable.Should().BeFalse();
        mediator.Consultas.Should().Be(0);
        decisiones.Solicitadas.Should().BeNull();
    }

    [Fact]
    public async Task Una_visita_con_datos_de_un_Tenant_propone_ese_Tenant_y_deja_las_fechas_pendientes()
    {
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro,
            new(Q.CampoCentro, CentroA, 92), new(Q.CampoTrabajadores, TrabajadorA, 88));

        var plan = (await Handler(decisiones, pantalla: TenantB).Handle(new(Orden), default)).Valor;

        plan.Situacion.Should().Be(SituacionPlan.Propuesto);
        plan.OrdenId.Should().Be(CatalogoOrdenesAsistente.VisitaPuntualACentro);
        plan.Destino!.Situacion.Should().Be(SituacionTenantDestino.Unico);
        plan.Destino.Tenant!.TenantId.Should().Be(TenantA);
        plan.Destino.DistintoDePantalla.Should().BeTrue();

        var centro = plan.Datos.Single(d => d.Campo == Q.CampoCentro);
        centro.CandidatoId.Should().Be(CentroA);
        centro.Nombre.Should().Be("Nave Norte · en Tenant A");
        centro.TenantId.Should().Be(TenantA);
        centro.Confianza.Should().Be(92);

        plan.Datos.Single(d => d.Campo == "fecha_inicio").Pendiente.Should().NotBeNull();
        plan.Confirmable.Should().BeFalse("las fechas obligatorias todavía no se resuelven");
    }

    [Fact]
    public async Task Un_Tenant_de_la_cartera_sin_instruccion_falla_cerrado_sin_enviar_el_texto_al_proveedor()
    {
        // El texto puede nombrar a Ana Ruiz de Tenant B aunque la pantalla sea la
        // de A: no hay forma de filtrarlo en local, así que no sale nada.
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro);
        var mediator = new MediatorFalso(DosTenants(), habilitados: [TenantA]);

        var resultado = await Handler(decisiones, habilitados: [TenantA], mediator: mediator).Handle(new(Orden), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);
        resultado.Error.Mensaje.Should().Contain("Tenant B").And.NotContain("Tenant A");
        decisiones.Clasificaciones.Should().Be(0);
        decisiones.Solicitadas.Should().BeNull();
        mediator.Consultas.Should().Be(0, "ni siquiera se leen candidatos");
    }

    [Fact]
    public async Task Un_Tenant_que_aparece_en_los_candidatos_sin_haberse_comprobado_falla_cerrado()
    {
        // La cartera cambió entre la comprobación y la lectura de candidatos: B no
        // se comprobó, así que sus candidatos no viajan.
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro);
        var mediator = new MediatorFalso(DosTenants(), comprobados: [new(TenantA, "Tenant A", true)]);

        var resultado = await Handler(decisiones, mediator: mediator).Handle(new(Orden), default);

        resultado.Error.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);
        resultado.Error.Mensaje.Should().Contain("Tenant B");
        decisiones.Solicitadas.Should().BeNull();
    }

    [Fact]
    public async Task Se_pregunta_por_el_Tenant_aunque_la_orden_del_catalogo_no_lo_declare()
    {
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro);

        await Handler(decisiones).Handle(new(Orden), default);

        CatalogoOrdenesAsistente.PorId(CatalogoOrdenesAsistente.VisitaPuntualACentro)!.Campos
            .Should().NotContain(c => c.Nombre == Q.CampoTenant);
        decisiones.Solicitadas!.Select(s => s.Campo.Nombre).Should().Contain(Q.CampoTenant);
    }

    [Fact]
    public async Task Un_Tenant_elegido_fuera_de_la_cartera_se_rechaza_sin_llamar_al_proveedor_para_seleccionar()
    {
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro);

        var resultado = await Handler(decisiones).Handle(new(Orden, TenantElegido: Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("AsistenteIa.TenantFueraDeCartera");
        decisiones.Solicitadas.Should().BeNull();
    }

    [Fact]
    public async Task Una_mezcla_de_Tenants_llega_al_plan_y_no_es_confirmable()
    {
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro,
            new(Q.CampoCentro, CentroA, 92), new(Q.CampoTrabajadores, TrabajadorB, 88));

        var plan = (await Handler(decisiones).Handle(new(Orden), default)).Valor;

        plan.Destino!.Situacion.Should().Be(SituacionTenantDestino.Mezcla);
        plan.Confirmable.Should().BeFalse();
    }

    [Fact]
    public async Task Un_candidato_que_no_estaba_en_la_lista_no_entra_en_el_plan()
    {
        // El contrato del servicio lo prohíbe; si una implementación lo incumple,
        // el dato queda sin resolver en vez de reventar o colarse en el plan.
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro,
            new SeleccionCandidatoDto(Q.CampoCentro, Guid.NewGuid(), 97));

        var plan = (await Handler(decisiones).Handle(new(Orden), default)).Valor;

        var centro = plan.Datos.Single(d => d.Campo == Q.CampoCentro);
        centro.CandidatoId.Should().BeNull();
        centro.TenantId.Should().BeNull();
        centro.Confianza.Should().Be(0);
    }

    [Fact]
    public async Task Un_fallo_del_proveedor_al_seleccionar_se_propaga()
    {
        var error = Error.Crear("AsistenteIa.Proveedor", "caído");
        var decisiones = new DecisionesFalsas(CatalogoOrdenesAsistente.VisitaPuntualACentro) { FalloSeleccion = error };

        var resultado = await Handler(decisiones).Handle(new(Orden), default);

        resultado.Error.Should().Be(error);
    }

    [Fact]
    public void Confirmable_exige_Tenant_unico_orden_ejecutable_y_todos_los_obligatorios()
    {
        var unico = new TenantDestinoDto(SituacionTenantDestino.Unico, new(TenantA, "Tenant A", true), [], "");
        var elegir = unico with { Situacion = SituacionTenantDestino.Elegir, Tenant = null };
        DatoPropuestoDto Dato(bool obligatorio, Guid? id) => new("c", "", obligatorio, id, null, null, 0, null);
        PlanPropuestoDto Plan(TenantDestinoDto destino, bool ejecutable, params DatoPropuestoDto[] datos) =>
            new(SituacionPlan.Propuesto, "x", 90, datos, destino, ejecutable, null);

        Plan(unico, true, Dato(true, Guid.NewGuid()), Dato(false, null)).Confirmable.Should().BeTrue();
        Plan(unico, true, Dato(true, null)).Confirmable.Should().BeFalse();
        Plan(unico, false, Dato(true, Guid.NewGuid())).Confirmable.Should().BeFalse();
        Plan(elegir, true, Dato(true, Guid.NewGuid())).Confirmable.Should().BeFalse();
    }

    private static ProponerPlanAsistenteQueryHandler Handler(
        DecisionesFalsas decisiones, Guid? pantalla = null, Guid[]? habilitados = null, MediatorFalso? mediator = null) =>
        new(mediator ?? new MediatorFalso(DosTenants(), habilitados), decisiones,
            new InstruccionPorTenant(habilitados ?? [TenantA, TenantB]), new TenantActualFalso(pantalla ?? TenantA));

    private static CandidatosAsistenteDto DosTenants() => new(
        [new(TenantA, "Tenant A", true), new(TenantB, "Tenant B", false)],
        new Dictionary<string, IReadOnlyList<CandidatoSelladoDto>>
        {
            [Q.CampoTenant] = [new(TenantA, "Tenant A", TenantA), new(TenantB, "Tenant B", TenantB)],
            [Q.CampoCentro] = [new(CentroA, "Nave Norte · en Tenant A", TenantA), new(CentroB, "Nave Norte · en Tenant B", TenantB)],
            [Q.CampoTrabajadores] = [new(TrabajadorA, "Ana Ruiz · en Tenant A", TenantA), new(TrabajadorB, "Ana Ruiz · en Tenant B", TenantB)],
        });

    private sealed class DecisionesFalsas(string? ordenId, params SeleccionCandidatoDto[] selecciones)
        : IDecisionesCerradasAsistenteService
    {
        public int Clasificaciones { get; private set; }
        public IReadOnlyList<SeleccionSolicitadaDto>? Solicitadas { get; private set; }
        public Error? FalloSeleccion { get; init; }

        public Task<Result<ClasificacionOrdenDto>> ClasificarOrdenAsync(string textoOrden, CancellationToken cancellationToken = default)
        {
            Clasificaciones++;
            return Task.FromResult(Result.Exito(new ClasificacionOrdenDto(ordenId, 90)));
        }

        public Task<Result<IReadOnlyList<SeleccionCandidatoDto>>> SeleccionarCandidatosAsync(
            string textoOrden, IReadOnlyList<SeleccionSolicitadaDto> solicitadas, CancellationToken cancellationToken = default)
        {
            Solicitadas = solicitadas;
            return Task.FromResult(FalloSeleccion is { } error
                ? Result.Fallo<IReadOnlyList<SeleccionCandidatoDto>>(error)
                : Result.Exito<IReadOnlyList<SeleccionCandidatoDto>>(selecciones));
        }
    }

    private sealed class InstruccionPorTenant(Guid[] habilitados) : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(habilitados.Contains(tenantId));
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    /// <summary>
    /// Resuelve la lectura de candidatos y la comprobación de la cartera. Sin
    /// <paramref name="comprobados"/>, la cartera comprobada es la misma que la de
    /// los candidatos, repartida según <paramref name="habilitados"/>.
    /// </summary>
    private sealed class MediatorFalso(
        CandidatosAsistenteDto candidatos, Guid[]? habilitados = null, TenantDeCarteraDto[]? comprobados = null) : IMediator
    {
        public int Consultas { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ComprobarInstruccionIaCarteraQuery)
            {
                var cartera = comprobados is not null
                    ? new InstruccionIaCarteraDto(comprobados, [])
                    : new InstruccionIaCarteraDto(
                        candidatos.Tenants.Where(t => (habilitados ?? [TenantA, TenantB]).Contains(t.TenantId)).ToList(),
                        candidatos.Tenants.Where(t => !(habilitados ?? [TenantA, TenantB]).Contains(t.TenantId)).ToList());
                return Task.FromResult((TResponse)(object)cartera);
            }
            if (request is not ObtenerCandidatosAsistenteQuery)
                throw new NotSupportedException(request.GetType().Name);
            Consultas++;
            return Task.FromResult((TResponse)(object)candidatos);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
