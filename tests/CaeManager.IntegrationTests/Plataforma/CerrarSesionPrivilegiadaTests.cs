using CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La ceremonia de cierre (B1.3).
///
/// <para>
/// El comando existía desde F2b-6 y <b>no tenía ni un test ni un llamador de
/// producción</b> — exactamente la condición en la que estaba la apertura antes
/// de B1.1. Borrarlo entero habría dejado la suite en verde.
/// </para>
///
/// <para>
/// <b>Por qué el cierre importa aunque exista la ventana.</b> La ventana acota el
/// peor caso; cerrar es lo que hace que el caso normal dure lo que dura la
/// incidencia. Una sesión que solo puede esperar a caducar convierte «he
/// terminado» en un dato que nadie registra, y es justo el dato que el Tenant
/// visitado tiene derecho a preguntar.
/// </para>
///
/// <para>
/// Lo que estos tests <b>no</b> cubren, y vive en <c>SesionSoporteEndpointsTests</c>:
/// que no se pueda cerrar desde dentro del tenant visitado. Esa guarda es del
/// endpoint porque depende de la cookie, y la cookie no existe en esta capa.
/// </para>
/// </summary>
public class CerrarSesionPrivilegiadaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();

    private Guid _sesionAbierta;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));

        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();

        var sesion = SesionPrivilegiada.Abrir(
            concesion, _tenantVisitado, "Reproducir la incidencia", ahora, TimeSpan.FromHours(1));

        contexto.SesionesPrivilegiadas.Add(sesion);
        await contexto.SaveChangesAsync();

        _sesionAbierta = sesion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// El efecto, comprobado en la base y no en el <c>Result</c>: que el comando
    /// devuelva éxito no demuestra que la fila haya cambiado.
    /// </summary>
    [Fact]
    public async Task La_sesion_queda_cerrada()
    {
        var resultado = await EjecutarAsync(_sesionAbierta);

        resultado.EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var sesion = await contexto.SesionesPrivilegiadas.SingleAsync(s => s.Id == _sesionAbierta);

        sesion.EstaAbierta.Should().BeFalse();
        sesion.CerradaEnUtc.Should().NotBeNull(
            "el cierre es el dato que responde a «cuándo terminó la visita»: sin instante grabado, " +
            "la sesión constaría cerrada sin decir desde cuándo");
    }

    /// <summary>
    /// La sesión sigue vigente hasta que se cierra — control negativo del test de
    /// arriba. Sin él, <c>EstaAbierta == false</c> podría venir de que la fila
    /// naciera cerrada y el comando no hiciera nada.
    /// </summary>
    [Fact]
    public async Task Antes_de_cerrarla_la_sesion_esta_abierta_y_vigente()
    {
        await using var contexto = CrearContexto();
        var sesion = await contexto.SesionesPrivilegiadas.SingleAsync(s => s.Id == _sesionAbierta);

        sesion.EstaAbierta.Should().BeTrue();
        sesion.EstaVigenteEn(DateTime.UtcNow).Should().BeTrue();
    }

    /// <summary>
    /// Cerrar dos veces es un fallo con su propio código, no una excepción y no un
    /// éxito silencioso. Importa porque el botón de la pantalla es reenviable: un
    /// F5 sobre el POST no puede acabar en un 500.
    /// </summary>
    [Fact]
    public async Task Una_sesion_ya_cerrada_no_se_cierra_dos_veces()
    {
        (await EjecutarAsync(_sesionAbierta)).EsExitoso.Should().BeTrue();

        var segundo = await EjecutarAsync(_sesionAbierta);

        segundo.EsFallido.Should().BeTrue();
        segundo.Error.Codigo.Should().Be("SesionPrivilegiada.YaCerrada");
    }

    /// <summary>
    /// Un id que no existe se comporta igual que uno que RLS no entrega: fallo
    /// con código, sin distinguir entre «no existe» y «no es tuya». Esa
    /// indistinguibilidad es deliberada — decir cuál de las dos es filtraría qué
    /// sesiones existen.
    /// </summary>
    [Fact]
    public async Task Una_sesion_inexistente_no_se_cierra()
    {
        var resultado = await EjecutarAsync(Guid.NewGuid());

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SesionPrivilegiada.NoEncontrada");

        await using var contexto = CrearContexto();
        (await contexto.SesionesPrivilegiadas.SingleAsync(s => s.Id == _sesionAbierta))
            .EstaAbierta.Should().BeTrue("pedir una sesión ajena no puede cerrar la propia");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Domain.Common.Result> EjecutarAsync(Guid sesionId)
    {
        await using var contexto = CrearContexto();

        var handler = new CerrarSesionPrivilegiadaCommandHandler(contexto, contexto);

        return await handler.Handle(
            new CerrarSesionPrivilegiadaCommand(sesionId), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantPlataforma };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
