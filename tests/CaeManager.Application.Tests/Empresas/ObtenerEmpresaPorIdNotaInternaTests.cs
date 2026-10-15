using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// El corte de lectura de la «Nota interna» en <see cref="ObtenerEmpresaPorIdQueryHandler"/>: la nota es del equipo
/// del Tenant propietario, y esta consulta la lee también el rol Cliente (portal, por el panel de Empresa). El corte
/// va en Application, no en la página: lo que este handler no entrega no puede pintarlo nadie.
/// </summary>
public class ObtenerEmpresaPorIdNotaInternaTests
{
    private const string Nota = "Pide la llave en portería. Contacto: Leire.";

    private static (ObtenerEmpresaPorIdQueryHandler Handler, Empresa Empresa) Montar(string? rol, string? nota = Nota)
    {
        var empresa = new Empresa("Montajes Skynet S.L.");
        empresa.FijarNotaInterna(nota);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);

        return (new ObtenerEmpresaPorIdQueryHandler(empresas, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol)), empresa);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_recibe_la_nota(string rol)
    {
        var (handler, empresa) = Montar(rol);

        var dto = await handler.Handle(new ObtenerEmpresaPorIdQuery(empresa.Id), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.NotaInternaVisible.Should().BeTrue();
        dto.Notas.Should().Be(Nota);
    }

    [Fact]
    public async Task El_equipo_ve_la_tarjeta_aunque_la_empresa_no_tenga_nota()
    {
        var (handler, empresa) = Montar("GestorCae", nota: null);

        var dto = await handler.Handle(new ObtenerEmpresaPorIdQuery(empresa.Id), CancellationToken.None);

        dto!.NotaInternaVisible.Should().BeTrue();
        dto.Notas.Should().BeNull();
    }

    /// <summary>
    /// El rol Cliente lee la ficha básica de la Empresa y recibe todo lo demás: lo único que no viaja es la nota, y el
    /// indicador le dice a quien pinte que no enseñe la tarjeta. Lista blanca: un rol desconocido tampoco la ve.
    /// </summary>
    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Ni_el_usuario_de_Cliente_ni_un_rol_nulo_o_desconocido_reciben_la_nota(string? rol)
    {
        var (handler, empresa) = Montar(rol);

        var dto = await handler.Handle(new ObtenerEmpresaPorIdQuery(empresa.Id), CancellationToken.None);

        dto.Should().NotBeNull("la ficha sí se lee: el corte es solo de la nota");
        dto!.RazonSocial.Should().Be("Montajes Skynet S.L.");
        dto.NotaInternaVisible.Should().BeFalse();
        dto.Notas.Should().BeNull();
    }
}
