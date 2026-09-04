using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.DocumentosIa;

public class ExtraccionIaCacheDocumentoTests
{
    [Fact]
    public void Crea_un_vinculo_valido()
    {
        var cacheId = Guid.NewGuid();
        var documentoId = Guid.NewGuid();

        var vinculo = ExtraccionIaCacheDocumento.Crear(cacheId, documentoId);

        vinculo.ExtraccionIaCacheId.Should().Be(cacheId);
        vinculo.DocumentoId.Should().Be(documentoId);
    }

    [Fact]
    public void Rechaza_un_vinculo_sin_entrada_de_cache()
    {
        var accion = () => ExtraccionIaCacheDocumento.Crear(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_vinculo_sin_documento()
    {
        var accion = () => ExtraccionIaCacheDocumento.Crear(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }
}
