using CaeManager.Application.Usuarios.Queries.ObtenerNombreCuenta;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// El handler solo delega en el lector. Que la lectura respete la RLS de la cuenta bajo el
/// rol de ejecución lo prueba <c>AvatarDeCuentasBajoRuntimeTests</c>.
/// </summary>
public class ObtenerNombreCuentaQueryHandlerTests
{
    [Fact]
    public async Task Pregunta_por_la_cuenta_pedida_y_devuelve_su_nombre()
    {
        var usuarioId = Guid.NewGuid();
        var lector = new LectorFalso(new NombreCuentaDto("Ana Ruiz", "ana@ejemplo.test"));

        var nombre = await new ObtenerNombreCuentaQueryHandler(lector)
            .Handle(new ObtenerNombreCuentaQuery(usuarioId), CancellationToken.None);

        lector.Preguntado.Should().Be(usuarioId);
        nombre.Should().Be(new NombreCuentaDto("Ana Ruiz", "ana@ejemplo.test"));
    }

    [Fact]
    public async Task Una_cuenta_que_no_se_ve_da_null()
    {
        var nombre = await new ObtenerNombreCuentaQueryHandler(new LectorFalso(null))
            .Handle(new ObtenerNombreCuentaQuery(Guid.NewGuid()), CancellationToken.None);

        nombre.Should().BeNull();
    }

    private sealed class LectorFalso(NombreCuentaDto? resultado) : ILectorNombreCuenta
    {
        public Guid? Preguntado { get; private set; }

        public Task<NombreCuentaDto?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken)
        {
            Preguntado = usuarioId;
            return Task.FromResult(resultado);
        }
    }
}
