using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.DesactivarGestorCaeConCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// FS-25, revisión Codex de la PR #931 (hallazgos 3 y 4): pasar la cartera entera y
/// desactivar van en una sola transacción, y la cartera se vuelve a leer al confirmar.
/// La transacción real se prueba contra PostgreSQL en integración; aquí, que el handler
/// no confirma nada salvo cuando todo salió bien.
/// </summary>
public class DesactivarGestorCaeConCarteraCommandHandlerTests
{
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Gestor = Guid.NewGuid();
    private static readonly Guid Destino = Guid.NewGuid();

    private readonly Empresa _uno = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, Gestor);
    private readonly Empresa _dos = Empresa.CrearComoCliente("Montajes del Norte", "A58818501", false, null, Gestor);
    private readonly EmpresaRepositorioFalso _empresas = new();
    private readonly CuentasFalsas _cuentas = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly TransaccionFalsa _transaccion = new();
    private readonly AsignacionesOperativasWriterFalso _writer = new();
    private DirectorioDestinosCarteraFalso _directorio = new(new DestinoCartera(true, "GestorCae", null, false));

    public DesactivarGestorCaeConCarteraCommandHandlerTests()
    {
        _empresas.Agregar(_uno);
        _empresas.Agregar(_dos);
        _cuentas.Cuentas[Gestor] = new CuentaUsuario(Gestor, "g@x.test", "Gestor", true, ["GestorCae"], false, true, false);
    }

    private DesactivarGestorCaeConCarteraCommandHandler Handler(string rol = "Administrador")
    {
        var usuario = new CurrentUserServiceFalso(Actor, rol);
        return new(_cuentas, usuario, _directorio,
            ReasignarEjecutivoClienteCommandHandlerTests.Reasignador(usuario, _empresas, directorio: _directorio, writer: _writer),
            _unitOfWork, _transaccion);
    }

    private DesactivarGestorCaeConCarteraCommand Comando(params Guid[] confirmados) =>
        new(Gestor, Destino, confirmados.Length == 0 ? [_uno.Id, _dos.Id] : confirmados);

    private void CarteraLeida(params IReadOnlyList<Guid>[] lecturas)
    {
        foreach (var lectura in lecturas) _directorio.Carteras.Enqueue(new CarteraVigente(false, lectura));
    }

    private void AfirmarQueNoSeConfirmoNada()
    {
        _transaccion.Confirmadas.Should().Be(0);
        _cuentas.Desactivadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Pasa_la_cartera_entera_y_desactiva_dentro_de_una_transaccion_confirmada()
    {
        CarteraLeida([_uno.Id, _dos.Id], []);

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        _uno.EjecutivoUsuarioId.Should().Be(Destino);
        _dos.EjecutivoUsuarioId.Should().Be(Destino);
        _writer.CarterasReasignadas.Should().BeEquivalentTo([(_uno.Id, (Guid?)Destino), (_dos.Id, (Guid?)Destino)]);
        _cuentas.Desactivadas.Should().Equal(Gestor);
        _unitOfWork.VecesGuardado.Should().Be(1);
        _transaccion.Confirmadas.Should().Be(1);
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Solo_quien_gestiona_cuentas_puede_hacerlo(string rol)
    {
        var resultado = await Handler(rol).Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        _transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task No_pasa_la_cartera_al_mismo_Gestor_CAE()
    {
        var resultado = await Handler().Handle(new DesactivarGestorCaeConCarteraCommand(Gestor, Gestor, [_uno.Id]), CancellationToken.None);

        resultado.Error.Should().Be(DesactivarGestorCaeConCarteraCommandHandler.DestinoEsElMismo);
        _transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task Una_cuenta_de_otra_organizacion_no_se_toca()
    {
        _cuentas.Cuentas[Gestor] = _cuentas.Cuentas[Gestor] with { EsPropiaDelTenantActual = false };

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(AutoridadSobreCuentas.NoEncontrado);
        _transaccion.Ejecutadas.Should().Be(0);
    }

    /// <summary>Hallazgo 3: un Cliente empresarial asignado con el diálogo abierto.</summary>
    [Fact]
    public async Task Si_la_cartera_cambio_desde_que_se_confirmo_no_pasa_nada_ni_desactiva()
    {
        var tercero = Guid.NewGuid();
        CarteraLeida([_uno.Id, _dos.Id, tercero]);

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(DesactivarGestorCaeConCarteraCommandHandler.CarteraCambiada);
        _writer.CarterasReasignadas.Should().BeEmpty();
        _uno.EjecutivoUsuarioId.Should().Be(Gestor);
        AfirmarQueNoSeConfirmoNada();
    }

    /// <summary>Hallazgo 4: un Cliente empresarial que llega mientras corre la transacción la deshace entera.</summary>
    [Fact]
    public async Task Si_llega_un_Cliente_empresarial_durante_la_transaccion_se_deshace_entera()
    {
        CarteraLeida([_uno.Id, _dos.Id], [Guid.NewGuid()]);

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(DesactivarGestorCaeConCarteraCommandHandler.CarteraCambiada);
        _transaccion.Deshechas.Should().Be(1);
        _transaccion.Confirmadas.Should().Be(0);
    }

    /// <summary>Revisión puente: una Asignación de Cartera universal que aparece con el diálogo abierto.</summary>
    [Fact]
    public async Task Si_aparecio_una_cartera_universal_no_pasa_nada_ni_desactiva()
    {
        _directorio.Carteras.Enqueue(new CarteraVigente(true, [_uno.Id, _dos.Id]));

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(DesactivarGestorCaeConCarteraCommandHandler.CarteraCambiada);
        _writer.CarterasReasignadas.Should().BeEmpty();
        AfirmarQueNoSeConfirmoNada();
    }

    /// <summary>
    /// Revisión puente: la proyección Empresa ya apunta al destino pero la Asignación de
    /// Cartera sigue siendo de quien se desactiva. Se alinea la cartera sin repetir avisos.
    /// </summary>
    [Fact]
    public async Task Si_la_proyeccion_ya_apunta_al_destino_alinea_la_cartera_igualmente()
    {
        _dos.AsignarEjecutivo(Destino);
        CarteraLeida([_uno.Id, _dos.Id], []);

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        _writer.CarterasReasignadas.Should().BeEquivalentTo([(_uno.Id, (Guid?)Destino), (_dos.Id, (Guid?)Destino)]);
        _transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Un_destino_invalido_no_pasa_nada_ni_desactiva()
    {
        _directorio = new DirectorioDestinosCarteraFalso(new DestinoCartera(false, "GestorCae", null, false));
        CarteraLeida([_uno.Id, _dos.Id]);

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(ReglaDestinoCarteraCliente.Inactivo);
        _writer.CarterasReasignadas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
        AfirmarQueNoSeConfirmoNada();
    }

    [Fact]
    public async Task Si_la_desactivacion_falla_la_cartera_pasada_se_deshace()
    {
        CarteraLeida([_uno.Id, _dos.Id], []);
        _cuentas.FalloAlDesactivar = Error.Crear("Usuarios.FalloAlCambiarActivacion", "Identity dijo que no.");

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Usuarios.FalloAlCambiarActivacion");
        _unitOfWork.VecesGuardado.Should().Be(1, "la cartera llegó a guardarse dentro de la transacción");
        _transaccion.Deshechas.Should().Be(1);
        _transaccion.Confirmadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_conflicto_al_guardar_se_traduce_y_no_confirma()
    {
        CarteraLeida([_uno.Id, _dos.Id], []);
        _unitOfWork.ExcepcionAlGuardar = new DbUpdateException("índice único de responsable vigente");

        var resultado = await Handler().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(DesactivarGestorCaeConCarteraCommandHandler.TraspasoNoGuardado,
            "un fallo de guardado no se presenta como una carrera con otra persona");
        AfirmarQueNoSeConfirmoNada();
    }

    /// <summary>
    /// Doble de <see cref="ITransaccionDeComando"/>: ejecuta la operación y anota si se
    /// habría confirmado o deshecho. Que el rollback deshaga de verdad lo prueba la
    /// integración contra PostgreSQL.
    /// </summary>
    private sealed class TransaccionFalsa : ITransaccionDeComando
    {
        public int Ejecutadas { get; private set; }
        public int Confirmadas { get; private set; }
        public int Deshechas { get; private set; }

        public async Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
        {
            Ejecutadas++;
            try
            {
                var resultado = await operacion(cancellationToken);
                if (resultado.EsFallido) Deshechas++;
                else Confirmadas++;
                return resultado;
            }
            catch
            {
                Deshechas++;
                throw;
            }
        }
    }

    private sealed class CuentasFalsas : IGestionCuentasUsuario
    {
        public Dictionary<Guid, CuentaUsuario> Cuentas { get; } = [];
        public List<Guid> Desactivadas { get; } = [];
        public Error? FalloAlDesactivar { get; set; }

        public Task<CuentaUsuario?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Cuentas.GetValueOrDefault(usuarioId));

        public Task<Result> CambiarActivacionAsync(Guid usuarioId, bool activar, CancellationToken cancellationToken = default)
        {
            if (FalloAlDesactivar is { } error) return Task.FromResult(Result.Fallo(error));
            if (!activar) Desactivadas.Add(usuarioId);
            return Task.FromResult(Result.Exito());
        }

        public Task<bool> EsPropiaDelTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TieneVinculoOperativoAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<Guid>> CrearAsync(NuevaCuentaUsuario cuenta, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> AsignarRolAsync(Guid usuarioId, string rol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ActualizarDatosAsync(Guid usuarioId, DatosCuentaUsuario datos, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResultadoCambioRol> CambiarRolAsync(Guid usuarioId, string rolNuevo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> EliminarAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<string>> GenerarTokenActivacionAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
