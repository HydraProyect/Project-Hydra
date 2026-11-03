using CaeManager.Application.Common;
using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using FluentAssertions;
using FluentValidation;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// Aunque este comando "solo" escribe una fila de historial, es alcanzable por
/// MediatR con cualquier rol de escritura — sin este guard, un rol distinto de
/// Administrador podría inyectar un registro de historial fabricado sin haber
/// ejecutado ninguna importación real. Mismo límite que las 4 páginas de
/// importación (Administrador).
///
/// Usa la implementación REAL de <see cref="IAutorizacionEscrituraEfectiva"/>,
/// no un doble, con <see cref="SesionPrivilegiadaAusente"/> (siempre sin
/// sesión): así estos tests también cubren la rama de rol de ese servicio, y
/// la rama de Aprovisionamiento la cubre su propio fichero de tests.
/// </summary>
public class RegistrarHistorialImportacionCommandAutorizacionTests
{
    private static RegistrarHistorialImportacionCommandHandler Handler(
        string? rol, HistorialImportacionRepositorioFalso repositorio, ClientesFalsos.UnitOfWorkFalso unitOfWork)
    {
        var currentUser = new CurrentUserServiceFalso(Guid.NewGuid(), rol);
        var autorizacion = new AutorizacionEscrituraEfectiva(
            currentUser, new SesionPrivilegiadaAusente(), new TenantActualFalso());
        return new(repositorio, unitOfWork, currentUser, autorizacion);
    }

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private static RegistrarHistorialImportacionCommand Comando() =>
        new("clientes", "archivo.xlsx", Exitosa: true, TotalCreados: 10, TotalAdvertencias: 0, TotalOmitidos: 0, MensajeError: null);

    public static TheoryData<string?> NombresInvalidos => new()
    {
        null, "", "   ", "\t\r\n", new string('a', 261)
    };

    [Theory]
    [MemberData(nameof(NombresInvalidos))]
    public async Task El_servidor_rechaza_el_nombre_invalido_sin_registrar_historial(string? nombre)
    {
        var repositorio = new HistorialImportacionRepositorioFalso();
        var unitOfWork = new ClientesFalsos.UnitOfWorkFalso();
        var handler = Handler("Administrador", repositorio, unitOfWork);
        var comando = Comando() with { NombreArchivo = nombre! };
        var comportamiento = new ValidationBehavior<RegistrarHistorialImportacionCommand, Result>(
            [new RegistrarHistorialImportacionCommandValidator()]);
        var ejecuciones = 0;

        var accion = async () => await comportamiento.Handle(comando, ct =>
        {
            ejecuciones++;
            return handler.Handle(comando, ct);
        }, default);

        var rechazo = (await accion.Should().ThrowAsync<ValidationException>()).Which;
        var mensaje = string.IsNullOrWhiteSpace(nombre)
            ? "El nombre del archivo es obligatorio."
            : "El nombre del archivo no puede superar los 260 caracteres.";
        rechazo.Errors.Should().ContainSingle(e => e.PropertyName == "NombreArchivo" && e.ErrorMessage == mensaje);
        ejecuciones.Should().Be(0);
        repositorio.Registros.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(260)]
    public async Task Acepta_un_nombre_hasta_el_limite_de_la_columna(int longitud)
    {
        var comando = Comando() with { NombreArchivo = new string('a', longitud) };

        (await new RegistrarHistorialImportacionCommandValidator().ValidateAsync(comando))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Administrador_puede_registrar_el_historial_de_una_importacion()
    {
        var repositorio = new HistorialImportacionRepositorioFalso();
        var unitOfWork = new ClientesFalsos.UnitOfWorkFalso();

        var resultado = await Handler("Administrador", repositorio, unitOfWork).Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Registros.Should().ContainSingle();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Otros_roles_no_pueden_registrar_el_historial_de_una_importacion(string? rol)
    {
        var repositorio = new HistorialImportacionRepositorioFalso();
        var unitOfWork = new ClientesFalsos.UnitOfWorkFalso();

        var resultado = await Handler(rol, repositorio, unitOfWork).Handle(Comando(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Importacion.SinAutoridadCargaMasiva");
        repositorio.Registros.Should().BeEmpty();
    }
}
