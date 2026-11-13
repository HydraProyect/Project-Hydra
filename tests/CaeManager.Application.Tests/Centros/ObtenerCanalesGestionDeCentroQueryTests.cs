using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Integraciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// La tarjeta «Canales de gestión» de Centro 360, la pestaña «Plataforma» del Context Workspace y el bloque «Plataforma CAE» de
/// la vista rápida de una Gestión leen esta consulta. Fija qué campos viajan según el alcance: quien ve el Centro recibe sus
/// canales, pero la dirección del portal y las notas solo llegan a quien lo gestiona, con el mismo corte que
/// <c>ObtenerAcreditacionesDelDocumentoQuery</c>. El rol Cliente recibe de <c>ObtenerCentroIdsParaGestionAsync</c> la lista
/// vacía, que es lo que aquí simula <c>centroIdsParaGestion: []</c>; que esa lista salga del rol efectivo lo fija
/// <c>AlcanceDatosService</c>, no este handler.
/// </summary>
public class ObtenerCanalesGestionDeCentroQueryTests
{
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly ProveedoresPlataformaCaeQueryContextFalso _proveedores = new();

    private (Centro Centro, CanalGestionDocumental Plataforma, CanalGestionDocumental Correo) CentroConCanales()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro Norte");
        _centros.ListaCentros.Add(centro);
        var nalanda = new ProveedorPlataformaCae("nalanda", "Nalanda");
        _proveedores.ListaProveedores.Add(nalanda);
        var plataforma = CanalGestionDocumental.DePlataforma(centro.Id, "Documentación", nalanda.Id,
            "https://nalanda.example/acceso", "usuario", "secreto", "Subir los lunes; contacto interno en la planta");
        var correo = CanalGestionDocumental.PorEmail(centro.Id, "Avisos de visita", "cae@example.com", "Marta",
            "Avisar con 48 h");
        _centros.ListaCanalesGestionDocumental.Add(plataforma);
        _centros.ListaCanalesGestionDocumental.Add(correo);
        return (centro, plataforma, correo);
    }

    private Task<IReadOnlyList<CanalGestionResumenDto>> LeerAsync(Guid centroId, AlcanceDatosServiceFalso alcance) =>
        new ObtenerCanalesGestionDeCentroQueryHandler(_centros, _proveedores, alcance)
            .Handle(new ObtenerCanalesGestionDeCentroQuery(centroId), default);

    [Fact]
    public async Task El_rol_Cliente_ve_los_canales_de_su_Centro_sin_direccion_del_portal_ni_notas()
    {
        var (centro, plataforma, correo) = CentroConCanales();
        var usuarioDeCliente = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            centroIdsVisibles: [centro.Id], centroIdsParaGestion: []);

        var canales = await LeerAsync(centro.Id, usuarioDeCliente);

        canales.Select(c => c.Id).Should().BeEquivalentTo([plataforma.Id, correo.Id], "ve el Centro, así que ve sus canales");
        canales.Should().AllSatisfy(c =>
        {
            c.UrlAcceso.Should().BeNull("la dirección del portal solo llega a quien gestiona el Centro");
            c.Notas.Should().BeNull("las notas del canal solo llegan a quien gestiona el Centro");
        });
        canales.Single(c => c.Id == plataforma.Id).Should().BeEquivalentTo(new
        {
            Tipo = TipoCanalGestion.Plataforma,
            EtiquetaProposito = "Documentación",
            ProveedorPlataformaCaeNombre = "Nalanda"
        }, "el resto del canal sigue viajando: solo se cortan la dirección y las notas");
    }

    [Fact]
    public async Task Un_Gestor_CAE_que_gestiona_el_Centro_recibe_la_direccion_del_portal_y_las_notas()
    {
        var (centro, plataforma, correo) = CentroConCanales();
        var gestorCae = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            centroIdsVisibles: [centro.Id], centroIdsParaGestion: [centro.Id]);

        var canales = await LeerAsync(centro.Id, gestorCae);

        canales.Single(c => c.Id == plataforma.Id).Should().BeEquivalentTo(new
        {
            UrlAcceso = "https://nalanda.example/acceso",
            Notas = "Subir los lunes; contacto interno en la planta",
            TieneCredenciales = true
        });
        canales.Single(c => c.Id == correo.Id).Notas.Should().Be("Avisar con 48 h");
    }

    [Fact]
    public async Task La_gestion_de_otro_Centro_no_abre_la_direccion_ni_las_notas_de_este()
    {
        var (centro, plataforma, _) = CentroConCanales();
        var gestorDeOtroCentro = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            centroIdsVisibles: [centro.Id], centroIdsParaGestion: [Guid.NewGuid()]);

        var canales = await LeerAsync(centro.Id, gestorDeOtroCentro);

        canales.Single(c => c.Id == plataforma.Id).Should().BeEquivalentTo(new { UrlAcceso = (string?)null, Notas = (string?)null });
    }

    [Fact]
    public async Task Quien_no_ve_el_Centro_no_recibe_ningun_canal()
    {
        var (centro, _, _) = CentroConCanales();
        var sinVisibilidad = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            centroIdsVisibles: [], centroIdsParaGestion: [centro.Id]);

        (await LeerAsync(centro.Id, sinVisibilidad)).Should().BeEmpty();
    }
}
