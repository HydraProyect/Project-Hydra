using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La UI ofrece los disparadores de escritura según <see cref="Roles.ConEscrituraCsv"/>
/// (<c>SoloConEscritura</c>); quien decide de verdad es
/// <c>AutorizacionEscrituraBehavior</c>, que repite la lista con literales
/// porque Application no puede referenciar Infrastructure.Identity. Si las dos
/// divergen, el defecto vuelve: un botón habilitado que falla al pulsarlo con
/// «Tu rol no permite crear, editar ni eliminar datos» (o, al revés, un botón
/// oculto a un rol que sí podía).
///
/// <para>
/// <b>Lo que SÍ observa:</b> para cada rol del sistema, si el behavior REAL deja
/// pasar un comando, y que coincide con si el rol figura en la lista de la UI.
/// No compara listas: ejecuta el behavior, que es lo que decide.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> que cada botón concreto esté envuelto en
/// <c>SoloConEscritura</c> (eso es de <c>DisparadoresDeEscrituraSoloParaRolesConEscrituraTests</c>,
/// solo para la familia de botones que mide, y de <c>SoloLecturaEnLaInterfazTests</c>
/// en Web.Tests por render), ni el rol efectivo en un
/// workspace delegado (lo pone <c>RolEfectivoDelWorkspaceMiddleware</c>).
/// </para>
/// </summary>
public class RolesConEscrituraParidadTests
{
    private record ComandoFalso : ICommand;
    private record ComandoDeAutoservicioFalso : ICommand, IComandoDeAutoservicio;

    private static Task<bool> ElBehaviorDejaPasar(string? rol) => ElBehaviorDejaPasar(new ComandoFalso(), rol);

    private static async Task<bool> ElBehaviorDejaPasar<TComando>(TComando comando, string? rol)
        where TComando : ICommand
    {
        var behavior = new AutorizacionEscrituraBehavior<TComando, Result>(
            new UsuarioConRol(rol), new SinSesionPrivilegiada(), new SinTenant());

        var resultado = await behavior.Handle(
            comando, _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        return resultado.EsExitoso;
    }

    [Fact]
    public async Task Cada_rol_del_sistema_escribe_en_el_behavior_si_y_solo_si_la_UI_le_ofrece_escribir()
    {
        var conEscrituraEnUi = Roles.ConEscrituraCsv.Split(',');

        // Control positivo del instrumento: si el behavior no dejara pasar a nadie, o
        // a todos, la igualdad de abajo se cumpliría por vacío.
        var pasan = new List<string>();
        foreach (var rol in Roles.Todos)
            if (await ElBehaviorDejaPasar(rol)) pasan.Add(rol);

        pasan.Should().NotBeEmpty("si el behavior no dejara pasar a ningún rol el test no observaría nada");
        pasan.Count.Should().BeLessThan(Roles.Todos.Count, "Consulta y Cliente son de solo lectura: si todos pasan, el instrumento no distingue");

        pasan.Should().BeEquivalentTo(conEscrituraEnUi,
            "una UI que ofrece lo que el behavior deniega (o esconde lo que permite) es el defecto que Roles.ConEscrituraCsv existe para evitar");
    }

    /// <summary>
    /// El behavior repite también, con literales, la lista de roles que existen: un
    /// <see cref="IComandoDeAutoservicio"/> pasa con cualquiera de ellos. Un rol nuevo
    /// en <see cref="Roles.Todos"/> que el behavior no conociera se quedaría sin poder
    /// aceptar sus términos, que es justo el atasco que el marcador existe para evitar.
    /// </summary>
    [Fact]
    public async Task Un_comando_de_autoservicio_pasa_con_todos_los_roles_del_sistema_y_con_ninguno_mas()
    {
        var pasan = new List<string>();
        foreach (var rol in Roles.Todos)
            if (await ElBehaviorDejaPasar(new ComandoDeAutoservicioFalso(), rol)) pasan.Add(rol);

        pasan.Should().BeEquivalentTo(Roles.Todos);
        (await ElBehaviorDejaPasar(new ComandoDeAutoservicioFalso(), null)).Should().BeFalse("sin rol no se escribe, ni lo propio");
        (await ElBehaviorDejaPasar(new ComandoDeAutoservicioFalso(), "RolInventado")).Should().BeFalse();
    }

    private record ConsultaDeSecretoFalsa : MediatR.IRequest<string?>, IConsultaDeSecretosDeTenant;

    /// <summary>
    /// Los botones que copian un secreto del Tenant (Centro 360, paneles de
    /// Empresa y Subcontrata) también van dentro de <c>SoloConEscritura</c>,
    /// porque la regla (decisión del propietario 2026-09-23) es la misma lista:
    /// leer una credencial de una plataforma CAE es poder actuar en ella. Si
    /// <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>
    /// divergiera de <see cref="Roles.ConEscrituraCsv"/>, la UI ofrecería un
    /// «Copiar contraseña» que no copia nada, o se lo escondería a quien sí puede.
    /// </summary>
    [Fact]
    public async Task Cada_rol_del_sistema_lee_secretos_del_tenant_si_y_solo_si_la_UI_le_ofrece_escribir()
    {
        var conEscrituraEnUi = Roles.ConEscrituraCsv.Split(',');

        var leen = new List<string>();
        foreach (var rol in Roles.Todos)
        {
            var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeSecretoFalsa, string?>(
                new SinSesionPrivilegiada(), new UsuarioConRol(rol));
            var resultado = await behavior.Handle(
                new ConsultaDeSecretoFalsa(), _ => Task.FromResult<string?>("secreto"), CancellationToken.None);
            if (resultado == "secreto") leen.Add(rol);
        }

        leen.Should().NotBeEmpty("si el behavior no dejara leer a ningún rol el test no observaría nada");
        leen.Count.Should().BeLessThan(Roles.Todos.Count, "Consulta y Cliente no leen secretos: si todos leen, el instrumento no distingue");
        leen.Should().BeEquivalentTo(conEscrituraEnUi);
    }

    private sealed class UsuarioConRol(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
