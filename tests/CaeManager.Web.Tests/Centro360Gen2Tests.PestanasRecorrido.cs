using Bunit;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Asignaciones.Queries.ObtenerTrabajadoresVisitaSinAsignacion;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Fact]
    public async Task Ficha_muestra_solo_los_datos_cargados_y_cambiar_pestana_conserva_q_y_estado_sin_consultar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var navegador = Services.GetRequiredService<NavigationManager>();
        navegador.NavigateTo($"centros/{id}?q=Ana&estado=Vencido&extra=conservar");
        var cut = Renderizar(id);
        var consultas = mediador.Enviadas.Count;
        await cut.Find("[role=tab][aria-controls$='-ficha']").ClickAsync(new MouseEventArgs());
        navegador.Uri.Should().Contain("pestana=ficha").And.Contain("q=Ana").And.Contain("estado=Vencido").And.Contain("extra=conservar");
        // Centro 360 del 20-10: la Ficha lleva siempre Canales de gestión y Contactos, más las cajas con datos.
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should()
            .Equal("canales-centro", "contactos-centro", "datos-centro", "acceso-centro");
        var caja = cut.Find("[data-caja=datos-centro]").TextContent;
        caja.Should().Contain("CN-01").And.Contain("Marta R.");
        cut.Find("[data-caja=acceso-centro]").TextContent.Should().Contain("Pol. Norte 4");
        caja.Should().NotContain("Horario").And.NotContain("Control de accesos").And.NotContain("Técnico de prevención");
        cut.Find(".barra-filtros").HasAttribute("hidden").Should().BeTrue();
        // Solo se añade la lectura de la caja de Contactos; el resto de la Ficha sale del detalle ya cargado.
        mediador.Enviadas.Count.Should().Be(consultas + 1);
        mediador.Enviadas.OfType<CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos.ObtenerAgendaContactosQuery>().Should().ContainSingle();
        await cut.Find("[role=tab][aria-controls$='-trabajadores']").ClickAsync(new MouseEventArgs());
        navegador.Uri.Should().NotContain("pestana=").And.Contain("q=Ana").And.Contain("estado=Vencido");
    }

    [Theory]
    [InlineData("CN-02", null, null, "Código del Centro")]
    [InlineData(null, "Calle Uno 2", null, "Dirección")]
    [InlineData(null, null, "Lucía", "Contacto")]
    public void Cada_dato_de_Ficha_basta_para_ofrecerla_sin_dibujar_campos_vacios(string? codigo, string? direccion, string? contacto, string etiqueta)
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Detalles[id] = Detalle(id, "Centro Norte") with { CodigoCentro = codigo, Direccion = direccion, Contacto = contacto };
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=ficha");
        var cut = Renderizar(id);
        var clave = direccion is null ? "datos-centro" : "acceso-centro";
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("canales-centro", "contactos-centro", clave);
        cut.Find($"[data-caja={clave}]").TextContent.Should().Contain(etiqueta).And.Contain(codigo ?? direccion ?? contacto!);
        cut.FindComponent<CajasFicha>().FindComponents<DatoRejilla>().Should().ContainSingle();
    }

    [Fact]
    public void Ficha_sin_datos_basicos_permite_configurar_canales_y_conserva_su_URL()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Detalles[id] = Detalle(id, "Centro Norte") with { CodigoCentro = null, Direccion = " ", Contacto = null };
        var navegador = Services.GetRequiredService<NavigationManager>();
        navegador.NavigateTo($"centros/{id}?pestana=ficha&q=Ana&estado=Vencido&extra=conservar");
        var cut = Renderizar(id);
        cut.FindAll("[role=tab][aria-controls$='-ficha']").Should().ContainSingle();
        // Sin datos básicos quedan solo las cajas de siempre: Canales de gestión (para configurarlos) y Contactos.
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("canales-centro", "contactos-centro");
        cut.Find("[role=tab][aria-selected=true]").GetAttribute("aria-controls").Should().EndWith("-ficha");
        navegador.Uri.Should().Contain("pestana=ficha").And.Contain("q=Ana").And.Contain("estado=Vencido").And.Contain("extra=conservar");
    }

    [Fact]
    public void Cambiar_a_otro_Centro_sin_datos_no_deja_una_Ficha_vacia_ni_datos_del_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[a] = Detalle(a, "Centro A");
        mediador.Detalles[b] = Detalle(b, "Centro B") with { CodigoCentro = null, Direccion = null, Contacto = null };
        var navegador = Services.GetRequiredService<NavigationManager>();
        navegador.NavigateTo($"centros/{a}?pestana=ficha&q=Ana&estado=Vencido");
        var cut = Renderizar(a);
        cut.FindComponents<CajasFicha>().Should().ContainSingle();
        cut.Render(p => p.Add(x => x.CentroId, b));
        cut.FindAll("[role=tab][aria-controls$='-ficha']").Should().ContainSingle();
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("canales-centro", "contactos-centro");
        cut.Markup.Should().NotContain("CN-01");
        navegador.Uri.Should().Contain("pestana=ficha").And.Contain("q=Ana").And.Contain("estado=Vencido");
    }

    [Fact]
    public void Historial_reutiliza_el_componente_real_con_el_Centro_actual_y_dice_sin_cambios_si_esta_vacio()
    {
        RegistrarHistorialReal();
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=historial&q=Ana&estado=Vencido");
        var cut = Renderizar(id);
        var historial = cut.FindComponent<PestanaHistorial>().Instance;
        historial.EntidadTipo.Should().Be("Centro");
        historial.EntidadId.Should().Be(id);
        cut.FindAll("h3").Select(x => x.TextContent.Trim()).Should().Contain("Sin cambios registrados");
        var consulta = mediador.Enviadas.OfType<ObtenerAuditoriaQuery>().Should().ContainSingle().Which;
        consulta.EntidadTipo.Should().Be("Centro");
        consulta.EntidadId.Should().Be(id);
        cut.Find(".barra-filtros").HasAttribute("hidden").Should().BeTrue();
    }

    [Fact]
    public async Task El_Historial_retardado_de_otro_Centro_no_reemplaza_el_del_Centro_actual()
    {
        RegistrarHistorialReal();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var retenida = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso { Retener = p => p is ObtenerAuditoriaQuery q && q.EntidadId == a ? retenida.Task : null });
        mediador.Detalles[a] = Detalle(a, "Centro A");
        mediador.Detalles[b] = Detalle(b, "Centro B");
        mediador.Auditoria[a] = [new(Guid.NewGuid(), "Centro", a, "Creado", null, DateTime.UtcNow, false, false)];
        mediador.Auditoria[b] = [new(Guid.NewGuid(), "Centro", b, "Eliminado", null, DateTime.UtcNow, false, false)];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{a}?pestana=historial");
        var cut = Renderizar(a);
        cut.Render(p => p.Add(x => x.CentroId, b));
        cut.WaitForAssertion(() => cut.Find(".workspace-historial-item").TextContent.Should().Contain("Eliminado"));
        await cut.InvokeAsync(retenida.SetResult);
        cut.FindAll(".workspace-historial-item").Should().ContainSingle();
        cut.Find(".workspace-historial-item").TextContent.Should().Contain("Eliminado").And.NotContain("Creado");
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task Visitas_vacias_ofrece_Programar_solo_con_escritura_y_conserva_el_Centro_en_el_destino(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Detalles[id] = Detalle(id, "Centro Norte");
        var navegador = Services.GetRequiredService<NavigationManager>();
        navegador.NavigateTo($"centros/{id}?pestana=visitas");
        var cut = Renderizar(id);
        cut.FindAll("h3").Select(x => x.TextContent.Trim()).Should().Contain("Sin visitas programadas");
        var botones = cut.FindAll("button[aria-label='Programar visita en este Centro']");
        botones.Should().HaveCount(seOfrece ? 1 : 0);
        if (seOfrece)
        {
            await botones[0].ClickAsync(new MouseEventArgs());
            navegador.Uri.Should().EndWith($"/visitas?centroId={id}");
        }
    }

    [Fact]
    public void Visitas_se_muestra_por_inicio_sin_cambiar_la_Visita_de_referencia_que_cargo_el_lector()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        var referencia = Guid.NewGuid();
        mediador.Visitas[id] = [new(referencia, new(2026, 12, 3), new(2026, 12, 4), 1), new(Guid.NewGuid(), new(2026, 12, 1), new(2026, 12, 10), 2)];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=visitas");
        var cut = Renderizar(id);
        cut.FindAll(".cuerpo-con-lateral-principal .elemento-lista-lateral-etiqueta").Select(x => x.TextContent.Trim()).Should()
            .Equal("01/12/2026 – 10/12/2026", "03/12/2026 – 04/12/2026");
        var acordeon = cut.FindComponent<AcordeonAsignacionesCentro>().Instance;
        acordeon.VisitaId.Should().Be(referencia);
        acordeon.VisitaFechaFin.Should().Be(new DateOnly(2026, 12, 4));
        mediador.Enviadas.OfType<ObtenerTrabajadoresVisitaSinAsignacionQuery>().Should().ContainSingle().Which.VisitaId.Should().Be(referencia);
    }

    private void RegistrarHistorialReal()
    {
        Services.AddScoped<PuertaAccesoDatos>();
    }
}
