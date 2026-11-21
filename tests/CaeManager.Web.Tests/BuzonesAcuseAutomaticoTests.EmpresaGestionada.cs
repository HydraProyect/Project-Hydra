using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Integraciones.Pages;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Buzones por Empresa gestionada (lienzo «Comunicaciones multi-buzón», tablero de Buzones, L-55): las pastillas, el
/// título y el subtítulo con la Empresa gestionada, y los buzones de otra en solo consulta. Qué Tenants se ofrecen y qué
/// buzones se leen lo decide el servidor (<c>ObtenerBuzonesVistaUnificadaQuery</c>, probada contra PostgreSQL en la
/// integración); aquí el puerto es un doble.
/// </summary>
public partial class BuzonesAcuseAutomaticoTests
{
    private static readonly Guid TenantActivo = Guid.Parse("0a0a0a0a-0000-0000-0000-00000000000a");
    private static readonly Guid TenantOtro = Guid.Parse("0b0b0b0b-0000-0000-0000-00000000000b");

    internal sealed class EmpresasGestionadasFalsas : IBuzonesDeTenantsBeneficiarios, ITenantActual
    {
        public Guid? TenantId { get; set; } = TenantActivo;
        public List<TenantBeneficiarioDeBuzonesDto> Empresas { get; set; } = [];
        public Exception? FalloEmpresas { get; set; }
        public Dictionary<Guid, BuzonesDeTenantBeneficiarioDto?> Buzones { get; } = [];
        public List<Guid> Consultas { get; } = [];

        public Task<IReadOnlyList<TenantBeneficiarioDeBuzonesDto>> ObtenerTenantsAsync(CancellationToken cancellationToken = default) =>
            FalloEmpresas is { } fallo
                ? throw fallo
                : Task.FromResult<IReadOnlyList<TenantBeneficiarioDeBuzonesDto>>(Empresas.ToList());

