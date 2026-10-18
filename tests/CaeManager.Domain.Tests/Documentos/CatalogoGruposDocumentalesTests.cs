using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Documentos;

public class CatalogoGruposDocumentalesTests
{
    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador, "Formación 20h", "Formación PRL")]
    [InlineData(AmbitoAplicacion.Empresa, "TC2", "Seguridad Social y Hacienda")]
    [InlineData(AmbitoAplicacion.Trabajador, "Documento requerido por el Cliente empresarial", "Información y EPI")]
    [InlineData(AmbitoAplicacion.Empresa, "Documento requerido por el Cliente empresarial", "Coordinación CAE")]
    [InlineData(AmbitoAplicacion.Vehiculo, "ITV", "Vehículo")]
    [InlineData(AmbitoAplicacion.Empresa, "Tipo propio", "Sin grupo")]
    public void Clasifica_nombre_y_alias_dentro_de_su_ambito(AmbitoAplicacion ambito, string nombre, string grupo) =>
        CatalogoGruposDocumentales.Grupo(ambito, nombre).Should().Be(grupo);

    [Fact]
    public void Los_archivados_conservan_su_grupo_y_no_archivan_el_sustituto()
    {
        CatalogoGruposDocumentales.Grupo(AmbitoAplicacion.Trabajador, "Formación EPIs").Should().Be("Formación PRL");
        CatalogoGruposDocumentales.EsArchivado(AmbitoAplicacion.Trabajador, "Formación EPIs").Should().BeTrue();
        CatalogoGruposDocumentales.EsArchivado(AmbitoAplicacion.Empresa, "Protocolo frente al acoso").Should().BeTrue();
        CatalogoGruposDocumentales.EsArchivado(AmbitoAplicacion.Trabajador, "Entrega de EPI").Should().BeFalse();
    }
}
