using System.Security.Claims;
using Bunit;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.CuadroDireccion;
using CaeManager.Web.Features.CuadroDireccion.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CuadroDireccionPagina = CaeManager.Web.Features.CuadroDireccion.Pages.CuadroDireccion;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cuadro de dirección (<c>/direccion</c>) contra su maqueta. El doble del mediador devuelve el
/// <see cref="CuadroDireccionDto"/> que cada prueba fabrica: lo que se afirma aquí es lo que la pantalla
/// hace con lo que la consulta devuelve (umbral de anonimato, rótulos de estado, quién ve la reapertura),
/// no las cifras, que prueba la consulta contra PostgreSQL. Los rótulos salen de
/// <c>IStringLocalizer&lt;TextosCuadroDireccion&gt;</c>.
/// </summary>
public partial class CuadroDireccionTests : BunitContext
{
    private static readonly Guid UsuarioPrueba = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");
    private static readonly Guid TenantA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    public CuadroDireccionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    // ---------------------------------------------------------------- doble y fabricación

    private sealed class MediatorFalso(Func<CuadroDireccionDto> cuadro) : IMediator
    {
        public List<ReabrirMesCuadroDireccionCommand> Reaperturas { get; } = [];
        public Func<ObtenerSerieCuadroDireccionQuery, SerieCuadroDireccionDto>? Serie { get; init; }
        public Func<ComunicacionesCuadroDireccionDto>? Comunicaciones { get; init; }
        public Func<GestionesPorDiaCuadroDireccionDto>? GestionesDia { get; init; }
        public List<ObtenerSerieCuadroDireccionQuery> SeriesPedidas { get; } = [];
        public Func<FacturacionOperadorCaeDto>? Facturacion { get; init; }
        public Func<ExtranjerosDelMesDto>? Extranjeros { get; init; }
        public Func<ConteoExtranjerosCobrablesDelMesDto>? Conteo { get; init; }
        public List<FijarTarifaOperadorCaeCommand> TarifasFijadas { get; } = [];
        public Func<EmpresasGestionadasDelMesDto>? Gestionadas { get; init; }
        public List<ContarExtranjerosCobrablesDelMesQuery> ConteosPedidos { get; } = [];
        public List<ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery> ExtranjerosGestionadosPedidos { get; } = [];
        /// <summary>Si se fija, el detalle por Empresa tarda en responder lo que tarde esta tarea (para probar el cambio de mes en vuelo).</summary>
        public Func<ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery, Task<ExtranjerosDelMesDto>>? ExtranjerosGestionadosAsync { get; init; }

        private ConteoExtranjerosCobrablesDelMesDto RegistrarConteo(ContarExtranjerosCobrablesDelMesQuery q)
        {
            ConteosPedidos.Add(q);
            return Conteo?.Invoke() ?? new ConteoExtranjerosCobrablesDelMesDto(q.Anyo, q.Mes, [], 0);
        }

        private Task<ExtranjerosDelMesDto> ExtranjerosGestionados(ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery q)
        {
            ExtranjerosGestionadosPedidos.Add(q);
            if (ExtranjerosGestionadosAsync is { } asincrono)
                return asincrono(q);

            var todo = Extranjeros?.Invoke() ?? new ExtranjerosDelMesDto(q.Anyo, q.Mes, [], 0);
            return Task.FromResult(q.TenantId is { } t ? todo with { Filas = todo.Filas.Where(f => f.TenantId == t).ToList() } : todo);
        }

        /// <summary>Si se fijan, la serie y las conversaciones responden cuando el test complete su tarea, no al pedirlas.</summary>
        public TaskCompletionSource<SerieCuadroDireccionDto>? SeriePendiente { get; init; }
        public TaskCompletionSource<ComunicacionesCuadroDireccionDto>? ComunicacionesPendientes { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerSerieCuadroDireccionQuery serieEnVuelo && SeriePendiente is { } seriePendiente)
            {
                SeriesPedidas.Add(serieEnVuelo);
                return (Task<TResponse>)(object)seriePendiente.Task;
            }

