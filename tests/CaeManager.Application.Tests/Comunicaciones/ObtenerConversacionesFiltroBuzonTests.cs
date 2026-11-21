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
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Desplegable «Buzón» de la bandeja (lienzo «Comunicaciones multi-buzón», L-9): <see cref="ObtenerConversacionesQuery.ConexionIntegracionId"/>
/// deja solo los hilos de esa conexión de buzón. Es una coordenada que solo estrecha: se suma con AND a la regla de visibilidad, así
/// que el buzón personal de otro Gestor CAE o un id que no corresponde a ninguna conexión visible dan la lista vacía, sin error. La
/// pantalla solo comprueba que manda el id; este test fija que el servidor lo aplica y que no amplía nada.
/// </summary>
public class ObtenerConversacionesFiltroBuzonTests
{
    private static readonly DateTime Fecha = new(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Usuario = Guid.NewGuid();

    private sealed record Datos(
        ObtenerConversacionesQueryHandler Handler, Guid BuzonNorte, Guid BuzonSur, Guid BuzonPersonalAjeno, Guid BuzonPersonalPropio,
        Guid HiloNorte, Guid HiloNorteResuelto, Guid HiloSur, Guid HiloSinBuzon, Guid HiloPersonalAjeno, Guid HiloPersonalPropio);

    private static Datos Escenario()
    {
        var comunicaciones = new ComunicacionesQueryContextFalso();
        var integraciones = new IntegracionesQueryContextFalso();

        var norte = new ConexionIntegracion("cae@norte.example.invalid", "CAE Norte");
        var sur = new ConexionIntegracion("cae@sur.example.invalid", "CAE Sur");
        var personalAjeno = new ConexionIntegracion("otro@norte.example.invalid", "Personal de otro", gestorPropietarioId: Guid.NewGuid());
        var personalPropio = new ConexionIntegracion("yo@norte.example.invalid", "Personal propio", gestorPropietarioId: Usuario);
        integraciones.ConexionesLista.AddRange([norte, sur, personalAjeno, personalPropio]);

        var hiloNorte = Hilo(comunicaciones, "Alta de un trabajador", norte.Id);
        var hiloNorteResuelto = Hilo(comunicaciones, "Certificado ya enviado", norte.Id, EstadoConversacion.Resuelta);
        var hiloSur = Hilo(comunicaciones, "Seguro de la contrata", sur.Id);
        var hiloSinBuzon = Hilo(comunicaciones, "Creado a mano", null);
        var hiloPersonalAjeno = Hilo(comunicaciones, "Correo privado de otro", personalAjeno.Id);
        var hiloPersonalPropio = Hilo(comunicaciones, "Correo de mi buzón", personalPropio.Id);

        var handler = new ObtenerConversacionesQueryHandler(
            new EmpresasQueryContextFalso(), comunicaciones, integraciones, new CentrosQueryContextFalso(),
            new GestionesVacias(), new VisitasVacias(), new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Usuario, "GestorCae"),
            new SinTenant());
        return new Datos(handler, norte.Id, sur.Id, personalAjeno.Id, personalPropio.Id,
            hiloNorte, hiloNorteResuelto, hiloSur, hiloSinBuzon, hiloPersonalAjeno, hiloPersonalPropio);
    }

    private static Guid Hilo(ComunicacionesQueryContextFalso comunicaciones, string asunto, Guid? conexionId,
        EstadoConversacion estado = EstadoConversacion.Abierta)
    {
        var conversacion = new Conversacion(asunto);
        if (conexionId is { } conexion) conversacion.AsociarConexion(conexion, $"hilo-{Guid.NewGuid():N}");
        if (estado != EstadoConversacion.Abierta) conversacion.CambiarEstado(estado);
        comunicaciones.ListaConversaciones.Add(conversacion);
        comunicaciones.ListaMensajes.Add(
            new Mensaje(conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "alguien@example.invalid", "<p>Hola</p>", Fecha));
        return conversacion.Id;
    }

    [Fact]
    public async Task Sin_buzon_devuelve_lo_visible_de_todos_los_buzones_y_sin_el_personal_ajeno()
    {
        var d = Escenario();

        var resultado = await d.Handler.Handle(new ObtenerConversacionesQuery(), CancellationToken.None);

        resultado.Elementos.Select(c => c.Id).Should().BeEquivalentTo(
            [d.HiloNorte, d.HiloNorteResuelto, d.HiloSur, d.HiloSinBuzon, d.HiloPersonalPropio],
            "control del instrumento: sin filtro, la visibilidad ya deja fuera el buzón personal de otro Gestor CAE");
    }

    [Fact]
    public async Task Con_un_buzon_devuelve_solo_los_hilos_de_esa_conexion()
    {
        var d = Escenario();

        var resultado = await d.Handler.Handle(new ObtenerConversacionesQuery(ConexionIntegracionId: d.BuzonNorte), CancellationToken.None);

        resultado.TotalElementos.Should().Be(2);
        resultado.Elementos.Select(c => c.Id).Should().BeEquivalentTo([d.HiloNorte, d.HiloNorteResuelto]);
        resultado.Elementos.Should().OnlyContain(c => c.BuzonNombre == "CAE Norte");
    }

    [Fact]
    public async Task El_buzon_se_suma_con_AND_al_grupo_de_estados_y_a_la_busqueda()
    {
        var d = Escenario();

        var abiertasDelNorte = await d.Handler.Handle(
            new ObtenerConversacionesQuery(ConexionIntegracionId: d.BuzonNorte, GrupoEstado: GrupoEstadoConversacion.Abiertas), CancellationToken.None);
        var surConBusquedaDelNorte = await d.Handler.Handle(
            new ObtenerConversacionesQuery(ConexionIntegracionId: d.BuzonSur, Busqueda: "trabajador"), CancellationToken.None);

        abiertasDelNorte.Elementos.Should().ContainSingle().Which.Id.Should().Be(d.HiloNorte);
        surConBusquedaDelNorte.TotalElementos.Should().Be(0, "el buzón no amplía lo que deja pasar otro filtro");
    }

    [Fact]
    public async Task El_buzon_personal_de_otro_Gestor_CAE_da_la_lista_vacia_y_no_error()
    {
        var d = Escenario();

        var resultado = await d.Handler.Handle(
            new ObtenerConversacionesQuery(ConexionIntegracionId: d.BuzonPersonalAjeno), CancellationToken.None);

        resultado.TotalElementos.Should().Be(0, "el filtro nunca abre un hilo que la visibilidad dejó fuera");
        resultado.Elementos.Should().BeEmpty();
    }

    [Fact]
    public async Task El_buzon_personal_propio_si_filtra_sus_hilos()
    {
        var d = Escenario();

        var resultado = await d.Handler.Handle(
            new ObtenerConversacionesQuery(ConexionIntegracionId: d.BuzonPersonalPropio), CancellationToken.None);

        resultado.Elementos.Should().ContainSingle().Which.Id.Should().Be(d.HiloPersonalPropio);
    }

    [Fact]
    public async Task Un_id_que_no_es_de_ninguna_conexion_visible_da_la_lista_vacia_y_no_error()
    {
        var d = Escenario();

        // Un id de una conexión de otro Tenant no llega a este contexto (filtro de Tenant y RLS): para la query es un id sin
        // ninguna conversación visible. La prueba bajo RLS real vive en la suite de integración.
        var resultado = await d.Handler.Handle(new ObtenerConversacionesQuery(ConexionIntegracionId: Guid.NewGuid()), CancellationToken.None);

        resultado.TotalElementos.Should().Be(0);
        resultado.Elementos.Should().BeEmpty();
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
