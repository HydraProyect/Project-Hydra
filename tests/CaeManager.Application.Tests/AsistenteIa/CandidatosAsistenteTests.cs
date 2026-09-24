using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Decisiones;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using FluentAssertions;
using MediatR;
using Q = CaeManager.Application.AsistenteIa.Candidatos.ObtenerCandidatosAsistenteQueryHandler;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// Candidatos del asistente entre varios Tenants de la cartera, y la regla del
/// Tenant destino (propietario, 2026-09-24). Aquí se prueba la composición: que
/// cada candidato lleva el sello del Tenant en cuyo ámbito se leyó, y que la
/// regla nunca junta dos Tenants. Que las Queries de selector no crucen Tenants
/// bajo PostgreSQL lo prueba el test de integración.
/// </summary>
public class CandidatosAsistenteTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid TenantSinCartera = Guid.NewGuid();

    [Fact]
    public async Task Cada_candidato_lleva_el_Tenant_del_ambito_en_que_se_leyo()
    {
        var mundo = new Mundo();
        var centroA = mundo.Centro(TenantA, "Nave Norte");
        var centroB = mundo.Centro(TenantB, "Nave Norte");

        var resultado = await mundo.Consultar(Q.CampoCentro);

        resultado.Tenants.Select(t => t.TenantId).Should().Equal(TenantA, TenantB);
        resultado.PorCampo[Q.CampoCentro].Should().BeEquivalentTo(
        [
            new CandidatoSelladoDto(centroA, "Nave Norte · Cliente · en Tenant A", TenantA),
            new CandidatoSelladoDto(centroB, "Nave Norte · Cliente · en Tenant B", TenantB),
        ]);
        // Fuera del bucle no queda ningún ámbito abierto.
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
    }

    [Fact]
    public async Task Un_Tenant_con_alcance_cero_no_es_de_la_cartera()
    {
        var mundo = new Mundo(conTenantSinCartera: true);
        mundo.Centro(TenantSinCartera, "Centro ajeno");
        mundo.Centro(TenantA, "Centro propio");

        var resultado = await mundo.Consultar(Q.CampoCentro, Q.CampoTenant);

        resultado.Tenants.Select(t => t.TenantId).Should().NotContain(TenantSinCartera);
        resultado.PorCampo[Q.CampoCentro].Should().OnlyContain(c => c.TenantId != TenantSinCartera);
        resultado.PorCampo[Q.CampoTenant].Select(c => c.Id).Should().Equal(TenantA, TenantB);
    }

    [Fact]
    public async Task Con_un_solo_Tenant_el_nombre_no_lleva_el_Tenant()
    {
        var mundo = new Mundo(soloTenantA: true);
        mundo.Centro(TenantA, "Nave Norte");

        var resultado = await mundo.Consultar(Q.CampoCentro);

        resultado.PorCampo[Q.CampoCentro].Should().ContainSingle().Which.Nombre.Should().Be("Nave Norte · Cliente");
    }

    [Fact]
    public async Task El_empleador_solo_ofrece_subcontratas_gestionables()
    {
        var gestionable = Guid.NewGuid();
        var ajena = Guid.NewGuid();
        var mundo = new Mundo(soloTenantA: true, subcontrataIdsParaGestion: [gestionable]);
        mundo.Subcontratas[TenantA] = [new(gestionable, "Montajes Sur"), new(ajena, "Montajes Este")];

        var resultado = await mundo.Consultar(Q.CampoEmpleador);

        resultado.PorCampo[Q.CampoEmpleador].Select(c => c.Id).Should().Equal(gestionable);
    }

    [Fact]
    public async Task Un_campo_sin_constructor_se_rechaza_en_vez_de_devolverlo_vacio()
    {
        var mundo = new Mundo();

        var consultar = () => mundo.Consultar("documentos");

        await consultar.Should().ThrowAsync<ArgumentException>().WithMessage("*documentos*");
    }

    /// <summary>
    /// Todo campo de selección del catálogo tiene constructor de candidatos o
    /// está en esta lista, con su motivo. Un campo nuevo pone esto en rojo hasta
    /// que alguien decida de dónde salen sus candidatos.
    /// </summary>
    [Fact]
    public void Todo_campo_de_seleccion_del_catalogo_tiene_candidatos_o_esta_declarado_pendiente()
    {
        var pendientes = new Dictionary<string, string>
        {
            ["destinatario"] = "Destinatarios de un envío: sale de los contactos del Cliente empresarial, sin constructor aún.",
            ["documentos"] = "Documentos de un Trabajador o Empresa ya elegidos: depende de una selección previa.",
            ["sujeto"] = "Trabajador o Empresa según la orden: depende de una selección previa.",
            ["es_critico"] = "Sí o no: no depende del Tenant ni de la cartera.",
        };

        var campos = CatalogoOrdenesAsistente.Ordenes
            .SelectMany(o => o.Campos)
            .Where(c => c.Forma == FormaDeExtraccion.SeleccionDeCatalogo)
            .Select(c => c.Nombre)
            .Distinct()
            .ToList();

        campos.Where(c => !Q.CamposConCandidatos.Contains(c) && !pendientes.ContainsKey(c)).Should().BeEmpty();
        pendientes.Keys.Should().OnlyContain(p => campos.Contains(p), "una entrada pendiente sin campo en el catálogo es ruido");
    }

    [Fact]
    public void Todos_los_datos_de_un_Tenant_lo_proponen_sin_preguntar()
    {
        var candidatos = DosTenants(out var centroA, out _, out var trabajadorA, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos,
            [new(Q.CampoCentro, centroA, 90), new(Q.CampoTrabajadores, trabajadorA, 90)]);

        destino.Situacion.Should().Be(SituacionTenantDestino.Unico);
        destino.Tenant!.TenantId.Should().Be(TenantA);
        destino.Bloquea.Should().BeFalse();
    }

    [Fact]
    public void Un_Trabajador_de_un_Tenant_con_un_Centro_de_otro_es_mezcla_y_bloquea()
    {
        var candidatos = DosTenants(out var centroA, out _, out _, out var trabajadorB);

        var destino = ResolucionTenantDestino.Resolver(candidatos,
            [new(Q.CampoCentro, centroA, 90), new(Q.CampoTrabajadores, trabajadorB, 90)]);

        destino.Situacion.Should().Be(SituacionTenantDestino.Mezcla);
        destino.Bloquea.Should().BeTrue();
        destino.Tenant.Should().BeNull();
        destino.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo([TenantA, TenantB]);
        destino.Motivo.Should().Contain("Tenant A").And.Contain("Tenant B");
    }

    [Fact]
    public void Si_la_orden_no_situa_el_Tenant_se_pregunta_con_los_de_la_cartera()
    {
        var candidatos = DosTenants(out _, out _, out _, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos, [new(Q.CampoCentro, null, 40)]);

        destino.Situacion.Should().Be(SituacionTenantDestino.Elegir);
        destino.Bloquea.Should().BeFalse();
        destino.Tenants.Select(t => t.TenantId).Should().Equal(TenantA, TenantB);
    }

    [Fact]
    public void Con_un_solo_Tenant_en_la_cartera_no_hace_falta_preguntar()
    {
        var soloA = DosTenants(out _, out _, out _, out _).SoloDelTenant(TenantA);

        var destino = ResolucionTenantDestino.Resolver(soloA, []);

        destino.Situacion.Should().Be(SituacionTenantDestino.Unico);
        destino.Tenant!.TenantId.Should().Be(TenantA);
    }

    [Fact]
    public void Elegir_a_mano_un_Tenant_distinto_del_de_los_datos_bloquea_y_recomienda_el_correcto()
    {
        var candidatos = DosTenants(out var centroA, out _, out _, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos, [new(Q.CampoCentro, centroA, 90)], tenantElegido: TenantB);

        destino.Situacion.Should().Be(SituacionTenantDestino.Discrepancia);
        destino.Bloquea.Should().BeTrue();
        destino.Tenant.Should().BeNull();
        destino.Recomendado!.TenantId.Should().Be(TenantA);
        destino.Motivo.Should().Contain("cambiar a Tenant A");
    }

    [Fact]
    public void Elegir_a_mano_el_Tenant_de_los_datos_no_es_discrepancia()
    {
        var candidatos = DosTenants(out var centroA, out _, out _, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos, [new(Q.CampoCentro, centroA, 90)], tenantElegido: TenantA);

        destino.Situacion.Should().Be(SituacionTenantDestino.Unico);
        destino.Tenant!.TenantId.Should().Be(TenantA);
        destino.Recomendado.Should().BeNull();
    }

    [Fact]
    public void Sin_datos_que_lo_situen_el_Tenant_de_la_pantalla_es_el_de_serie()
    {
        var candidatos = DosTenants(out _, out _, out _, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos, [new(Q.CampoCentro, null, 40)], tenantPantalla: TenantB);

        destino.Situacion.Should().Be(SituacionTenantDestino.Unico);
        destino.Tenant!.TenantId.Should().Be(TenantB);
        destino.DistintoDePantalla.Should().BeFalse();
    }

    [Fact]
    public void Los_datos_de_la_orden_mandan_sobre_la_pantalla_y_se_resalta()
    {
        var candidatos = DosTenants(out var centroA, out _, out _, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos, [new(Q.CampoCentro, centroA, 90)], tenantPantalla: TenantB);

        destino.Situacion.Should().Be(SituacionTenantDestino.Unico);
        destino.Tenant!.TenantId.Should().Be(TenantA);
        destino.DistintoDePantalla.Should().BeTrue();
        destino.Bloquea.Should().BeFalse();
    }

    [Fact]
    public void El_Tenant_elegido_a_mano_manda_sobre_la_pantalla()
    {
        var candidatos = DosTenants(out _, out _, out _, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos, [], tenantElegido: TenantA, tenantPantalla: TenantB);

        destino.Situacion.Should().Be(SituacionTenantDestino.Unico);
        destino.Tenant!.TenantId.Should().Be(TenantA);
        destino.DistintoDePantalla.Should().BeTrue();
    }

    [Fact]
    public void Una_pantalla_fuera_de_la_cartera_no_sirve_de_valor_por_defecto()
    {
        var candidatos = DosTenants(out _, out _, out _, out _);

        var destino = ResolucionTenantDestino.Resolver(candidatos, [], tenantPantalla: TenantSinCartera);

        destino.Situacion.Should().Be(SituacionTenantDestino.Elegir);
        destino.Tenants.Select(t => t.TenantId).Should().Equal(TenantA, TenantB);
    }

    [Fact]
    public void Una_mezcla_de_datos_bloquea_aunque_haya_Tenant_elegido_y_pantalla()
    {
        var candidatos = DosTenants(out var centroA, out _, out _, out var trabajadorB);

        var destino = ResolucionTenantDestino.Resolver(candidatos,
            [new(Q.CampoCentro, centroA, 90), new(Q.CampoTrabajadores, trabajadorB, 90)],
            tenantElegido: TenantA, tenantPantalla: TenantA);

        destino.Situacion.Should().Be(SituacionTenantDestino.Mezcla);
        destino.Bloquea.Should().BeTrue();
    }

    [Fact]
    public void Un_Tenant_elegido_fuera_de_la_cartera_se_rechaza()
    {
        var candidatos = DosTenants(out _, out _, out _, out _);

        var resolver = () => ResolucionTenantDestino.Resolver(candidatos, [], tenantElegido: TenantSinCartera);

        resolver.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_candidato_que_no_se_envio_no_tiene_Tenant_y_se_rechaza()
    {
        var candidatos = DosTenants(out _, out _, out _, out _);

        var resolver = () => ResolucionTenantDestino.Resolver(candidatos, [new(Q.CampoCentro, Guid.NewGuid(), 90)]);

        resolver.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Sin_ningun_Tenant_en_la_cartera_no_hay_donde_ejecutar()
    {
        var vacio = new CandidatosAsistenteDto([], new Dictionary<string, IReadOnlyList<CandidatoSelladoDto>>());

        var destino = ResolucionTenantDestino.Resolver(vacio, []);

        destino.Situacion.Should().Be(SituacionTenantDestino.SinCartera);
        destino.Bloquea.Should().BeTrue();
    }

    [Fact]
    public void Reducir_a_un_Tenant_deja_solo_sus_candidatos()
    {
        var candidatos = DosTenants(out var centroA, out _, out var trabajadorA, out _);

        var soloA = candidatos.SoloDelTenant(TenantA);

        soloA.Tenants.Should().ContainSingle().Which.TenantId.Should().Be(TenantA);
        soloA.PorCampo[Q.CampoCentro].Select(c => c.Id).Should().Equal(centroA);
        soloA.PorCampo[Q.CampoTrabajadores].Select(c => c.Id).Should().Equal(trabajadorA);
        candidatos.SoloDelTenant(TenantSinCartera).PorCampo.Values.Should().OnlyContain(l => l.Count == 0);
    }

    private static CandidatosAsistenteDto DosTenants(out Guid centroA, out Guid centroB, out Guid trabajadorA, out Guid trabajadorB)
    {
        centroA = Guid.NewGuid();
        centroB = Guid.NewGuid();
        trabajadorA = Guid.NewGuid();
        trabajadorB = Guid.NewGuid();
        return new CandidatosAsistenteDto(
            [new(TenantA, "Tenant A", true), new(TenantB, "Tenant B", false)],
            new Dictionary<string, IReadOnlyList<CandidatoSelladoDto>>
            {
                [Q.CampoCentro] = [new(centroA, "Nave · en Tenant A", TenantA), new(centroB, "Nave · en Tenant B", TenantB)],
                [Q.CampoTrabajadores] = [new(trabajadorA, "Ana Ruiz · en Tenant A", TenantA), new(trabajadorB, "Ana Ruiz · en Tenant B", TenantB)],
            });
    }

    /// <summary>
    /// Varios Tenants simulados. El mediador y el alcance contestan según el
    /// Tenant del <see cref="AmbitoTenantExplicito"/> vigente, como lo haría el
    /// filtro global: si el handler leyera fuera del ámbito, no vería nada.
    /// </summary>
    private sealed class Mundo
    {
        private readonly List<ClienteAutorizadoDto> _autorizados;
        private readonly Dictionary<Guid, IAlcanceDatosService> _alcance = new();
        public Dictionary<Guid, List<CentroSelectorDto>> Centros { get; } = new();
        public Dictionary<Guid, List<SubcontrataSelectorDto>> Subcontratas { get; } = new();

        public Mundo(bool conTenantSinCartera = false, bool soloTenantA = false, IReadOnlyList<Guid>? subcontrataIdsParaGestion = null)
        {
            _autorizados = [new(TenantA, "Tenant A", true)];
            if (!soloTenantA) _autorizados.Add(new(TenantB, "Tenant B", false));
            if (conTenantSinCartera) _autorizados.Add(new(TenantSinCartera, "Tenant sin cartera", false));

            foreach (var tenant in _autorizados)
                _alcance[tenant.TenantId] = tenant.TenantId == TenantSinCartera
                    ? new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [])
                    : new AlcanceDatosServiceFalso(subcontrataIdsParaGestion: subcontrataIdsParaGestion);
        }

        public Guid Centro(Guid tenant, string nombre)
        {
            var id = Guid.NewGuid();
            (Centros.TryGetValue(tenant, out var lista) ? lista : Centros[tenant] = []).Add(new(id, nombre, "Cliente", "Empresa"));
            return id;
        }

        public Task<CandidatosAsistenteDto> Consultar(params string[] campos) =>
            new ObtenerCandidatosAsistenteQueryHandler(new MediatorPorTenant(this), new AlcancePorTenant(_alcance))
                .Handle(new ObtenerCandidatosAsistenteQuery(campos), CancellationToken.None);

        public object Responder(object request)
        {
            var tenant = AmbitoTenantExplicito.TenantIdActual;
            return request switch
            {
                ObtenerClientesAutorizadosQuery => _autorizados,
                ObtenerCentrosParaSelectorQuery => DelTenant(Centros, tenant),
                ObtenerSubcontratasParaSelectorQuery => DelTenant(Subcontratas, tenant),
                ObtenerClientesParaSelectorQuery => new List<ClienteSelectorDto>(),
                ObtenerEmpresasParaSelectorQuery => new List<EmpresaSelectorDto>(),
                ObtenerTrabajadoresParaSelectorQuery => new List<TrabajadorSelectorDto>(),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
        }

        private static IReadOnlyList<T> DelTenant<T>(Dictionary<Guid, List<T>> datos, Guid? tenant) =>
            tenant is { } t && datos.TryGetValue(t, out var lista) ? lista : [];
    }

    private sealed class AlcancePorTenant(Dictionary<Guid, IAlcanceDatosService> alcance) : IAlcanceDatosService
    {
        private IAlcanceDatosService Actual => alcance[AmbitoTenantExplicito.TenantIdActual
            ?? throw new InvalidOperationException("Alcance leído fuera de un ámbito de Tenant.")];

        public Task<bool> TieneAccesoTotalAsync(CancellationToken c = default) => Actual.TieneAccesoTotalAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerClienteIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerCentroIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerCentroIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerCentroIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerEmpresaIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerEmpresaIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerSubcontrataIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken c = default) => Actual.ObtenerSubcontrataIdsParaGestionAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerTrabajadorIdsVisiblesAsync(c);
        public Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken c = default) => Actual.ObtenerVehiculoIdsVisiblesAsync(c);
        public Task<bool> ConexionIntegracionVisibleAsync(Guid id, CancellationToken c = default) => Actual.ConexionIntegracionVisibleAsync(id, c);
    }

    private sealed class MediatorPorTenant(Mundo mundo) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)mundo.Responder(request));

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
