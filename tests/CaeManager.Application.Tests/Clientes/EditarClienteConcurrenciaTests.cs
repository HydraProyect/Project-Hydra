using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Domain.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

/// <summary>
/// El escenario real de la edición concurrente: dos personas abren la misma
/// ficha, una guarda y la otra guarda después con lo que tenía en pantalla.
///
/// Es un test de Application y no de base de datos a propósito: aquí es donde
/// se detecta. El token de EF por sí solo no ve este caso, porque el handler
/// recarga la entidad justo antes de escribir y compara la versión consigo
/// misma — se descubrió probándolo en navegador con dos pestañas, donde el
/// segundo guardado respondía "correctamente" y pisaba al primero.
/// </summary>
public class EditarClienteConcurrenciaTests
{
    [Fact]
    public async Task Guardar_con_una_version_vieja_no_pisa_al_que_guardo_antes()
    {
        var cliente = new Cliente("Original S.L.", "B12345674", esCritico: false);
        var repositorio = new ClienteRepositorioFalso();
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();

        // La versión que la segunda persona tenía en pantalla: cualquiera que
        // no sea la vigente sirve para expresarlo, porque la primera persona
        // ya guardó y el interceptor renovó Version en la fila real.
        var versionQueViola = Guid.NewGuid();
        versionQueViola.Should().NotBe(cliente.Version);

        var handler = new EditarClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new EditarClienteCommand(cliente.Id, "Pisada S.L.", "B12345674", true, null, versionQueViola),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Concurrencia.Conflicto");
        cliente.RazonSocial.Should().Be("Original S.L.", "el cambio del segundo no debe aplicarse");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_la_version_vigente_la_edicion_se_aplica()
    {
        var cliente = new Cliente("Original S.L.", "B12345674", esCritico: false);
        var repositorio = new ClienteRepositorioFalso();
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new EditarClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new EditarClienteCommand(cliente.Id, "Editada S.L.", "B12345674", true, null, cliente.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.RazonSocial.Should().Be("Editada S.L.");
    }

    [Fact]
    public async Task Un_llamador_que_no_propaga_version_sigue_funcionando()
    {
        // Guid.Empty = "sin comprobación". Los demás agregados todavía no
        // propagan su versión, y no pueden quedarse sin poder editar por eso.
        var cliente = new Cliente("Original S.L.", "B12345674", esCritico: false);
        var repositorio = new ClienteRepositorioFalso();
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new EditarClienteCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new EditarClienteCommand(cliente.Id, "Editada Sin Version S.L.", "B12345674", false, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }
}
