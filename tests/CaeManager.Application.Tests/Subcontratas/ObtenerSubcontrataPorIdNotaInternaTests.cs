using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// El corte de lectura de la «Nota interna» en <see cref="ObtenerSubcontrataPorIdQueryHandler"/>: la nota es del
/// equipo del Tenant propietario, y la ficha de la subcontrata la lee también el usuario de un Cliente. El corte va en
/// Application, no en la página: lo que este handler no entrega no puede pintarlo nadie.
/// </summary>
public class ObtenerSubcontrataPorIdNotaInternaTests
{
    private const string Nota = "Avisar a Leire antes de pedir documentación.";

    private static (ObtenerSubcontrataPorIdQueryHandler Handler, Empresa Subcontrata) Montar(
        string? rol, string? nota = Nota, bool soloEnCarteraDeLectura = false)
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", "B12345674", NivelServicioSubcontrata.Gestionada.ToString());
        subcontrata.FijarNotaInterna(nota);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(subcontrata);

        var alcance = soloEnCarteraDeLectura
            ? new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id], subcontrataIdsParaGestion: [])
            : new AlcanceDatosServiceFalso();

        return (new ObtenerSubcontrataPorIdQueryHandler(empresas, alcance, new CurrentUserServiceFalso(Guid.NewGuid(), rol)), subcontrata);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_recibe_la_nota(string rol)
    {
        var (handler, subcontrata) = Montar(rol);

        var dto = await handler.Handle(new ObtenerSubcontrataPorIdQuery(subcontrata.Id), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.NotaInternaVisible.Should().BeTrue();
        dto.Notas.Should().Be(Nota);
    }

    [Fact]
    public async Task El_equipo_ve_la_tarjeta_aunque_la_subcontrata_no_tenga_nota()
    {
        var (handler, subcontrata) = Montar("GestorCae", nota: null);

        var dto = await handler.Handle(new ObtenerSubcontrataPorIdQuery(subcontrata.Id), CancellationToken.None);

        dto!.NotaInternaVisible.Should().BeTrue();
        dto.Notas.Should().BeNull();
    }

    /// <summary>
    /// El usuario de un Cliente lee la ficha (la subcontrata está en su cartera de lectura) y recibe todo lo demás:
    /// lo único que no viaja es la nota, y el indicador le dice a la página que no pinte la tarjeta.
    /// </summary>
    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Ni_el_usuario_de_Cliente_ni_un_rol_nulo_o_desconocido_reciben_la_nota(string? rol)
    {
        var (handler, subcontrata) = Montar(rol, soloEnCarteraDeLectura: true);

        var dto = await handler.Handle(new ObtenerSubcontrataPorIdQuery(subcontrata.Id), CancellationToken.None);

        dto.Should().NotBeNull("la ficha sí se lee: el corte es solo de la nota");
        dto!.RazonSocial.Should().Be("Andamios del Sur S.L.");
        dto.NotaInternaVisible.Should().BeFalse();
        dto.Notas.Should().BeNull();
    }

    /// <summary>
    /// Quien puede guardar la nota tiene que haberla visto: si un rol con escritura quedara fuera de la lista de
    /// lectura, el editor se abriría vacío y «Guardar» borraría una nota que nadie le enseñó. Los cuatro literales
    /// son los de <c>AutorizacionEscrituraBehavior.RolesConEscritura</c>.
    /// </summary>
    [Fact]
    public void Todo_rol_con_escritura_esta_entre_los_que_ven_la_nota_y_el_usuario_de_Cliente_no()
    {
        ObtenerSubcontrataPorIdQueryHandler.RolesQueVenLaNotaInterna
            .Should().Contain(["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"])
            .And.NotContain("Cliente");
    }
}
