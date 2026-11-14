using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Application.Reclamaciones.Queries.ObtenerUltimaReclamacionCliente;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Los dos rastros de reclamaciones que no son el historial (decisión 2026-11-14): «Reclamado y sin respuesta» de Inicio
/// (<see cref="ObtenerReclamacionesSinRespuestaQuery"/>) y «Última reclamación al Cliente» del Context Workspace del Centro
/// (<see cref="ObtenerUltimaReclamacionClienteQuery"/>). Los leen los mismos roles que el historial de reclamaciones
/// enviadas (<c>SituacionDocumentosEnCentrosService.RolesQueLeenReclamaciones</c>); Consulta y el Usuario de Cliente
/// reciben vacío aunque la reclamación exista y su Cliente empresarial sea visible.
/// </summary>
public class RastrosDeReclamacionesPorRolTests
{
    private readonly ReclamacionesQueryContextFalso _reclamaciones = new();
    private readonly ComunicacionesQueryContextFalso _comunicaciones = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly Empresa _cliente = Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);

    public RastrosDeReclamacionesPorRolTests()
    {
        _empresas.ListaEmpresas.Add(_cliente);
        // Diez días sin entrante en su hilo: cuenta como «sin respuesta» (corte de siete días).
        _reclamaciones.Lista.Add(ReclamacionDocumental.ParaCliente(
            _cliente.Id, Guid.NewGuid(), "portal@cliente.example", DateTime.UtcNow.AddDays(-10),
            [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid()));
    }

    private DestinoDeReclamacionesEnviadasService Destinos() => new(
        _reclamaciones, new DocumentosQueryContextFalso(), new TrabajadoresQueryContextFalso(), _empresas,
        new AsignacionesQueryContextFalso(), new CentrosQueryContextFalso());

    private Task<IReadOnlyList<ReclamacionSinRespuestaDto>> SinRespuestaComo(string? rol) =>
        new ObtenerReclamacionesSinRespuestaQueryHandler(
                _reclamaciones, _comunicaciones, _empresas, new AlcanceDatosServiceFalso(), Destinos(),
                new CurrentUserServiceFalso(Guid.NewGuid(), rol))
            .Handle(new ObtenerReclamacionesSinRespuestaQuery(), CancellationToken.None);

    private Task<UltimaReclamacionClienteDto?> UltimaComo(string? rol) =>
        new ObtenerUltimaReclamacionClienteQueryHandler(
                _reclamaciones, new AlcanceDatosServiceFalso(), Destinos(), new CurrentUserServiceFalso(Guid.NewGuid(), rol))
            .Handle(new ObtenerUltimaReclamacionClienteQuery(_cliente.Id), CancellationToken.None);

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Los_roles_que_leen_reclamaciones_ven_lo_reclamado_y_sin_respuesta(string rol)
    {
        var filas = await SinRespuestaComo(rol);

        filas.Should().ContainSingle().Which.TitularId.Should().Be(_cliente.Id);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Consulta_y_el_Usuario_de_Cliente_no_reciben_lo_reclamado_y_sin_respuesta_ni_se_consulta(string? rol)
    {
        var filas = await SinRespuestaComo(rol);

        filas.Should().BeEmpty();
        _comunicaciones.ConsultasFueraDeInstantanea.Should().Be(0, "fuera de la lista no se llega a mirar ningún hilo");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Los_roles_que_leen_reclamaciones_ven_la_ultima_reclamacion_al_Cliente(string rol)
    {
        var ultima = await UltimaComo(rol);

        ultima.Should().NotBeNull();
        ultima!.Id.Should().Be(_reclamaciones.Lista[0].Id);
        ultima.TotalDocumentos.Should().Be(2);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Consulta_y_el_Usuario_de_Cliente_no_reciben_la_ultima_reclamacion_al_Cliente(string? rol)
    {
        (await UltimaComo(rol)).Should().BeNull("el Cliente empresarial es visible, pero la reclamación es historial");
    }
}
