using CaeManager.Application.Centros;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// La pareja Cliente empresarial–Centro que comparten el recorte de la vista previa al ámbito y el analizador de la
/// plantilla combinada (#2117): empareja con el Cliente empresarial del Centro, nunca con su Empresa titular.
/// </summary>
public class ClienteEmpresarialDelCentroTests
{
    private static Empresa Cliente(string razonSocial, string cif) =>
        Empresa.CrearComoCliente(razonSocial, cif, false, notas: null, ejecutivoUsuarioId: null);

    [Fact]
    public void Empareja_el_Centro_con_su_Cliente_empresarial_y_no_con_su_Empresa_titular()
    {
        var clienteEmpresarial = Cliente("Cliente Empresarial S.A.", "A12345674");
        var titular = Cliente("Empresa Titular S.L.", "B76543214");
        var centro = new Centro(clienteEmpresarial.Id, titular.Id, "Planta Norte");

        var parejas = new[] { centro }.AsQueryable()
            .ConSuClienteEmpresarial(new[] { clienteEmpresarial, titular }.AsQueryable())
            .ToList();

        parejas.Should().ContainSingle().Which.Should().Be(new CentroConClienteEmpresarial(
            centro.Id, "Planta Norte", clienteEmpresarial.Id, "Cliente Empresarial S.A."));
    }

    [Fact]
    public void Un_Centro_cuyo_Cliente_empresarial_no_esta_en_el_contexto_queda_fuera()
    {
        var titular = Cliente("Empresa Titular S.L.", "B76543214");
        var centro = new Centro(Guid.NewGuid(), titular.Id, "Planta Huérfana");

        var parejas = new[] { centro }.AsQueryable()
            .ConSuClienteEmpresarial(new[] { titular }.AsQueryable())
            .ToList();

        parejas.Should().BeEmpty("sin su Cliente empresarial no hay pareja que dar por existente, aunque la titular exista");
    }
}
