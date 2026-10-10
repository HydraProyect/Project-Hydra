using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// La comprobación que comparten los doce agregados editables. Se prueba una
/// vez aquí en vez de repetir el mismo test doce veces: lo que cada handler
/// añade es solo el nombre que sale en el mensaje.
/// </summary>
public class VerificacionConcurrenciaTests
{
    private static Empresa CrearCliente() =>
        Empresa.CrearComoCliente("Ficha S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);

    [Fact]
    public void Con_la_version_vigente_deja_pasar()
    {
        var cliente = CrearCliente();

        ConcurrenciaOptimista.Verificar(cliente, cliente.Version, "este Cliente").Should().BeNull();
    }

    [Fact]
    public void Con_una_version_distinta_devuelve_conflicto()
    {
        var cliente = CrearCliente();

        var error = ConcurrenciaOptimista.Verificar(cliente, Guid.NewGuid(), "este Cliente");

        error.Should().NotBeNull();
        error!.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        error.Mensaje.Should().Contain("Otra persona modificó este Cliente");
    }

    [Fact]
    public void Guid_vacio_significa_sin_comprobacion()
    {
        // Es lo que permitió migrar agregado a agregado sin romper ninguna
        // edición por el camino. Si esto dejara de ser cierto, cualquier
        // llamador que aún no propague la versión quedaría bloqueado.
        var cliente = CrearCliente();

        ConcurrenciaOptimista.Verificar(cliente, Guid.Empty, "este Cliente").Should().BeNull();
    }

    [Fact]
    public void El_nombre_del_agregado_llega_al_mensaje()
    {
        var cliente = CrearCliente();

        var error = ConcurrenciaOptimista.Verificar(cliente, Guid.NewGuid(), "esta visita");

        error!.Mensaje.Should().Contain("esta visita");
    }
}
