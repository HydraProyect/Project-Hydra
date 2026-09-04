using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// DEC-53/DEC-62: esta consulta precarga el formulario de edición sin la
/// contraseña — su DTO no tiene esa propiedad, así que la proyección de EF
/// nunca la lee ni el protector la descifra.
/// </summary>
public class ObtenerCredencialAccesoEmpresaSinContrasenaQueryTests
{
    [Fact]
    public async Task Devuelve_los_campos_no_sensibles_de_la_credencial()
    {
        var empresaId = Guid.NewGuid();
        var contexto = new EmpresasQueryContextFalso();
        contexto.ListaCredencialesAccesoEmpresa.Add(new CredencialAccesoEmpresa(
            empresaId, "https://portal.example", "campo", "usuario", "secreta", "notas"));

        var handler = new ObtenerCredencialAccesoEmpresaSinContrasenaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId]));

        var resultado = await handler.Handle(new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(empresaId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.UrlAcceso.Should().Be("https://portal.example");
        resultado.CampoEmpresa.Should().Be("campo");
        resultado.Usuario.Should().Be("usuario");
        resultado.Notas.Should().Be("notas");
    }

    [Fact]
    public async Task Fuera_de_cartera_de_gestion_no_se_lee_ni_siquiera_la_tabla()
    {
        var handler = new ObtenerCredencialAccesoEmpresaSinContrasenaQueryHandler(
            new EmpresasQueryContextQueExplota(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: []));

        var resultado = await handler.Handle(
            new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
    }

    /// <summary>Un usuario de portal está en la cartera de LECTURA de su contratista pero no en la de GESTIÓN.</summary>
    [Fact]
    public async Task Usuario_de_portal_no_precarga_el_formulario_de_edicion_de_su_contratista()
    {
        var empresaId = Guid.NewGuid();
        var handler = new ObtenerCredencialAccesoEmpresaSinContrasenaQueryHandler(
            new EmpresasQueryContextQueExplota(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId], empresaIdsParaGestion: []));

        var resultado = await handler.Handle(new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(empresaId), CancellationToken.None);

        resultado.Should().BeNull();
    }

    private sealed class EmpresasQueryContextQueExplota : IEmpresasQueryContext
    {
        private static IQueryable<T> Explota<T>() =>
            throw new InvalidOperationException(
                "La consulta llegó a la base de datos con el agregado fuera de la cartera del usuario.");

        public IQueryable<Empresa> Empresas => Explota<Empresa>();
        public IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa => Explota<CredencialAccesoEmpresa>();
        public IQueryable<CaeManager.Domain.RelacionesEmpresariales.RelacionEmpresarial> RelacionesEmpresariales =>
            Explota<CaeManager.Domain.RelacionesEmpresariales.RelacionEmpresarial>();
    }
}
