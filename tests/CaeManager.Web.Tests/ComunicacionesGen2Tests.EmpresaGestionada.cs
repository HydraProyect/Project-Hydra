using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Comunicaciones.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Selector «Empresa gestionada» y vista unificada en solo consulta de la página de Comunicaciones (PR-S2b, L-1, D-6 b). La página
/// consume solo las dos lecturas de main (ObtenerTenantsBeneficiariosVistaUnificadaQuery y ObtenerConversacionesUnificadasQuery); la
/// elección viaja como filtro y nunca cambia el Tenant activo. Fija lo que se conserva de main (sin selector cuando no hay otra
/// Empresa gestionada, la lista de siempre al elegir el Tenant activo) y lo nuevo.
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private static readonly Guid TenantActivo = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");
    private static readonly Guid TenantOtro = Guid.Parse("d4d4d4d4-0000-0000-0000-000000000004");

    private sealed class TenantActualConId(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class NotificadorInerte : INotificadorMensajesTiempoReal
    {
        public IDisposable Suscribir(Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback) => new Baja();

        public Task PublicarAsync(MensajeWhatsAppRecibidoEvent aviso, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class Baja : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static TenantsBeneficiariosVistaUnificadaDto ConjuntoDeEmpresas(params TenantBeneficiarioVistaUnificadaDto[] tenants) => new(tenants, []);

    private static readonly TenantBeneficiarioVistaUnificadaDto EmpresaActiva = new(TenantActivo, "Talleres Norte", true, 3);
    private static readonly TenantBeneficiarioVistaUnificadaDto EmpresaOtra = new(TenantOtro, "Construcciones Ebro", false, 2);

    private static ConversacionUnificadaDto FilaUnificada(Guid tenantId, string tenantNombre, string asunto) =>
        new(Guid.NewGuid(), tenantId, tenantNombre, 41, asunto, EstadoConversacion.Abierta, CanalConversacion.Correo,
            DateTime.UtcNow, null, null, null, "cae@ebro.example.invalid");

    /// <summary>La página con Tenant activo; las dos lecturas de la vista unificada las responde el test.</summary>
    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) RenderizarConEmpresas(
        Escenario escenario, Func<object, Task<object?>?> vistaUnificada)
    {
        Services.AddSingleton<ILogger<ListaConversacionesUnificadas>>(_ => NullLogger<ListaConversacionesUnificadas>.Instance);
        escenario.Interceptar = vistaUnificada;
        return Renderizar(escenario, tenantActivo: TenantActivo);
    }

    private static Func<object, Task<object?>?> Responde(TenantsBeneficiariosVistaUnificadaDto conjunto, params ConversacionUnificadaDto[] filas) =>
        peticion => peticion switch
        {
            ObtenerTenantsBeneficiariosVistaUnificadaQuery => Task.FromResult<object?>(conjunto),
            ObtenerConversacionesUnificadasQuery q => Task.FromResult<object?>(new PaginaConversacionesUnificadasDto(
                filas.Where(f => q.TenantIds is null || q.TenantIds.Contains(f.TenantId)).ToList(), [], null)),
            _ => null
        };

    private static void Elegir(IRenderedComponent<Bandeja> cut, string texto)
    {
        cut.Find(".eg-boton").Click();
        cut.FindAll(".eg-opcion").Single(o => o.TextContent.Contains(texto, StringComparison.Ordinal)).Click();
    }

    [Fact]
    public void Sin_otra_Empresa_gestionada_no_hay_selector_y_la_pagina_es_la_de_main()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva)));

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerTenantsBeneficiariosVistaUnificadaQuery));
        Assert.Empty(cut.FindAll(".eg-boton"));
        Assert.DoesNotContain(mediador.Enviados, p => p is ObtenerConversacionesUnificadasQuery);
    }

    [Fact]
    public void Sin_Tenant_activo_no_se_pide_el_conjunto_de_Empresas_gestionadas()
    {
        var escenario = new Escenario { Interceptar = Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)) };
        Services.AddSingleton<ILogger<ListaConversacionesUnificadas>>(_ => NullLogger<ListaConversacionesUnificadas>.Instance);
        var (cut, mediador) = Renderizar(escenario);

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerConversacionesQuery));
        Assert.DoesNotContain(mediador.Enviados, p => p is ObtenerTenantsBeneficiariosVistaUnificadaQuery);
        Assert.Empty(cut.FindAll(".eg-boton"));
    }

    [Fact]
    public void El_selector_nombra_el_Tenant_activo_y_lista_las_Empresas_gestionadas_con_sus_abiertas()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaOtra, EmpresaActiva)));

        cut.WaitForElement(".eg-boton");
        Assert.Equal("Talleres Norte", cut.Find(".eg-nom").TextContent);
        Assert.Equal("TN", cut.Find(".eg-boton .eg-mono").TextContent);
        Assert.Equal("Empresa gestionada: Talleres Norte. Cambiar de Empresa gestionada", cut.Find(".eg-boton").GetAttribute("aria-label"));

        cut.Find(".eg-boton").Click();

        Assert.Equal("Empresas gestionadas · 2", cut.Find(".eg-pop-t").TextContent);
        var opciones = cut.FindAll(".eg-opcion");
        Assert.Equal(3, opciones.Count);
        Assert.Equal("Todas las Empresas gestionadas5", opciones[0].TextContent.Trim());
        Assert.Equal("CEConstrucciones Ebro2", opciones[1].TextContent.Trim());
        // La opción marcada es lo que muestra la lista: el Tenant activo.
        Assert.Equal("true", opciones[2].GetAttribute("aria-selected"));
        Assert.Equal("false", opciones[0].GetAttribute("aria-selected"));
        Assert.DoesNotContain("Tenant", cut.Find(".eg-selector").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void La_busqueda_estrecha_las_opciones_y_avisa_cuando_ninguna_coincide()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        cut.WaitForElement(".eg-boton").Click();

        cut.Find(".eg-buscar").Input("ebro");
        Assert.Equal(2, cut.FindAll(".eg-opcion").Count);

        cut.Find(".eg-buscar").Input("zzz");
        Assert.Single(cut.FindAll(".eg-opcion"));
        Assert.Contains(cut.FindAll(".eg-pop-t"), t => t.TextContent == "Ninguna Empresa gestionada coincide con «zzz»");
    }

    [Fact]
    public void Elegir_otra_Empresa_gestionada_la_consulta_en_solo_consulta_sin_cambiar_de_Tenant_ni_ofrecer_acciones()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra),
            FilaUnificada(TenantOtro, "Construcciones Ebro", "Renovación del TC2")));
        cut.WaitForElement(".eg-boton");
        Assert.NotEmpty(cut.FindAll("button[aria-label='Redactar correo']"));

        Elegir(cut, "Construcciones Ebro");

        cut.WaitForElement(".eu-fila");
        var consulta = mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>().Single();
        Assert.Equal(new[] { TenantOtro }, consulta.TenantIds);
        Assert.Equal(GrupoEstadoConversacion.Abiertas, consulta.GrupoEstado);
        Assert.Equal("Construcciones Ebro · Abiertas", cut.Find(".eu-titulo").TextContent);
        Assert.Equal("Solo consulta. Para responder, abre arriba, en «Trabajando en», Construcciones Ebro.", cut.Find(".eu-aviso").TextContent);
        Assert.Equal("Construcciones Ebro", cut.Find(".eg-nom").TextContent);
        Assert.Contains("Renovación del TC2", cut.Find(".eu-fila").TextContent, StringComparison.Ordinal);
        // Solo consulta: sin «Redactar correo», sin filtros, sin buscador y sin filas que abran nada.
        Assert.Empty(cut.FindAll("button[aria-label='Redactar correo']"));
        Assert.Empty(cut.FindAll(".bandeja-filtros-form"));
        Assert.Empty(cut.FindAll(".bandeja-buscar-cabecera"));
        Assert.Empty(cut.FindAll(".eu-fila button, .eu-fila a"));
        Assert.DoesNotContain("Tenant", cut.Find(".eu-lista").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Elegir_Todas_pide_la_vista_unificada_sin_filtro_de_Empresa_gestionada()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra),
            FilaUnificada(TenantOtro, "Construcciones Ebro", "Renovación del TC2"),
            FilaUnificada(TenantActivo, "Talleres Norte", "Alta de trabajador")));
        cut.WaitForElement(".eg-boton");

        Elegir(cut, "Todas las Empresas gestionadas");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".eu-fila").Count));
        Assert.Null(mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>().Single().TenantIds);
        Assert.Equal("Todas mis Empresas gestionadas · Abiertas", cut.Find(".eu-titulo").TextContent);
        Assert.Equal("Solo consulta. Para responder una conversación, abre su Empresa gestionada arriba, en «Trabajando en».",
            cut.Find(".eu-aviso").TextContent);
    }

    [Fact]
    public void Elegir_el_Tenant_activo_vuelve_a_la_lista_de_main()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación de marzo", ClienteRefrielectric));
        var (cut, mediador) = RenderizarConEmpresas(escenario, Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        cut.WaitForElement(".eg-boton");
        Elegir(cut, "Construcciones Ebro");
        cut.WaitForElement(".eu-lista");
        var listasDeMain = mediador.Enviados.Count(p => p is ObtenerConversacionesQuery);

        Elegir(cut, "Talleres Norte");

        Assert.Empty(cut.FindAll(".eu-lista"));
        Assert.Contains("Documentación de marzo", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("button[aria-label='Redactar correo']"));
        Assert.Single(mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>());
        Assert.True(mediador.Enviados.Count(p => p is ObtenerConversacionesQuery) >= listasDeMain);
    }

    [Fact]
    public void Con_Empresas_gestionadas_sin_leer_la_lista_lo_dice_y_no_pinta_Al_dia()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), peticion => peticion switch
        {
            ObtenerTenantsBeneficiariosVistaUnificadaQuery => Task.FromResult<object?>(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)),
            ObtenerConversacionesUnificadasQuery => Task.FromResult<object?>(new PaginaConversacionesUnificadasDto(
                [], [new CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado.TenantNoConsultadoDto(TenantOtro, "Construcciones Ebro", false)], null)),
            _ => null
        });
        cut.WaitForElement(".eg-boton");

        Elegir(cut, "Todas las Empresas gestionadas");

        cut.WaitForElement(".eu-aviso-incompleto");
        Assert.Equal("No pudimos leer las conversaciones de Construcciones Ebro.", cut.Find(".eu-aviso-incompleto").TextContent);
        Assert.Empty(cut.FindAll(".eu-al-dia"));
    }

    [Fact]
    public void Si_el_servidor_rechaza_la_vista_unificada_la_pagina_queda_como_en_main()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), peticion => peticion is ObtenerTenantsBeneficiariosVistaUnificadaQuery
            ? Task.FromException<object?>(new UnauthorizedAccessException("fuera de la vista unificada"))
            : null);

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerTenantsBeneficiariosVistaUnificadaQuery));
        Assert.Empty(cut.FindAll(".eg-boton"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Redactar correo']"));
    }

    [Theory]
    [InlineData("Construcciones Ebro", "CE")]
    [InlineData("  instalaciones   del sur", "ID")]
    [InlineData("Ñandú", "Ñ")]
    [InlineData("", "")]
    public void Las_iniciales_del_monograma_son_las_de_las_dos_primeras_palabras(string nombre, string esperadas) =>
        Assert.Equal(esperadas, SelectorEmpresaGestionada.Iniciales(nombre));
}
