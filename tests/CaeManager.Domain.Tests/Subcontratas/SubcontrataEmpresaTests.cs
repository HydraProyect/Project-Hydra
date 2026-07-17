using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Subcontratas;

public class SubcontrataEmpresaTests
{
    [Fact]
    public void Crea_una_asociacion_valida()
    {
        var subcontrataId = Guid.NewGuid();
        var empresaId = Guid.NewGuid();

        var asociacion = new SubcontrataEmpresa(subcontrataId, empresaId);

        asociacion.SubcontrataId.Should().Be(subcontrataId);
        asociacion.EmpresaId.Should().Be(empresaId);
    }

    [Fact]
    public void Requiere_una_subcontrata_valida()
    {
        var accion = () => new SubcontrataEmpresa(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requiere_una_empresa_valida()
    {
        var accion = () => new SubcontrataEmpresa(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }
}
