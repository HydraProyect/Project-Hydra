using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class TipoDocumentoAliasTests
{
    [Fact]
    public void Crea_un_alias_con_el_texto_normalizado()
    {
        var alias = new TipoDocumentoAlias(Guid.NewGuid(), "  TC2  ");

        alias.Texto.Should().Be("TC2");
    }

    [Fact]
    public void No_permite_texto_vacio()
    {
        var accion = () => new TipoDocumentoAlias(Guid.NewGuid(), "   ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_superar_la_longitud_maxima()
    {
        var textoDemasiadoLargo = new string('a', TipoDocumentoAlias.LongitudMaximaTexto + 1);

        var accion = () => new TipoDocumentoAlias(Guid.NewGuid(), textoDemasiadoLargo);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_un_tipoDocumentoId_vacio()
    {
        var accion = () => new TipoDocumentoAlias(Guid.Empty, "TC2");

        accion.Should().Throw<ArgumentException>();
    }
}
