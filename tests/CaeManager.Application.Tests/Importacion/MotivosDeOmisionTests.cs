using CaeManager.Application.Importacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>Los textos compartidos con los analizadores son los que el recorte busca: un retoque aquí reabriría el oráculo de existencia.</summary>
public class MotivosDeOmisionTests
{
    [Fact]
    public void Los_motivos_posteriores_a_la_existencia_siguen_siendo_los_que_reconoce_el_recorte()
    {
        MotivosDeOmision.TipoDocumentoNoExiste.Should().Be("No existe este tipo de documento en el catálogo del sistema.");
        MotivosDeOmision.PrefijoFechaEmisionFutura.Should().Be("La fecha de emisión (");
        MotivosDeOmision.FilaDuplicadaEnElArchivo.Should().Be("Fila duplicada dentro del propio archivo.");
        MotivosDeOmision.CentroNoExiste.Should().StartWith(MotivosDeOmision.PrefijoCentroNoExiste);
    }
}