            if (request is ObtenerComunicacionesCuadroDireccionQuery && ComunicacionesPendientes is { } comunicacionesPendientes)
                return (Task<TResponse>)(object)comunicacionesPendientes.Task;

            if (request is ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery gestionados)
                return (Task<TResponse>)(object)ExtranjerosGestionados(gestionados);

            object? respuesta = request switch
            {
                ObtenerCuadroDireccionQuery => cuadro(),
                ObtenerSerieCuadroDireccionQuery q => PedirSerie(q),
                ObtenerComunicacionesCuadroDireccionQuery q => Comunicaciones?.Invoke()
                    ?? ObtenerComunicacionesCuadroDireccionQueryHandler.Fusionar(q.Anyo, q.Mes, [], 0),
                ObtenerGestionesPorDiaCuadroDireccionQuery => GestionesDia?.Invoke() ?? new GestionesPorDiaCuadroDireccionDto([], 0),
                ReabrirMesCuadroDireccionCommand c => Registrar(c),
                ObtenerFacturacionOperadorCaeQuery q => Facturacion?.Invoke() ?? throw new NotSupportedException("Falta la facturación del Operador CAE en este test."),
                ObtenerExtranjerosDelMesQuery q => Extranjeros?.Invoke() ?? new ExtranjerosDelMesDto(q.Anyo, q.Mes, [], 0),
                ContarExtranjerosCobrablesDelMesQuery q => RegistrarConteo(q),
                ObtenerEmpresasGestionadasDelMesQuery => Gestionadas?.Invoke() ?? EmpresasGestionadasDelMesDto.Ninguna,
                FijarTarifaOperadorCaeCommand c => RegistrarTarifa(c),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private SerieCuadroDireccionDto PedirSerie(ObtenerSerieCuadroDireccionQuery q)
        {
            SeriesPedidas.Add(q);
            return Serie?.Invoke(q) ?? new SerieCuadroDireccionDto([], [], 0);
        }

        private Result RegistrarTarifa(FijarTarifaOperadorCaeCommand c)
        {
            TarifasFijadas.Add(c);
            return Result.Exito();
        }

