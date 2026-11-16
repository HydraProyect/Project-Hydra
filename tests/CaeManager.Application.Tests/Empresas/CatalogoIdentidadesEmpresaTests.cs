using CaeManager.Application.Empresas;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// #1991: el catálogo responde lo mismo que las consultas de una en una a las que sustituye en la plantilla maestra
/// (<see cref="IEmpresaRepository.ObtenerDuplicadaPorCifAsync"/> y <see cref="DuplicadosEmpresa.ComprobarRazonSocialAsync"/>),
/// sobre el mismo repositorio falso, que compara como la consulta real: recorte, mayúsculas del CIF, bajas dentro y
/// solo el Tenant propietario (aquí, el vacío por defecto del repositorio falso).
/// </summary>
public class CatalogoIdentidadesEmpresaTests
{
    private static (ClientesFalsos.EmpresaRepositorioFalso Repositorio, Empresa Activa, Empresa Baja) Preparar()
    {
        var repositorio = new ClientesFalsos.EmpresaRepositorioFalso();
        var activa = new Empresa("Industrias Ficticias, S.L.", "B12345674");
        var baja = new Empresa("Distribuciones Ficticias, S.A.", "A58818501");
        baja.MarcarComoEliminado(Guid.NewGuid());
        var deOtroTenant = new Empresa("Otra Ficticia, S.L.", "B00000000");
        typeof(CaeManager.Domain.Common.EntidadConTenant).GetProperty(nameof(Empresa.TenantId))!.SetValue(deOtroTenant, Guid.NewGuid());
        var sinCif = new Empresa("Sin Cif Ficticia, S.L.");
        repositorio.Empresas.AddRange([activa, baja, deOtroTenant, sinCif]);
        return (repositorio, activa, baja);
    }

    public static TheoryData<string> Cifs => new()
    {
        "B12345674", " b12345674 ", "A58818501", "a58818501", "B00000000", "B99999999", "", "   ",
    };

    public static TheoryData<string?> RazonesSociales => new()
    {
        "Industrias Ficticias, S.L.", "  Industrias Ficticias, S.L.  ", "industrias ficticias, s.l.",
        "Distribuciones Ficticias, S.A.", "Otra Ficticia, S.L.", "Sin Cif Ficticia, S.L.", "Nadie, S.L.", "", "  ", null,
    };

    [Theory]
    [MemberData(nameof(Cifs))]
    public async Task El_duplicado_por_CIF_es_el_de_la_consulta_unitaria(string cif)
    {
        var (repositorio, activa, baja) = Preparar();
        var catalogo = await CatalogoIdentidadesEmpresa.CargarAsync(repositorio, default);

        foreach (var excluir in new Guid?[] { null, activa.Id, baja.Id })
        {
            var esperada = await repositorio.ObtenerDuplicadaPorCifAsync(cif, excluir);
            var obtenida = catalogo.DuplicadaPorCif(cif, excluir);

            (obtenida?.Id).Should().Be(esperada?.Id, $"CIF «{cif}», excluyendo {excluir}");
            (obtenida?.EstaEliminado).Should().Be(esperada?.EstaEliminado);
        }
    }

    [Theory]
    [MemberData(nameof(RazonesSociales))]
    public async Task La_razon_social_ocupada_es_la_de_la_comprobacion_unitaria(string? razonSocial)
    {
        var (repositorio, activa, baja) = Preparar();
        var catalogo = await CatalogoIdentidadesEmpresa.CargarAsync(repositorio, default);

        foreach (var excluir in new Guid?[] { null, activa.Id, baja.Id })
        {
            var esperado = await DuplicadosEmpresa.ComprobarRazonSocialAsync(repositorio, razonSocial, excluir);
            var obtenido = catalogo.ComprobarRazonSocial(razonSocial, excluir);

            obtenido.Should().BeEquivalentTo(esperado, $"razón social «{razonSocial}», excluyendo {excluir}");
        }
    }

    [Fact]
    public async Task Los_controles_distinguen_cada_caso()
    {
        // Sin estos controles, las comparaciones de arriba pasarían igual con un catálogo que nunca encontrara nada.
        var (repositorio, activa, _) = Preparar();
        var catalogo = await CatalogoIdentidadesEmpresa.CargarAsync(repositorio, default);

        catalogo.DuplicadaPorCif(" b12345674 ")!.Id.Should().Be(activa.Id);
        catalogo.DuplicadaPorCif("A58818501")!.EstaEliminado.Should().BeTrue();
        catalogo.DuplicadaPorCif("B00000000").Should().BeNull("es de otro Tenant");
        catalogo.DuplicadaPorCif("B12345674", activa.Id).Should().BeNull();
        catalogo.ComprobarRazonSocial("  Industrias Ficticias, S.L.  ").Should().BeEquivalentTo(DuplicadosEmpresa.ErrorRazonSocialDuplicada(dadaDeBaja: false));
        catalogo.ComprobarRazonSocial("Distribuciones Ficticias, S.A.").Should().BeEquivalentTo(DuplicadosEmpresa.ErrorRazonSocialDuplicada(dadaDeBaja: true));
        catalogo.ComprobarRazonSocial("industrias ficticias, s.l.").Should().BeNull("el índice no cambia mayúsculas");
        catalogo.ComprobarRazonSocial("Industrias Ficticias, S.L.", activa.Id).Should().BeNull();
        repositorio.LecturasDeIdentidades.Should().Be(1);
    }
}