        public Task<BuzonesDeTenantBeneficiarioDto?> ObtenerBuzonesAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            Consultas.Add(tenantId);
            return Task.FromResult(Buzones.GetValueOrDefault(tenantId));
        }
    }

    private static EmpresasGestionadasFalsas DosEmpresas() => new()
    {
        Empresas = [new(TenantActivo, "Propia"), new(TenantOtro, "Otra gestionada")],
    };

    private static void Pulsar(IRenderedComponent<Buzones> cut, string empresa) =>
        cut.FindAll("[data-testid=empresas-gestionadas] button").Single(b => b.TextContent.Trim() == empresa).Click();

    private static string? Pulsada(IRenderedComponent<Buzones> cut) =>
        cut.FindAll("[data-testid=empresas-gestionadas] button")
            .SingleOrDefault(b => b.GetAttribute("aria-pressed") == "true")?.TextContent.Trim();

    [Fact]
    public void Sin_otra_Empresa_gestionada_no_hay_pastillas_y_el_titulo_es_el_de_siempre()
    {
        var empresas = new EmpresasGestionadasFalsas { Empresas = [new(TenantActivo, "Propia")] };
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);

        cut.FindAll("[data-testid=empresas-gestionadas]").Should().BeEmpty();
        cut.Find("h1").TextContent.Trim().Should().Be("Buzones de correo");
        cut.FindAll("[data-testid=subtitulo-empresa]").Should().BeEmpty();
        cut.FindAll("[data-testid=interruptor-acuse]").Should().ContainSingle("control positivo: la página es la de siempre");
    }

    [Fact]
    public void Con_otra_Empresa_gestionada_salen_las_pastillas_con_la_activa_pulsada_y_el_titulo_la_nombra()
    {
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: DosEmpresas());

        var grupo = cut.Find("[data-testid=empresas-gestionadas]");
        grupo.GetAttribute("role").Should().Be("group");
        grupo.GetAttribute("aria-label").Should().Be("Empresa gestionada");
        cut.FindAll("[data-testid=empresas-gestionadas] button").Select(b => b.TextContent.Trim())
            .Should().Equal("Propia", "Otra gestionada");
        Pulsada(cut).Should().Be("Propia");
        cut.Find("h1").TextContent.Trim().Should().Be("Buzones de correo de Propia");
        cut.Find("[data-testid=subtitulo-empresa]").TextContent.Trim().Should().Be("Lo que entra por ellos es de Propia.");
        cut.Markup.Should().NotContain("Tenant");
        cut.Find("a[href='/integraciones']").Should().NotBeNull("control positivo: en el Tenant activo se enlaza a Conexiones");
    }

    [Fact]
    public void Si_la_Empresa_gestionada_activa_no_esta_en_el_conjunto_no_hay_pastillas_y_la_pagina_es_la_de_siempre()
    {
        // El Tenant de origen sin gestionar, por ejemplo: no habría pastilla pulsada ni nombre para el título.
        var empresas = new EmpresasGestionadasFalsas { Empresas = [new(TenantOtro, "Otra gestionada")] };
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);

        cut.FindAll("[data-testid=empresas-gestionadas]").Should().BeEmpty();
        cut.Find("h1").TextContent.Trim().Should().Be("Buzones de correo");
        cut.FindAll("[data-testid=interruptor-acuse]").Should().ContainSingle();
    }

    [Fact]
    public void En_solo_consulta_no_se_explica_el_acuse_ni_se_enlaza_a_las_Conexiones_del_Tenant_activo()
    {
        var empresas = DosEmpresas();
        empresas.Buzones[TenantOtro] = new BuzonesDeTenantBeneficiarioDto(
            [Buzon("obra")], new Dictionary<Guid, string>(), new Dictionary<Guid, ActividadBuzonDto>());
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);
        var subtituloDeMain = cut.Find("[data-testid=subtitulo-empresa]").ParentElement!.TextContent;
        subtituloDeMain.Should().Contain("acuse", "control positivo: en el Tenant activo el subtítulo de main explica el acuse");

        Pulsar(cut, "Otra gestionada");

        cut.FindAll("a[href='/integraciones']").Should().BeEmpty();
        var descripcion = cut.Find("[data-testid=subtitulo-empresa]").ParentElement!.TextContent;
        descripcion.Should().NotContain("acuse").And.Contain("Lo que entra por ellos es de Otra gestionada.");
    }

    [Fact]
    public void Con_el_modal_del_acuse_abierto_pulsar_otra_Empresa_gestionada_no_la_consulta()
    {
        var empresas = DosEmpresas();
        empresas.Buzones[TenantOtro] = new BuzonesDeTenantBeneficiarioDto(
            [Buzon("obra")], new Dictionary<Guid, string>(), new Dictionary<Guid, ActividadBuzonDto>());
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);
        cut.Find("[data-testid=interruptor-acuse]").Click();

        Pulsar(cut, "Otra gestionada");

        empresas.Consultas.Should().BeEmpty("el modal del acuse actúa en el Tenant activo");
        Pulsada(cut).Should().Be("Propia");
    }

    [Fact]
    public void Los_buzones_de_otra_Empresa_gestionada_se_ven_en_solo_consulta_sin_ninguna_accion()
    {
        var responsable = Guid.NewGuid();
        var compartido = Buzon("obra", acuse: true, responsable: responsable);
        var conError = Buzon("roto", estado: EstadoConexionIntegracion.ConError, ultimoError: "sin permiso");
        var empresas = DosEmpresas();
        empresas.Buzones[TenantOtro] = new BuzonesDeTenantBeneficiarioDto(
            [compartido, conError], new Dictionary<Guid, string> { [responsable] = "Marta Gestora" },
            new Dictionary<Guid, ActividadBuzonDto> { [compartido.Id] = new(null, 4) });
        var mediador = new MediadorBuzones { Conexiones = [Buzon("cae")] };
        var cut = Renderizar(mediador, empresas: empresas);

        Pulsar(cut, "Otra gestionada");

        empresas.Consultas.Should().Equal(TenantOtro);
        Pulsada(cut).Should().Be("Otra gestionada");
        cut.Find("h1").TextContent.Trim().Should().Be("Buzones de correo de Otra gestionada");
        cut.Find("[data-testid=aviso-consulta-empresa]").TextContent.Trim()
            .Should().Be("Solo consulta. Para cambiar estos buzones, abre arriba, en «Trabajando en», Otra gestionada.");
        cut.FindAll("[data-testid=fila-buzon]").Should().HaveCount(2);
        Fila(cut, "obra").Should().Contain("Marta Gestora").And.Contain("Activado").And.Contain("4");
        cut.FindAll("[data-testid=franja-error-buzon]").Should().ContainSingle("control positivo: el error se ve");
        foreach (var control in new[] { "interruptor-acuse", "selector-responsable", "comprobar-acceso-buzon", "reconectar-buzon", "anadir-buzon" })
            cut.FindAll($"[data-testid={control}]").Should().BeEmpty($"{control} actuaría en el Tenant activo");
        mediador.ComandosAcuse.Should().BeEmpty();
    }

    [Fact]
    public void El_Administrador_que_consulta_otra_Empresa_gestionada_lee_como_pasar_a_ella_para_cambiar_los_buzones()
    {
        var cut = RenderizarConsultaDeOtra(Roles.Administrador);

        var aviso = cut.FindAll("[data-testid=aviso-consulta-empresa]").Should().ContainSingle().Subject;
        aviso.TextContent.Trim()
            .Should().Be("Solo consulta. Para cambiar estos buzones, abre arriba, en «Trabajando en», Otra gestionada.");
        cut.Markup.Should().NotContain("Tenant");
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(null)]
    public void Quien_no_es_Administrador_y_consulta_otra_Empresa_gestionada_lee_quien_puede_cambiar_los_buzones(string? rol)
    {
        var cut = RenderizarConsultaDeOtra(rol);

        var aviso = cut.FindAll("[data-testid=aviso-consulta-empresa]").Should().ContainSingle().Subject;
        aviso.TextContent.Trim()
            .Should().Be("Solo consulta. Solo el Administrador de Otra gestionada puede cambiar estos buzones.");
        cut.Markup.Should().NotContain("Tenant").And.NotContain("Trabajando en");
    }

    private IRenderedComponent<Buzones> RenderizarConsultaDeOtra(string? rol)
    {
        var empresas = DosEmpresas();
        empresas.Buzones[TenantOtro] = new BuzonesDeTenantBeneficiarioDto(
            [Buzon("obra")], new Dictionary<Guid, string>(), new Dictionary<Guid, ActividadBuzonDto>());
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, rol, empresas: empresas);

        Pulsar(cut, "Otra gestionada");

        cut.FindAll("[data-testid=fila-buzon]").Should().ContainSingle("control positivo: se consultan los buzones de la otra Empresa gestionada");
        return cut;
    }

    [Fact]
    public void Pulsar_otra_vez_la_Empresa_consultada_vuelve_a_los_buzones_del_Tenant_activo_con_sus_controles()
    {
        var empresas = DosEmpresas();
        empresas.Buzones[TenantOtro] = new BuzonesDeTenantBeneficiarioDto(
            [Buzon("obra")], new Dictionary<Guid, string>(), new Dictionary<Guid, ActividadBuzonDto>());
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);

        Pulsar(cut, "Otra gestionada");
        cut.FindAll("[data-testid=interruptor-acuse]").Should().BeEmpty();
        Pulsar(cut, "Otra gestionada");

        Pulsada(cut).Should().Be("Propia");
        cut.FindAll("[data-testid=aviso-consulta-empresa]").Should().BeEmpty();
        Fila(cut, "cae").Should().NotBeNull();
        cut.FindAll("[data-testid=interruptor-acuse]").Should().ContainSingle();
        cut.FindAll("[data-testid=anadir-buzon]").Should().ContainSingle();
    }

    [Fact]
    public void Si_el_servidor_no_lee_esa_Empresa_gestionada_la_pagina_avisa_y_no_afirma_nada_de_sus_buzones()
    {
        var empresas = DosEmpresas();
        empresas.Buzones[TenantOtro] = null;
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);

        Pulsar(cut, "Otra gestionada");

        cut.Find("[data-testid=aviso-buzones-empresa-no-cargados]").TextContent.Trim()
            .Should().StartWith("No pudimos cargar los buzones de Otra gestionada.");
        cut.FindAll("[data-testid=fila-buzon]").Should().BeEmpty();
        cut.FindAll("[data-testid=aviso-buzones-no-cargados]").Should().BeEmpty();
    }

    [Fact]
    public void Otra_Empresa_gestionada_sin_buzones_no_invita_a_anadir_uno()
    {
        var empresas = DosEmpresas();
        empresas.Buzones[TenantOtro] = new BuzonesDeTenantBeneficiarioDto(
            [], new Dictionary<Guid, string>(), new Dictionary<Guid, ActividadBuzonDto>());
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);

        Pulsar(cut, "Otra gestionada");

        cut.Markup.Should().Contain("Aún no hay buzones conectados.").And.NotContain("Añade el primero con su dirección.");
    }

    [Fact]
    public void Fuera_de_la_vista_unificada_la_pagina_sigue_como_siempre_sin_pastillas()
    {
        var empresas = DosEmpresas();
        empresas.FalloEmpresas = new UnauthorizedAccessException("papel Inbound");
        var cut = Renderizar(new MediadorBuzones { Conexiones = [Buzon("cae")] }, empresas: empresas);

        cut.FindAll("[data-testid=empresas-gestionadas]").Should().BeEmpty();
        cut.Find("h1").TextContent.Trim().Should().Be("Buzones de correo");
        cut.FindAll("[data-testid=interruptor-acuse]").Should().ContainSingle();
        cut.FindAll("[data-testid=aviso-buzones-no-cargados]").Should().BeEmpty();
    }
}