        private Result Registrar(ReabrirMesCuadroDireccionCommand c)
        {
            Reaperturas.Add(c);
            return Result.Exito();
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static CifrasCuadroDireccionDto Cifras(
        EstadoCifraMensual estado, int solicitudes = 120, IReadOnlyList<FranjaSolicitudesDto>? franjas = null)
    {
        CifraMensual c(int v) => new(v, estado);
        return new CifrasCuadroDireccionDto(
            TrabajadoresActivos: c(300), AltasTrabajadores: c(12), BajasTrabajadores: c(4),
            CentrosActivos: c(30), AltasCentros: c(2), DocumentosGestionados: c(410), Requerimientos: c(37),
            Solicitudes: new SolicitudesDelMesDto(
                c(solicitudes),
                franjas ?? [new FranjaSolicitudesDto(DayOfWeek.Monday, 9, 30), new FranjaSolicitudesDto(DayOfWeek.Tuesday, 10, 90)]),
            Cumplimiento: new CumplimientoActualDto(90, 5, 3, 2, 0, 0, 0, 92, false, DateTime.UtcNow),
            Gestiones: c(42),
            Extranjeros: c(7));
    }

    private static CuadroDireccionDto Cuadro(
        EstadoCifraMensual estado, int solicitudes = 120, IReadOnlyList<FranjaSolicitudesDto>? franjas = null,
        EstadoCifraMensual? estadoTenantB = null)
    {
        var hoy = DiaDeNegocio.Hoy();
        var cifrasA = Cifras(estado, solicitudes, franjas);
        var cifrasB = Cifras(estadoTenantB ?? estado, 5);
        return new CuadroDireccionDto(
            hoy.Year, hoy.Month, estado, DateTime.UtcNow.AddDays(-20), DateTime.UtcNow.AddDays(10), DateTime.UtcNow,
            [
                new CuadroDireccionTenantDto(TenantA, "Refrielectric S.A.", cifrasA, new PrincipalDeCarteraDto(Guid.NewGuid(), "Marta Gestora", Roles.GestorCae)),
                new CuadroDireccionTenantDto(TenantB, "Montajes Ebro S.L.", cifrasB, null),
            ],
            cifrasA, 0);
    }

    private IRenderedComponent<CuadroDireccionPagina> Pintar(
        CuadroDireccionDto cuadro, string rol, out MediatorFalso mediador,
        Func<ObtenerSerieCuadroDireccionQuery, SerieCuadroDireccionDto>? serie = null,
        ComunicacionesCuadroDireccionDto? comunicaciones = null,
        GestionesPorDiaCuadroDireccionDto? gestionesDia = null,
        FacturacionOperadorCaeDto? facturacion = null, ExtranjerosDelMesDto? extranjeros = null, ConteoExtranjerosCobrablesDelMesDto? conteo = null,
        EmpresasGestionadasDelMesDto? gestionadas = null,
        Func<ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery, Task<ExtranjerosDelMesDto>>? extranjerosGestionadosAsync = null)
    {
        var falso = new MediatorFalso(() => cuadro)
        {
            Gestionadas = gestionadas is null ? null : () => gestionadas,
            ExtranjerosGestionadosAsync = extranjerosGestionadosAsync,
            Serie = serie,
            Facturacion = facturacion is null ? null : () => facturacion,
            Extranjeros = extranjeros is null ? null : () => extranjeros,
            Conteo = conteo is null ? null : () => conteo,
            Comunicaciones = comunicaciones is null ? null : () => comunicaciones,
            GestionesDia = gestionesDia is null ? null : () => gestionesDia,
        };
        mediador = falso;
        Services.AddScoped<IMediator>(_ => falso);
        Services.AddScoped<ToastService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var usuario = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UsuarioPrueba.ToString()), new Claim(ClaimTypes.Name, "dir@talveg.test"), new Claim(ClaimTypes.Role, rol)], "prueba"));
        var cut = Render<CuadroDireccionPagina>(ps => ps.AddCascadingValue(Task.FromResult(new AuthenticationState(usuario))));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("cuadro-barra").And.NotContain("Cargando"));
        cut.WaitForAssertion(() => cut.FindAll(".cuadro-kpi").Should().NotBeEmpty());
        return cut;
    }

    private static void IrA(IRenderedComponent<CuadroDireccionPagina> cut, string etiqueta) =>
        cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim().StartsWith(etiqueta, StringComparison.Ordinal)).Click();

    // ---------------------------------------------------------------- umbral de anonimato

    [Theory]
    [InlineData(3, 20, true)]
    [InlineData(2, 20, false)]
    [InlineData(3, 19, false)]
    [InlineData(5, 412, true)]
    [InlineData(null, 19, false)]
    [InlineData(null, 20, true)]
    public void El_umbral_exige_tres_personas_y_veinte_conversaciones(int? personas, int conversaciones, bool cumple) =>
        UmbralAnonimatoCuadro.Cumple(personas, conversaciones).Should().Be(cumple);

    [Fact]
    public void Un_equipo_bajo_el_umbral_no_enseña_ninguna_cifra_y_dice_por_que()
    {
        Services.AddLocalization();
        var cut = Render<TablaEquiposComunicaciones>(ps => ps.Add(p => p.Filas,
        [
            new FilaEquipoComunicaciones("Outbound", 5, 412, 1.8, 1.4, 44, 5, 91, 1.2),
            new FilaEquipoComunicaciones("Validación", 4, 356, 2.4, 1.9, 38, 7, 84, 1.5),
            new FilaEquipoComunicaciones("Fincas", 2, 14, 2.0, 1.5, 42, 6, 89, 1.3),
        ]));

        var filas = cut.FindAll("tbody tr");
        filas.Should().HaveCount(3);
        filas[0].TextContent.Should().Contain("Outbound").And.Contain("412").And.Contain("91 %");
        filas[1].TextContent.Should().Contain("Validación");

        var fincas = filas[2];
        fincas.TextContent.Should().Contain("Fincas");
        fincas.TextContent.Should().Contain("No se muestra: menos de 3 personas o menos de 20 conversaciones");
        fincas.TextContent.Should().NotContain("89 %", "ninguna cifra de un equipo bajo el umbral llega a la pantalla");
        fincas.TextContent.Should().NotContain("2,0 h");
    }

    [Fact]
    public void Un_equipo_suprimido_va_el_ultimo_aunque_se_ordene_por_su_columna()
    {
        var cut = Render<TablaEquiposComunicaciones>(ps => ps.Add(p => p.Filas,
        [
            new FilaEquipoComunicaciones("Fincas", 2, 14, 2.0, 1.5, 42, 6, 89, 1.3),
            new FilaEquipoComunicaciones("Outbound", 5, 412, 1.8, 1.4, 44, 5, 91, 1.2),
        ]));

        cut.FindAll("th button").Single(b => b.TextContent.Contains("Conversaciones")).Click();

        cut.FindAll("tbody tr").Last().TextContent.Should().Contain("Fincas");
    }

    [Fact]
    public void El_mapa_de_calor_de_una_Empresa_con_menos_de_veinte_conversaciones_no_se_muestra()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial, solicitudes: 19), Roles.DireccionCae, out _, serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, i == 5 ? 19 : 0)));

        IrA(cut, "Demanda");

        cut.Find("[data-suprimida=mapa-calor]").TextContent.Should().Be("No se muestra: menos de 20 conversaciones");
        cut.FindAll(".cuadro-calor-celda").Should().BeEmpty("la medida suprimida no pinta ninguna celda con su peso");
    }

    [Fact]
    public void El_mapa_de_calor_con_veinte_conversaciones_se_pinta()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial, solicitudes: 20), Roles.DireccionCae, out _, serie: _ => SerieDe(i => Cifras(EstadoCifraMensual.Estimado, i == 5 ? 20 : 0)));

        IrA(cut, "Demanda");

        cut.FindAll("[data-suprimida]").Should().BeEmpty();
        cut.FindAll(".cuadro-calor-celda").Should().NotBeEmpty();
    }

    [Fact]
    public void La_columna_de_solicitudes_de_una_Empresa_bajo_el_umbral_no_enseña_el_recuento()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        IrA(cut, "Empresas");

        cut.Find($"tr[data-empresa='{TenantB}'] .cuadro-expandir").Click();
        cut.Find($"tr[data-empresa='{TenantA}'] .cuadro-expandir").Click();

        cut.Find($"tr[data-detalle='{TenantB}']").TextContent.Should().Contain("No se muestra");
        cut.Find($"tr[data-detalle='{TenantA}']").TextContent.Should().Contain("120");
    }

    // ---------------------------------------------------------------- parcial y estimado

    [Theory]
    [InlineData(EstadoCifraMensual.Parcial, "parcial")]
    [InlineData(EstadoCifraMensual.Estimado, "estimado")]
    [InlineData(EstadoCifraMensual.Existe, "cerrado")]
    public void El_mes_lleva_el_rotulo_de_su_estado(EstadoCifraMensual estado, string rotulo)
    {
        var cut = Pintar(Cuadro(estado), Roles.DireccionCae, out _);

        var etiqueta = cut.Find(".cuadro-mes .cuadro-estado");
        etiqueta.TextContent.Trim().Should().Be(rotulo);
        etiqueta.GetAttribute("data-e").Should().Be(rotulo);
    }

    [Fact]
    public void El_rotulo_estimado_explica_el_primer_cierre_real_de_octubre_de_2026()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Estimado), Roles.DireccionCae, out _);

        cut.Find(".cuadro-mes .cuadro-estado").GetAttribute("title").Should()
            .Contain("octubre de 2026").And.Contain("1 de noviembre a las 00:00");
    }

    [Fact]
    public void El_rotulo_parcial_dice_hasta_cuando_lo_es()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        cut.Find(".cuadro-mes .cuadro-estado").GetAttribute("title").Should()
            .Contain("Parcial hasta el cierre del día 1 a las 00:00, hora de Madrid");
    }

    [Fact]
    public void Una_Empresa_cuyo_estado_difiere_del_del_mes_lo_lleva_en_su_fila()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Estimado, estadoTenantB: EstadoCifraMensual.Existe), Roles.DireccionCae, out _);

        IrA(cut, "Empresas");

        cut.Find($"tr[data-empresa='{TenantB}'] .cuadro-estado").TextContent.Trim().Should().Be("cerrado");
        cut.FindAll($"tr[data-empresa='{TenantA}'] .cuadro-estado").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- reabrir un mes

    [Fact]
    public void El_boton_de_reabrir_no_existe_para_Coordinador_CAE()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Existe), Roles.CoordinadorCae, out _);

        cut.Markup.Should().NotContain("Reabrir un mes cerrado");
    }

    [Fact]
    public void El_boton_de_reabrir_existe_para_Direccion_CAE()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Existe), Roles.DireccionCae, out _);

        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Reabrir un mes cerrado"));
    }

    [Fact]
    public void Reabrir_exige_un_motivo_y_lo_envia_a_la_Empresa_elegida()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Existe), Roles.DireccionCae, out var mediador);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Reabrir un mes cerrado")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Motivo de la reapertura"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reabrir mes").Click();
        mediador.Reaperturas.Should().BeEmpty("sin motivo no se envía nada");
        cut.Markup.Should().Contain("Indica el motivo de la reapertura, de hasta 500 caracteres.");

        cut.Find("textarea").Input("Falta una factura de septiembre");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reabrir mes").Click();

        cut.WaitForAssertion(() => mediador.Reaperturas.Should().ContainSingle());
        var orden = mediador.Reaperturas.Single();
        orden.TenantBeneficiarioId.Should().Be(TenantA);
        orden.Motivo.Should().Be("Falta una factura de septiembre");
    }

    [Fact]
    public void Un_motivo_de_mas_de_quinientos_caracteres_no_se_envia()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Existe), Roles.DireccionCae, out var mediador);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Reabrir un mes cerrado")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Motivo de la reapertura"));
        cut.Find("textarea").Input(new string('x', 501));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reabrir mes").Click();

        mediador.Reaperturas.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- lo que no se mide

    [Fact]
    public void El_cuadro_no_tiene_ocupacion_de_Gestores_CAE_en_ninguna_pestana()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        // Lo único que se excluye por decisión es la ocupación (la actividad) de cada Gestor CAE, en ninguna pestaña.
        foreach (var pestana in new[]
                 {
                     "Visión general", "Facturación", "Empresas", "Demanda", "Vacaciones y cobertura", "Comunicaciones", "Comparativa anual", "Cartera por Gestor CAE",
                 })
        {
            IrA(cut, pestana);
            cut.Markup.Should().NotContainEquivalentOf("Ocupación de Gestores CAE", pestana);
            cut.Markup.Should().NotContainEquivalentOf("ocupación por gestor", pestana);
        }
    }

    [Fact]
    public void La_Cartera_por_Gestor_CAE_cuenta_Empresas_asignadas_y_no_mide_actividad()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        IrA(cut, "Cartera por Gestor CAE");

        var filas = cut.FindAll("[data-tabla=cartera] tbody tr");
        filas.Should().HaveCount(2);
        filas[0].TextContent.Should().Contain("Marta Gestora");
        filas[0].InnerHtml.Should().Contain("Refrielectric S.A.", "las Empresas de la cartera salen como ayuda de la celda");
        filas[1].TextContent.Should().Contain("Sin Gestor CAE principal");
        filas[1].InnerHtml.Should().Contain("Montajes Ebro S.L.");
        cut.Markup.Should().Contain("No mide horas, conexiones, velocidad ni errores de nadie");
    }

    [Fact]
    public void El_selector_de_ambito_ofrece_el_Operador_CAE_y_los_equipos_de_Gestor_CAE_pero_no_las_Empresas()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        var opciones = cut.FindAll("select.cuadro-select option").Select(o => o.TextContent.Trim()).ToList();

        opciones.Should().Equal("Todo el Operador CAE", "Equipo Marta Gestora");
        opciones.Should().NotContain(o => o.Contains("Refrielectric") || o.Contains("Montajes Ebro"));
    }

    [Fact]
    public void Elegir_un_equipo_reduce_el_cuadro_a_las_Empresas_de_su_Gestor_CAE()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        cut.Find("select.cuadro-select").Change(cut.FindAll("select.cuadro-select option")[1].GetAttribute("value")!);
        IrA(cut, "Empresas");

        cut.FindAll("[data-tabla=empresas] tbody tr[data-empresa]").Select(f => f.GetAttribute("data-empresa"))
            .Should().Equal(TenantA.ToString());
    }

    [Fact]
    public void Los_momentos_Mes_1_Mes_6_y_Año_3_estan_en_la_barra_y_el_futuro_se_desactiva()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _,
            serie: q => new SerieCuadroDireccionDto([], [], 0));

        cut.FindAll(".cuadro-momento button").Select(b => b.FirstChild!.TextContent.Trim()).Should().Equal("Mes 1", "Mes 6", "Año 3");
        cut.FindAll(".cuadro-momento button small").Select(s => s.TextContent.Trim())
            .Should().OnlyContain(m => m == m.ToLowerInvariant(), "los rótulos de segmento van en minúsculas («oct 2026»)");
    }

    [Fact]
    public void La_barra_reune_Personalizar_Etiquetas_y_Presentar_en_el_orden_de_Design()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        var filas = cut.FindAll(".cuadro-barra");
        filas.Should().ContainSingle();
        var primera = filas[0].QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();
        primera.Should().ContainInOrder("Personalizar esta pestaña", "Etiquetas de datos", "Presentar");
    }

    /// <summary>
    /// «Exportar extranjeros del mes» (decisión de Chris, 2026-10-20): una descarga del mes seleccionado, fuera de la
    /// navegación mejorada de Blazor para que el navegador reciba el .xlsx.
    /// </summary>
    [Fact]
    public void Exportar_extranjeros_del_mes_descarga_el_mes_seleccionado()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        var enlace = cut.Find(".cuadro-exportar-extranjeros");
        enlace.TextContent.Trim().Should().Be("Exportar extranjeros del mes");
        enlace.GetAttribute("href").Should().MatchRegex(@"^/direccion/extranjeros\.xlsx\?anyo=\d{4}&mes=\d{1,2}$");
        enlace.HasAttribute("download").Should().BeTrue();
        enlace.GetAttribute("data-enhance-nav").Should().Be("false");
    }

    [Fact]
    public void El_mes_y_el_chip_de_estado_van_en_la_misma_fila_que_el_titulo()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        var titular = cut.Find(".cabecera-pagina-titular");
        titular.QuerySelector("h1")!.Should().NotBeNull();
        titular.QuerySelector(".cuadro-mes .cuadro-estado").Should().NotBeNull("el chip «parcial» va junto al mes y al título");
        cut.FindAll(".cabecera-pagina-descripcion").Should().BeEmpty();
    }

    [Fact]
    public void Presentar_activa_el_modo_presentacion_y_Escape_lo_cierra()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);
        cut.FindAll(".cuadro-presenta").Should().BeEmpty();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Presentar").Click();
        cut.FindAll(".cuadro-presenta").Should().HaveCount(1);

        cut.Find(".cuadro").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.FindAll(".cuadro-presenta").Should().BeEmpty();
    }

    [Fact]
    public void La_pestaña_Cartera_lleva_la_insignia_de_Empresas_sin_Gestor_CAE_principal()
    {
        var cut = Pintar(Cuadro(EstadoCifraMensual.Parcial), Roles.DireccionCae, out _);

        var pestana = cut.FindAll("button[role=tab]").Single(b => b.TextContent.Contains("Cartera por Gestor CAE"));

        pestana.TextContent.Should().Contain("1");
    }

    [Fact]
    public void El_mes_futuro_sale_con_su_estado_vacio_y_sin_cifras()
    {
        var hoy = DiaDeNegocio.Hoy();
        var futuro = new CuadroDireccionDto(
            hoy.Year, hoy.Month, EstadoCifraMensual.Parcial, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
            [], Cifras(EstadoCifraMensual.Parcial, 0, []), 0, CuadroDireccionSinCifras.MesFuturo);
        var mediador = new MediatorFalso(() => futuro);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<CuadroDireccionPagina>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ese mes todavía no ha empezado"));
        cut.FindAll(".cuadro-kpi").Should().BeEmpty();
    }
}
