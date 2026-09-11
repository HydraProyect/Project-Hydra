using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrataSinContrasena;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// DEC-53/DEC-62, gemela de <c>ObtenerCredencialAccesoEmpresaSinContrasenaQueryTests</c>:
/// esta consulta precarga el formulario de edición de Subcontrata sin la
/// contraseña — su DTO no tiene esa propiedad, así que la proyección de EF
/// nunca la lee ni el protector la descifra.
/// </summary>
public class ObtenerCredencialAccesoSubcontrataSinContrasenaQueryTests
{
    [Fact]
    public async Task Devuelve_los_campos_no_sensibles_de_la_credencial()
    {
        var subcontrataId = Guid.NewGuid();
        var contexto = new SubcontratasQueryContextFalso();
        contexto.ListaCredencialesAccesoSubcontrata.Add(new CredencialAccesoSubcontrata(
            subcontrataId, "https://portal.example", "campo", "usuario", "secreta", "notas"));

        var handler = new ObtenerCredencialAccesoSubcontrataSinContrasenaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrataId]));

        var resultado = await handler.Handle(new ObtenerCredencialAccesoSubcontrataSinContrasenaQuery(subcontrataId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.UrlAcceso.Should().Be("https://portal.example");
        resultado.CampoEmpresa.Should().Be("campo");
        resultado.Usuario.Should().Be("usuario");
        resultado.Notas.Should().Be("notas");
    }

    [Fact]
    public async Task Fuera_de_cartera_de_gestion_no_se_lee_ni_siquiera_la_tabla()
    {
        var handler = new ObtenerCredencialAccesoSubcontrataSinContrasenaQueryHandler(
            new SubcontratasQueryContextQueExplota(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: []));

        var resultado = await handler.Handle(
            new ObtenerCredencialAccesoSubcontrataSinContrasenaQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
    }

    /// <summary>REC-159: un usuario de portal está en la cartera de LECTURA de su subcontrata pero no en la de GESTIÓN.</summary>
    [Fact]
    public async Task Usuario_de_portal_no_precarga_el_formulario_de_edicion_de_su_subcontrata()
    {
        var subcontrataId = Guid.NewGuid();
        var handler = new ObtenerCredencialAccesoSubcontrataSinContrasenaQueryHandler(
            new SubcontratasQueryContextQueExplota(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrataId], subcontrataIdsParaGestion: []));

        var resultado = await handler.Handle(new ObtenerCredencialAccesoSubcontrataSinContrasenaQuery(subcontrataId), CancellationToken.None);

        resultado.Should().BeNull();
    }

    private sealed class SubcontratasQueryContextQueExplota : ISubcontratasQueryContext
    {
        private static IQueryable<T> Explota<T>() =>
            throw new InvalidOperationException(
                "La consulta llegó a la base de datos con el agregado fuera de la cartera del usuario.");

        public IQueryable<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata => Explota<CredencialAccesoSubcontrata>();
        public IQueryable<VerificacionExternaSubcontrata> VerificacionesExternaSubcontrata => Explota<VerificacionExternaSubcontrata>();
    }
}
