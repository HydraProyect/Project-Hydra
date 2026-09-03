using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// El escalón "¿recurso permitido?" del plano 3: una sesión de
/// <c>SoporteLectura</c> ve el tenant entero, y aun así no puede llevarse las
/// contraseñas de las plataformas externas del cliente.
///
/// La distinción no es de grado sino de naturaleza. Un documento del cliente es
/// un dato que se inspecciona y cuya lectura queda auditada. Una contraseña de
/// un portal de terceros es autoridad sobre otro sistema: sigue funcionando
/// cuando la sesión se cierre, fuera de TALVEG, donde nuestra auditoría no
/// llega. Ninguna de las cuatro capacidades del plano 3 la incluye — tampoco
/// break-glass, que puede escribir en los datos del cliente pero no quedarse
/// con sus llaves.
/// </summary>
public class AutorizacionSecretosDeTenantBehaviorTests
{
    private record CredencialDto(string Usuario, string Contrasena);

    private record ConsultaDeCredencialQuery
        : IRequest<CredencialDto?>, IConsultaDeSecretosDeTenant;

    private record ConsultaNormalQuery : IRequest<string?>;

    private static readonly CredencialDto Secreto = new("admin@cliente", "hunter2");

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task Ninguna_capacidad_del_plano_3_obtiene_los_secretos_del_tenant(CapacidadPrivilegio capacidad)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SesionCon(capacidad));

        var handlerFueLlamado = false;
        var resultado = await behavior.Handle(new ConsultaDeCredencialQuery(), _ =>
        {
            handlerFueLlamado = true;
            return Task.FromResult<CredencialDto?>(Secreto);
        }, CancellationToken.None);

        resultado.Should().BeNull();

        // El handler ni siquiera corre: la credencial no llega a descifrarse,
        // así que no pasa por memoria ni por ningún log del camino.
        handlerFueLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_sesion_privilegiada_la_consulta_de_credenciales_funciona_con_normalidad()
    {
        // Guarda de no regresión: quien gestiona de verdad esas plataformas es
        // el operador del tenant, y para él no cambia nada.
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SinSesion);

        var resultado = await behavior.Handle(
            new ConsultaDeCredencialQuery(), _ => Task.FromResult<CredencialDto?>(Secreto), CancellationToken.None);

        resultado.Should().Be(Secreto);
    }

    [Fact]
    public async Task Una_consulta_sin_marcar_no_se_toca_ni_bajo_sesion_privilegiada()
    {
        // Es lo que hace útil a SoporteLectura: todo lo demás del tenant sí se
        // ve. Si esto denegara, el incremento no habría introducido una
        // capacidad de inspección sino una pantalla en blanco.
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaNormalQuery, string?>(
            SesionCon(CapacidadPrivilegio.SoporteLectura));

        var resultado = await behavior.Handle(
            new ConsultaNormalQuery(), _ => Task.FromResult<string?>("datos del tenant"), CancellationToken.None);

        resultado.Should().Be("datos del tenant");
    }

    [Fact]
    public async Task Una_consulta_marcada_que_devolviera_un_tipo_por_valor_falla_ruidosamente()
    {
        // No es un caso de ejecución sino un error de programación: denegar
        // devolviendo default(int) sería inventarse un 0 que el consumidor
        // interpretaría como un dato. Mejor romper en el primer test que lo
        // toque que devolver un valor falso en producción.
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, int>(
            SesionCon(CapacidadPrivilegio.SoporteLectura));

        var accion = async () => await behavior.Handle(
            new ConsultaDeCredencialQuery(), _ => Task.FromResult(42), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tipo por valor*");
    }

    private static ISesionPrivilegiadaActual SesionCon(CapacidadPrivilegio capacidad) =>
        new SesionPrivilegiadaActualFalsa(
            new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), capacidad, null));

    private static readonly ISesionPrivilegiadaActual SinSesion = new SesionPrivilegiadaActualFalsa(null);

    private sealed class SesionPrivilegiadaActualFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }
}
