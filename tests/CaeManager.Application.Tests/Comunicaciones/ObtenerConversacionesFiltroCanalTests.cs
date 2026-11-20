using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Chips de canal de la bandeja (lienzo «Pulida: bandeja de la cartera», L-10; D1 del 2026-11-17): <see cref="ObtenerConversacionesQuery.Canal"/>
/// deja en la lista solo las conversaciones de ese canal y, sin canal, todas. Es un filtro que se suma con AND a la visibilidad: nunca
/// amplía lo que se ve. El test lo fija en esta capa porque la pantalla solo comprueba que manda el canal, no que el servidor lo aplique.
/// </summary>
public class ObtenerConversacionesFiltroCanalTests
{
    private static readonly DateTime Fecha = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    private static (ObtenerConversacionesQueryHandler Handler, Guid CorreoA, Guid CorreoB, Guid WhatsApp) Escenario()
    {
        var comunicaciones = new ComunicacionesQueryContextFalso();

        var correoA = new Conversacion("Certificado de la contrata");
        var correoB = new Conversacion("Alta de un trabajador");
        var whatsApp = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId: null, ejecutivoId: null);
        comunicaciones.ListaConversaciones.AddRange([correoA, correoB, whatsApp]);
        comunicaciones.ListaMensajes.AddRange([
            new Mensaje(correoA.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "uno@ejemplo.test", "<p>Uno</p>", Fecha),
            new Mensaje(correoB.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "dos@ejemplo.test", "<p>Dos</p>", Fecha),
            new Mensaje(whatsApp.Id, DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Aviso", Fecha)]);

        var handler = new ObtenerConversacionesQueryHandler(
            new EmpresasQueryContextFalso(), comunicaciones, new IntegracionesQueryContextFalso(), new CentrosQueryContextFalso(),
            new GestionesVacias(), new VisitasVacias(), new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"),
            new SinTenant());
        return (handler, correoA.Id, correoB.Id, whatsApp.Id);
    }

    [Fact]
    public async Task Sin_canal_devuelve_las_conversaciones_de_todos_los_canales()
    {
        var (handler, correoA, correoB, whatsApp) = Escenario();

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(), CancellationToken.None);

        resultado.TotalElementos.Should().Be(3);
        resultado.Elementos.Select(c => c.Id).Should().BeEquivalentTo([correoA, correoB, whatsApp]);
    }

    [Fact]
    public async Task Con_el_canal_Correo_devuelve_solo_las_de_correo()
    {
        var (handler, correoA, correoB, _) = Escenario();

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(Canal: CanalConversacion.Correo), CancellationToken.None);

        resultado.TotalElementos.Should().Be(2);
        resultado.Elementos.Select(c => c.Id).Should().BeEquivalentTo([correoA, correoB]);
        resultado.Elementos.Should().OnlyContain(c => c.Canal == CanalConversacion.Correo);
    }

    [Fact]
    public async Task Con_el_canal_WhatsApp_devuelve_solo_las_de_WhatsApp()
    {
        var (handler, _, _, whatsApp) = Escenario();

        var resultado = await handler.Handle(new ObtenerConversacionesQuery(Canal: CanalConversacion.WhatsApp), CancellationToken.None);

        resultado.TotalElementos.Should().Be(1);
        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(whatsApp);
        resultado.Elementos.Single().Canal.Should().Be(CanalConversacion.WhatsApp);
    }

    [Fact]
    public async Task El_canal_se_suma_a_los_demas_filtros_con_AND()
    {
        var (handler, _, _, whatsApp) = Escenario();

        var whatsAppSinAsignar = await handler.Handle(
            new ObtenerConversacionesQuery(Canal: CanalConversacion.WhatsApp, SoloSinAsignar: true), CancellationToken.None);
        var correoConLaBusquedaDeWhatsApp = await handler.Handle(
            new ObtenerConversacionesQuery(Canal: CanalConversacion.Correo, Busqueda: "WhatsApp"), CancellationToken.None);

        whatsAppSinAsignar.Elementos.Should().ContainSingle().Which.Id.Should().Be(whatsApp, "la de WhatsApp no tiene Gestor CAE asignado");
        correoConLaBusquedaDeWhatsApp.TotalElementos.Should().Be(0, "el canal no amplía lo que deja pasar otro filtro");
    }

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(new List<Visita>().AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => throw new NotSupportedException();
    }

    private sealed class GestionesVacias : IGestionesQueryContext
    {
        public IQueryable<Gestion> Gestiones => new TestAsyncQueryable<Gestion>(new List<Gestion>().AsQueryable());
        public IQueryable<GestionAutomatica> GestionesAutomaticas => throw new NotSupportedException();
        public IQueryable<PasoGestionAutomatica> PasosGestionAutomatica => throw new NotSupportedException();
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
